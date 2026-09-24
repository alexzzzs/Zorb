const std = @import("std");
const diagnostics = @import("lsp/diagnostics.zig");
const rpc = @import("lsp/rpc.zig");

const SERVER_NAME = "zorb-lsp";

const Server = struct {
    allocator: std.mem.Allocator,
    io: std.Io,
    compiler_path: []const u8,
    overlay_counter: usize = 1,
    shutdown_received: bool = false,
};

pub fn main(init: std.process.Init) !void {
    const compiler_path = try parseCompilerPath(init);
    defer init.gpa.free(compiler_path);

    var server = Server{
        .allocator = init.gpa,
        .io = init.io,
        .compiler_path = compiler_path,
    };
    var input_buffer: [rpc.READER_BUFFER_BYTES]u8 = undefined;
    var reader = std.Io.File.stdin().readerStreaming(init.io, &input_buffer);

    while (try rpc.readMessage(init.gpa, &reader.interface)) |message| {
        defer init.gpa.free(message);
        var message_arena = std.heap.ArenaAllocator.init(init.gpa);
        defer message_arena.deinit();
        if (try handleMessage(message_arena.allocator(), &server, message)) break;
    }
}

fn parseCompilerPath(init: std.process.Init) ![]u8 {
    var args = try std.process.Args.iterateAllocator(init.minimal.args, init.gpa);
    defer args.deinit();
    _ = args.next();

    while (args.next()) |arg| {
        if (std.mem.eql(u8, arg, "--help") or std.mem.eql(u8, arg, "-h")) {
            std.debug.print("usage: zorb-lsp [--compiler path/to/zorb]\n", .{});
            std.process.exit(0);
        }
        if (std.mem.eql(u8, arg, "--compiler")) {
            const path = args.next() orelse return error.MissingCompilerPath;
            return try init.gpa.dupe(u8, path);
        }
        std.debug.print("zorb-lsp: unknown argument: {s}\n", .{arg});
        return error.InvalidArgument;
    }

    const executable = try std.process.executablePathAlloc(init.io, init.gpa);
    defer init.gpa.free(executable);
    const directory = std.fs.path.dirname(executable) orelse ".";
    const compiler_name = if (@import("builtin").os.tag == .windows) "zorb.exe" else "zorb";
    return try std.fs.path.join(init.gpa, &.{ directory, compiler_name });
}

fn handleMessage(allocator: std.mem.Allocator, server: *Server, message: []const u8) !bool {
    var parsed = std.json.parseFromSlice(std.json.Value, allocator, message, .{}) catch {
        try sendError(allocator, server.io, .null, -32700, "Parse error");
        return false;
    };
    defer parsed.deinit();
    const root = parsed.value;
    if (root != .object) {
        try sendError(allocator, server.io, .null, -32600, "Invalid Request");
        return false;
    }

    const method_value = root.object.get("method") orelse {
        try sendError(allocator, server.io, .null, -32600, "Invalid Request");
        return false;
    };
    if (method_value != .string) {
        try sendError(allocator, server.io, field(root, "id") orelse .null, -32600, "Invalid Request");
        return false;
    }
    const method = method_value.string;
    const id = field(root, "id");
    const params = field(root, "params") orelse .null;

    if (std.mem.eql(u8, method, "initialize")) {
        if (id) |request_id| try sendResponse(allocator, server.io, request_id, .{
            .capabilities = .{
                .textDocumentSync = .{
                    .openClose = true,
                    .change = 1,
                },
            },
            .serverInfo = .{ .name = SERVER_NAME },
        });
        return false;
    }
    if (std.mem.eql(u8, method, "shutdown")) {
        server.shutdown_received = true;
        if (id) |request_id| try sendResponse(allocator, server.io, request_id, std.json.Value.null);
        return false;
    }
    if (std.mem.eql(u8, method, "exit")) {
        if (!server.shutdown_received) std.process.exit(1);
        return true;
    }
    if (std.mem.eql(u8, method, "textDocument/didOpen")) {
        const text_document = field(params, "textDocument") orelse return false;
        const uri = stringField(text_document, "uri") orelse return false;
        const text = stringField(text_document, "text") orelse return false;
        try publishForDocument(allocator, server, uri, text);
        return false;
    }
    if (std.mem.eql(u8, method, "textDocument/didChange")) {
        const text_document = field(params, "textDocument") orelse return false;
        const uri = stringField(text_document, "uri") orelse return false;
        const changes = field(params, "contentChanges") orelse return false;
        if (changes != .array or changes.array.items.len == 0) return false;
        var changed_text: ?[]const u8 = null;
        for (changes.array.items) |change| {
            if (stringField(change, "text")) |text| changed_text = text;
        }
        if (changed_text) |text| try publishForDocument(allocator, server, uri, text);
        return false;
    }
    if (std.mem.eql(u8, method, "textDocument/didClose")) {
        const text_document = field(params, "textDocument") orelse return false;
        const uri = stringField(text_document, "uri") orelse return false;
        try publishDiagnostics(allocator, server.io, uri, &.{});
        return false;
    }

    if (id) |request_id| try sendError(allocator, server.io, request_id, -32601, "Method not found");
    return false;
}

fn publishForDocument(
    allocator: std.mem.Allocator,
    server: *Server,
    uri: []const u8,
    source_text: []const u8,
) !void {
    const source_path = diagnostics.uriToPath(allocator, uri) catch |err| {
        const diagnostic = [_]diagnostics.Diagnostic{.{
            .range = diagnostics.zeroRange(),
            .severity = 1,
            .code = "ZORB_LSP_URI",
            .message = try std.fmt.allocPrint(allocator, "Unable to read this document URI: {s}", .{@errorName(err)}),
        }};
        try publishDiagnostics(allocator, server.io, uri, &diagnostic);
        return;
    };

    const results = diagnostics.collect(
        allocator,
        server.io,
        server.compiler_path,
        source_path,
        source_text,
        &server.overlay_counter,
    ) catch |err| {
        const diagnostic = [_]diagnostics.Diagnostic{.{
            .range = diagnostics.zeroRange(),
            .severity = 1,
            .code = "ZORB_LSP_COMPILER",
            .message = try std.fmt.allocPrint(allocator, "Unable to run the Zorb compiler: {s}", .{@errorName(err)}),
        }};
        try publishDiagnostics(allocator, server.io, uri, &diagnostic);
        return;
    };
    try publishDiagnostics(allocator, server.io, uri, results.items);
}

fn publishDiagnostics(
    allocator: std.mem.Allocator,
    io: std.Io,
    uri: []const u8,
    items: []const diagnostics.Diagnostic,
) !void {
    try rpc.writeJson(allocator, io, .{
        .jsonrpc = "2.0",
        .method = "textDocument/publishDiagnostics",
        .params = .{ .uri = uri, .diagnostics = items },
    });
}

fn sendResponse(allocator: std.mem.Allocator, io: std.Io, id: std.json.Value, result: anytype) !void {
    const Response = struct {
        jsonrpc: []const u8 = "2.0",
        id: std.json.Value,
        result: @TypeOf(result),
    };
    try rpc.writeJson(allocator, io, Response{ .id = id, .result = result });
}

fn sendError(
    allocator: std.mem.Allocator,
    io: std.Io,
    id: std.json.Value,
    code: i64,
    message: []const u8,
) !void {
    const ErrorBody = struct { code: i64, message: []const u8 };
    const ErrorResponse = struct {
        jsonrpc: []const u8 = "2.0",
        id: std.json.Value,
        @"error": ErrorBody,
    };
    try rpc.writeJson(allocator, io, ErrorResponse{
        .id = id,
        .@"error" = .{ .code = code, .message = message },
    });
}

fn field(value: std.json.Value, name: []const u8) ?std.json.Value {
    if (value != .object) return null;
    return value.object.get(name);
}

fn stringField(value: std.json.Value, name: []const u8) ?[]const u8 {
    const child = field(value, name) orelse return null;
    return if (child == .string) child.string else null;
}
