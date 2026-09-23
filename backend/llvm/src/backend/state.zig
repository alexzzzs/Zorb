const std = @import("std");
const llvm = @import("llvm");
const ir = @import("../backend_ir.zig");
const llvm_support = @import("../llvm_support.zig");
const shared = @import("shared.zig");
const backend_types = @import("types.zig");
const backend_constants = @import("constants.zig");
const backend_functions = @import("functions.zig");
const backend_output = @import("output.zig");

const FunctionRecord = shared.FunctionRecord;

pub const Backend = struct {
    allocator: std.mem.Allocator,
    module_ir: *const ir.Module,
    context: llvm.LLVMContextRef,
    module: llvm.LLVMModuleRef,
    target_machine: llvm.LLVMTargetMachineRef,
    target_data: llvm.LLVMTargetDataRef,
    types: std.AutoHashMapUnmanaged(u32, llvm.LLVMTypeRef) = .empty,
    globals: std.AutoHashMapUnmanaged(u32, llvm.LLVMValueRef) = .empty,
    functions: std.AutoHashMapUnmanaged(u32, FunctionRecord) = .empty,
    strings: std.StringHashMapUnmanaged(llvm.LLVMValueRef) = .empty,

    pub fn init(allocator: std.mem.Allocator, module_ir: *const ir.Module) !Backend {
        initializeSupportedTargets();

        const triple = try allocator.dupeZ(u8, module_ir.target.triple);
        defer allocator.free(triple);
        var target: llvm.LLVMTargetRef = null;
        var target_error: [*c]u8 = null;
        if (llvm.LLVMGetTargetFromTriple(triple, &target, &target_error) != 0) {
            defer llvm_support.disposeMessage(target_error);
            return llvm_support.LlvmError.LlvmTargetLookupFailed;
        }

        const cpu = try allocator.dupeZ(u8, module_ir.target.cpu);
        defer allocator.free(cpu);
        const features = try allocator.dupeZ(u8, module_ir.target.features);
        defer allocator.free(features);
        const target_machine = llvm.LLVMCreateTargetMachine(
            target,
            triple,
            cpu,
            features,
            codeGenOptLevel(module_ir.target.optimize),
            llvm.LLVMRelocDefault,
            llvm.LLVMCodeModelDefault,
        ) orelse return llvm_support.LlvmError.LlvmTargetMachineCreationFailed;
        errdefer llvm.LLVMDisposeTargetMachine(target_machine);

        const context = llvm.LLVMContextCreate() orelse return error.OutOfMemory;
        errdefer llvm.LLVMContextDispose(context);
        const module_name = try allocator.dupeZ(u8, module_ir.module_name);
        defer allocator.free(module_name);
        const module = llvm.LLVMModuleCreateWithNameInContext(module_name, context) orelse return error.OutOfMemory;
        errdefer llvm.LLVMDisposeModule(module);

        llvm.LLVMSetTarget(module, triple);
        const target_data = llvm.LLVMCreateTargetDataLayout(target_machine) orelse return error.OutOfMemory;
        errdefer llvm.LLVMDisposeTargetData(target_data);
        const data_layout_message = llvm.LLVMCopyStringRepOfTargetData(target_data);
        defer llvm.LLVMDisposeMessage(data_layout_message);
        llvm.LLVMSetDataLayout(module, data_layout_message);

        return .{
            .allocator = allocator,
            .module_ir = module_ir,
            .context = context,
            .module = module,
            .target_machine = target_machine,
            .target_data = target_data,
        };
    }

    pub fn deinit(self: *Backend) void {
        var string_iterator = self.strings.iterator();
        while (string_iterator.next()) |entry|
            self.allocator.free(entry.key_ptr.*);
        self.strings.deinit(self.allocator);
        self.functions.deinit(self.allocator);
        self.globals.deinit(self.allocator);
        self.types.deinit(self.allocator);
        llvm.LLVMDisposeTargetData(self.target_data);
        llvm.LLVMDisposeModule(self.module);
        llvm.LLVMContextDispose(self.context);
        llvm.LLVMDisposeTargetMachine(self.target_machine);
        self.* = undefined;
    }

    pub fn emit(self: *Backend) !void {
        try backend_types.initializeTypes(self);
        try backend_functions.declareFunctions(self);
        try backend_constants.declareGlobals(self);
        try backend_functions.emitFunctionBodies(self);
        try backend_output.verify(self);
        try backend_output.optimize(self);
        try backend_output.verify(self);
        try backend_output.writeOutput(self);
    }

    fn initializeSupportedTargets() void {
        llvm.LLVMInitializeAArch64TargetInfo();
        llvm.LLVMInitializeAArch64Target();
        llvm.LLVMInitializeAArch64TargetMC();
        llvm.LLVMInitializeAArch64AsmPrinter();
        llvm.LLVMInitializeAArch64AsmParser();
        llvm.LLVMInitializeAArch64Disassembler();

        llvm.LLVMInitializeX86TargetInfo();
        llvm.LLVMInitializeX86Target();
        llvm.LLVMInitializeX86TargetMC();
        llvm.LLVMInitializeX86AsmPrinter();
        llvm.LLVMInitializeX86AsmParser();
        llvm.LLVMInitializeX86Disassembler();
    }

fn codeGenOptLevel(level: ir.OptimizeLevel) llvm.LLVMCodeGenOptLevel {
    return switch (level) {
        .O0 => llvm.LLVMCodeGenLevelNone,
        .O1 => llvm.LLVMCodeGenLevelLess,
        .O2 => llvm.LLVMCodeGenLevelDefault,
        .O3 => llvm.LLVMCodeGenLevelAggressive,
    };
}
};
