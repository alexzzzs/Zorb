# Backend IR contract

Backend IR is the versioned JSON boundary between the compiler frontend and
`backend/llvm`. It is an internal compiler protocol, not a source-language or
LLVM API. The C# seed writer and the native Zorb writer must emit the same
contract.

The canonical schema is defined by
[`backend/llvm/src/backend_ir.zig`](../backend/llvm/src/backend_ir.zig). The
current schema version is `2`. Unknown JSON fields are rejected by the backend.

## Module envelope

Every document contains:

| Field | Type | Contract |
| --- | --- | --- |
| `schema_version` | unsigned integer | Must equal the backend schema version. |
| `module_name` | string | Non-empty diagnostic/module identity. |
| `target` | object | Target triple plus optional CPU, features, and optimization level. |
| `output_kind` | string | `llvm_ir`, `bitcode`, `object`, or `assembly`. |
| `output_path` | string | Non-empty backend output path. |
| `types` | array | Interned type definitions. Defaults to empty. |
| `globals` | array | Global declarations and constants. Defaults to empty. |
| `functions` | array | Function declarations and definitions. Defaults to empty. |

`target.cpu`, `target.features`, and `target.optimize` default to `generic`, an
empty feature string, and `O0` respectively.

## Identity and references

Type, global, function, parameter, instruction, and block references are
unsigned numeric IDs. Zero is reserved for type and function IDs. IDs must be
stable within one emitted module but need not remain stable between compiler
runs or schema versions.

References are scoped as follows:

- type, global, and function IDs are module-wide;
- block IDs are unique within a function;
- parameter and instruction IDs share the function's value namespace;
- terminators reference values and blocks by ID;
- aggregate fields retain source order.

Defined functions must contain at least one block. External functions may have
no blocks. Every block has exactly one terminator.

## Types and constants

Type kinds are `scalar`, `pointer`, `string`, `array`, `struct`, `slice`,
`error_union`, `enum`, `union`, and `function`. Optional fields are interpreted
according to the type kind. Scalar types use the scalar names declared by the
canonical Zig schema.

Global initializers are recursive constants. Constant kinds are `zero`,
`integer`, `string`, `pointer_integer`, `function`, and `aggregate`.

## Instructions and control flow

Instructions use a common record with an `op`, result `type`, result `id`, and
operation-specific optional fields. Operation names and their payload fields
are defined by `InstructionOp` in the canonical schema. Producers must omit
irrelevant optional fields rather than assigning invented sentinel values.

Control flow ends with one of `return_void`, `return_value`, `branch`,
`conditional_branch`, or `unreachable`. Phi instructions pair
`incoming_values` and `incoming_blocks` by position.

## Evolution rules

Any incompatible field, enum, reference, or semantic change increments
`schema_version`. A change is complete only when all of these agree:

1. the Zig schema and validator;
2. the C# seed writer;
3. the native Zorb writer;
4. the checked-in JSON fixtures and contract tests; and
5. this document.

Additive fields still require a version increment while unknown fields are
rejected. This keeps old bootstrap compilers from silently producing a module
with different semantics.

[`backend/llvm/tests/scalar.json`](../backend/llvm/tests/scalar.json) is the
minimal executable example. The backend validates it before lowering it to
LLVM.

The native frontend can emit this contract with
`zorb-self-check --emit-backend-ir <target-triple> <output-path> <entry.zorb>`.
Its current supported lowering slice is one or more `i32` or `i64` functions
sharing the same scalar type. Each function either returns a positive integer
literal or applies an arithmetic, remainder, bitwise, or shift operation to two
same-typed parameters. Unsupported AST shapes fail explicitly while native
lowering is expanded incrementally.
