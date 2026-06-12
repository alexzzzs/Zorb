const std = @import("std");
const llvm = @import("llvm");

pub const LlvmError = error{
    LlvmInitializationFailed,
    LlvmTargetLookupFailed,
    LlvmTargetMachineCreationFailed,
    LlvmVerificationFailed,
    LlvmOptimizationFailed,
    LlvmEmissionFailed,
};

pub fn disposeMessage(message: [*c]u8) void {
    if (message != null) llvm.LLVMDisposeMessage(message);
}

pub fn copyMessage(allocator: std.mem.Allocator, message: [*c]u8) ![]u8 {
    if (message == null) return allocator.dupe(u8, "unknown LLVM error");
    return allocator.dupe(u8, std.mem.span(message));
}

pub fn consumeError(allocator: std.mem.Allocator, llvm_error: llvm.LLVMErrorRef) !?[]u8 {
    if (llvm_error == null) return null;
    const message = llvm.LLVMGetErrorMessage(llvm_error);
    if (message == null)
        return @as(?[]u8, try allocator.dupe(u8, "unknown LLVM error"));
    defer llvm.LLVMDisposeErrorMessage(message);
    return try allocator.dupe(u8, std.mem.span(message));
}
