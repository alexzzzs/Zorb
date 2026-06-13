const std = @import("std");
const llvm = @import("llvm");
const ir = @import("backend_ir.zig");
const llvm_support = @import("llvm_support.zig");

const FunctionRecord = struct {
    function: llvm.LLVMValueRef,
    function_type: llvm.LLVMTypeRef,
};

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
        try self.initializeTypes();
        try self.declareFunctions();
        try self.declareGlobals();
        try self.emitFunctionBodies();
        try self.verify();
        try self.optimize();
        try self.verify();
        try self.writeOutput();
    }

    fn declareGlobals(self: *Backend) !void {
        for (self.module_ir.globals) |global_ir| {
            const global_type = try self.typeById(global_ir.type);
            const name = try self.allocator.dupeZ(u8, global_ir.name);
            defer self.allocator.free(name);
            const global = llvm.LLVMAddGlobal(self.module, global_type, name);
            llvm.LLVMSetLinkage(global, switch (global_ir.linkage) {
                .external => llvm.LLVMExternalLinkage,
                .internal => llvm.LLVMInternalLinkage,
            });
            llvm.LLVMSetGlobalConstant(global, @intFromBool(global_ir.constant));
            llvm.LLVMSetInitializer(
                global,
                try self.constantValue(global_ir.type, global_ir.initializer),
            );
            try self.globals.put(self.allocator, global_ir.id, global);
        }
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

    fn constantValue(self: *Backend, type_id: u32, constant: ir.Constant) !llvm.LLVMValueRef {
        const value_type = try self.typeById(type_id);
        return switch (constant.kind) {
                .zero => llvm.LLVMConstNull(value_type),
                .integer => llvm.LLVMConstInt(
                    value_type,
                    @bitCast(constant.integer orelse return error.InvalidBackendIr),
                    @intFromBool((constant.integer orelse 0) < 0),
                ),
                .string => blk: {
                    const text = constant.text orelse return error.InvalidBackendIr;
                    const bytes = llvm.LLVMConstStringInContext2(
                        self.context,
                        text.ptr,
                        text.len,
                        0,
                    );
                    const array_type = llvm.LLVMTypeOf(bytes);
                    const string_global = llvm.LLVMAddGlobal(self.module, array_type, ".str.global");
                    llvm.LLVMSetLinkage(string_global, llvm.LLVMPrivateLinkage);
                    llvm.LLVMSetGlobalConstant(string_global, 1);
                    llvm.LLVMSetUnnamedAddress(string_global, llvm.LLVMGlobalUnnamedAddr);
                    llvm.LLVMSetInitializer(string_global, bytes);
                    const zero = llvm.LLVMConstInt(
                        llvm.LLVMInt32TypeInContext(self.context),
                        0,
                        0,
                    );
                    var indices = [_]llvm.LLVMValueRef{ zero, zero };
                    break :blk llvm.LLVMConstInBoundsGEP2(
                        array_type,
                        string_global,
                        &indices,
                        indices.len,
                    );
                },
                .pointer_integer => llvm.LLVMConstIntToPtr(
                    llvm.LLVMConstInt(
                        llvm.LLVMInt64TypeInContext(self.context),
                        @bitCast(constant.integer orelse return error.InvalidBackendIr),
                        @intFromBool((constant.integer orelse 0) < 0),
                    ),
                    value_type,
                ),
                .function => (self.functions.get(
                    constant.function orelse return error.InvalidBackendIr,
                ) orelse return error.InvalidBackendIr).function,
                .aggregate => blk: {
                    const type_def = self.findTypeDef(type_id) orelse return error.InvalidBackendIr;
                    if (type_def.kind != .array or
                        constant.elements.len != (type_def.length orelse return error.InvalidBackendIr))
                    {
                        return error.InvalidBackendIr;
                    }
                    const element_type_id = type_def.element_type orelse return error.InvalidBackendIr;
                    var elements = try self.allocator.alloc(llvm.LLVMValueRef, constant.elements.len);
                    defer self.allocator.free(elements);
                    for (constant.elements, 0..) |element, index|
                        elements[index] = try self.constantValue(element_type_id, element);
                    break :blk llvm.LLVMConstArray2(
                        try self.typeById(element_type_id),
                        if (elements.len == 0) null else elements.ptr,
                        elements.len,
                    );
                },
            };
    }

    fn initializeTypes(self: *Backend) !void {
        for (self.module_ir.types) |type_def| {
            const llvm_type = switch (type_def.kind) {
                .@"struct", .slice, .error_union, .@"union" => blk: {
                    const type_name = type_def.name orelse return error.InvalidBackendIr;
                    const name = try self.allocator.dupeZ(u8, type_name);
                    defer self.allocator.free(name);
                    break :blk llvm.LLVMStructCreateNamed(self.context, name);
                },
                else => continue,
            };
            try self.types.put(self.allocator, type_def.id, llvm_type);
        }

        for (self.module_ir.types) |type_def| {
            if (self.types.contains(type_def.id)) {
                switch (type_def.kind) {
                    .@"struct" => try self.completeStructType(type_def),
                    .slice => try self.completeSliceType(type_def),
                    .error_union => try self.completeErrorUnionType(type_def),
                    .@"union" => try self.completeUnionType(type_def),
                    else => {},
                }
                continue;
            }
            try self.types.put(self.allocator, type_def.id, try self.createType(type_def));
        }
    }

    fn createType(self: *Backend, type_def: ir.TypeDef) anyerror!llvm.LLVMTypeRef {
        return switch (type_def.kind) {
            .scalar => self.scalarType(type_def.scalar orelse return error.InvalidBackendIr),
            .pointer, .string => llvm.LLVMPointerTypeInContext(self.context, 0),
            .function => llvm.LLVMPointerTypeInContext(self.context, 0),
            .array => llvm.LLVMArrayType2(
                try self.typeById(type_def.element_type orelse return error.InvalidBackendIr),
                type_def.length orelse return error.InvalidBackendIr,
            ),
            .@"enum" => try self.typeById(type_def.element_type orelse return error.InvalidBackendIr),
            .@"struct", .slice, .error_union, .@"union" => return error.InvalidBackendIr,
        };
    }

    fn completeStructType(self: *Backend, type_def: ir.TypeDef) !void {
        const struct_type = try self.typeById(type_def.id);
        var field_types = try self.allocator.alloc(llvm.LLVMTypeRef, type_def.fields.len);
        defer self.allocator.free(field_types);
        for (type_def.fields, 0..) |field, index|
            field_types[index] = try self.typeById(field.type);
        llvm.LLVMStructSetBody(
            struct_type,
            if (field_types.len == 0) null else field_types.ptr,
            @intCast(field_types.len),
            @intFromBool(type_def.@"packed"),
        );
    }

    fn completeSliceType(self: *Backend, type_def: ir.TypeDef) !void {
        _ = type_def.element_type orelse return error.InvalidBackendIr;
        const fields = [_]llvm.LLVMTypeRef{
            llvm.LLVMPointerTypeInContext(self.context, 0),
            llvm.LLVMInt64TypeInContext(self.context),
        };
        llvm.LLVMStructSetBody(try self.typeById(type_def.id), @constCast(&fields), fields.len, 0);
    }

    fn completeErrorUnionType(self: *Backend, type_def: ir.TypeDef) !void {
        const value_type = try self.typeById(type_def.element_type orelse return error.InvalidBackendIr);
        const fields = [_]llvm.LLVMTypeRef{
            value_type,
            llvm.LLVMInt32TypeInContext(self.context),
        };
        llvm.LLVMStructSetBody(try self.typeById(type_def.id), @constCast(&fields), fields.len, 0);
    }

    fn completeUnionType(self: *Backend, type_def: ir.TypeDef) !void {
        var field_types = try self.allocator.alloc(llvm.LLVMTypeRef, type_def.fields.len + 1);
        defer self.allocator.free(field_types);
        field_types[0] = llvm.LLVMInt32TypeInContext(self.context);
        for (type_def.fields, 0..) |field, index|
            field_types[index + 1] = try self.typeById(field.type);
        llvm.LLVMStructSetBody(
            try self.typeById(type_def.id),
            field_types.ptr,
            @intCast(field_types.len),
            0,
        );
    }

    fn typeById(self: *Backend, type_id: u32) anyerror!llvm.LLVMTypeRef {
        if (self.types.get(type_id)) |type_ref| return type_ref;
        for (self.module_ir.types) |type_def| {
            if (type_def.id != type_id) continue;
            const type_ref = try self.createType(type_def);
            try self.types.put(self.allocator, type_id, type_ref);
            return type_ref;
        }
        return error.InvalidBackendIr;
    }

    fn findTypeDef(self: *Backend, type_id: u32) ?ir.TypeDef {
        for (self.module_ir.types) |type_def| {
            if (type_def.id == type_id) return type_def;
        }
        return null;
    }

    fn declareFunctions(self: *Backend) !void {
        for (self.module_ir.functions) |function_ir| {
            var parameter_types = try self.allocator.alloc(llvm.LLVMTypeRef, function_ir.parameters.len);
            defer self.allocator.free(parameter_types);
            for (function_ir.parameters, 0..) |parameter, index| {
                parameter_types[index] = try self.typeById(parameter.type);
            }
            const function_type = llvm.LLVMFunctionType(
                try self.typeById(function_ir.return_type),
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
            try self.functions.put(self.allocator, function_ir.id, .{
                .function = function,
                .function_type = function_type,
            });
        }
    }

    fn emitFunctionBodies(self: *Backend) !void {
        for (self.module_ir.functions) |function_ir| {
            if (function_ir.blocks.len == 0) continue;
            const record = self.functions.get(function_ir.id) orelse return error.InvalidBackendIr;
            const builder = llvm.LLVMCreateBuilderInContext(self.context) orelse return error.OutOfMemory;
            defer llvm.LLVMDisposeBuilder(builder);

            var values: std.AutoHashMapUnmanaged(u32, llvm.LLVMValueRef) = .empty;
            defer values.deinit(self.allocator);
            var blocks: std.AutoHashMapUnmanaged(u32, llvm.LLVMBasicBlockRef) = .empty;
            defer blocks.deinit(self.allocator);
            for (function_ir.parameters, 0..) |parameter, index| {
                const value = llvm.LLVMGetParam(record.function, @intCast(index));
                const name = try self.allocator.dupeZ(u8, parameter.name);
                defer self.allocator.free(name);
                llvm.LLVMSetValueName2(value, name, parameter.name.len);
                try values.put(self.allocator, parameter.id, value);
            }

            for (function_ir.blocks) |block_ir| {
                const block_name = try self.allocator.dupeZ(u8, block_ir.name);
                defer self.allocator.free(block_name);
                const block = llvm.LLVMAppendBasicBlockInContext(self.context, record.function, block_name);
                try blocks.put(self.allocator, block_ir.id, block);
            }

            for (function_ir.blocks) |block_ir| {
                const block = blocks.get(block_ir.id) orelse return error.InvalidBackendIr;
                llvm.LLVMPositionBuilderAtEnd(builder, block);

                for (block_ir.instructions) |instruction| {
                    const value = try self.emitInstruction(builder, &values, instruction);
                    try values.put(self.allocator, instruction.id, value);
                }
                switch (block_ir.terminator.op) {
                    .return_void => _ = llvm.LLVMBuildRetVoid(builder),
                    .return_value => {
                        const value_id = block_ir.terminator.value orelse return error.InvalidBackendIr;
                        const value = values.get(value_id) orelse return error.InvalidBackendIr;
                        _ = llvm.LLVMBuildRet(builder, value);
                    },
                    .branch => {
                        const target = blocks.get(block_ir.terminator.target orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr;
                        _ = llvm.LLVMBuildBr(builder, target);
                    },
                    .conditional_branch => {
                        const condition_value = values.get(block_ir.terminator.condition orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr;
                        const condition = toCondition(builder, condition_value);
                        const true_target = blocks.get(block_ir.terminator.true_target orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr;
                        const false_target = blocks.get(block_ir.terminator.false_target orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr;
                        _ = llvm.LLVMBuildCondBr(builder, condition, true_target, false_target);
                    },
                    .@"unreachable" => _ = llvm.LLVMBuildUnreachable(builder),
                }
            }

            for (function_ir.blocks) |block_ir| {
                for (block_ir.instructions) |instruction| {
                    if (instruction.op != .phi) continue;
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
                    for (
                        instruction.incoming_values,
                        instruction.incoming_blocks,
                        0..,
                    ) |value_id, block_id, index| {
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
            }
        }
    }

    fn emitInstruction(
        self: *Backend,
        builder: llvm.LLVMBuilderRef,
        values: *std.AutoHashMapUnmanaged(u32, llvm.LLVMValueRef),
        instruction: ir.Instruction,
    ) !llvm.LLVMValueRef {
        return switch (instruction.op) {
            .zero_constant => llvm.LLVMConstNull(try self.typeById(instruction.type)),
            .integer_constant => blk: {
                const integer = instruction.integer orelse return error.InvalidBackendIr;
                const type_ref = try self.typeById(instruction.type);
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
            .alloca => llvm.LLVMBuildAlloca(builder, try self.typeById(instruction.type), ""),
            .load => blk: {
                const address = values.get(instruction.lhs orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr;
                break :blk llvm.LLVMBuildLoad2(builder, try self.typeById(instruction.type), address, "");
            },
            .store => blk: {
                const address = values.get(instruction.lhs orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr;
                const value = values.get(instruction.rhs orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr;
                break :blk llvm.LLVMBuildStore(builder, value, address);
            },
            .binary => blk: {
                const lhs = values.get(instruction.lhs orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr;
                const rhs = values.get(instruction.rhs orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr;
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
            .compare => blk: {
                const lhs = values.get(instruction.lhs orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr;
                const rhs = values.get(instruction.rhs orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr;
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
                break :blk llvm.LLVMBuildZExt(builder, condition, try self.typeById(instruction.type), "");
            },
            .cast => blk: {
                const value = values.get(instruction.lhs orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr;
                const target_type = try self.typeById(instruction.type);
                break :blk switch (instruction.cast_op orelse return error.InvalidBackendIr) {
                    .truncate => llvm.LLVMBuildTrunc(builder, value, target_type, ""),
                    .sign_extend => llvm.LLVMBuildSExt(builder, value, target_type, ""),
                    .zero_extend => llvm.LLVMBuildZExt(builder, value, target_type, ""),
                    .pointer_to_integer => llvm.LLVMBuildPtrToInt(builder, value, target_type, ""),
                    .integer_to_pointer => llvm.LLVMBuildIntToPtr(builder, value, target_type, ""),
                };
            },
            .phi => blk: {
                if (instruction.incoming_values.len != instruction.incoming_blocks.len or
                    instruction.incoming_values.len == 0)
                {
                    return error.InvalidBackendIr;
                }
                break :blk llvm.LLVMBuildPhi(builder, try self.typeById(instruction.type), "");
            },
            .aggregate => blk: {
                const aggregate_type = try self.typeById(instruction.type);
                var aggregate = llvm.LLVMGetUndef(aggregate_type);
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
            .extract_value => blk: {
                const aggregate = values.get(instruction.lhs orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr;
                break :blk llvm.LLVMBuildExtractValue(
                    builder,
                    aggregate,
                    instruction.field_index orelse return error.InvalidBackendIr,
                    "",
                );
            },
            .index_address => blk: {
                const base = values.get(instruction.lhs orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr;
                const index = values.get(instruction.rhs orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr;
                const source_type_id = instruction.source_type orelse return error.InvalidBackendIr;
                const source_type = try self.typeById(source_type_id);
                const source_def = self.findTypeDef(source_type_id) orelse return error.InvalidBackendIr;
                if (source_def.kind == .array) {
                    const zero = llvm.LLVMConstInt(llvm.LLVMInt32TypeInContext(self.context), 0, 0);
                    var indices = [_]llvm.LLVMValueRef{ zero, index };
                    break :blk llvm.LLVMBuildGEP2(builder, source_type, base, &indices, indices.len, "");
                }
                var indices = [_]llvm.LLVMValueRef{index};
                break :blk llvm.LLVMBuildGEP2(builder, source_type, base, &indices, indices.len, "");
            },
            .field_address => blk: {
                const base = values.get(instruction.lhs orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr;
                const source_type = try self.typeById(instruction.source_type orelse return error.InvalidBackendIr);
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
            .inline_asm => blk: {
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
                    output_types[index] = try self.typeById(type_id);

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
                break :blk result;
            },
            .syscall => blk: {
                const i64_type = llvm.LLVMInt64TypeInContext(self.context);
                if (std.mem.indexOf(u8, self.module_ir.target.triple, "linux") != null) {
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
                    break :blk llvm.LLVMBuildCall2(
                        builder,
                        function_type,
                        inline_asm,
                        &arguments,
                        arguments.len,
                        "",
                    );
                }
                break :blk llvm.LLVMConstInt(i64_type, @bitCast(@as(i64, -38)), 1);
            },
            .size_of => llvm.LLVMConstInt(
                try self.typeById(instruction.type),
                llvm.LLVMABISizeOfType(
                    self.target_data,
                    try self.typeById(instruction.source_type orelse return error.InvalidBackendIr),
                ),
                0,
            ),
            .process_exit => blk: {
                const code = values.get(instruction.lhs orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr;
                if (std.mem.indexOf(u8, self.module_ir.target.triple, "linux") != null) {
                    const i64_type = llvm.LLVMInt64TypeInContext(self.context);
                    const code_i64 = llvm.LLVMBuildZExt(builder, code, i64_type, "");
                    const is_aarch64 = std.mem.startsWith(u8, self.module_ir.target.triple, "aarch64");
                    const syscall_number: u64 = if (is_aarch64) 93 else 60;
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
                    break :blk llvm.LLVMBuildCall2(
                        builder,
                        function_type,
                        inline_asm,
                        &arguments,
                        arguments.len,
                        "",
                    );
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
                    break :blk llvm.LLVMBuildCall2(
                        builder,
                        function_type,
                        exit_function,
                        &arguments,
                        arguments.len,
                        "",
                    );
                }

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
                break :blk llvm.LLVMBuildCall2(builder, trap_type, trap_function, null, 0, "");
            },
            .trap => blk: {
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
                break :blk llvm.LLVMBuildCall2(builder, trap_type, trap_function, null, 0, "");
            },
            .call => blk: {
                const callee = self.functions.get(instruction.callee orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr;
                var arguments = try self.allocator.alloc(llvm.LLVMValueRef, instruction.arguments.len);
                defer self.allocator.free(arguments);
                for (instruction.arguments, 0..) |argument_id, index| {
                    arguments[index] = values.get(argument_id) orelse return error.InvalidBackendIr;
                }
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
                const callee = values.get(instruction.lhs orelse return error.InvalidBackendIr) orelse return error.InvalidBackendIr;
                const function_type = try self.functionTypeById(
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
        };
    }

    fn functionTypeById(self: *Backend, type_id: u32) !llvm.LLVMTypeRef {
        const type_def = self.findTypeDef(type_id) orelse return error.InvalidBackendIr;
        if (type_def.kind != .function) return error.InvalidBackendIr;
        var parameter_types = try self.allocator.alloc(llvm.LLVMTypeRef, type_def.fields.len);
        defer self.allocator.free(parameter_types);
        for (type_def.fields, 0..) |field, index|
            parameter_types[index] = try self.typeById(field.type);
        return llvm.LLVMFunctionType(
            try self.typeById(type_def.element_type orelse return error.InvalidBackendIr),
            if (parameter_types.len == 0) null else parameter_types.ptr,
            @intCast(parameter_types.len),
            0,
        );
    }

    fn toCondition(builder: llvm.LLVMBuilderRef, value: llvm.LLVMValueRef) llvm.LLVMValueRef {
        const zero = llvm.LLVMConstInt(llvm.LLVMTypeOf(value), 0, 0);
        return llvm.LLVMBuildICmp(
            builder,
            @intCast(llvm.LLVMIntNE),
            value,
            zero,
            "",
        );
    }

    fn scalarType(self: *Backend, scalar: ir.ScalarKind) llvm.LLVMTypeRef {
        return switch (scalar) {
            .void => llvm.LLVMVoidTypeInContext(self.context),
            .bool, .i32, .u32 => llvm.LLVMInt32TypeInContext(self.context),
            .i8, .u8 => llvm.LLVMInt8TypeInContext(self.context),
            .i16, .u16 => llvm.LLVMInt16TypeInContext(self.context),
            .i64, .u64 => llvm.LLVMInt64TypeInContext(self.context),
            .pointer, .string => llvm.LLVMPointerTypeInContext(self.context, 0),
        };
    }

    fn verify(self: *Backend) !void {
        var message: [*c]u8 = null;
        if (llvm.LLVMVerifyModule(self.module, llvm.LLVMReturnStatusAction, &message) != 0) {
            defer llvm_support.disposeMessage(message);
            if (message != null)
                std.debug.print("LLVM verification failed:\n{s}\n", .{std.mem.span(message)});
            return llvm_support.LlvmError.LlvmVerificationFailed;
        }
    }

    fn optimize(self: *Backend) !void {
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

    fn writeOutput(self: *Backend) !void {
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
};

fn codeGenOptLevel(level: ir.OptimizeLevel) llvm.LLVMCodeGenOptLevel {
    return switch (level) {
        .O0 => llvm.LLVMCodeGenLevelNone,
        .O1 => llvm.LLVMCodeGenLevelLess,
        .O2 => llvm.LLVMCodeGenLevelDefault,
        .O3 => llvm.LLVMCodeGenLevelAggressive,
    };
}
