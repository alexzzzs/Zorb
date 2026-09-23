const std = @import("std");
const llvm = @import("llvm");
const ir = @import("../backend_ir.zig");
const backend_types = @import("types.zig");

pub fn declareGlobals(self: anytype) !void {
    for (self.module_ir.globals) |global_ir| {
        const global_type = try backend_types.typeById(self, global_ir.type);
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
            try constantValue(self, global_ir.type, global_ir.initializer),
        );
        try self.globals.put(self.allocator, global_ir.id, global);
    }
}

pub fn constantValue(self: anytype, type_id: u32, constant: ir.Constant) !llvm.LLVMValueRef {
    const value_type = try backend_types.typeById(self, type_id);
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
            const type_def = backend_types.findTypeDef(self, type_id) orelse return error.InvalidBackendIr;
            const expected_element_count: usize = switch (type_def.kind) {
                .array => @intCast(type_def.length orelse return error.InvalidBackendIr),
                .@"struct" => type_def.fields.len,
                .@"union" => type_def.fields.len + 1,
                else => return error.InvalidBackendIr,
            };
            if (constant.elements.len != expected_element_count)
                return error.InvalidBackendIr;
            var elements = try self.allocator.alloc(llvm.LLVMValueRef, constant.elements.len);
            defer self.allocator.free(elements);
            for (constant.elements, 0..) |element, index| {
                if (type_def.kind == .@"union" and index == 0) {
                    const tag_type = llvm.LLVMInt32TypeInContext(self.context);
                    elements[index] = switch (element.kind) {
                        .zero => llvm.LLVMConstNull(tag_type),
                        .integer => llvm.LLVMConstInt(
                            tag_type,
                            @bitCast(element.integer orelse return error.InvalidBackendIr),
                            @intFromBool((element.integer orelse 0) < 0),
                        ),
                        else => return error.InvalidBackendIr,
                    };
                    continue;
                }
                const element_type_id = switch (type_def.kind) {
                    .array => type_def.element_type orelse return error.InvalidBackendIr,
                    .@"struct" => type_def.fields[index].type,
                    .@"union" => type_def.fields[index - 1].type,
                    else => unreachable,
                };
                elements[index] = try constantValue(self, element_type_id, element);
            }
            break :blk switch (type_def.kind) {
                .array => llvm.LLVMConstArray2(
                    try backend_types.typeById(self, type_def.element_type orelse return error.InvalidBackendIr),
                    if (elements.len == 0) null else elements.ptr,
                    elements.len,
                ),
                .@"struct", .@"union" => llvm.LLVMConstNamedStruct(
                    value_type,
                    if (elements.len == 0) null else elements.ptr,
                    @intCast(elements.len),
                ),
                else => unreachable,
            };
        },
    };
}
