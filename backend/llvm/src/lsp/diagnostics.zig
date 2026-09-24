const builtin = @import("builtin");
const std = @import("std");

pub const MAX_COMPILER_OUTPUT_BYTES: usize = 16 * 1024 * 1024;
const DIAGNOSTIC_SOURCE = "zorb";
const COMPILER_FAILURE_CODE = "ZORB_LSP_COMPILER";

pub const Position = struct {
    line: usize,
    character: usize,
};

pub const Range = struct {
    start: Position,
    end: Position,
};

pub const Diagnostic = struct {
    range: Range,
    severity: u8,
    code: []const u8,
    source: []const u8 = DIAGNOSTIC_SOURCE,
    message: []const u8,
};

pub fn uriToPath(allocator: std.mem.Allocator, uri_text: []const u8) ![]const u8 {
    const uri = try std.Uri.parse(uri_text);
    if (!std.mem.eql(u8, uri.scheme, "file")) return error.UnsupportedDocumentUri;
    if (uri.query != null or uri.fragment != null) return error.UnsupportedDocumentUri;

    const path = try uri.path.toRawMaybeAlloc(allocator);
    if (path.len == 0) return error.InvalidDocumentPath;

    if (uri.host) |host_component| {
        const host = try host_component.toRawMaybeAlloc(allocator);
        if (std.ascii.eqlIgnoreCase(host, "localhost")) {
            return try normalizeLocalPath(allocator, path);
        }
        if (builtin.os.tag == .windows) {
            return try std.fmt.allocPrint(allocator, "\\\\{s}{s}", .{ host, path });
        }
        return error.UnsupportedDocumentUri;
    }

    return try normalizeLocalPath(allocator, path);
}

fn normalizeLocalPath(allocator: std.mem.Allocator, path: []const u8) ![]const u8 {
    var normalized = path;
    if (builtin.os.tag == .windows and path.len >= 3 and path[0] == '/' and
        std.ascii.isAlphabetic(path[1]) and path[2] == ':')
    {
        normalized = path[1..];
    }
    if (!std.fs.path.isAbsolute(normalized)) return error.InvalidDocumentPath;
    return try allocator.dupe(u8, normalized);
}

pub fn collect(
    allocator: std.mem.Allocator,
    io: std.Io,
    compiler_path: []const u8,
    source_path: []const u8,
    source_text: []const u8,
    overlay_counter: *usize,
) !std.ArrayList(Diagnostic) {
    const overlay_path = try writeOverlay(allocator, io, source_path, source_text, overlay_counter);
    defer std.Io.Dir.deleteFileAbsolute(io, overlay_path) catch {};

    const argv = [_][]const u8{ compiler_path, "check", overlay_path, "--json" };
    const result = try std.process.run(allocator, io, .{
        .argv = &argv,
        .stdout_limit = .limited(MAX_COMPILER_OUTPUT_BYTES),
        .stderr_limit = .limited(MAX_COMPILER_OUTPUT_BYTES),
    });

    var diagnostics: std.ArrayList(Diagnostic) = .empty;
    try appendCompilerDiagnostics(allocator, source_text, overlay_path, result.stdout, &diagnostics);

    const succeeded = switch (result.term) {
        .exited => |status| status == 0,
        else => false,
    };
    if (!succeeded and diagnostics.items.len == 0) {
        const stderr = std.mem.trim(u8, result.stderr, " \r\n\t");
        const message = if (stderr.len != 0)
            try allocator.dupe(u8, stderr)
        else
            try allocator.dupe(u8, "The compiler exited without returning diagnostics.");
        try diagnostics.append(allocator, .{
            .range = zeroRange(),
            .severity = 1,
            .code = COMPILER_FAILURE_CODE,
            .message = message,
        });
    }

    return diagnostics;
}

fn writeOverlay(
    allocator: std.mem.Allocator,
    io: std.Io,
    source_path: []const u8,
    source_text: []const u8,
    counter: *usize,
) ![]u8 {
    while (true) {
        const overlay_path = try std.fmt.allocPrint(allocator, "{s}.zorb-lsp-{d}.tmp", .{
            source_path,
            counter.*,
        });
        counter.* = std.math.add(usize, counter.*, 1) catch return error.OverlayCounterExhausted;

        var file = std.Io.Dir.createFileAbsolute(io, overlay_path, .{
            .exclusive = true,
        }) catch |err| switch (err) {
            error.PathAlreadyExists => continue,
            else => return err,
        };

        var file_open = true;
        var retain_overlay = false;
        defer {
            if (file_open) file.close(io);
            if (!retain_overlay) std.Io.Dir.deleteFileAbsolute(io, overlay_path) catch {};
        }
        try file.writeStreamingAll(io, source_text);
        file.close(io);
        file_open = false;
        retain_overlay = true;
        return overlay_path;
    }
}

fn appendCompilerDiagnostics(
    allocator: std.mem.Allocator,
    source_text: []const u8,
    overlay_path: []const u8,
    compiler_output: []const u8,
    diagnostics: *std.ArrayList(Diagnostic),
) !void {
    var lines = std.mem.splitScalar(u8, compiler_output, '\n');
    while (lines.next()) |line| {
        const json_line = std.mem.trim(u8, line, "\r \t");
        if (json_line.len == 0) continue;

        var parsed = std.json.parseFromSlice(std.json.Value, allocator, json_line, .{}) catch continue;
        defer parsed.deinit();
        const value = parsed.value;
        if (stringField(value, "kind") == null or
            !std.mem.eql(u8, stringField(value, "kind").?, "diagnostic"))
        {
            continue;
        }
        const diagnostic_file = stringField(value, "file") orelse continue;
        if (!std.mem.eql(u8, diagnostic_file, overlay_path)) continue;

        const severity: u8 = if (std.mem.eql(u8, stringField(value, "severity") orelse "error", "warning"))
            2
        else
            1;
        const start_offset = sourceByteOffset(
            source_text,
            integerField(value, "line", 1),
            integerField(value, "column", 1),
        );
        const raw_length = integerField(value, "length", 1);
        const length: usize = if (raw_length <= 0) 1 else @intCast(raw_length);
        const end_offset = @min(source_text.len, start_offset +| length);
        const start = positionAtByteOffset(source_text, utf8Floor(source_text, start_offset));
        const end = positionAtByteOffset(source_text, utf8Ceil(source_text, end_offset));
        try diagnostics.append(allocator, .{
            .range = .{ .start = start, .end = end },
            .severity = severity,
            .code = try allocator.dupe(u8, stringField(value, "code") orelse ""),
            .message = try allocator.dupe(u8, stringField(value, "message") orelse "Compilation error"),
        });
    }
}

fn sourceByteOffset(source: []const u8, line_1: i64, column_1: i64) usize {
    const requested_line: usize = if (line_1 < 1) 1 else @intCast(line_1);
    const column_offset: usize = if (column_1 < 1) 0 else @intCast(column_1 - 1);
    var current_line: usize = 1;
    var line_start: usize = 0;
    for (source, 0..) |byte, index| {
        if (current_line >= requested_line) break;
        if (byte == '\n') {
            current_line += 1;
            line_start = index + 1;
        }
    }
    return line_start + @min(column_offset, source.len - line_start);
}

fn positionAtByteOffset(source: []const u8, offset: usize) Position {
    var line: usize = 0;
    var line_start: usize = 0;
    for (source[0..offset], 0..) |byte, index| {
        if (byte == '\n') {
            line += 1;
            line_start = index + 1;
        }
    }
    var prefix = source[line_start..offset];
    if (offset < source.len and source[offset] == '\n' and std.mem.endsWith(u8, prefix, "\r")) {
        prefix = prefix[0 .. prefix.len - 1];
    }
    return .{ .line = line, .character = utf16Length(prefix) };
}

fn utf16Length(text: []const u8) usize {
    const view = std.unicode.Utf8View.init(text) catch return text.len;
    var iterator = view.iterator();
    var count: usize = 0;
    while (iterator.nextCodepoint()) |codepoint| {
        count += if (codepoint > 0xffff) 2 else 1;
    }
    return count;
}

fn utf8Floor(source: []const u8, offset: usize) usize {
    var result = offset;
    while (result > 0 and result < source.len and isUtf8Continuation(source[result])) : (result -= 1) {}
    return result;
}

fn utf8Ceil(source: []const u8, offset: usize) usize {
    var result = offset;
    while (result < source.len and isUtf8Continuation(source[result])) : (result += 1) {}
    return result;
}

fn isUtf8Continuation(byte: u8) bool {
    return (byte & 0xc0) == 0x80;
}

fn stringField(value: std.json.Value, name: []const u8) ?[]const u8 {
    if (value != .object) return null;
    const field = value.object.get(name) orelse return null;
    return if (field == .string) field.string else null;
}

fn integerField(value: std.json.Value, name: []const u8, default: i64) i64 {
    if (value != .object) return default;
    const field = value.object.get(name) orelse return default;
    return switch (field) {
        .integer => |number| number,
        .number_string => |number| std.fmt.parseInt(i64, number, 10) catch default,
        else => default,
    };
}

pub fn zeroRange() Range {
    const start = Position{ .line = 0, .character = 0 };
    return .{ .start = start, .end = start };
}
