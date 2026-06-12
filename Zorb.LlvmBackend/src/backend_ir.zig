const std = @import("std");

pub const schema_version = 2;

pub const OutputKind = enum {
    llvm_ir,
    bitcode,
    object,
    assembly,
};

pub const OptimizeLevel = enum {
    O0,
    O1,
    O2,
    O3,

    pub fn passPipeline(self: OptimizeLevel) [:0]const u8 {
        return switch (self) {
            .O0 => "default<O0>",
            .O1 => "default<O1>",
            .O2 => "default<O2>",
            .O3 => "default<O3>",
        };
    }
};

pub const Target = struct {
    triple: []const u8,
    cpu: []const u8 = "generic",
    features: []const u8 = "",
    optimize: OptimizeLevel = .O0,
};

pub const ScalarKind = enum {
    void,
    bool,
    i8,
    u8,
    i16,
    u16,
    i32,
    u32,
    i64,
    u64,
    pointer,
    string,

    pub fn bitWidth(self: ScalarKind) ?u16 {
        return switch (self) {
            .void => null,
            .bool, .i32, .u32 => 32,
            .i8, .u8 => 8,
            .i16, .u16 => 16,
            .i64, .u64 => 64,
            .pointer, .string => null,
        };
    }

    pub fn isSigned(self: ScalarKind) bool {
        return switch (self) {
            .i8, .i16, .i32, .i64 => true,
            else => false,
        };
    }
};

pub const TypeKind = enum {
    scalar,
    pointer,
    string,
    array,
    @"struct",
    slice,
    error_union,
    @"enum",
    @"union",
    function,
};

pub const TypeField = struct {
    name: []const u8,
    type: u32,
};

pub const TypeDef = struct {
    id: u32,
    kind: TypeKind,
    name: ?[]const u8 = null,
    scalar: ?ScalarKind = null,
    element_type: ?u32 = null,
    length: ?u64 = null,
    fields: []const TypeField = &.{},
    @"packed": bool = false,
};

pub const Linkage = enum {
    external,
    internal,
};

pub const ConstantKind = enum {
    zero,
    integer,
    string,
    pointer_integer,
    function,
    aggregate,
};

pub const Constant = struct {
    kind: ConstantKind,
    integer: ?i64 = null,
    text: ?[]const u8 = null,
    function: ?u32 = null,
    elements: []const Constant = &.{},
};

pub const Global = struct {
    id: u32,
    name: []const u8,
    type: u32,
    linkage: Linkage = .internal,
    constant: bool = false,
    initializer: Constant = .{ .kind = .zero },
};

pub const BinaryOp = enum {
    add,
    sub,
    mul,
    signed_div,
    unsigned_div,
    signed_rem,
    unsigned_rem,
    bit_and,
    bit_or,
    bit_xor,
    shift_left,
    arithmetic_shift_right,
    logical_shift_right,
};

pub const CompareOp = enum {
    equal,
    not_equal,
    signed_less,
    signed_less_equal,
    signed_greater,
    signed_greater_equal,
    unsigned_less,
    unsigned_less_equal,
    unsigned_greater,
    unsigned_greater_equal,
};

pub const CastOp = enum {
    truncate,
    sign_extend,
    zero_extend,
    pointer_to_integer,
    integer_to_pointer,
};

pub const InstructionOp = enum {
    zero_constant,
    integer_constant,
    string_constant,
    alloca,
    load,
    store,
    binary,
    compare,
    cast,
    phi,
    aggregate,
    extract_value,
    index_address,
    field_address,
    global_address,
    function_address,
    inline_asm,
    syscall,
    size_of,
    process_exit,
    trap,
    call,
    indirect_call,
};

pub const Instruction = struct {
    id: u32,
    op: InstructionOp,
    type: u32,
    integer: ?i64 = null,
    text: ?[]const u8 = null,
    binary_op: ?BinaryOp = null,
    compare_op: ?CompareOp = null,
    cast_op: ?CastOp = null,
    lhs: ?u32 = null,
    rhs: ?u32 = null,
    callee: ?u32 = null,
    arguments: []const u32 = &.{},
    incoming_values: []const u32 = &.{},
    incoming_blocks: []const u32 = &.{},
    source_type: ?u32 = null,
    field_index: ?u32 = null,
    global: ?u32 = null,
    asm_template: ?[]const u8 = null,
    constraints: ?[]const u8 = null,
    output_types: []const u32 = &.{},
    output_addresses: []const u32 = &.{},
};

pub const TerminatorOp = enum {
    return_void,
    return_value,
    branch,
    conditional_branch,
    @"unreachable",
};

pub const Terminator = struct {
    op: TerminatorOp,
    value: ?u32 = null,
    target: ?u32 = null,
    condition: ?u32 = null,
    true_target: ?u32 = null,
    false_target: ?u32 = null,
};

pub const Block = struct {
    id: u32,
    name: []const u8,
    instructions: []const Instruction = &.{},
    terminator: Terminator,
};

pub const Parameter = struct {
    id: u32,
    name: []const u8,
    type: u32,
};

pub const Function = struct {
    id: u32,
    name: []const u8,
    linkage: Linkage = .internal,
    return_type: u32,
    parameters: []const Parameter = &.{},
    blocks: []const Block = &.{},
};

pub const Module = struct {
    schema_version: u32,
    module_name: []const u8,
    target: Target,
    output_kind: OutputKind,
    output_path: []const u8,
    types: []const TypeDef = &.{},
    globals: []const Global = &.{},
    functions: []const Function = &.{},

    pub fn validate(self: Module, diagnostics: *std.Io.Writer) !void {
        if (self.schema_version != schema_version) {
            try diagnostics.print(
                "unsupported backend IR schema version {d}; expected {d}\n",
                .{ self.schema_version, schema_version },
            );
            return error.InvalidBackendIr;
        }
        if (self.module_name.len == 0 or self.target.triple.len == 0 or self.output_path.len == 0) {
            try diagnostics.writeAll("module_name, target.triple, and output_path must be non-empty\n");
            return error.InvalidBackendIr;
        }

        var type_ids: std.AutoHashMapUnmanaged(u32, void) = .empty;
        defer type_ids.deinit(std.heap.page_allocator);
        for (self.types) |type_def| {
            if (type_def.id == 0 or type_ids.contains(type_def.id)) {
                try diagnostics.print("invalid or duplicate type id {d}\n", .{type_def.id});
                return error.InvalidBackendIr;
            }
            try type_ids.put(std.heap.page_allocator, type_def.id, {});
        }

        var function_ids: std.AutoHashMapUnmanaged(u32, void) = .empty;
        defer function_ids.deinit(std.heap.page_allocator);
        for (self.functions) |function| {
            if (function.id == 0 or function.name.len == 0 or function_ids.contains(function.id)) {
                try diagnostics.print("invalid or duplicate function id {d}\n", .{function.id});
                return error.InvalidBackendIr;
            }
            try function_ids.put(std.heap.page_allocator, function.id, {});
            if (function.blocks.len == 0 and function.linkage != .external) {
                try diagnostics.print("defined function '{s}' has no blocks\n", .{function.name});
                return error.InvalidBackendIr;
            }
            var block_ids: std.AutoHashMapUnmanaged(u32, void) = .empty;
            defer block_ids.deinit(std.heap.page_allocator);
            for (function.blocks) |block| {
                if (block.name.len == 0 or block_ids.contains(block.id)) {
                    try diagnostics.print(
                        "function '{s}' has an invalid or duplicate block id {d}\n",
                        .{ function.name, block.id },
                    );
                    return error.InvalidBackendIr;
                }
                try block_ids.put(std.heap.page_allocator, block.id, {});
            }
        }
    }
};

test "parse and validate scalar module" {
    const json =
        \\{
        \\  "schema_version": 2,
        \\  "module_name": "test",
        \\  "target": {"triple":"x86_64-pc-linux-gnu"},
        \\  "output_kind": "llvm_ir",
        \\  "output_path": "test.ll",
        \\  "types": [],
        \\  "functions": []
        \\}
    ;
    const parsed = try std.json.parseFromSlice(Module, std.testing.allocator, json, .{});
    defer parsed.deinit();

    var diagnostic_buffer: [256]u8 = undefined;
    var diagnostics: std.Io.Writer = .fixed(&diagnostic_buffer);
    try parsed.value.validate(&diagnostics);
}
