const std = @import("std");
const llvm = @import("llvm");

pub const FunctionRecord = struct {
    function: llvm.LLVMValueRef,
    function_type: llvm.LLVMTypeRef,
};

pub const ValueMap = std.AutoHashMapUnmanaged(u32, llvm.LLVMValueRef);
pub const BlockMap = std.AutoHashMapUnmanaged(u32, llvm.LLVMBasicBlockRef);
