const std = @import("std");
const llvm = @import("llvm");
const ir = @import("../backend_ir.zig");
const llvm_support = @import("../llvm_support.zig");

pub fn verify(self: anytype) !void {
    var message: [*c]u8 = null;
    if (llvm.LLVMVerifyModule(self.module, llvm.LLVMReturnStatusAction, &message) != 0) {
        defer llvm_support.disposeMessage(message);
        if (message != null)
            std.debug.print("LLVM verification failed:\n{s}\n", .{std.mem.span(message)});
        return llvm_support.LlvmError.LlvmVerificationFailed;
    }
}

pub fn optimize(self: anytype) !void {
    const options = llvm.LLVMCreatePassBuilderOptions() orelse return error.OutOfMemory;
    defer llvm.LLVMDisposePassBuilderOptions(options);
    const llvm_error = llvm.LLVMRunPasses(
        self.module,
        self.module_ir.target.optimize.passPipeline(),
        self.target_machine,
        options,
    );
    if (try llvm_support.consumeError(self.allocator, llvm_error)) |message| {
        defer self.allocator.free(message);
        return llvm_support.LlvmError.LlvmOptimizationFailed;
    }
}

pub fn writeOutput(self: anytype) !void {
    const output_path = try self.allocator.dupeZ(u8, self.module_ir.output_path);
    defer self.allocator.free(output_path);
    var message: [*c]u8 = null;
    const failed = switch (self.module_ir.output_kind) {
        .llvm_ir => llvm.LLVMPrintModuleToFile(self.module, output_path, &message),
        .bitcode => @intFromBool(llvm.LLVMWriteBitcodeToFile(self.module, output_path) != 0),
        .object => llvm.LLVMTargetMachineEmitToFile(
            self.target_machine,
            self.module,
            output_path,
            llvm.LLVMObjectFile,
            &message,
        ),
        .assembly => llvm.LLVMTargetMachineEmitToFile(
            self.target_machine,
            self.module,
            output_path,
            llvm.LLVMAssemblyFile,
            &message,
        ),
    };
    if (failed != 0) {
        defer llvm_support.disposeMessage(message);
        return llvm_support.LlvmError.LlvmEmissionFailed;
    }
}
