const std = @import("std");

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});
    const static_llvm = b.option(
        bool,
        "static-llvm",
        "Statically link LLVM component libraries",
    ) orelse false;
    const cxx_runtime = b.option(
        []const u8,
        "cxx-runtime",
        "Path to the C++ runtime used to build LLVM (required with -Dstatic-llvm=true)",
    );
    const llvm_prefix = b.option(
        []const u8,
        "llvm-prefix",
        "LLVM installation prefix",
    ) orelse "/usr/lib/llvm-21";
    const llvm_include_dir = b.option(
        []const u8,
        "llvm-include-dir",
        "LLVM include directory containing llvm-c headers",
    ) orelse "include";
    const llvm_lib_dir = b.option(
        []const u8,
        "llvm-lib-dir",
        "LLVM library directory containing LLVM-C and component libraries",
    ) orelse b.pathJoin(&.{ llvm_prefix, "lib" });
    const llvm_library = b.option(
        []const u8,
        "llvm-library",
        "LLVM C API library name for shared linking",
    ) orelse if (target.result.os.tag == .windows) "LLVM-C" else "LLVM-21";

    const llvm_header = b.addTranslateC(.{
        .root_source_file = b.path("include/zorb_llvm.h"),
        .target = target,
        .optimize = optimize,
        .link_libc = true,
    });
    llvm_header.addSystemIncludePath(.{ .cwd_relative = llvm_include_dir });

    const root_module = b.createModule(.{
        .root_source_file = b.path("src/main.zig"),
        .target = target,
        .optimize = optimize,
        .imports = &.{
            .{ .name = "llvm", .module = llvm_header.createModule() },
        },
    });
    root_module.addLibraryPath(.{ .cwd_relative = llvm_lib_dir });
    if (static_llvm) {
        linkStaticLlvm(
            root_module,
            cxx_runtime orelse @panic("-Dcxx-runtime=<path> is required with -Dstatic-llvm=true"),
        );
    } else {
        root_module.linkSystemLibrary(llvm_library, .{
            .use_pkg_config = .no,
            .preferred_link_mode = .dynamic,
        });
        if (target.result.os.tag != .windows)
            root_module.addRPath(.{ .cwd_relative = llvm_lib_dir });
    }

    const backend = b.addExecutable(.{
        .name = "zorb-llvm-backend",
        .root_module = root_module,
    });
    b.installArtifact(backend);

    const api_module = b.createModule(.{
        .root_source_file = b.path("src/api.zig"),
        .target = target,
        .optimize = optimize,
        .link_libc = true,
        .imports = &.{
            .{ .name = "llvm", .module = llvm_header.createModule() },
        },
    });
    api_module.addLibraryPath(.{ .cwd_relative = llvm_lib_dir });
    if (static_llvm) {
        linkStaticLlvm(
            api_module,
            cxx_runtime orelse @panic("-Dcxx-runtime=<path> is required with -Dstatic-llvm=true"),
        );
    } else {
        api_module.linkSystemLibrary(llvm_library, .{
            .use_pkg_config = .no,
            .preferred_link_mode = .dynamic,
        });
        if (target.result.os.tag != .windows)
            api_module.addRPath(.{ .cwd_relative = llvm_lib_dir });
    }
    const backend_api = b.addLibrary(.{
        .name = "zorb-llvm",
        .linkage = .static,
        .root_module = api_module,
    });
    backend_api.bundle_compiler_rt = true;
    b.installArtifact(backend_api);

    const run_backend = b.addRunArtifact(backend);
    if (b.args) |args| run_backend.addArgs(args);
    const run_step = b.step("run-backend", "Run the LLVM backend");
    run_step.dependOn(&run_backend.step);

    const tests = b.addTest(.{
        .root_module = b.createModule(.{
            .root_source_file = b.path("src/backend_ir.zig"),
            .target = target,
            .optimize = optimize,
        }),
    });
    const run_tests = b.addRunArtifact(tests);
    const link_args_tests = b.addTest(.{
        .root_module = b.createModule(.{
            .root_source_file = b.path("src/link_args.zig"),
            .target = target,
            .optimize = optimize,
        }),
    });
    const run_link_args_tests = b.addRunArtifact(link_args_tests);
    const test_step = b.step("test", "Run Zig backend tests");
    test_step.dependOn(&run_tests.step);
    test_step.dependOn(&run_link_args_tests.step);
}

fn linkStaticLlvm(module: *std.Build.Module, cxx_runtime: []const u8) void {
    const llvm_libraries = [_][]const u8{
        "LLVMPasses",
        "LLVMHipStdPar",
        "LLVMCoroutines",
        "LLVMipo",
        "LLVMLinker",
        "LLVMFrontendOpenMP",
        "LLVMFrontendAtomic",
        "LLVMFrontendOffloading",
        "LLVMAArch64Disassembler",
        "LLVMAArch64AsmParser",
        "LLVMAArch64CodeGen",
        "LLVMVectorize",
        "LLVMSandboxIR",
        "LLVMAArch64Desc",
        "LLVMAArch64Utils",
        "LLVMAArch64Info",
        "LLVMX86TargetMCA",
        "LLVMMCA",
        "LLVMX86Disassembler",
        "LLVMX86AsmParser",
        "LLVMX86CodeGen",
        "LLVMX86Desc",
        "LLVMX86Info",
        "LLVMMCDisassembler",
        "LLVMInstrumentation",
        "LLVMIRPrinter",
        "LLVMGlobalISel",
        "LLVMSelectionDAG",
        "LLVMCFGuard",
        "LLVMAsmPrinter",
        "LLVMCodeGen",
        "LLVMTarget",
        "LLVMScalarOpts",
        "LLVMInstCombine",
        "LLVMAggressiveInstCombine",
        "LLVMObjCARCOpts",
        "LLVMTransformUtils",
        "LLVMCodeGenTypes",
        "LLVMCGData",
        "LLVMBitWriter",
        "LLVMAnalysis",
        "LLVMProfileData",
        "LLVMSymbolize",
        "LLVMDebugInfoBTF",
        "LLVMDebugInfoPDB",
        "LLVMDebugInfoMSF",
        "LLVMDebugInfoCodeView",
        "LLVMDebugInfoDWARF",
        "LLVMDebugInfoDWARFLowLevel",
        "LLVMObject",
        "LLVMTextAPI",
        "LLVMMCParser",
        "LLVMIRReader",
        "LLVMAsmParser",
        "LLVMMC",
        "LLVMBitReader",
        "LLVMCore",
        "LLVMRemarks",
        "LLVMBitstreamReader",
        "LLVMBinaryFormat",
        "LLVMTargetParser",
        "LLVMSupport",
        "LLVMDemangle",
    };
    for (llvm_libraries) |library| {
        module.linkSystemLibrary(library, .{
            .use_pkg_config = .no,
            .preferred_link_mode = .static,
            .search_strategy = .no_fallback,
        });
    }
    module.addObjectFile(.{ .cwd_relative = cxx_runtime });
    for ([_][]const u8{ "rt", "dl", "m", "z", "zstd", "xml2" }) |library| {
        module.linkSystemLibrary(library, .{ .use_pkg_config = .no });
    }
}
