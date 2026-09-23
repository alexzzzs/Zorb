# Backend IR contract

Backend IR is the versioned JSON boundary between the compiler frontend and
`backend/llvm`. It is an internal compiler protocol, not a source-language or
LLVM API. The native Zorb writer emits the current contract; the frozen C# seed
writer remains a version 2 compatibility producer.

The canonical schema is defined by
[`backend/llvm/src/backend_ir.zig`](../backend/llvm/src/backend_ir.zig). The
current schema version is `3`. The backend also accepts version 2 modules from
the frozen C# seed writer, but version 2 cannot contain the `pointer_difference`
instruction. Unknown JSON fields are rejected by the backend.

## Module envelope

Every document contains:

| Field | Type | Contract |
| --- | --- | --- |
| `schema_version` | unsigned integer | Must be `3`, or legacy version `2` without version 3 instructions. |
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

Schema version 3 adds `pointer_difference`. Its `lhs` and `rhs` are pointer
values, `source_type` is their pointee type, and the result `type` is signed
`i64`. The backend subtracts their target pointer representations and divides
the signed byte difference by the pointee's ABI size. Version 2 modules cannot
use this instruction.

Control flow ends with one of `return_void`, `return_value`, `branch`,
`conditional_branch`, or `unreachable`. Phi instructions pair
`incoming_values` and `incoming_blocks` by position.

## Evolution rules

Any incompatible field, enum, reference, or semantic change increments
`schema_version`. A change is complete only when all of these agree:

1. the Zig schema and validator;
2. the native Zorb writer;
3. the checked-in JSON fixtures and contract tests; and
4. this document.

The frozen C# seed writer may continue producing version 2 while it is used for
recovery bootstrapping. New version 3 instructions must be emitted by the
native writer.

Additive fields still require a version increment while unknown fields are
rejected. This keeps old bootstrap compilers from silently producing a module
with different semantics.

[`backend/llvm/tests/scalar.json`](../backend/llvm/tests/scalar.json) is the
minimal executable example. The backend validates it before lowering it to
LLVM.

The native frontend can emit this contract with
`zorb-self-check --emit-backend-ir <target-triple> <output-path> <entry.zorb>`.
Native lowering covers the compiler's admitted language surface: scalar and
aggregate types, globals and constants, declarations and direct/function-value
calls, locals and assignments, casts, pointers, arrays, slices and strings,
struct/enum/union/error-union operations, pointer differences, inline assembly, and structured
control flow including loops, switch, match, catch, break, and continue.
Generic declarations are monomorphized before or during lowering.

Lowering is graph-wide. The frontend concatenates owning declarations from all
retained source modules, rebases their compact arena references, and omits
metadata-only imported projections. Parameters are materialized once into
stable locals so assignments and address-taking use one representation.

The production driver serializes this contract to a private temporary file and
calls `zorb_llvm_emit_file` from the statically linked backend API. The JSON
boundary remains useful for schema validation and bootstrap fixed-point tests;
it is not a separate user-facing backend process.
