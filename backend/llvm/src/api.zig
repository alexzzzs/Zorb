const std = @import("std");
const builtin = @import("builtin");
const ir = @import("backend_ir.zig");
const Backend = @import("backend.zig").Backend;

// This library runs inside a compiler linked by the host toolchain. Avoid the
// default stack-tracing panic path, which depends on private Windows loader
// notification imports that are not consistently exposed by ntdll.lib.
pub const panic = std.debug.simple_panic;

const max_ir_bytes = 64 * 1024 * 1024;
const max_linker_output_bytes = 8 * 1024 * 1024;
const linker_timeout: std.Io.Timeout = .{ .duration = .{
    .clock = .awake,
    .raw = .fromSeconds(300),
} };

pub fn emitFile(allocator: std.mem.Allocator, io: std.Io, path: []const u8) !void {
    const input = try std.Io.Dir.cwd().readFileAlloc(io, path, allocator, .limited(max_ir_bytes));
    defer allocator.free(input);

    const parsed = try std.json.parseFromSlice(ir.Module, allocator, input, .{
        .ignore_unknown_fields = false,
    });
    defer parsed.deinit();

    var diagnostic_buffer: [4096]u8 = undefined;
    var diagnostics: std.Io.Writer = .fixed(&diagnostic_buffer);
    parsed.value.validate(&diagnostics) catch |err| {
        try std.Io.File.stderr().writeStreamingAll(io, diagnostics.buffered());
        return err;
    };

    var backend = try Backend.init(allocator, &parsed.value);
    defer backend.deinit();
    try backend.emit();
}

fn reportError(io: std.Io, prefix: []const u8, err: anyerror) void {
    var buffer: [1024]u8 = undefined;
    var writer: std.Io.Writer = .fixed(&buffer);
    writer.print("{s}: {s}\n", .{ prefix, @errorName(err) }) catch return;
    std.Io.File.stderr().writeStreamingAll(io, writer.buffered()) catch {};
}

fn processEnviron() std.process.Environ {
    if (builtin.os.tag == .windows) return .{ .block = .global };
    var count: usize = 0;
    while (std.c.environ[count] != null) : (count += 1) {}
    return .{ .block = .{ .slice = std.c.environ[0..count :null] } };
}

export fn zorb_llvm_emit_file(path: [*:0]const u8) callconv(.c) c_int {
    const io = std.Io.Threaded.global_single_threaded.io();
    emitFile(std.heap.c_allocator, io, std.mem.span(path)) catch |err| {
        reportError(io, "LLVM backend emission failed", err);
        return 1;
    };
    return 0;
}

export fn zorb_llvm_link_object(
    object_path: [*:0]const u8,
    output_path: [*:0]const u8,
    run_after_link: c_int,
    native_args: [*c]const [*c]const u8,
    native_arg_count: i64,
) callconv(.c) c_int {
    var threaded = std.Io.Threaded.init(std.heap.c_allocator, .{
        .async_limit = .nothing,
        .concurrent_limit = .nothing,
        .environ = processEnviron(),
    });
    defer threaded.deinit();
    const io = threaded.io();
    const object = std.mem.span(object_path);
    const output = std.mem.span(output_path);
    if (object.len == 0 or output.len == 0 or native_arg_count < 0 or
        native_arg_count > 4096 or (native_arg_count > 0 and native_args == null))
    {
        reportError(io, "invalid target linker arguments", error.InvalidArgument);
        return 1;
    }
    const base_arg_count: usize = if (builtin.os.tag == .linux) 5 else 4;
    const extra_arg_count: usize = @intCast(native_arg_count);
    const link_args = std.heap.c_allocator.alloc(
        []const u8,
        base_arg_count + extra_arg_count,
    ) catch |err| {
        reportError(io, "unable to prepare target linker arguments", err);
        return 1;
    };
    defer std.heap.c_allocator.free(link_args);
    var windows_output_arg: ?[]u8 = null;
    defer if (windows_output_arg) |argument| std.heap.c_allocator.free(argument);
    if (builtin.os.tag == .windows) {
        windows_output_arg = std.fmt.allocPrint(
            std.heap.c_allocator,
            "/Fe:{s}",
            .{output},
        ) catch |err| {
            reportError(io, "unable to prepare target output argument", err);
            return 1;
        };
        link_args[0] = "clang-cl";
        link_args[1] = "/nologo";
        link_args[2] = object;
        link_args[3] = windows_output_arg.?;
    } else if (builtin.os.tag == .linux) {
        link_args[0] = "cc";
        // Zorb's current hosted code model uses absolute relocations for
        // globals, so explicitly request a non-PIE executable.
        link_args[1] = "-no-pie";
        link_args[2] = object;
        link_args[3] = "-o";
        link_args[4] = output;
    } else {
        link_args[0] = "cc";
        link_args[1] = object;
        link_args[2] = "-o";
        link_args[3] = output;
    }
    for (0..extra_arg_count) |index| {
        const argument = native_args[index] orelse {
            reportError(io, "invalid native linker argument", error.InvalidArgument);
            return 1;
        };
        link_args[base_arg_count + index] = std.mem.span(argument);
    }
    const link_result = std.process.run(std.heap.c_allocator, io, .{
        .argv = link_args,
        .stdout_limit = .limited(max_linker_output_bytes),
        .stderr_limit = .limited(max_linker_output_bytes),
        .timeout = linker_timeout,
    }) catch |err| {
        reportError(io, "target linker failed", err);
        return 1;
    };
    defer std.heap.c_allocator.free(link_result.stdout);
    defer std.heap.c_allocator.free(link_result.stderr);
    var linked = switch (link_result.term) {
        .exited => |code| code == 0,
        else => false,
    };
    if (!linked and builtin.os.tag == .linux) {
        // A source-level `_start` intentionally replaces the host CRT entry
        // point. Retry without startup files; ordinary `main` programs keep
        // the successful first invocation and its normal CRT initialization.
        const retry_args = std.heap.c_allocator.alloc(
            []const u8,
            base_arg_count + extra_arg_count + 1,
        ) catch |err| {
            reportError(io, "unable to prepare target linker retry", err);
            return 1;
        };
        defer std.heap.c_allocator.free(retry_args);
        retry_args[0] = "cc";
        retry_args[1] = "-no-pie";
        retry_args[2] = "-nostartfiles";
        retry_args[3] = object;
        retry_args[4] = "-o";
        retry_args[5] = output;
        for (0..extra_arg_count) |index| {
            retry_args[base_arg_count + 1 + index] =
                link_args[base_arg_count + index];
        }
        const retry_result = std.process.run(std.heap.c_allocator, io, .{
            .argv = retry_args,
            .stdout_limit = .limited(max_linker_output_bytes),
            .stderr_limit = .limited(max_linker_output_bytes),
            .timeout = linker_timeout,
        }) catch |err| {
            std.Io.File.stdout().writeStreamingAll(io, link_result.stdout) catch {};
            std.Io.File.stderr().writeStreamingAll(io, link_result.stderr) catch {};
            reportError(io, "target linker retry failed", err);
            return 1;
        };
        defer std.heap.c_allocator.free(retry_result.stdout);
        defer std.heap.c_allocator.free(retry_result.stderr);
        linked = switch (retry_result.term) {
            .exited => |code| code == 0,
            else => false,
        };
        if (linked) {
            std.Io.File.stdout().writeStreamingAll(io, retry_result.stdout) catch {};
            std.Io.File.stderr().writeStreamingAll(io, retry_result.stderr) catch {};
        } else {
            std.Io.File.stdout().writeStreamingAll(io, link_result.stdout) catch {};
            std.Io.File.stderr().writeStreamingAll(io, link_result.stderr) catch {};
            std.Io.File.stdout().writeStreamingAll(io, retry_result.stdout) catch {};
            std.Io.File.stderr().writeStreamingAll(io, retry_result.stderr) catch {};
            return 1;
        }
    } else {
        std.Io.File.stdout().writeStreamingAll(io, link_result.stdout) catch {};
        std.Io.File.stderr().writeStreamingAll(io, link_result.stderr) catch {};
        if (!linked) return 1;
    }
    if (run_after_link == 0) return 0;
    const executable_result = if (output[0] == '/' or builtin.os.tag == .windows)
        std.heap.c_allocator.dupe(u8, output)
    else
        std.fmt.allocPrint(std.heap.c_allocator, "./{s}", .{output});
    const executable = executable_result catch |err| {
        reportError(io, "unable to prepare compiled program", err);
        return 1;
    };
    defer std.heap.c_allocator.free(executable);
    var child = std.process.spawn(io, .{ .argv = &.{executable} }) catch |err| {
        reportError(io, "unable to launch compiled program", err);
        return 1;
    };
    const run_term = child.wait(io) catch |err| {
        reportError(io, "unable to wait for compiled program", err);
        return 1;
    };
    return switch (run_term) {
        .exited => |code| if (code == 0) 0 else 1,
        else => 1,
    };
}
