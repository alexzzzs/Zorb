const std = @import("std");
const builtin = @import("builtin");

pub const aarch64_compiler_env = "ZORB_AARCH64_LINUX_GCC";
pub const aarch64_qemu_env = "ZORB_QEMU_AARCH64";
pub const aarch64_sysroot_env = "ZORB_AARCH64_LINUX_SYSROOT";
pub const bare_metal_linker_env = "ZORB_LLD";
pub const default_aarch64_sysroot = "/usr/aarch64-linux-gnu";

pub const bare_metal_default_linker_script =
    \\OUTPUT_FORMAT(elf64-x86-64)
    \\OUTPUT_ARCH(i386:x86-64)
    \\ENTRY(_start)
    \\
    \\SECTIONS
    \\{
    \\    . = 1M;
    \\
    \\    .text ALIGN(4K) :
    \\    {
    \\        *(.text .text.*)
    \\    }
    \\
    \\    .rodata ALIGN(4K) :
    \\    {
    \\        *(.rodata .rodata.*)
    \\    }
    \\
    \\    .data ALIGN(4K) :
    \\    {
    \\        *(.data .data.*)
    \\    }
    \\
    \\    .bss ALIGN(4K) :
    \\    {
    \\        *(COMMON)
    \\        *(.bss .bss.*)
    \\    }
    \\
    \\    /DISCARD/ :
    \\    {
    \\        *(.comment)
    \\        *(.eh_frame)
    \\        *(.note .note.*)
    \\    }
    \\}
;

pub const LinkTarget = enum {
    host_linux,
    freestanding_linux,
    host_linux_aarch64,
    freestanding_linux_aarch64,
    bare_metal_x86_64,
    host_windows,

    pub fn parse(name: []const u8) !LinkTarget {
        if (std.mem.eql(u8, name, "host-linux")) return .host_linux;
        if (std.mem.eql(u8, name, "freestanding-linux")) return .freestanding_linux;
        if (std.mem.eql(u8, name, "host-linux-aarch64")) return .host_linux_aarch64;
        if (std.mem.eql(u8, name, "freestanding-linux-aarch64")) return .freestanding_linux_aarch64;
        if (std.mem.eql(u8, name, "bare-metal-x86_64")) return .bare_metal_x86_64;
        if (std.mem.eql(u8, name, "host-windows")) return .host_windows;
        return error.UnsupportedTarget;
    }

    pub fn isAarch64Linux(target: LinkTarget) bool {
        return target == .host_linux_aarch64 or target == .freestanding_linux_aarch64;
    }

    pub fn isBareMetal(target: LinkTarget) bool {
        return target == .bare_metal_x86_64;
    }

    pub fn supportsHost(target: LinkTarget) bool {
        return switch (target) {
            .host_linux, .freestanding_linux => builtin.os.tag == .linux and
                (builtin.cpu.arch == .x86_64 or builtin.cpu.arch == .aarch64),
            .host_linux_aarch64, .freestanding_linux_aarch64 => builtin.os.tag == .linux,
            .bare_metal_x86_64 => (builtin.os.tag == .linux or builtin.os.tag == .windows) and builtin.cpu.arch == .x86_64,
            .host_windows => builtin.os.tag == .windows and
                (builtin.cpu.arch == .x86_64 or builtin.cpu.arch == .aarch64),
        };
    }
};

pub fn baseArgCount(target: LinkTarget) usize {
    return switch (target) {
        .host_linux, .host_linux_aarch64 => 5,
        .freestanding_linux, .freestanding_linux_aarch64 => 10,
        .bare_metal_x86_64 => 10,
        .host_windows => 9,
    };
}

pub const PopulateOptions = struct {
    target: LinkTarget,
    object_path: []const u8,
    output_path: []const u8,
    windows_output_arg: []const u8 = "",
    linker_script_path: []const u8 = "",
    aarch64_compiler: []const u8 = "aarch64-linux-gnu-gcc",
    bare_metal_linker: []const u8 = "ld.lld",
};

pub fn populateBaseArgs(args: [][]const u8, options: PopulateOptions) !void {
    if (args.len < baseArgCount(options.target)) return error.InvalidArgument;
    switch (options.target) {
        .host_linux, .host_linux_aarch64 => {
            args[0] = if (options.target.isAarch64Linux()) options.aarch64_compiler else "gcc";
            args[1] = "-no-pie";
            args[2] = options.object_path;
            args[3] = "-o";
            args[4] = options.output_path;
        },
        .freestanding_linux, .freestanding_linux_aarch64 => {
            args[0] = if (options.target.isAarch64Linux()) options.aarch64_compiler else "gcc";
            args[1] = "-nostdlib";
            args[2] = "-fno-pie";
            args[3] = "-no-pie";
            args[4] = "-z";
            args[5] = "execstack";
            args[6] = "-fno-builtin";
            args[7] = options.object_path;
            args[8] = "-o";
            args[9] = options.output_path;
        },
        .bare_metal_x86_64 => {
            if (options.linker_script_path.len == 0) return error.InvalidArgument;
            args[0] = options.bare_metal_linker;
            args[1] = "-m";
            args[2] = "elf_x86_64";
            args[3] = "-T";
            args[4] = options.linker_script_path;
            args[5] = "-z";
            args[6] = "max-page-size=0x1000";
            args[7] = "-o";
            args[8] = options.output_path;
            args[9] = options.object_path;
        },
        .host_windows => try populateWindowsBaseArgs(args, options.object_path, options.windows_output_arg),
    }
}

pub fn populateWindowsBaseArgs(
    args: [][]const u8,
    object_path: []const u8,
    output_arg: []const u8,
) !void {
    if (args.len < baseArgCount(.host_windows) or output_arg.len == 0)
        return error.InvalidArgument;

    args[0] = "clang-cl";
    args[1] = "/nologo";
    args[2] = object_path;
    args[3] = output_arg;
    args[4] = "/link";
    args[5] = "/subsystem:console";
    args[6] = "kernel32.lib";
    args[7] = "ucrt.lib";
    args[8] = "ws2_32.lib";
}

pub fn prepareWindowsEntryRetry(
    allocator: std.mem.Allocator,
    initial_args: []const []const u8,
) ![][]const u8 {
    if (initial_args.len < baseArgCount(.host_windows))
        return error.InvalidArgument;

    const retry_args = try allocator.alloc([]const u8, initial_args.len + 1);
    const entry_arg_index: usize = 6;
    for (0..entry_arg_index) |index| retry_args[index] = initial_args[index];
    retry_args[entry_arg_index] = "/entry:_start";
    for (entry_arg_index..initial_args.len) |index| retry_args[index + 1] = initial_args[index];
    return retry_args;
}

pub fn prepareHostedLinuxEntryRetry(
    allocator: std.mem.Allocator,
    initial_args: []const []const u8,
) ![][]const u8 {
    if (initial_args.len < baseArgCount(.host_linux)) return error.InvalidArgument;
    const retry_args = try allocator.alloc([]const u8, initial_args.len + 1);
    retry_args[0] = initial_args[0];
    retry_args[1] = initial_args[1];
    retry_args[2] = "-nostartfiles";
    for (2..initial_args.len) |index| retry_args[index + 1] = initial_args[index];
    return retry_args;
}

fn expectArgs(expected: []const []const u8, actual: []const []const u8) !void {
    try std.testing.expectEqual(expected.len, actual.len);
    for (expected, actual) |expected_arg, actual_arg|
        try std.testing.expectEqualStrings(expected_arg, actual_arg);
}

test "supported target names map to link policies" {
    try std.testing.expectEqual(LinkTarget.host_linux, try LinkTarget.parse("host-linux"));
    try std.testing.expectEqual(LinkTarget.freestanding_linux_aarch64, try LinkTarget.parse("freestanding-linux-aarch64"));
    try std.testing.expectEqual(LinkTarget.bare_metal_x86_64, try LinkTarget.parse("bare-metal-x86_64"));
    try std.testing.expectError(error.UnsupportedTarget, LinkTarget.parse("host-macos"));
}

test "freestanding AArch64 arguments use the cross compiler policy" {
    var args: [10][]const u8 = undefined;
    try populateBaseArgs(&args, .{
        .target = .freestanding_linux_aarch64,
        .object_path = "program.o",
        .output_path = "program",
        .aarch64_compiler = "/toolchain/aarch64-gcc",
    });
    try expectArgs(&.{
        "/toolchain/aarch64-gcc", "-nostdlib",    "-fno-pie",  "-no-pie", "-z",
        "execstack",              "-fno-builtin", "program.o", "-o",      "program",
    }, &args);
}

test "bare-metal arguments include the linker script policy" {
    var args: [10][]const u8 = undefined;
    try populateBaseArgs(&args, .{
        .target = .bare_metal_x86_64,
        .object_path = "kernel.o",
        .output_path = "kernel.elf",
        .linker_script_path = "kernel.ld",
        .bare_metal_linker = "/toolchain/ld.lld",
    });
    try expectArgs(&.{
        "/toolchain/ld.lld",    "-m", "elf_x86_64", "-T",       "kernel.ld", "-z",
        "max-page-size=0x1000", "-o", "kernel.elf", "kernel.o",
    }, &args);
}

test "hosted Linux entry retry preserves target compiler and native arguments" {
    var initial_args = [_][]const u8{
        "aarch64-linux-gnu-gcc", "-no-pie", "program.o", "-o", "program", "-lm",
    };
    const retry_args = try prepareHostedLinuxEntryRetry(std.testing.allocator, &initial_args);
    defer std.testing.allocator.free(retry_args);
    try expectArgs(&.{
        "aarch64-linux-gnu-gcc", "-no-pie", "-nostartfiles", "program.o", "-o", "program", "-lm",
    }, retry_args);
}

test "Windows entry retry selects the Zorb start symbol" {
    var initial_args: [10][]const u8 = undefined;
    try populateWindowsBaseArgs(&initial_args, "program.obj", "/Fe:program.exe");
    initial_args[9] = "/debug";
    const retry_args = try prepareWindowsEntryRetry(std.testing.allocator, &initial_args);
    defer std.testing.allocator.free(retry_args);

    try expectArgs(&.{
        "clang-cl",           "/nologo",       "program.obj",  "/Fe:program.exe", "/link",
        "/subsystem:console", "/entry:_start", "kernel32.lib", "ucrt.lib",        "ws2_32.lib",
        "/debug",
    }, retry_args);
}
