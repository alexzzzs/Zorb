const std = @import("std");
const llvm = @import("llvm");
const ir = @import("../backend_ir.zig");

pub fn initializeTypes(self: anytype) !void {
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
                .@"struct" => try completeStructType(self, type_def),
                .slice => try completeSliceType(self, type_def),
                .error_union => try completeErrorUnionType(self, type_def),
                .@"union" => try completeUnionType(self, type_def),
                else => {},
            }
            continue;
        }
        try self.types.put(self.allocator, type_def.id, try createType(self, type_def));
    }
}

pub fn createType(self: anytype, type_def: ir.TypeDef) anyerror!llvm.LLVMTypeRef {
    return switch (type_def.kind) {
        .scalar => scalarType(self, type_def.scalar orelse return error.InvalidBackendIr),
        .pointer, .string => llvm.LLVMPointerTypeInContext(self.context, 0),
        .function => llvm.LLVMPointerTypeInContext(self.context, 0),
        .array => llvm.LLVMArrayType2(
            try typeById(self, type_def.element_type orelse return error.InvalidBackendIr),
            type_def.length orelse return error.InvalidBackendIr,
        ),
        .@"enum" => try typeById(self, type_def.element_type orelse return error.InvalidBackendIr),
        .@"struct", .slice, .error_union, .@"union" => return error.InvalidBackendIr,
    };
}

pub fn completeStructType(self: anytype, type_def: ir.TypeDef) !void {
    const struct_type = try typeById(self, type_def.id);
    var field_types = try self.allocator.alloc(llvm.LLVMTypeRef, type_def.fields.len);
    defer self.allocator.free(field_types);
    for (type_def.fields, 0..) |field, index|
        field_types[index] = try typeById(self, field.type);
    llvm.LLVMStructSetBody(
        struct_type,
        if (field_types.len == 0) null else field_types.ptr,
        @intCast(field_types.len),
        @intFromBool(type_def.@"packed"),
    );
}

pub fn completeSliceType(self: anytype, type_def: ir.TypeDef) !void {
    _ = type_def.element_type orelse return error.InvalidBackendIr;
    const fields = [_]llvm.LLVMTypeRef{
        llvm.LLVMPointerTypeInContext(self.context, 0),
        llvm.LLVMInt64TypeInContext(self.context),
    };
    llvm.LLVMStructSetBody(try typeById(self, type_def.id), @constCast(&fields), fields.len, 0);
}

pub fn completeErrorUnionType(self: anytype, type_def: ir.TypeDef) !void {
    const value_type = try typeById(self, type_def.element_type orelse return error.InvalidBackendIr);
    const fields = [_]llvm.LLVMTypeRef{
        value_type,
        llvm.LLVMInt32TypeInContext(self.context),
    };
    llvm.LLVMStructSetBody(try typeById(self, type_def.id), @constCast(&fields), fields.len, 0);
}

pub fn completeUnionType(self: anytype, type_def: ir.TypeDef) !void {
    var field_types = try self.allocator.alloc(llvm.LLVMTypeRef, type_def.fields.len + 1);
    defer self.allocator.free(field_types);
    field_types[0] = llvm.LLVMInt32TypeInContext(self.context);
    for (type_def.fields, 0..) |field, index|
        field_types[index + 1] = try typeById(self, field.type);
    llvm.LLVMStructSetBody(
        try typeById(self, type_def.id),
        field_types.ptr,
        @intCast(field_types.len),
        0,
    );
}

pub fn typeById(self: anytype, type_id: u32) anyerror!llvm.LLVMTypeRef {
    if (self.types.get(type_id)) |type_ref| return type_ref;
    for (self.module_ir.types) |type_def| {
        if (type_def.id != type_id) continue;
        const type_ref = try createType(self, type_def);
        try self.types.put(self.allocator, type_id, type_ref);
        return type_ref;
    }
    return error.InvalidBackendIr;
}

pub fn findTypeDef(self: anytype, type_id: u32) ?ir.TypeDef {
    for (self.module_ir.types) |type_def| {
        if (type_def.id == type_id) return type_def;
    }
    return null;
}

pub fn functionTypeById(self: anytype, type_id: u32) !llvm.LLVMTypeRef {
    const type_def = findTypeDef(self, type_id) orelse return error.InvalidBackendIr;
    if (type_def.kind != .function) return error.InvalidBackendIr;
    var parameter_types = try self.allocator.alloc(llvm.LLVMTypeRef, type_def.fields.len);
    defer self.allocator.free(parameter_types);
    for (type_def.fields, 0..) |field, index|
        parameter_types[index] = try typeById(self, field.type);
    return llvm.LLVMFunctionType(
        try typeById(self, type_def.element_type orelse return error.InvalidBackendIr),
        if (parameter_types.len == 0) null else parameter_types.ptr,
        @intCast(parameter_types.len),
        0,
    );
}

pub fn scalarType(self: anytype, scalar: ir.ScalarKind) llvm.LLVMTypeRef {
    return switch (scalar) {
        .void => llvm.LLVMVoidTypeInContext(self.context),
        .bool, .i32, .u32 => llvm.LLVMInt32TypeInContext(self.context),
        .i8, .u8 => llvm.LLVMInt8TypeInContext(self.context),
        .i16, .u16 => llvm.LLVMInt16TypeInContext(self.context),
        .i64, .u64 => llvm.LLVMInt64TypeInContext(self.context),
        .pointer, .string => llvm.LLVMPointerTypeInContext(self.context, 0),
    };
}
