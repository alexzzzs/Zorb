const std = @import("std");
const builtin = @import("builtin");
const ir = @import("backend_ir.zig");
const Backend = @import("backend.zig").Backend;
const link_args = @import("link_args.zig");

pub const panic = std.debug.simple_panic;

const max_ir_bytes = 64 * 1024 * 1024;
const max_linker_script_bytes = 1024 * 1024;
const max_linker_output_bytes = 8 * 1024 * 1024;
const max_native_arg_count = 4096;
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

fn configuredValue(environ: std.process.Environ.Map, key: []const u8, fallback: []const u8) []const u8 {
    return environ.get(key) orelse fallback;
}

fn writeLinkerScript(io: std.Io, path: []const u8, contents: []const u8) !void {
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = contents });
}

fn createTemporaryLinkerScript(
    allocator: std.mem.Allocator,
    io: std.Io,
    contents: []const u8,
) ![]u8 {
    var random_bytes: [8]u8 = undefined;
    try std.Io.randomSecure(io, &random_bytes);
    const random_id: u64 = @bitCast(random_bytes);
    const path = try std.fmt.allocPrint(allocator, ".zorb-link-{x}.ld", .{random_id});
    errdefer allocator.free(path);

    var atomic_file = try std.Io.Dir.cwd().createFileAtomic(io, path, .{ .replace = false });
    defer atomic_file.deinit(io);
    try atomic_file.file.writeStreamingAll(io, contents);
    try atomic_file.link(io);
    return path;
}

fn emitSelectedLinkerScript(
    allocator: std.mem.Allocator,
    io: std.Io,
    selected_path: []const u8,
    selected_is_default: bool,
    emit_path: []const u8,
) !void {
    if (emit_path.len == 0) return;
    if (selected_is_default) {
        return writeLinkerScript(io, emit_path, link_args.bare_metal_default_linker_script);
    }
    const contents = try std.Io.Dir.cwd().readFileAlloc(
        io,
        selected_path,
        allocator,
        .limited(max_linker_script_bytes),
    );
    defer allocator.free(contents);
    try writeLinkerScript(io, emit_path, contents);
}

fn runProcess(io: std.Io, argv: []const []const u8) !std.process.RunResult {
    return std.process.run(std.heap.c_allocator, io, .{
        .argv = argv,
        .stdout_limit = .limited(max_linker_output_bytes),
        .stderr_limit = .limited(max_linker_output_bytes),
        .timeout = linker_timeout,
    });
}

fn writeProcessOutput(io: std.Io, result: *const std.process.RunResult) void {
    std.Io.File.stdout().writeStreamingAll(io, result.stdout) catch {};
    std.Io.File.stderr().writeStreamingAll(io, result.stderr) catch {};
}

fn processSucceeded(result: *const std.process.RunResult) bool {
    return switch (result.term) {
        .exited => |code| code == 0,
        else => false,
    };
}

fn runLinkedProgram(
    allocator: std.mem.Allocator,
    io: std.Io,
    environ: std.process.Environ.Map,
    target: link_args.LinkTarget,
    output: []const u8,
) !c_int {
    const executable = if (output[0] == '/' or builtin.os.tag == .windows)
        try allocator.dupe(u8, output)
    else
        try std.fmt.allocPrint(allocator, "./{s}", .{output});
    defer allocator.free(executable);

    var run_argv: [4][]const u8 = undefined;
    var run_arg_count: usize = 1;
    run_argv[0] = executable;
    if (target.isAarch64Linux() and builtin.cpu.arch != .aarch64) {
        run_argv[0] = configuredValue(environ, link_args.aarch64_qemu_env, "qemu-aarch64");
        run_argv[1] = "-L";
        run_argv[2] = configuredValue(
            environ,
            link_args.aarch64_sysroot_env,
            link_args.default_aarch64_sysroot,
        );
        run_argv[3] = executable;
        run_arg_count = 4;
    }

    var child = try std.process.spawn(io, .{ .argv = run_argv[0..run_arg_count] });
    const term = try child.wait(io);
    return switch (term) {
        .exited => |code| @intCast(code),
        else => 1,
    };
}

export fn zorb_llvm_process_id() callconv(.c) i64 {
    if (builtin.os.tag == .windows)
        return @intCast(std.os.windows.GetCurrentProcessId());
    return @intCast(std.c.getpid());
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
    target_name: [*:0]const u8,
    linker_script_path: [*:0]const u8,
    emit_linker_script_path: [*:0]const u8,
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
    const environ = processEnviron();
    const allocator = std.heap.c_allocator;
    var environ_map = environ.createMap(allocator) catch |err| {
        reportError(io, "unable to read process environment", err);
        return 1;
    };
    defer environ_map.deinit();
    const object = std.mem.span(object_path);
    const output = std.mem.span(output_path);
    const requested_target = std.mem.span(target_name);
    const requested_linker_script = std.mem.span(linker_script_path);
    const emit_linker_script = std.mem.span(emit_linker_script_path);
    if (object.len == 0 or output.len == 0 or requested_target.len == 0 or
        native_arg_count < 0 or native_arg_count > max_native_arg_count or
        (native_arg_count > 0 and native_args == null))
    {
        reportError(io, "invalid target linker arguments", error.InvalidArgument);
        return 1;
    }

    const target = link_args.LinkTarget.parse(requested_target) catch |err| {
        reportError(io, "unsupported executable target", err);
        return 1;
    };
    if (!target.supportsHost()) {
        reportError(io, "target is not supported on this host", error.UnsupportedHost);
        return 1;
    }
    if (!target.isBareMetal() and (requested_linker_script.len > 0 or emit_linker_script.len > 0)) {
        reportError(io, "linker scripts require bare-metal-x86_64", error.InvalidArgument);
        return 1;
    }
    if (emit_linker_script.len > 0 and
        (std.mem.eql(u8, emit_linker_script, object) or std.mem.eql(u8, emit_linker_script, output)))
    {
        reportError(io, "linker script output conflicts with a build artifact", error.InvalidArgument);
        return 1;
    }
    if (target.isBareMetal() and run_after_link != 0) {
        reportError(io, "bare-metal targets cannot run", error.InvalidArgument);
        return 1;
    }

    var generated_script: ?[]u8 = null;
    defer if (generated_script) |path| {
        std.Io.Dir.cwd().deleteFile(io, path) catch {};
        allocator.free(path);
    };
    var selected_linker_script: []const u8 = requested_linker_script;
    if (target.isBareMetal() and selected_linker_script.len == 0) {
        generated_script = createTemporaryLinkerScript(
            allocator,
            io,
            link_args.bare_metal_default_linker_script,
        ) catch |err| {
            reportError(io, "unable to prepare bundled linker script", err);
            return 1;
        };
        selected_linker_script = generated_script.?;
    }
    if (target.isBareMetal()) {
        emitSelectedLinkerScript(
            allocator,
            io,
            selected_linker_script,
            requested_linker_script.len == 0,
            emit_linker_script,
        ) catch |err| {
            reportError(io, "unable to emit linker script", err);
            return 1;
        };
    }

    var windows_output_arg: ?[]u8 = null;
    defer if (windows_output_arg) |argument| allocator.free(argument);
    if (target == .host_windows) {
        windows_output_arg = std.fmt.allocPrint(allocator, "/Fe:{s}", .{output}) catch |err| {
            reportError(io, "unable to prepare target output argument", err);
            return 1;
        };
    }

    const base_arg_count = link_args.baseArgCount(target);
    const extra_arg_count: usize = @intCast(native_arg_count);
    const argv = allocator.alloc([]const u8, base_arg_count + extra_arg_count) catch |err| {
        reportError(io, "unable to prepare target linker arguments", err);
        return 1;
    };
    defer allocator.free(argv);
    link_args.populateBaseArgs(argv, .{
        .target = target,
        .object_path = object,
        .output_path = output,
        .windows_output_arg = windows_output_arg orelse "",
        .linker_script_path = selected_linker_script,
        .aarch64_compiler = configuredValue(
            environ_map,
            link_args.aarch64_compiler_env,
            "aarch64-linux-gnu-gcc",
        ),
        .bare_metal_linker = configuredValue(environ_map, link_args.bare_metal_linker_env, "ld.lld"),
    }) catch |err| {
        reportError(io, "unable to prepare target linker arguments", err);
        return 1;
    };
    for (0..extra_arg_count) |index| {
        const argument = native_args[index] orelse {
            reportError(io, "invalid native linker argument", error.InvalidArgument);
            return 1;
        };
        argv[base_arg_count + index] = std.mem.span(argument);
    }

    const link_result = runProcess(io, argv) catch |err| {
        reportError(io, "target linker failed", err);
        return 1;
    };
    defer allocator.free(link_result.stdout);
    defer allocator.free(link_result.stderr);
    if (!processSucceeded(&link_result) and
        (target == .host_windows or target == .host_linux or target == .host_linux_aarch64))
    {
        const retry_argv = if (target == .host_windows)
            link_args.prepareWindowsEntryRetry(allocator, argv)
        else
            link_args.prepareHostedLinuxEntryRetry(allocator, argv);
        const prepared_retry_argv = retry_argv catch |err| {
            reportError(io, "unable to prepare target linker retry", err);
            return 1;
        };
        defer allocator.free(prepared_retry_argv);
        const retry_result = runProcess(io, prepared_retry_argv) catch |err| {
            writeProcessOutput(io, &link_result);
            reportError(io, "target linker retry failed", err);
            return 1;
        };
        defer allocator.free(retry_result.stdout);
        defer allocator.free(retry_result.stderr);
        if (!processSucceeded(&retry_result)) {
            writeProcessOutput(io, &link_result);
            writeProcessOutput(io, &retry_result);
            return 1;
        }
        writeProcessOutput(io, &retry_result);
    } else {
        writeProcessOutput(io, &link_result);
        if (!processSucceeded(&link_result)) return 1;
    }

    if (run_after_link == 0) return 0;
    return runLinkedProgram(allocator, io, environ_map, target, output) catch |err| {
        reportError(io, "unable to run compiled program", err);
        return 1;
    };
}
