const std = @import("std");
const ir = @import("backend_ir.zig");
const Backend = @import("backend.zig").Backend;

const max_ir_bytes = 64 * 1024 * 1024;

pub fn main(init: std.process.Init) !void {
    const args = try init.minimal.args.toSlice(init.arena.allocator());
    if (args.len != 2) {
        try std.Io.File.stderr().writeStreamingAll(
            init.io,
            "usage: zorb-llvm-backend <backend-ir.json>\n",
        );
        return error.InvalidArguments;
    }

    const input = try std.Io.Dir.cwd().readFileAlloc(
        init.io,
        args[1],
        init.gpa,
        .limited(max_ir_bytes),
    );
    defer init.gpa.free(input);

    const parsed = std.json.parseFromSlice(ir.Module, init.gpa, input, .{
        .ignore_unknown_fields = false,
    }) catch |err| {
        try writeError(init.io, "failed to parse backend IR", err);
        return err;
    };
    defer parsed.deinit();

    var diagnostic_buffer: [4096]u8 = undefined;
    var diagnostics: std.Io.Writer = .fixed(&diagnostic_buffer);
    parsed.value.validate(&diagnostics) catch |err| {
        try std.Io.File.stderr().writeStreamingAll(init.io, diagnostics.buffered());
        return err;
    };

    var backend = Backend.init(init.gpa, &parsed.value) catch |err| {
        try writeError(init.io, "failed to initialize LLVM backend", err);
        return err;
    };
    defer backend.deinit();
    backend.emit() catch |err| {
        try writeError(init.io, "LLVM backend emission failed", err);
        return err;
    };
}

fn writeError(io: std.Io, prefix: []const u8, err: anyerror) !void {
    var buffer: [1024]u8 = undefined;
    var writer: std.Io.Writer = .fixed(&buffer);
    try writer.print("{s}: {s}\n", .{ prefix, @errorName(err) });
    try std.Io.File.stderr().writeStreamingAll(io, writer.buffered());
}
