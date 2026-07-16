const std = @import("std");

pub const windows_base_arg_count: usize = 4;

pub fn prepareWindowsEntryRetry(
    allocator: std.mem.Allocator,
    initial_args: []const []const u8,
) ![][]const u8 {
    if (initial_args.len < windows_base_arg_count)
        return error.InvalidArgument;

    const entry_arg_count: usize = 3;
    const retry_args = try allocator.alloc(
        []const u8,
        initial_args.len + entry_arg_count,
    );
    for (0..windows_base_arg_count) |index|
        retry_args[index] = initial_args[index];
    retry_args[windows_base_arg_count] = "/link";
    retry_args[windows_base_arg_count + 1] = "/entry:_start";
    retry_args[windows_base_arg_count + 2] = "/subsystem:console";
    for (windows_base_arg_count..initial_args.len) |index|
        retry_args[index + entry_arg_count] = initial_args[index];
    return retry_args;
}

test "Windows entry retry selects the Zorb start symbol" {
    const initial_args = [_][]const u8{
        "clang-cl",
        "/nologo",
        "program.obj",
        "/Fe:program.exe",
        "/defaultlib:kernel32.lib",
    };
    const retry_args = try prepareWindowsEntryRetry(
        std.testing.allocator,
        &initial_args,
    );
    defer std.testing.allocator.free(retry_args);

    const expected_args = [_][]const u8{
        "clang-cl",
        "/nologo",
        "program.obj",
        "/Fe:program.exe",
        "/link",
        "/entry:_start",
        "/subsystem:console",
        "/defaultlib:kernel32.lib",
    };
    try std.testing.expectEqual(expected_args.len, retry_args.len);
    for (expected_args, retry_args) |expected, actual|
        try std.testing.expectEqualStrings(expected, actual);
}
