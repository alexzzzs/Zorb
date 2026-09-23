const std = @import("std");
const llvm = @import("llvm");
const ir = @import("../backend_ir.zig");
const shared = @import("shared.zig");
const backend_types = @import("types.zig");
const backend_instructions = @import("instructions.zig");
const ValueMap = shared.ValueMap;
const BlockMap = shared.BlockMap;

pub fn declareFunctions(self: anytype) !void {
    for (self.module_ir.functions) |function_ir| {
        var parameter_types = try self.allocator.alloc(llvm.LLVMTypeRef, function_ir.parameters.len);
        defer self.allocator.free(parameter_types);
        for (function_ir.parameters, 0..) |parameter, index| {
            parameter_types[index] = try backend_types.typeById(self, parameter.type);
        }
        const function_type = llvm.LLVMFunctionType(
            try backend_types.typeById(self, function_ir.return_type),
            if (parameter_types.len == 0) null else parameter_types.ptr,
            @intCast(parameter_types.len),
            0,
        );
        const name = try self.allocator.dupeZ(u8, function_ir.name);
        defer self.allocator.free(name);
        const function = llvm.LLVMAddFunction(self.module, name, function_type);
        llvm.LLVMSetLinkage(function, switch (function_ir.linkage) {
            .external => llvm.LLVMExternalLinkage,
            .internal => llvm.LLVMInternalLinkage,
        });
        // Inline assembly may implement an ABI boundary such as a stack
        // switch. Keep its containing frame intact across optimization.
        if (function_ir.containsInlineAsm()) {
            const attribute_name = "noinline";
            const attribute_kind = llvm.LLVMGetEnumAttributeKindForName(
                attribute_name,
                attribute_name.len,
            );
            if (attribute_kind == 0) return error.InvalidBackendIr;
            const attribute = llvm.LLVMCreateEnumAttribute(self.context, attribute_kind, 0) orelse
                return error.OutOfMemory;
            llvm.LLVMAddAttributeAtIndex(
                function,
                std.math.maxInt(llvm.LLVMAttributeIndex),
                attribute,
            );
        }
        try self.functions.put(self.allocator, function_ir.id, .{
            .function = function,
            .function_type = function_type,
        });
    }
}

pub fn emitFunctionBodies(self: anytype) !void {
    for (self.module_ir.functions) |function_ir| {
        if (function_ir.blocks.len == 0) continue;
        try emitFunctionBody(self, function_ir);
    }
}

pub fn emitFunctionBody(self: anytype, function_ir: ir.Function) !void {
    const record = self.functions.get(function_ir.id) orelse return error.InvalidBackendIr;
    const builder = llvm.LLVMCreateBuilderInContext(self.context) orelse return error.OutOfMemory;
    defer llvm.LLVMDisposeBuilder(builder);

    var values: ValueMap = .empty;
    defer values.deinit(self.allocator);
    var blocks: BlockMap = .empty;
    defer blocks.deinit(self.allocator);

    try populateParameterValues(self, function_ir, record.function, &values);
    try createFunctionBlocks(self, function_ir, record.function, &blocks);
    try emitFunctionAllocas(self, builder, function_ir, &values, &blocks);
    try emitFunctionInstructions(self, builder, function_ir, &values, &blocks);
    try addPhiIncomingEdges(self, function_ir, &values, &blocks);
}

pub fn populateParameterValues(
    self: anytype,
    function_ir: ir.Function,
    function_ref: llvm.LLVMValueRef,
    values: *ValueMap,
) !void {
    for (function_ir.parameters, 0..) |parameter, index| {
        const value = llvm.LLVMGetParam(function_ref, @intCast(index));
        const name = try self.allocator.dupeZ(u8, parameter.name);
        defer self.allocator.free(name);
        llvm.LLVMSetValueName2(value, name, parameter.name.len);
        try values.put(self.allocator, parameter.id, value);
    }
}

pub fn createFunctionBlocks(
    self: anytype,
    function_ir: ir.Function,
    function_ref: llvm.LLVMValueRef,
    blocks: *BlockMap,
) !void {
    for (function_ir.blocks) |block_ir| {
        const block_name = try self.allocator.dupeZ(u8, block_ir.name);
        defer self.allocator.free(block_name);
        const block = llvm.LLVMAppendBasicBlockInContext(self.context, function_ref, block_name);
        try blocks.put(self.allocator, block_ir.id, block);
    }
}

pub fn emitFunctionInstructions(
    self: anytype,
    builder: llvm.LLVMBuilderRef,
    function_ir: ir.Function,
    values: *ValueMap,
    blocks: *BlockMap,
) !void {
    for (function_ir.blocks) |block_ir| {
        const block = blocks.get(block_ir.id) orelse return error.InvalidBackendIr;
        llvm.LLVMPositionBuilderAtEnd(builder, block);

        for (block_ir.instructions) |instruction| {
            if (instruction.op == .alloca) continue;
            const value = try backend_instructions.emitInstruction(self, builder, values, instruction);
            try values.put(self.allocator, instruction.id, value);
        }

        try backend_instructions.emitTerminator(self, builder, block_ir.terminator, values, blocks);
    }
}

// stack slot into the entry block so loop-local declarations reuse their
// slot instead of growing the native stack on every iteration.
fn emitFunctionAllocas(
    self: anytype,
    builder: llvm.LLVMBuilderRef,
    function_ir: ir.Function,
    values: *ValueMap,
    blocks: *BlockMap,
) !void {
    const entry = blocks.get(function_ir.blocks[0].id) orelse return error.InvalidBackendIr;
    llvm.LLVMPositionBuilderAtEnd(builder, entry);
    for (function_ir.blocks) |block_ir| {
        for (block_ir.instructions) |instruction| {
            if (instruction.op != .alloca) continue;
            const value = llvm.LLVMBuildAlloca(
                builder,
                try backend_types.typeById(self, instruction.type),
                "",
            );
            try values.put(self.allocator, instruction.id, value);
        }
    }
}

pub fn addPhiIncomingEdges(
    self: anytype,
    function_ir: ir.Function,
    values: *ValueMap,
    blocks: *BlockMap,
) !void {
    for (function_ir.blocks) |block_ir| {
        for (block_ir.instructions) |instruction| {
            if (instruction.op != .phi) continue;
            try addPhiIncomingEdgesForInstruction(self, instruction, values, blocks);
        }
    }
}

pub fn addPhiIncomingEdgesForInstruction(
    self: anytype,
    instruction: ir.Instruction,
    values: *ValueMap,
    blocks: *BlockMap,
) !void {
    if (instruction.incoming_values.len != instruction.incoming_blocks.len or
        instruction.incoming_values.len == 0)
    {
        return error.InvalidBackendIr;
    }

    const phi = values.get(instruction.id) orelse return error.InvalidBackendIr;
    var incoming_values = try self.allocator.alloc(
        llvm.LLVMValueRef,
        instruction.incoming_values.len,
    );
    defer self.allocator.free(incoming_values);
    var incoming_blocks = try self.allocator.alloc(
        llvm.LLVMBasicBlockRef,
        instruction.incoming_blocks.len,
    );
    defer self.allocator.free(incoming_blocks);

    for (instruction.incoming_values, instruction.incoming_blocks, 0..) |value_id, block_id, index| {
        incoming_values[index] = values.get(value_id) orelse return error.InvalidBackendIr;
        incoming_blocks[index] = blocks.get(block_id) orelse return error.InvalidBackendIr;
    }

    llvm.LLVMAddIncoming(
        phi,
        incoming_values.ptr,
        incoming_blocks.ptr,
        @intCast(incoming_values.len),
    );
}
