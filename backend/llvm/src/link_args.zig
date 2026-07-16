const std = @import("std");

pub const windows_base_arg_count: usize = 9;

pub fn populateWindowsBaseArgs(
    args: [][]const u8,
    object_path: []const u8,
    output_arg: []const u8,
) !void {
    if (args.len < windows_base_arg_count)
        return error.InvalidArgument;

    args[0] = "clang-cl";
    args[1] = "/nologo";
    args[2] = object_path;
    args[3] = output_arg;
    args[4] = "/link";
    args[5] = "/subsystem:console";
    // LLVM-generated objects do not contain MSVC /DEFAULTLIB directives.
    args[6] = "kernel32.lib";
    args[7] = "ucrt.lib";
    args[8] = "ws2_32.lib";
}

pub fn prepareWindowsEntryRetry(
    allocator: std.mem.Allocator,
    initial_args: []const []const u8,
) ![][]const u8 {
    if (initial_args.len < windows_base_arg_count)
        return error.InvalidArgument;

    const entry_arg_count: usize = 1;
    const retry_args = try allocator.alloc(
        []const u8,
        initial_args.len + entry_arg_count,
    );
    const entry_arg_index: usize = 6;
    for (0..entry_arg_index) |index|
        retry_args[index] = initial_args[index];
    retry_args[entry_arg_index] = "/entry:_start";
    for (entry_arg_index..initial_args.len) |index|
        retry_args[index + entry_arg_count] = initial_args[index];
    return retry_args;
}

test "Windows entry retry selects the Zorb start symbol" {
    var initial_args: [windows_base_arg_count + 1][]const u8 = undefined;
    try populateWindowsBaseArgs(
        &initial_args,
        "program.obj",
        "/Fe:program.exe",
    );
    initial_args[windows_base_arg_count] = "/debug";
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
        "/subsystem:console",
        "/entry:_start",
        "kernel32.lib",
        "ucrt.lib",
        "ws2_32.lib",
        "/debug",
    };
    try std.testing.expectEqual(expected_args.len, retry_args.len);
    for (expected_args, retry_args) |expected, actual|
        try std.testing.expectEqualStrings(expected, actual);
}
