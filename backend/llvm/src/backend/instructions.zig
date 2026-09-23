const std = @import("std");
const llvm = @import("llvm");
const ir = @import("../backend_ir.zig");
const backend_types = @import("types.zig");
const shared = @import("shared.zig");

const ValueMap = shared.ValueMap;
const BlockMap = shared.BlockMap;

pub fn emitTerminator(
    self: anytype,
    builder: llvm.LLVMBuilderRef,
    terminator: ir.Terminator,
    values: *ValueMap,
    blocks: *BlockMap,
) !void {
    _ = self;
    switch (terminator.op) {
        .return_void => _ = llvm.LLVMBuildRetVoid(builder),
        .return_value => {
            const value_id = terminator.value orelse return error.InvalidBackendIr;
            const value = values.get(value_id) orelse return error.InvalidBackendIr;
            _ = llvm.LLVMBuildRet(builder, value);
        },
        .branch => {
            const target_id = terminator.target orelse return error.InvalidBackendIr;
            const target = blocks.get(target_id) orelse return error.InvalidBackendIr;
            _ = llvm.LLVMBuildBr(builder, target);
        },
        .conditional_branch => {
            const condition_id = terminator.condition orelse return error.InvalidBackendIr;
            const condition_value = values.get(condition_id) orelse return error.InvalidBackendIr;
            const condition = toCondition(builder, condition_value);
            const true_target_id = terminator.true_target orelse return error.InvalidBackendIr;
            const false_target_id = terminator.false_target orelse return error.InvalidBackendIr;
            const true_target = blocks.get(true_target_id) orelse return error.InvalidBackendIr;
            const false_target = blocks.get(false_target_id) orelse return error.InvalidBackendIr;
            _ = llvm.LLVMBuildCondBr(builder, condition, true_target, false_target);
        },
        .@"unreachable" => _ = llvm.LLVMBuildUnreachable(builder),
    }
}

pub fn emitInstruction(
    self: anytype,
    builder: llvm.LLVMBuilderRef,
    values: *std.AutoHashMapUnmanaged(u32, llvm.LLVMValueRef),
    instruction: ir.Instruction,
) !llvm.LLVMValueRef {
    return switch (instruction.op) {
        .zero_constant,
        .integer_constant,
        .string_constant,
        .size_of,
        => try emitConstantInstruction(self, builder, instruction),
        .alloca,
        .load,
        .store,
        .extract_value,
        .index_address,
        .field_address,
        .global_address,
        .function_address,
        => try emitMemoryInstruction(self, builder, values, instruction),
        .binary,
        .pointer_difference,
        .compare,
        .cast,
        => try emitArithmeticInstruction(self, builder, values, instruction),
        .phi,
        .aggregate,
        => try emitAggregateInstruction(self, builder, values, instruction),
        .inline_asm => try emitInlineAsmInstruction(self, builder, values, instruction),
        .syscall => try emitSyscallInstruction(self, builder, values, instruction),
        .process_exit => try emitProcessExitInstruction(self, builder, values, instruction),
        .trap => try emitTrapInstruction(self, builder),
        .call,
        .indirect_call,
        => try emitCallInstruction(self, builder, values, instruction),
    };
}

pub fn emitConstantInstruction(
    self: anytype,
    builder: llvm.LLVMBuilderRef,
    instruction: ir.Instruction,
) !llvm.LLVMValueRef {
    return switch (instruction.op) {
        .zero_constant => llvm.LLVMConstNull(try backend_types.typeById(self, instruction.type)),
        .integer_constant => blk: {
            const integer = instruction.integer orelse return error.InvalidBackendIr;
            const type_ref = try backend_types.typeById(self, instruction.type);
            break :blk llvm.LLVMConstInt(type_ref, @bitCast(integer), @intFromBool(integer < 0));
        },
        .string_constant => blk: {
            const text = instruction.text orelse return error.InvalidBackendIr;
            if (self.strings.get(text)) |existing|
                break :blk existing;
            const terminated = try self.allocator.dupeZ(u8, text);
            defer self.allocator.free(terminated);
            const global = llvm.LLVMBuildGlobalStringPtr(builder, terminated, "");
            const owned_key = try self.allocator.dupe(u8, text);
            errdefer self.allocator.free(owned_key);
            try self.strings.put(self.allocator, owned_key, global);
            break :blk global;
        },
        .size_of => llvm.LLVMConstInt(
            try backend_types.typeById(self, instruction.type),
            llvm.LLVMABISizeOfType(
                self.target_data,
                try backend_types.typeById(self, instruction.source_type orelse return error.InvalidBackendIr),
            ),
            0,
        ),
        else => unreachable,
    };
}

pub fn emitMemoryInstruction(
    self: anytype,
    builder: llvm.LLVMBuilderRef,
    values: *std.AutoHashMapUnmanaged(u32, llvm.LLVMValueRef),
    instruction: ir.Instruction,
) !llvm.LLVMValueRef {
    return switch (instruction.op) {
        .alloca => llvm.LLVMBuildAlloca(builder, try backend_types.typeById(self, instruction.type), ""),
        .load => blk: {
            const address = try instructionValue(values, instruction.lhs);
            break :blk llvm.LLVMBuildLoad2(builder, try backend_types.typeById(self, instruction.type), address, "");
        },
        .store => blk: {
            const address = try instructionValue(values, instruction.lhs);
            const value = try instructionValue(values, instruction.rhs);
            break :blk llvm.LLVMBuildStore(builder, value, address);
        },
        .extract_value => blk: {
            const aggregate = try instructionValue(values, instruction.lhs);
            break :blk llvm.LLVMBuildExtractValue(
                builder,
                aggregate,
                instruction.field_index orelse return error.InvalidBackendIr,
                "",
            );
        },
        .index_address => blk: {
            const base = try instructionValue(values, instruction.lhs);
            const index = try instructionValue(values, instruction.rhs);
            const source_type_id = instruction.source_type orelse return error.InvalidBackendIr;
            const source_type = try backend_types.typeById(self, source_type_id);
            const source_def = backend_types.findTypeDef(self, source_type_id) orelse return error.InvalidBackendIr;
            if (source_def.kind == .array) {
                const zero = llvm.LLVMConstInt(llvm.LLVMInt32TypeInContext(self.context), 0, 0);
                var indices = [_]llvm.LLVMValueRef{ zero, index };
                break :blk llvm.LLVMBuildGEP2(builder, source_type, base, &indices, indices.len, "");
            }
            var indices = [_]llvm.LLVMValueRef{index};
            break :blk llvm.LLVMBuildGEP2(builder, source_type, base, &indices, indices.len, "");
        },
        .field_address => blk: {
            const base = try instructionValue(values, instruction.lhs);
            const source_type = try backend_types.typeById(self, instruction.source_type orelse return error.InvalidBackendIr);
            break :blk llvm.LLVMBuildStructGEP2(
                builder,
                source_type,
                base,
                instruction.field_index orelse return error.InvalidBackendIr,
                "",
            );
        },
        .global_address => self.globals.get(instruction.global orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr,
        .function_address => (self.functions.get(instruction.callee orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr).function,
        else => unreachable,
    };
}

pub fn emitArithmeticInstruction(
    self: anytype,
    builder: llvm.LLVMBuilderRef,
    values: *std.AutoHashMapUnmanaged(u32, llvm.LLVMValueRef),
    instruction: ir.Instruction,
) !llvm.LLVMValueRef {
    return switch (instruction.op) {
        .binary => blk: {
            const lhs = try instructionValue(values, instruction.lhs);
            const rhs = try instructionValue(values, instruction.rhs);
            const op = instruction.binary_op orelse return error.InvalidBackendIr;
            break :blk switch (op) {
                .add => llvm.LLVMBuildAdd(builder, lhs, rhs, ""),
                .sub => llvm.LLVMBuildSub(builder, lhs, rhs, ""),
                .mul => llvm.LLVMBuildMul(builder, lhs, rhs, ""),
                .signed_div => llvm.LLVMBuildSDiv(builder, lhs, rhs, ""),
                .unsigned_div => llvm.LLVMBuildUDiv(builder, lhs, rhs, ""),
                .signed_rem => llvm.LLVMBuildSRem(builder, lhs, rhs, ""),
                .unsigned_rem => llvm.LLVMBuildURem(builder, lhs, rhs, ""),
                .bit_and => llvm.LLVMBuildAnd(builder, lhs, rhs, ""),
                .bit_or => llvm.LLVMBuildOr(builder, lhs, rhs, ""),
                .bit_xor => llvm.LLVMBuildXor(builder, lhs, rhs, ""),
                .shift_left => llvm.LLVMBuildShl(builder, lhs, rhs, ""),
                .arithmetic_shift_right => llvm.LLVMBuildAShr(builder, lhs, rhs, ""),
                .logical_shift_right => llvm.LLVMBuildLShr(builder, lhs, rhs, ""),
            };
        },
        .pointer_difference => blk: {
            const lhs = try instructionValue(values, instruction.lhs);
            const rhs = try instructionValue(values, instruction.rhs);
            const result_type_def = backend_types.findTypeDef(self, instruction.type) orelse return error.InvalidBackendIr;
            if (result_type_def.kind != .scalar or result_type_def.scalar != .i64) {
                return error.InvalidBackendIr;
            }
            const difference_type = try backend_types.typeById(self, instruction.type);
            const element_type = try backend_types.typeById(self, instruction.source_type orelse return error.InvalidBackendIr);
            const element_size = llvm.LLVMABISizeOfType(self.target_data, element_type);
            if (element_size == 0 or element_size > 0x7fff_ffff_ffff_ffff) {
                return error.InvalidBackendIr;
            }
            const lhs_integer = llvm.LLVMBuildPtrToInt(builder, lhs, difference_type, "");
            const rhs_integer = llvm.LLVMBuildPtrToInt(builder, rhs, difference_type, "");
            const byte_difference = llvm.LLVMBuildSub(builder, lhs_integer, rhs_integer, "");
            const element_size_value = llvm.LLVMConstInt(difference_type, element_size, 0);
            break :blk llvm.LLVMBuildSDiv(builder, byte_difference, element_size_value, "");
        },
        .compare => blk: {
            const lhs = try instructionValue(values, instruction.lhs);
            const rhs = try instructionValue(values, instruction.rhs);
            const predicate: llvm.LLVMIntPredicate = @intCast(switch (instruction.compare_op orelse return error.InvalidBackendIr) {
                .equal => llvm.LLVMIntEQ,
                .not_equal => llvm.LLVMIntNE,
                .signed_less => llvm.LLVMIntSLT,
                .signed_less_equal => llvm.LLVMIntSLE,
                .signed_greater => llvm.LLVMIntSGT,
                .signed_greater_equal => llvm.LLVMIntSGE,
                .unsigned_less => llvm.LLVMIntULT,
                .unsigned_less_equal => llvm.LLVMIntULE,
                .unsigned_greater => llvm.LLVMIntUGT,
                .unsigned_greater_equal => llvm.LLVMIntUGE,
            });
            const condition = llvm.LLVMBuildICmp(builder, predicate, lhs, rhs, "");
            break :blk llvm.LLVMBuildZExt(builder, condition, try backend_types.typeById(self, instruction.type), "");
        },
        .cast => blk: {
            const value = try instructionValue(values, instruction.lhs);
            const target_type = try backend_types.typeById(self, instruction.type);
            break :blk switch (instruction.cast_op orelse return error.InvalidBackendIr) {
                .truncate => llvm.LLVMBuildTrunc(builder, value, target_type, ""),
                .sign_extend => llvm.LLVMBuildSExt(builder, value, target_type, ""),
                .zero_extend => llvm.LLVMBuildZExt(builder, value, target_type, ""),
                .pointer_to_integer => llvm.LLVMBuildPtrToInt(builder, value, target_type, ""),
                .integer_to_pointer => llvm.LLVMBuildIntToPtr(builder, value, target_type, ""),
            };
        },
        else => unreachable,
    };
}

pub fn emitAggregateInstruction(
    self: anytype,
    builder: llvm.LLVMBuilderRef,
    values: *std.AutoHashMapUnmanaged(u32, llvm.LLVMValueRef),
    instruction: ir.Instruction,
) !llvm.LLVMValueRef {
    return switch (instruction.op) {
        .phi => blk: {
            if (instruction.incoming_values.len != instruction.incoming_blocks.len or
                instruction.incoming_values.len == 0)
            {
                return error.InvalidBackendIr;
            }
            break :blk llvm.LLVMBuildPhi(builder, try backend_types.typeById(self, instruction.type), "");
        },
        .aggregate => blk: {
            var aggregate = llvm.LLVMGetUndef(try backend_types.typeById(self, instruction.type));
            for (instruction.arguments, 0..) |value_id, index| {
                const value = values.get(value_id) orelse return error.InvalidBackendIr;
                aggregate = llvm.LLVMBuildInsertValue(
                    builder,
                    aggregate,
                    value,
                    @intCast(index),
                    "",
                );
            }
            break :blk aggregate;
        },
        else => unreachable,
    };
}

pub fn emitInlineAsmInstruction(
    self: anytype,
    builder: llvm.LLVMBuilderRef,
    values: *std.AutoHashMapUnmanaged(u32, llvm.LLVMValueRef),
    instruction: ir.Instruction,
) !llvm.LLVMValueRef {
    if (instruction.output_types.len != instruction.output_addresses.len)
        return error.InvalidBackendIr;

    var input_types = try self.allocator.alloc(llvm.LLVMTypeRef, instruction.arguments.len);
    defer self.allocator.free(input_types);
    var arguments = try self.allocator.alloc(llvm.LLVMValueRef, instruction.arguments.len);
    defer self.allocator.free(arguments);
    for (instruction.arguments, 0..) |argument_id, index| {
        arguments[index] = values.get(argument_id) orelse return error.InvalidBackendIr;
        input_types[index] = llvm.LLVMTypeOf(arguments[index]);
    }

    var output_types = try self.allocator.alloc(llvm.LLVMTypeRef, instruction.output_types.len);
    defer self.allocator.free(output_types);
    for (instruction.output_types, 0..) |type_id, index|
        output_types[index] = try backend_types.typeById(self, type_id);

    const return_type = switch (output_types.len) {
        0 => llvm.LLVMVoidTypeInContext(self.context),
        1 => output_types[0],
        else => llvm.LLVMStructTypeInContext(
            self.context,
            output_types.ptr,
            @intCast(output_types.len),
            0,
        ),
    };
    const function_type = llvm.LLVMFunctionType(
        return_type,
        if (input_types.len == 0) null else input_types.ptr,
        @intCast(input_types.len),
        0,
    );
    const template = instruction.asm_template orelse return error.InvalidBackendIr;
    const constraints = instruction.constraints orelse return error.InvalidBackendIr;
    const inline_asm = llvm.LLVMGetInlineAsm(
        function_type,
        template.ptr,
        template.len,
        constraints.ptr,
        constraints.len,
        1,
        0,
        llvm.LLVMInlineAsmDialectATT,
        0,
    );
    const result = llvm.LLVMBuildCall2(
        builder,
        function_type,
        inline_asm,
        if (arguments.len == 0) null else arguments.ptr,
        @intCast(arguments.len),
        "",
    );
    for (instruction.output_addresses, 0..) |address_id, index| {
        const address = values.get(address_id) orelse return error.InvalidBackendIr;
        const output = if (output_types.len == 1)
            result
        else
            llvm.LLVMBuildExtractValue(builder, result, @intCast(index), "");
        _ = llvm.LLVMBuildStore(builder, output, address);
    }
    return result;
}

pub fn emitSyscallInstruction(
    self: anytype,
    builder: llvm.LLVMBuilderRef,
    values: *std.AutoHashMapUnmanaged(u32, llvm.LLVMValueRef),
    instruction: ir.Instruction,
) !llvm.LLVMValueRef {
    const i64_type = llvm.LLVMInt64TypeInContext(self.context);
    if (std.mem.indexOf(u8, self.module_ir.target.triple, "linux") == null)
        return llvm.LLVMConstInt(i64_type, @bitCast(@as(i64, -38)), 1);

    var arguments: [7]llvm.LLVMValueRef = undefined;
    for (&arguments) |*argument|
        argument.* = llvm.LLVMConstInt(i64_type, 0, 0);
    for (instruction.arguments, 0..) |argument_id, index| {
        if (index >= arguments.len) return error.InvalidBackendIr;
        arguments[index] = values.get(argument_id) orelse return error.InvalidBackendIr;
    }

    var parameter_types = [_]llvm.LLVMTypeRef{i64_type} ** arguments.len;
    const function_type = llvm.LLVMFunctionType(
        i64_type,
        &parameter_types,
        parameter_types.len,
        0,
    );
    return emitLinuxSyscallInlineAsm(self, builder, function_type, &arguments, arguments.len);
}

pub fn emitProcessExitInstruction(
    self: anytype,
    builder: llvm.LLVMBuilderRef,
    values: *std.AutoHashMapUnmanaged(u32, llvm.LLVMValueRef),
    instruction: ir.Instruction,
) !llvm.LLVMValueRef {
    const code = try instructionValue(values, instruction.lhs);
    if (std.mem.indexOf(u8, self.module_ir.target.triple, "linux") != null) {
        const i64_type = llvm.LLVMInt64TypeInContext(self.context);
        const code_i64 = llvm.LLVMBuildZExt(builder, code, i64_type, "");
        const is_aarch64 = std.mem.startsWith(u8, self.module_ir.target.triple, "aarch64");
        const syscall_number: u64 = if (is_aarch64) 94 else 231;
        var arguments = [_]llvm.LLVMValueRef{
            llvm.LLVMConstInt(i64_type, syscall_number, 0),
            code_i64,
            llvm.LLVMConstInt(i64_type, 0, 0),
            llvm.LLVMConstInt(i64_type, 0, 0),
            llvm.LLVMConstInt(i64_type, 0, 0),
            llvm.LLVMConstInt(i64_type, 0, 0),
            llvm.LLVMConstInt(i64_type, 0, 0),
        };
        var parameter_types = [_]llvm.LLVMTypeRef{i64_type} ** arguments.len;
        const function_type = llvm.LLVMFunctionType(
            i64_type,
            &parameter_types,
            parameter_types.len,
            0,
        );
        return emitLinuxSyscallInlineAsm(self, builder, function_type, &arguments, arguments.len);
    }

    if (std.mem.indexOf(u8, self.module_ir.target.triple, "windows") != null) {
        const exit_name = "ExitProcess";
        const i32_type = llvm.LLVMInt32TypeInContext(self.context);
        var parameter_types = [_]llvm.LLVMTypeRef{i32_type};
        const function_type = llvm.LLVMFunctionType(
            llvm.LLVMVoidTypeInContext(self.context),
            &parameter_types,
            parameter_types.len,
            0,
        );
        const exit_function = llvm.LLVMGetNamedFunction(self.module, exit_name) orelse
            llvm.LLVMAddFunction(self.module, exit_name, function_type);
        var arguments = [_]llvm.LLVMValueRef{code};
        return llvm.LLVMBuildCall2(
            builder,
            function_type,
            exit_function,
            &arguments,
            arguments.len,
            "",
        );
    }

    return emitTrapInstruction(self, builder);
}

pub fn emitTrapInstruction(self: anytype, builder: llvm.LLVMBuilderRef) !llvm.LLVMValueRef {
    const intrinsic_name = "llvm.trap";
    const intrinsic_id = llvm.LLVMLookupIntrinsicID(intrinsic_name, intrinsic_name.len);
    if (intrinsic_id == 0) return error.InvalidBackendIr;
    const trap_function = llvm.LLVMGetIntrinsicDeclaration(self.module, intrinsic_id, null, 0);
    const trap_type = llvm.LLVMFunctionType(
        llvm.LLVMVoidTypeInContext(self.context),
        null,
        0,
        0,
    );
    return llvm.LLVMBuildCall2(builder, trap_type, trap_function, null, 0, "");
}

pub fn emitCallInstruction(
    self: anytype,
    builder: llvm.LLVMBuilderRef,
    values: *std.AutoHashMapUnmanaged(u32, llvm.LLVMValueRef),
    instruction: ir.Instruction,
) !llvm.LLVMValueRef {
    return switch (instruction.op) {
        .call => blk: {
            const callee = self.functions.get(instruction.callee orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr;
            var arguments = try self.allocator.alloc(llvm.LLVMValueRef, instruction.arguments.len);
            defer self.allocator.free(arguments);
            for (instruction.arguments, 0..) |argument_id, index|
                arguments[index] = values.get(argument_id) orelse return error.InvalidBackendIr;
            break :blk llvm.LLVMBuildCall2(
                builder,
                callee.function_type,
                callee.function,
                if (arguments.len == 0) null else arguments.ptr,
                @intCast(arguments.len),
                "",
            );
        },
        .indirect_call => blk: {
            const callee = try instructionValue(values, instruction.lhs);
            const function_type = try backend_types.functionTypeById(self,
                instruction.source_type orelse return error.InvalidBackendIr,
            );
            var arguments = try self.allocator.alloc(llvm.LLVMValueRef, instruction.arguments.len);
            defer self.allocator.free(arguments);
            for (instruction.arguments, 0..) |argument_id, index|
                arguments[index] = values.get(argument_id) orelse return error.InvalidBackendIr;
            break :blk llvm.LLVMBuildCall2(
                builder,
                function_type,
                callee,
                if (arguments.len == 0) null else arguments.ptr,
                @intCast(arguments.len),
                "",
            );
        },
        else => unreachable,
    };
}

pub fn emitLinuxSyscallInlineAsm(
    self: anytype,
    builder: llvm.LLVMBuilderRef,
    function_type: llvm.LLVMTypeRef,
    arguments: [*]llvm.LLVMValueRef,
    argument_count: usize,
) llvm.LLVMValueRef {
    const is_aarch64 = std.mem.startsWith(u8, self.module_ir.target.triple, "aarch64");
    const template = if (is_aarch64) "svc #0" else "syscall";
    const constraints = if (is_aarch64)
        "={x0},{x8},0,{x1},{x2},{x3},{x4},{x5},~{memory}"
    else
        "={rax},{rax},{rdi},{rsi},{rdx},{r10},{r8},{r9},~{rcx},~{r11},~{memory}";
    const inline_asm = llvm.LLVMGetInlineAsm(
        function_type,
        template.ptr,
        template.len,
        constraints.ptr,
        constraints.len,
        1,
        0,
        llvm.LLVMInlineAsmDialectATT,
        0,
    );
    return llvm.LLVMBuildCall2(
        builder,
        function_type,
        inline_asm,
        arguments,
        @intCast(argument_count),
        "",
    );
}

pub fn instructionValue(
    values: *std.AutoHashMapUnmanaged(u32, llvm.LLVMValueRef),
    id: ?u32,
) !llvm.LLVMValueRef {
    return values.get(id orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr;
}

pub fn toCondition(builder: llvm.LLVMBuilderRef, value: llvm.LLVMValueRef) llvm.LLVMValueRef {
    const zero = llvm.LLVMConstInt(llvm.LLVMTypeOf(value), 0, 0);
    return llvm.LLVMBuildICmp(
        builder,
        @intCast(llvm.LLVMIntNE),
        value,
        zero,
        "",
    );
}
