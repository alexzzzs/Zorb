const std = @import("std");

pub const MAX_MESSAGE_BYTES: usize = 16 * 1024 * 1024;
pub const READER_BUFFER_BYTES: usize = 8192;

pub fn readMessage(allocator: std.mem.Allocator, reader: *std.Io.Reader) !?[]u8 {
    var content_length: ?usize = null;

    while (true) {
        const maybe_line = reader.takeDelimiter('\n') catch |err| switch (err) {
            error.ReadFailed => return error.ReadFailed,
            error.StreamTooLong => return error.HeaderLineTooLong,
        };
        const line = maybe_line orelse return null;
        const header = std.mem.trim(u8, line, "\r\n");
        if (header.len == 0) break;

        if (std.mem.startsWith(u8, header, "Content-Length:")) {
            if (content_length != null) return error.DuplicateContentLength;
            const value = std.mem.trim(u8, header["Content-Length:".len..], " ");
            content_length = try std.fmt.parseInt(usize, value, 10);
        }
    }

    const length = content_length orelse return error.MissingContentLength;
    if (length > MAX_MESSAGE_BYTES) return error.MessageTooLarge;
    return try reader.readAlloc(allocator, length);
}

pub fn writeMessage(allocator: std.mem.Allocator, io: std.Io, payload: []const u8) !void {
    const header = try std.fmt.allocPrint(
        allocator,
        "Content-Length: {d}\r\n\r\n",
        .{payload.len},
    );
    defer allocator.free(header);

    const stdout = std.Io.File.stdout();
    try stdout.writeStreamingAll(io, header);
    try stdout.writeStreamingAll(io, payload);
}

pub fn writeJson(allocator: std.mem.Allocator, io: std.Io, value: anytype) !void {
    const payload = try std.json.Stringify.valueAlloc(allocator, value, .{});
    defer allocator.free(payload);
    try writeMessage(allocator, io, payload);
}
