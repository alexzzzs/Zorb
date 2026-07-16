const std = @import("std");
const api = @import("api.zig");

pub fn main(init: std.process.Init) !void {
    const args = try init.minimal.args.toSlice(init.arena.allocator());
    if (args.len != 2) {
        try std.Io.File.stderr().writeStreamingAll(
            init.io,
            "usage: zorb-llvm-backend <backend-ir.json>\n",
        );
        return error.InvalidArguments;
    }

    api.emitFile(init.gpa, init.io, args[1]) catch |err| {
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
