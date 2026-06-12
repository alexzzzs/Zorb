# Zig 0.16 and LLVM Backend Rewrite Mapping

## Purpose

This document is a design and migration reference for replacing
`Zorb.Compiler/Codegen/CGenerator.cs` with the Zig 0.16 backend under
`Zorb.LlvmBackend/` that emits LLVM
IR through LLVM's C API.

It does not implement the backend. It maps:

1. C# language and library constructs used by the current generator to
   idiomatic Zig 0.16 patterns.
2. Current C-emission responsibilities to LLVM IR construction operations.
3. The backend boundary needed between the existing .NET frontend and a Zig
   implementation.
4. A staged migration and verification strategy that preserves current Zorb
   semantics.

The current source file is named `CGenerator.cs`, despite references to
`CCodegenerator.cs`.

## Version Baseline

The intended baseline is:

- Zig: 0.16.0
- LLVM C API: LLVM 20 on the current development machine
- Installed LLVM include directory: `/usr/lib/llvm-20/include`
- Installed LLVM library: `libLLVM-20`
- Current local default `zig`: 0.15.2

The local Zig executable must be upgraded or explicitly selected before
implementation begins. Examples in this document follow Zig 0.16 conventions,
especially:

- use `std.process.Init` for the executable entry point;
- pass allocators explicitly to containers;
- pass `std.Io` explicitly to I/O operations;
- use `std.ArrayList(T).initCapacity(...)`;
- use unmanaged-style maps and lists where ownership should remain in a larger
  context object;
- generate C bindings with `std.Build.addTranslateC` and import the generated
  module, rather than introducing new `@cImport` usage.

LLVM API names in this document are checked against the installed LLVM 20 C
headers. A different LLVM major may add, remove, or deprecate functions.

## Current Backend Responsibilities

`CGenerator.cs` is approximately 3,100 lines and currently owns all of these
concerns:

- recursively collecting imported AST nodes;
- collecting imported C header names;
- resetting mutable generator state between compilations;
- allocating collision-free C names;
- avoiding C reserved words and runtime helper names;
- lowering Zorb scalar, pointer, array, slice, function, enum, struct, union,
  generic, and error-union types;
- computing and materializing explicit struct layout;
- monomorphizing generic structs and functions;
- cloning generic AST bodies with type substitution;
- emitting function declarations and definitions;
- tracking local scopes and local names;
- preserving expression evaluation order through generated preludes;
- lowering statements and expressions;
- implementing short-circuit control flow;
- implementing array copy semantics;
- adding slice bounds checks;
- lowering `catch`;
- emitting inline assembly;
- emitting hosted and freestanding entry-point shims;
- emitting Linux syscall helpers and runtime failure helpers;
- selecting platform builtins;
- producing a C translation unit for a later native compiler invocation.

An LLVM backend should not remain a single object with all of these concerns.
The rewrite should separate frontend normalization, symbol planning, type
lowering, function lowering, target configuration, and object emission.

## Critical Boundary Decision

The existing frontend produces a managed C# object graph containing
`Node`, `Statement`, `Expr`, `TypeNode`, `SymbolTable`, and related subclasses.
Zig cannot directly traverse those .NET objects.

Replacing only `CGenerator.cs` therefore requires one of these boundaries.

| Boundary | Shape | Advantages | Costs |
| --- | --- | --- | --- |
| Serialized backend IR executable | C# writes a versioned binary or JSON-like typed IR; Zig reads it and writes an object or LLVM IR | Process isolation, straightforward ownership, easy differential testing | Serialization cost and a new schema |
| Native Zig library through C ABI | C# marshals a flat C-compatible backend IR and calls Zig through P/Invoke | No child process, direct diagnostics | More complex lifetime and ABI management |
| Rewrite frontend in Zig | Parser, checker, symbols, and backend share native data | Clean final architecture | Much larger project than replacing code generation |
| Host .NET from Zig or expose managed objects | Zig interacts with CLR hosting APIs or many callbacks | Avoids serialization in theory | High complexity, fragile ownership, poor portability |

### Recommended boundary

Use a versioned backend IR, initially transferred to a Zig backend executable.

The backend IR should be lower-level than the parser AST and richer than the
current raw AST:

- all names resolved to stable symbol IDs;
- every expression annotated with its checked type;
- all array lengths, alignments, offsets, enum values, and target builtins
  resolved;
- imports flattened into one compilation unit;
- source locations preserved for diagnostics and debug information;
- generic declarations and concrete instantiation requests represented
  explicitly;
- declaration visibility and linkage represented explicitly;
- lvalue versus rvalue requirements either represented or derivable;
- target configuration passed as data, not inferred from the backend host.

This removes several fragile backend behaviors, including repeated symbol
lookups, ad hoc `GetExprType`, AST cloning during generic instantiation, and
platform decisions based on the process running the compiler.

### Backend IR stability rules

- Include a schema version and reject incompatible versions.
- Use numeric node and symbol tags, not C# class names.
- Use stable integer IDs for symbols, types, strings, and source files.
- Keep source-language types distinct from LLVM types.
- Do not serialize pointers or process-local object identities.
- Encode signed integers without passing through JSON floating-point numbers.
- Define endianness and integer width if using a binary format.
- Treat unknown optional fields as forward-compatible only when semantics are
  unchanged.

## Proposed Zig Module Layout

The exact names can follow the eventual Zig repository layout, but the
responsibility split should resemble this:

| Module | Responsibility |
| --- | --- |
| `backend_ir.zig` | Versioned typed input model and decoding |
| `backend.zig` | Compilation orchestration and public backend API |
| `llvm.zig` | Generated LLVM C bindings import and thin ownership wrappers |
| `target.zig` | Target triple, CPU, features, ABI, relocation, code model |
| `names.zig` | Stable mangling, reserved names, symbol-to-linker-name maps |
| `types.zig` | Source type interning and LLVM type lowering |
| `layout.zig` | Struct layout validation against LLVM target data |
| `generics.zig` | Concrete instantiation keys and work queues |
| `module_emitter.zig` | Module setup, declarations, globals, and passes |
| `function_emitter.zig` | Function-local state and statement lowering |
| `expression.zig` | Rvalue/lvalue expression lowering |
| `runtime.zig` | Bounds-failure, syscall, entry, and trap declarations |
| `diagnostics.zig` | Source-aware backend errors |
| `link.zig` | Object-link command construction or linker integration |

Avoid a generic wrapper around every LLVM function. Thin wrappers are useful
only where they establish ownership, convert LLVM error conventions, or encode
an invariant such as "this basic block has not been terminated."

## Zig Ownership Model

### Compilation lifetime

A backend compilation naturally has arena-like lifetime:

- decoded backend IR;
- interned names;
- mangled names;
- type keys;
- declaration maps;
- temporary work queues;
- diagnostics;
- LLVM wrapper metadata.

Use a compilation arena for immutable or append-only data that is discarded
together. Use the process GPA for objects that must be individually freed or
survive multiple compilations.

LLVM objects are not Zig allocations. They require their matching LLVM dispose
functions:

| LLVM object | Creation | Disposal |
| --- | --- | --- |
| Context | `LLVMContextCreate` | `LLVMContextDispose` |
| Module | `LLVMModuleCreateWithNameInContext` | `LLVMDisposeModule` |
| Builder | `LLVMCreateBuilderInContext` | `LLVMDisposeBuilder` |
| Target machine | `LLVMCreateTargetMachine*` | `LLVMDisposeTargetMachine` |
| Target data | `LLVMCreateTargetDataLayout` | `LLVMDisposeTargetData` |
| Pass options | `LLVMCreatePassBuilderOptions` | `LLVMDisposePassBuilderOptions` |
| LLVM message | returned through `char **` or message function | `LLVMDisposeMessage` |
| LLVM error message | `LLVMGetErrorMessage` | `LLVMDisposeErrorMessage` |

Use `defer` immediately after successful ownership acquisition. When ownership
is transferred, clear or invalidate the local owner so it is not disposed
twice.

### Recommended context shapes

`Backend` should own compilation-wide state:

- allocator and arena;
- target configuration;
- LLVM context, module, target machine, and target data;
- source type cache;
- named LLVM struct placeholders;
- function and global declaration maps;
- string literal pool;
- monomorphization work queues;
- diagnostics.

`FunctionEmitter` should own function-local state:

- current LLVM function;
- LLVM builder;
- entry block and current insertion block;
- local symbol ID to storage/value map;
- break and continue target stacks;
- optional return block and return slot;
- temporary name counter used only for readable IR;
- source location state;
- cleanup or deferred control-flow state if the language gains it later.

Do not retain a single mutable local map on the module-wide backend. A separate
function emitter prevents state leakage between functions and makes nested
control-flow state explicit.

## C# to Idiomatic Zig Mapping

### Classes with mutable fields

C#:

```csharp
public class CGenerator
{
    private readonly Dictionary<string, TypeNode> _localVars = new();
    private int _tempCounter;
}
```

Zig mapping:

- use a `struct`;
- initialize it through an explicit `init` function;
- release owned resources through `deinit`;
- pass `*Self` to mutating methods;
- keep allocator fields only when the object truly owns long-lived
  allocator-backed containers;
- prefer function-local unmanaged containers when ownership is local.

Conceptual shape:

```zig
const Backend = struct {
    allocator: std.mem.Allocator,
    temp_counter: u32,
    // Maps and LLVM handles.

    pub fn init(allocator: std.mem.Allocator, ...) !Backend { ... }
    pub fn deinit(self: *Backend) void { ... }
};
```

This is an illustrative shape, not an implementation commitment.

### Constructors and overloads

C# constructor overloads such as:

```csharp
public CGenerator()
public CGenerator(string currentDir)
public CGenerator(string currentDir, SymbolTable symbolTable)
```

should become one explicit initializer with required dependencies. Add small
named convenience initializers only if they encode valid policy, not merely
default missing dependencies.

For a backend, target configuration, allocator, and input IR should be explicit.
Avoid silently constructing an empty symbol table because LLVM lowering should
consume already-resolved backend IR.

### Properties

C# mutable properties such as `PreserveStart`, `NoStdLib`, and
`BuiltinIsLinux` should become a configuration value:

```zig
const TargetOptions = struct {
    entry_mode: EntryMode,
    libc_mode: LibcMode,
    os: Os,
    arch: Arch,
};
```

Prefer enums over related booleans. The current boolean combinations permit
invalid states, such as "bare metal and Windows" or an unspecified architecture
where an architecture is required.

### Records

The current records:

```csharp
private sealed record GeneratedExpression(string Prelude, string Code, TypeNode? Type);
private sealed record LocalScope(...);
```

map to value structs, but `GeneratedExpression` should not be translated
literally. LLVM emission should use a semantic result:

```zig
const EmittedValue = union(enum) {
    rvalue: TypedValue,
    address: Address,
    noreturn,
};
```

The C backend's `Prelude` exists because a C expression string cannot naturally
contain arbitrary control flow. LLVM IR has explicit basic blocks, so lowering
can emit instructions directly and return a value or address.

`LocalScope` should usually become scope stack entries or a snapshot length
into append-only local bindings. Copying whole hash maps for each nested block
is avoidable.

### Inheritance and runtime type tests

C# AST dispatch uses:

```csharp
switch (expr)
{
    case CallExpr call:
    case BinaryExpr binary:
    ...
}
```

The idiomatic Zig representation is a tagged union:

```zig
const Expr = union(enum) {
    call: CallExpr,
    binary: BinaryExpr,
    ...
};
```

Then use `switch (expr)` with payload capture. This gives exhaustive compile
errors when a new AST variant is added and avoids heap allocation per node when
the backend IR uses indexed storage.

For recursive trees, prefer node IDs into arrays:

```text
ExprId -> expressions[ExprId] -> tagged payload containing other ExprId values
```

This avoids pointer-rich ownership and serializes cleanly.

### Nullable references

C# `T?` maps to Zig `?T`.

Use:

- `if (value) |v| { ... }` when both branches matter;
- `value orelse fallback` for defaults;
- `value orelse return error.MissingType` for required values;
- optional handles only where absence is valid.

Do not represent a required semantic type as nullable just because the current
generator sometimes fails to rediscover it. The backend IR should require types
on checked expressions.

### Exceptions

C# uses `throw new Exception(...)` and catches around code generation.

Zig should use:

- an error set for expected backend failures;
- a diagnostics collection carrying source location and message;
- `try` and `catch` for propagation;
- `@panic` only for violated internal invariants that indicate a compiler bug.

Suggested categories:

```text
error.InvalidBackendIr
error.UnsupportedTarget
error.InvalidLayout
error.LlvmVerificationFailed
error.LlvmEmissionFailed
error.OutOfMemory
```

Detailed diagnostic text should not be encoded into the error tag. Store it in
the diagnostics sink and return a compact error.

### `try`/`finally`

C# `try/finally` blocks that restore local state map to:

- `defer` for unconditional restoration;
- `errdefer` for error-only cleanup;
- scoped objects whose `deinit` restores insertion or scope state.

Example uses in the current generator include `_insideFunctionBody`,
`_continueTargets`, and local variable snapshots. In Zig, make these operations
lexically scoped so restoration cannot be forgotten.

### Lists

C# `List(T)` maps based on ownership:

- mutable variable-length data: `std.ArrayList(T)`;
- immutable borrowed data: `[]const T`;
- mutable borrowed data: `[]T`;
- compilation-arena sequences: build in an `ArrayList`, then retain its slice
  for arena lifetime;
- fixed small sets: arrays or enum sets where appropriate.

Zig 0.16 list operations take an allocator explicitly. A list should not embed
an allocator merely to mimic C#.

### Dictionaries

C# `Dictionary<K,V>` mappings:

- strings with byte equality: `std.StringHashMap(V)`;
- integer IDs or enum-like keys: `std.AutoHashMap(K, V)`;
- structural type keys: `std.HashMap(K, V, Context, load_percentage)`;
- interned string IDs: prefer integer-key maps after interning.

For compiler data, stable integer IDs are preferable to repeated string hashing.
Name resolution should happen before code generation.

### Hash sets

C# `HashSet<T>` maps to:

- `std.AutoHashMap(T, void)` for integer or structural keys;
- `std.StringHashMap(void)` for string keys;
- bit sets or enum sets for bounded enum domains.

Sets like `_generatedResultTypes` and `_generatedSliceTypes` should become a
single type cache keyed by canonical source type ID, not separate string sets.

### Stacks

C# `Stack<T>` maps to an `ArrayList(T)` used with append and pop.

For break and continue targets, use:

```text
LoopTargets { break_block, continue_block }
```

Push one entry when entering a loop and pop it with `defer`.

### `StringBuilder`

Most current `StringBuilder` usage disappears because LLVM instructions are
created directly.

Remaining textual output, such as diagnostics, target triples, pass pipelines,
and linker command arguments, can use:

- `std.ArrayList(u8)` with a writer;
- `std.Io.Writer.Allocating` in Zig 0.16;
- fixed buffers for bounded formatting;
- interned strings for long-lived names.

Do not build LLVM IR text manually.

### LINQ

Current patterns such as:

```csharp
nodes.OfType<FunctionDecl>().ToList()
items.Select(...).ToList()
items.Any(...)
items.FirstOrDefault(...)
```

should become explicit loops. Compiler code benefits from visible allocation
and failure points.

Typical mappings:

| LINQ | Zig |
| --- | --- |
| `OfType<T>()` | switch over tagged union and append matching IDs |
| `Select` | allocate output and loop, or compute lazily in one pass |
| `Any` | loop and early return |
| `FirstOrDefault` | loop returning `?T` |
| `Contains` | map/set lookup |
| `ToList` | explicit `ArrayList` allocation |
| `string.Join` | writer loop with separators |

Avoid allocating intermediate arrays for declaration filtering.

### Strings and names

C# `string` is UTF-16 managed text. Zig compiler strings should normally be
UTF-8 byte slices, `[]const u8`.

LLVM C functions generally require NUL-terminated names, `[*:0]const u8`.
Choose one of these policies:

- intern all linker-visible names as sentinel-terminated strings;
- format into temporary sentinel-terminated buffers at the FFI boundary;
- expose wrapper helpers that allocate `[:0]const u8` in the compilation arena.

Never pass a normal non-sentinel slice pointer to an LLVM API expecting a C
string.

### String comparison

C# `StringComparer.Ordinal` means byte/code-unit exact comparison. For UTF-8
identifiers, use `std.mem.eql(u8, a, b)` and byte hashing. Do not use
locale-aware comparison.

### Substrings and replacement

C# slicing like `name[(lastDot + 1)..]` maps directly to Zig slices after index
validation.

Repeated `Replace(".", "_")` should not be the primary mangling algorithm.
Build names once through a writer and cache them by symbol ID.

### Static fields and constants

C# `static readonly HashSet<string>` should become:

- a compile-time array plus a lookup strategy;
- a `std.StaticStringMap` if available and appropriate in the selected Zig
  release;
- or, preferably, an escaping mangler that does not need a complete reserved
  word set.

LLVM names do not have C keyword restrictions. Preserve stable external names
where ABI requires them and use deterministic mangling for internal symbols.

### Enums

C# enums map to Zig `enum`, but target and ABI enums should include an explicit
integer representation when serialized or passed through C.

Use exhaustive switches. Add an `unknown` case only for data that genuinely
comes from a version-skewed external API.

### Tuples

C# tuples such as `List<(string Name, TypeNode Type)>` can become named structs.
Named fields are clearer in compiler code and remain stable if another field is
added.

### Cloning

The current backend deeply clones statements and expressions to substitute
generic types. Avoid cloning the AST in Zig.

Recommended generic strategy:

- keep the generic body immutable;
- create an `Instantiation` containing declaration ID and concrete type IDs;
- carry a substitution map while lowering;
- cache the emitted LLVM function or struct by canonical instantiation key.

This reduces allocation and prevents copied semantic annotations from becoming
stale.

### Resetting reusable state

`ResetGenerationState` exists because one `CGenerator` instance can run more
than once. Prefer one backend compilation object per invocation.

If process-level caches are added later, make them immutable or explicitly
versioned by target and frontend schema. Never retain function locals, imported
headers, target flags, LLVM values, or module-owned types across contexts.

## LLVM C Binding Integration in Zig 0.16

### Headers

The binding module should include only the required public C headers, likely:

```c
#include <llvm-c/Core.h>
#include <llvm-c/Analysis.h>
#include <llvm-c/Target.h>
#include <llvm-c/TargetMachine.h>
#include <llvm-c/Transforms/PassBuilder.h>
#include <llvm-c/BitWriter.h>
#include <llvm-c/Error.h>
```

Add `llvm-c/DebugInfo.h` only when debug information is implemented.
Add ORC headers only if JIT execution becomes a requirement.

### Zig build integration

For Zig 0.16:

1. Create a small project-owned C header that includes the required LLVM C
   headers.
2. Use `b.addTranslateC` with the selected target and optimization mode.
3. Add LLVM's include directory to the translation step.
4. Import the translated module as `llvm`.
5. Link against the matching LLVM library and required system libraries.

Do not use new `@cImport` code. Zig 0.16 deprecates it in favor of build-system
C translation.

### LLVM discovery

Do not hardcode `/usr/lib/llvm-20` in committed build logic.

Accept one or more of:

- an explicit build option such as `-Dllvm-prefix=/path`;
- an `LLVM_CONFIG` environment or build option;
- `llvm-config-20` or `llvm-config` discovery;
- platform-specific package configuration.

Validate that:

- header major version matches linked library major version;
- required C API symbols exist;
- target backends needed by Zorb are present.

The current machine reports LLVM 20.1.8 and links through `-lLLVM-20`.

### Opaque pointers and typed API variants

LLVM 20 uses opaque pointers. Code must carry the pointee/source type separately
when calling APIs such as:

- `LLVMBuildLoad2`;
- `LLVMBuildGEP2`;
- `LLVMBuildInBoundsGEP2`;
- `LLVMBuildStructGEP2`;
- `LLVMBuildCall2`;
- `LLVMBuildPtrDiff2`.

Do not infer pointee types from `LLVMTypeOf(pointer)`. The backend's own typed
value/address structures must retain that information.

### Error conventions

LLVM C APIs use several incompatible conventions:

- nullable pointer for failure;
- `LLVMBool` where nonzero means failure;
- `char **OutError`;
- `LLVMErrorRef`, where null means success.

Wrap each convention once. Every returned message must be disposed with the
correct function.

## Recommended LLVM Emission Pipeline

### Phase 1: Validate and index backend IR

- validate schema version;
- validate all referenced IDs;
- validate target-required fields;
- index declarations by symbol ID;
- construct canonical type keys;
- collect requested generic instantiations;
- reject backend IR that violates frontend invariants.

### Phase 2: Initialize target

- initialize required target info, target, target MC, and asm printer;
- obtain or validate the target triple;
- resolve the LLVM target with `LLVMGetTargetFromTriple`;
- create a target machine;
- create target data with `LLVMCreateTargetDataLayout`;
- set module target triple with `LLVMSetTarget`;
- set module data layout with `LLVMSetModuleDataLayout` or its string form.

Prefer initializing only supported targets in production. "Initialize all
targets" is convenient during bring-up but increases linkage and startup cost.

### Phase 3: Create all named type shells

For recursive or mutually dependent structs:

1. call `LLVMStructCreateNamed` for every concrete named aggregate;
2. cache each `LLVMTypeRef`;
3. lower field types after all names exist;
4. complete each body with `LLVMStructSetBody`.

This replaces the C backend's dynamic text ordering and early-struct sets.

### Phase 4: Declare globals and functions

- create all function types with `LLVMFunctionType`;
- declare functions with `LLVMAddFunction`;
- apply linkage, visibility, calling convention, section, alignment, and
  attributes;
- create globals with `LLVMAddGlobal`;
- apply constant/global linkage and initializers;
- declare runtime and external functions.

Every direct call should resolve to a previously declared `LLVMValueRef`.

### Phase 5: Emit function bodies

Create a new `FunctionEmitter` per function:

- append entry block;
- allocate mutable locals in the entry block;
- bind parameters;
- emit statements;
- ensure every reachable block has exactly one terminator;
- emit an implicit return only where source semantics permit it.

### Phase 6: Verify

- verify each function during development with `LLVMVerifyFunction`;
- verify the module with `LLVMVerifyModule`;
- convert verifier output into a backend diagnostic;
- optionally write failing IR to a temporary artifact for debugging.

Verification failure is a compiler bug or invalid backend IR, not a user program
error.

### Phase 7: Optimize

Use the new pass manager C API:

- create pass builder options;
- choose a textual pipeline such as `default<O0>`, `default<O1>`,
  `default<O2>`, or `default<O3>`;
- call `LLVMRunPasses`;
- consume and dispose any `LLVMErrorRef`.

Start with `O0` until semantic parity is established. Optimization can expose
undefined behavior in incorrect IR that appears to work at `O0`.

### Phase 8: Emit

Supported backend outputs should be separate:

- textual LLVM IR for diagnostics and tests;
- bitcode for tooling;
- object file for normal builds;
- assembly as an optional debugging artifact.

Use:

- `LLVMPrintModuleToFile` for textual IR;
- `LLVMWriteBitcodeToFile` for bitcode;
- `LLVMTargetMachineEmitToFile` for object or assembly.

LLVM object emission does not produce an executable. Linking remains a separate
stage.

## Source Type to LLVM Type Mapping

LLVM integer types do not encode signedness. Signedness remains source type
metadata and controls comparisons, division, remainder, shifts, and extension.

| Zorb type | LLVM representation | Notes |
| --- | --- | --- |
| `i8`, `u8`, `char` | `i8` | Signedness retained separately |
| `i16`, `u16` | `i16` | |
| `i32`, `u32` | `i32` | |
| `i64`, `u64` | `i64` | |
| `bool` | Prefer `i1` in SSA, with ABI conversion where needed | Current C ABI uses `int32_t`; choose and document boundary rules |
| `void` | LLVM `void` | Not a storable value |
| `string` | `ptr` to `i8` data | Current semantics are NUL-terminated C string-like pointers |
| `*T` | opaque `ptr` | Preserve pointee source type and address space in backend metadata |
| `[N]T` | `[N x T]` | Value aggregate with copy semantics |
| `[]T` | named or literal `{ ptr, i64 }` | Preserve field indices as constants |
| function type | LLVM function type plus opaque function pointer | Calling convention is part of compatibility |
| enum | underlying integer LLVM type | Keep nominal source identity separately |
| struct | named LLVM struct | Complete body after creating shells |
| tagged union | `{ tag, payload_storage }` | Payload strategy requires an explicit ABI decision |
| `!T` | named `{ T-or-placeholder, i32 }` | Match current C representation initially |
| extern type | opaque named struct or opaque pointer ABI | Depends on source declaration semantics |

### Boolean representation

LLVM conditions require `i1`. Two viable policies are:

1. Store and pass booleans as `i1` everywhere unless an external ABI requires
   widening.
2. Preserve current C ABI storage as `i32`, normalize to `i1` for conditions,
   and widen comparison results back to `i32` when stored or returned.

For compatibility with existing generated C and external functions, policy 2
is safer during migration. The backend should centralize:

- `toCondition`: compare integer value against zero to produce `i1`;
- `fromCondition`: zero-extend `i1` to the configured stored bool type.

### `void` error unions

The C backend uses an `int8_t` placeholder for the value field of `!void`.
Preserve this layout initially:

```text
Result_void = { i8 value, i32 error }
```

Changing it to `{ i32 error }` is an ABI change and should be separate.

### Slices

Current layout:

```text
{ ptr, i64 len }
```

The backend should intern one source slice type per element type, even though
opaque LLVM pointer types may make several slice LLVM structs structurally
identical. Source-level nominal distinctions and debug information still need
the element type.

### Structs

For ordinary structs:

- use a named LLVM struct;
- preserve source field order;
- use `LLVMStructSetBody(..., packed = 0)`;
- query offsets through target data when validating layout.

For `packed` structs:

- use a packed LLVM struct body;
- verify expected field offsets.

For explicit layout:

- insert `[N x i8]` padding fields where required;
- maintain a source-field-to-LLVM-index map because padding shifts indices;
- reject overlap unless Zorb explicitly defines overlapping fields;
- validate each source field offset with `LLVMOffsetOfElement`;
- validate total ABI size and alignment.

Do not assume the frontend's C layout calculation exactly matches LLVM target
layout. Treat disagreement as a diagnostic during migration.

### Tagged unions

The current C representation is:

```text
struct {
    i32 tag;
    union { variant payloads... } data;
}
```

LLVM has no source-level C union type. Choose an explicit payload
representation:

1. Byte storage sized and aligned to the largest variant.
2. A struct whose body is the largest variant plus padding.
3. A target-specific ABI representation computed from all variants.

Recommended initial representation:

```text
{ i32 tag, [payload_size x i8] payload }
```

with aggregate alignment raised to the maximum variant alignment. Access a
variant by GEP to payload storage and pointer cast to the variant's expected
address type. Validate size and alignment with target data.

This must be proven ABI-compatible with the existing C representation before
using it for external interfaces.

### Function types

Create with `LLVMFunctionType`.

Track:

- return type;
- parameter types;
- variadic flag;
- calling convention;
- parameter and return attributes;
- whether aggregate ABI lowering requires indirect passing.

The current C backend relies on the platform C compiler to implement aggregate
calling conventions. Direct LLVM IR emission takes responsibility for ABI
correctness. This is a major risk for structs, slices, unions, and error unions
passed by value.

For initial parity, restrict supported external ABI shapes or implement target
ABI classification before promising general C interoperability.

## Values, Addresses, and Expression Results

The current string backend sometimes returns code that is a value and sometimes
code that designates writable storage. LLVM lowering must distinguish them.

Suggested concepts:

```text
TypedValue {
    value: LLVMValueRef,
    source_type: TypeId,
}

Address {
    pointer: LLVMValueRef,
    pointee_type: TypeId,
    alignment: optional alignment,
    volatile: bool,
}
```

Core operations:

- `emitRValue(expr)` returns a computed value;
- `emitAddress(expr)` returns writable/readable storage;
- `load(address)` emits `LLVMBuildLoad2`;
- `store(address, value)` emits `LLVMBuildStore`;
- aggregate values may remain SSA values or be materialized into temporary
  storage when an address is required.

This replaces C-specific `.` versus `->`, array decay, and compound literal
text generation.

## Current C Pattern to LLVM Mapping

### Module and declarations

| Current C backend pattern | LLVM C API direction |
| --- | --- |
| `#include` | No LLVM equivalent; declare external types/functions explicitly |
| C preprocessor platform macro | Resolve in frontend/target config before IR emission |
| function prototype | `LLVMFunctionType` plus `LLVMAddFunction` |
| global declaration | `LLVMAddGlobal` |
| global initializer | `LLVMSetInitializer` with an LLVM constant |
| `const` global | mark global constant and set linkage |
| `extern` function | function declaration with no basic blocks |
| section attribute | `LLVMSetSection` |
| alignment attribute | `LLVMSetAlignment` |
| static/internal symbol | `LLVMSetLinkage(..., LLVMInternalLinkage)` |
| exported symbol | external linkage and requested visibility |
| function calling convention | `LLVMSetFunctionCallConv` and matching call-site convention |

### Function emission

Current C function text becomes:

1. construct function type;
2. call `LLVMAddFunction`;
3. create entry block with `LLVMAppendBasicBlockInContext`;
4. bind parameters with `LLVMGetParam`;
5. create mutable local storage with `LLVMBuildAlloca`;
6. emit body instructions;
7. terminate all reachable blocks with `LLVMBuildRet`,
   `LLVMBuildRetVoid`, branch, or `LLVMBuildUnreachable`.

Place allocas in the entry block even when the declaration appears in a nested
scope. This enables LLVM's mem2reg promotion while scope visibility remains a
frontend/backend symbol concern.

### Integer literals

Use `LLVMConstInt`.

The source type and signedness determine bit width and whether a negative value
must be represented by its two's-complement bit pattern. Do not always emit
`i64`, even though `NumberExpr.Value` is currently `long`; use the checked or
coerced expression type.

### String literals

Intern string bytes as private constant globals:

- include a trailing NUL to preserve current C string behavior;
- use `LLVMConstStringInContext2` for the array constant;
- create a private unnamed-address global;
- return a GEP to the first byte;
- deduplicate identical byte strings within a module.

`LLVMBuildGlobalStringPtr` is convenient but gives less control over pooling,
linkage, and naming.

### Unary operations

| Zorb operation | LLVM lowering |
| --- | --- |
| `-x` | `LLVMBuildNeg` for integers |
| `!x` | convert to `i1`, then `LLVMBuildNot` |
| `&x` | return `emitAddress(x)` without loading |
| pointer dereference if added | load from pointer address |

For `&array`, preserve the current semantics of obtaining a pointer to the first
element, not a pointer to the whole array. Emit GEP indices `[0, 0]`.

### Arithmetic and bitwise binary operations

| Operator | Signed integer | Unsigned integer |
| --- | --- | --- |
| `+` | `LLVMBuildAdd` | `LLVMBuildAdd` |
| `-` | `LLVMBuildSub` | `LLVMBuildSub` |
| `*` | `LLVMBuildMul` | `LLVMBuildMul` |
| `/` | `LLVMBuildSDiv` | `LLVMBuildUDiv` |
| `%` | `LLVMBuildSRem` | `LLVMBuildURem` |
| `&` | `LLVMBuildAnd` | `LLVMBuildAnd` |
| `|` | `LLVMBuildOr` | `LLVMBuildOr` |
| `^` | `LLVMBuildXor` | `LLVMBuildXor` |
| `<<` | `LLVMBuildShl` | `LLVMBuildShl` |
| `>>` | `LLVMBuildAShr` | `LLVMBuildLShr` |

Do not add `nsw`, `nuw`, or `exact` flags unless Zorb semantics guarantee the
corresponding behavior. Incorrect flags cause optimizer-visible undefined
behavior.

### Comparisons

Use `LLVMBuildICmp`.

| Operator | Signed predicate | Unsigned/pointer predicate |
| --- | --- | --- |
| `==` | `LLVMIntEQ` | `LLVMIntEQ` |
| `!=` | `LLVMIntNE` | `LLVMIntNE` |
| `<` | `LLVMIntSLT` | `LLVMIntULT` |
| `<=` | `LLVMIntSLE` | `LLVMIntULE` |
| `>` | `LLVMIntSGT` | `LLVMIntUGT` |
| `>=` | `LLVMIntSGE` | `LLVMIntUGE` |

Pointer ordering is currently a semantic question. Do not silently give it
unsigned integer semantics unless the language specification permits it.

### Pointer arithmetic

For `pointer + integer` and `integer + pointer`:

- coerce the index to the target's pointer index width as required;
- call `LLVMBuildGEP2` with the source element LLVM type;
- do not mark inbounds unless source semantics guarantee the result remains
  within the same allocated object.

For `pointer - integer`, negate or subtract the index before GEP.

Pointer-to-pointer subtraction remains unsupported according to current
semantics.

### Casts

Choose the LLVM operation from source and destination categories:

| Conversion | LLVM operation |
| --- | --- |
| narrower integer | `LLVMBuildTrunc` |
| signed widening integer | `LLVMBuildSExt` |
| unsigned widening integer | `LLVMBuildZExt` |
| same-width integer reinterpretation | no instruction or bitcast where valid |
| pointer to integer | `LLVMBuildPtrToInt` |
| integer to pointer | `LLVMBuildIntToPtr` |
| pointer address-space change | `LLVMBuildAddrSpaceCast` |
| pointer representation-preserving cast | usually no-op with opaque pointers |
| error union to value | extract value field, only where semantics permit |

The current C cast text delegates many details to C. The LLVM backend needs a
complete checked cast matrix.

### Function calls

Use `LLVMBuildCall2` with:

- the exact LLVM function type;
- direct function value or indirect function pointer;
- arguments already coerced to parameter types;
- matching calling convention and call-site attributes.

Evaluation order must remain source order. Emit each argument's instructions in
order before constructing the call.

Generic calls first resolve or enqueue a concrete instantiation, then call its
declared LLVM function.

### Variable declarations

Local mutable variable:

- allocate in entry block;
- emit initializer at declaration point;
- store initializer.

Local `const`:

- keep as an SSA value when no address is taken;
- otherwise allocate and store once.

Global:

- initializer must be an LLVM constant;
- reject runtime control flow in global initializers;
- set linkage, constness, section, and alignment.

### Assignment

- emit target address;
- emit source rvalue;
- coerce source;
- store to target.

For fixed arrays, preserve value copy semantics with one of:

- load aggregate then store aggregate;
- `LLVMBuildMemCpy` using target data size and correct alignment;
- element loop when volatile or semantic constraints require element access.

Do not use `restrict` as the C backend does unless non-aliasing is guaranteed.

### Struct literals

For constant globals, use `LLVMConstNamedStruct`.

For runtime values:

- begin with poison or undef only if every field is definitely inserted;
- use `LLVMBuildInsertValue` for SSA aggregate construction;
- or allocate temporary storage and store fields by GEP.

Prefer SSA aggregate construction for small ordinary structs. Use storage for
explicit-layout aggregates, arrays, volatile fields, and cases requiring an
address.

### Field access

For an aggregate SSA value:

- `LLVMBuildExtractValue` for reads;
- `LLVMBuildInsertValue` for updates that produce a new aggregate.

For an address:

- `LLVMBuildStructGEP2` with the complete LLVM struct type and mapped LLVM field
  index;
- then load or store.

Explicit-layout padding means source field ordinal is not always LLVM field
index.

### Array literals

Constant array:

- use `LLVMConstArray2`.

Runtime array:

- build an aggregate with insert-value operations;
- or allocate storage and store each element.

Preserve source evaluation order for element expressions.

### Array indexing

For an array address:

- use GEP indices `[0, index]`.

For a pointer:

- use one index with `LLVMBuildGEP2`.

The current language does not require array bounds checks, but slice indexing
does.

### Slice construction and coercion

Array-to-slice coercion:

1. obtain pointer to array element zero;
2. create the slice aggregate;
3. insert pointer at field 0;
4. insert constant length at field 1.

This must alias the original array storage.

### Slice indexing

Emit:

1. evaluate slice once;
2. evaluate index once;
3. compare `index < 0` for signed indexes;
4. compare `index >= len`;
5. OR the failure conditions;
6. conditionally branch to failure or success block;
7. failure block calls the runtime failure function and ends in
   `LLVMBuildUnreachable`;
8. success block computes element GEP.

Mark the failure function `noreturn` and preferably `cold`.

### `if`

Create:

- condition block or current block;
- then block;
- optional else block;
- merge block when at least one branch falls through.

Do not emit a merge block if both branches terminate. Before appending a branch,
check whether the current block already has a terminator.

Platform builtin conditions that are fixed by the target should be folded
before LLVM emission. Emit only the selected branch when the value is known.

### `while`

Create:

- condition block;
- body block;
- exit block.

Push `{ break = exit, continue = condition }`.

If condition lowering itself emits control flow, it still ends with an `i1`
available in the condition block.

### `for`

The current C backend lowers `for` through a `while (1)` plus an explicit
continue label. LLVM should create:

- initializer in current block;
- condition block;
- body block;
- update block;
- exit block.

Push `{ break = exit, continue = update }`.

This directly models the required behavior for `continue`.

### `break` and `continue`

- `break`: `LLVMBuildBr` to current loop's break block;
- `continue`: `LLVMBuildBr` to current loop's continue block.

After emitting either terminator, statement emission for that path must stop.
Do not append instructions to a terminated block.

### `switch`

Current Zorb `switch` compares cases in source order and case expressions may
require evaluation. That is not always equivalent to an LLVM `switch`
instruction, whose case values must be constants.

Use:

- `LLVMBuildSwitch` only when all case values are compile-time integer
  constants and source-order side effects are irrelevant;
- otherwise emit an ordered chain of compare-and-branch blocks.

Evaluate the controlling expression exactly once.

### Enum `match`

For constant enum patterns, an LLVM switch is appropriate:

- switch on the underlying integer value;
- one block per case;
- default to `else` or unreachable if exhaustiveness is trusted;
- merge only for falling-through case bodies.

### Tagged-union `match`

- evaluate the union once;
- extract/load tag;
- switch on tag;
- in a variant block, obtain payload address;
- bind payload by value or address according to source semantics;
- emit body;
- branch to merge when body falls through.

The frontend should already have checked exhaustiveness and variant ownership.

### Logical `&&` and `||`

Use control flow and a PHI node:

For `a && b`:

- evaluate `a`;
- branch to RHS if true, false-result block otherwise;
- evaluate `b` only in RHS;
- merge with `LLVMBuildPhi`.

For `a || b`:

- branch to true-result block if `a` is true, RHS otherwise;
- evaluate `b` only in RHS;
- merge with PHI.

Use `LLVMAddIncoming` to add the incoming values and blocks.

This replaces `GeneratedExpression.Prelude`.

### Error-union return

Preserve the current representation:

```text
{ value, error_code }
```

Success return:

- insert returned value;
- insert zero error code;
- return aggregate.

Error return:

- insert zero or placeholder value;
- insert declared error code;
- return aggregate.

Do not read an error union's value field until its error field has been checked,
unless current language semantics explicitly permit unchecked extraction.

### `catch`

The current lowering:

1. evaluates the error union once;
2. extracts error;
3. enters catch body if nonzero;
4. exposes the error code binding;
5. otherwise yields the value.

LLVM lowering:

- emit left operand;
- extract error and value;
- compare error against zero;
- branch to catch or success;
- bind error code in catch scope;
- emit catch body;
- if catch body falls through and the expression needs a value, define the
  language behavior explicitly.

The existing C lowering returns the original `.value` after a falling-through
catch body. That value may be a zero placeholder. Preserve this only if it is
the intended language rule; otherwise require catch bodies to terminate or
produce a replacement value in a future semantic change.

No LLVM exception handling constructs are needed. Zorb error unions are normal
values, not native exceptions.

### `sizeof`

Prefer target-data computation for compile-time folding:

- `LLVMABISizeOfType` for ABI allocation size;
- `LLVMStoreSizeOfType` when storage size is intended;
- `LLVMSizeOfTypeInBits` for bit size.

The current C `sizeof` includes ABI padding. `LLVMABISizeOfType` is the closest
match.

If a runtime LLVM constant expression is needed, `LLVMSizeOf` is available, but
frontend constant folding is preferable.

### Inline assembly

Use `LLVMGetInlineAsm` and call it with `LLVMBuildCall2`.

Map:

- assembly template;
- constraints string;
- side-effect flag;
- stack-alignment flag;
- dialect;
- operand values;
- return type for outputs.

The current AST permits multiple output operands. LLVM inline assembly models
multiple outputs as an aggregate return. Extract each result and store it to
the corresponding output address.

Constraint compatibility is target-specific. Preserve source diagnostics and
verify generated IR.

### Compile errors

`Builtin.CompileError` currently becomes a C preprocessor `#error`. In a direct
LLVM backend it should be rejected before module emission with a source-located
diagnostic. There is no reason to encode it in LLVM IR.

## Attributes and ABI Mapping

### `noinline`

- obtain the enum attribute kind ID for `"noinline"` with
  `LLVMGetEnumAttributeKindForName`;
- create with `LLVMCreateEnumAttribute`;
- attach at the function attribute index with `LLVMAddAttributeAtIndex`.

### `noclone`

LLVM's attribute availability and spelling must be checked for the selected
LLVM version. Do not assume GCC's `noclone` maps one-to-one. If unsupported,
diagnose or document it as ignored only after deciding language semantics.

### `section`

Use `LLVMSetSection` on functions and globals.

### `align`

Use `LLVMSetAlignment` where the C API supports the relevant value. Validate
power-of-two and target limits before calling LLVM.

### `volatile`

Volatility belongs on load and store instructions, not the LLVM type. Mark
loads and stores with LLVM's volatility setter. A `volatile` source variable
must retain that property in its address metadata so every access is emitted
correctly.

### `abi(sysv)` and `abi(ms)`

Map to LLVM calling convention IDs on:

- function declarations/definitions;
- every direct and indirect call site;
- function pointer type compatibility in source metadata.

Confirm the exact convention IDs from LLVM headers for LLVM 20. Do not embed
unverified magic numbers in lowering code.

### Export

Define exact linkage and visibility behavior:

- internal declarations: internal or private linkage;
- exported Zorb declarations: external linkage;
- extern declarations: external declarations without initializers or bodies;
- generic instantiations: link-once ODR or internal linkage depending on
  cross-module strategy.

The current compiler emits one module, so internal linkage is simplest for
non-exported monomorphizations.

## Name Mangling

LLVM does not require escaping C keywords. A new mangling scheme can be simpler
but must remain:

- deterministic;
- injective for all valid source names and type arguments;
- stable across runs;
- independent of hash-map iteration order;
- explicit about exported ABI names;
- able to encode namespaces and generic arguments;
- versioned if symbol stability matters.

Recommended model:

```text
_Zorb<version>_<kind>_<length><segment>..._<type-encoding>
```

Length-prefix segments instead of replacing punctuation. Keep exact names for
extern declarations and explicitly exported C ABI functions where required.

Do not use LLVM temporary value names as semantic identities.

## Generic Monomorphization

### Canonical keys

Struct instantiation key:

```text
{ generic_struct_symbol_id, ordered concrete_type_ids }
```

Function instantiation key:

```text
{ generic_function_symbol_id, ordered concrete_type_ids }
```

### Work queue

Use a queue and cache:

1. requesting an instantiation interns its key;
2. create its declaration shell immediately;
3. enqueue body/type completion once;
4. lowering may enqueue more instantiations;
5. process until the queue reaches a fixed point.

This handles recursive generic references without text-generation recursion.

### Substitution

Carry a mapping from generic parameter ID to concrete type ID. Type lowering
resolves through this mapping. Expression and statement nodes remain immutable.

## External C Interoperability

`import c "header.h"` currently affects emitted includes, while source-level
extern declarations provide callable names. Direct LLVM emission cannot rely on
a C compiler seeing a header.

Required policy:

- `extern fn` must fully describe the function signature and calling
  convention;
- `extern type` must define whether it is an opaque value type or only legal
  behind pointers;
- constants and macros from headers are not automatically available;
- C struct-by-value ABI requires exact layout and target ABI classification;
- variadic functions require explicit language and backend support;
- symbol spelling and platform decoration must be tested.

The header path may remain useful for a separate validation or binding
generation tool, but it has no direct LLVM IR meaning.

## Runtime and Entry-Point Mapping

### Hosted Linux and Windows

The module should declare or define the platform entry symbol expected by the
linker:

- hosted `_start` source behavior can still be implemented with a generated
  `main` shim;
- preserve the current renaming of user `_start` and `main` when both exist;
- return a defined integer status from generated `main`.

### Freestanding Linux

Emit `_start` directly and avoid libc dependencies.

The current C backend embeds target-specific syscall inline assembly. Options:

1. emit a private LLVM function containing inline assembly;
2. provide a tiny target-specific runtime object linked with generated objects;
3. call platform symbols only where a runtime is guaranteed.

Recommended approach is a small runtime object per target. It isolates fragile
assembly and ABI details from general IR lowering and can be tested separately.

### Bare metal x86_64

- emit an object suitable for the existing linker script;
- preserve requested sections and alignments;
- avoid host runtime references;
- preserve or generate `_start`;
- keep linking as a separate `ld` or linker-driver step initially.

### Slice bounds failure

Represent `__zorb_slice_oob` as a runtime function:

- no parameters;
- no return;
- `noreturn` and `cold`;
- target-specific implementation;
- call followed by `unreachable`.

### Platform builtins

`Builtin.IsLinux`, `Builtin.IsWindows`, `Builtin.IsBareMetal`,
`Builtin.IsX86_64`, and `Builtin.IsAArch64` should be constants derived from the
requested target triple and target mode.

Do not derive them from the host running the compiler.

## Object Emission and Linking

The current C backend delegates both ABI lowering and final linking to GCC,
Clang, or MSVC-compatible drivers. The LLVM backend should initially emit object
files and keep linking external.

Suggested flow:

```text
Zorb source
  -> C# parse and semantic checking
  -> typed backend IR
  -> Zig LLVM backend
  -> target object file
  -> platform linker or compiler driver
  -> executable or kernel ELF
```

### Linux hosted

Use `cc`, `clang`, or the selected linker driver to link startup objects and
libc as needed.

### Linux freestanding

Use explicit entry point and no standard libraries. Preserve current native
flags behavior carefully; raw flags are an escape hatch and should remain
outside LLVM IR generation.

### Windows

LLVM object format and calling conventions must match the selected linker and
runtime. Decide between:

- MSVC ABI plus `lld-link`/`link.exe`;
- MinGW ABI plus GNU-style linker driver.

Do not treat both as one `host-windows` ABI.

### Bare metal

Continue using the linker script and explicit object linking. LLVM target
machine relocation model, code model, target features, and freestanding
assumptions must be configured for kernel code.

## Validation and Testing Strategy

### Current backend coverage

LLVM is now the default backend. Every semantically successful fixture and
example emits verified LLVM IR, and every runtime fixture supported by the
current host is built and executed through LLVM.

The old `expect-generated*.txt` C fragment snapshots and GCC/QEMU runtime path
have been removed. Backend tests now use:

- semantic IR assertions for key operations;
- verifier success;
- target object emission success;
- runtime behavior;
- limited normalized LLVM IR snapshots for difficult lowering cases.

Avoid full-module LLVM IR snapshots for every fixture. LLVM version upgrades
can change harmless textual details.

The compatibility backend described earlier in this document has now been
removed. The active suite is LLVM-only.

### Required focused test groups

- scalar signed and unsigned arithmetic;
- widening, truncation, pointer/integer casts;
- short-circuit side effects;
- nested `if`, `while`, `for`, `break`, and `continue`;
- switch case evaluation order;
- enum and tagged-union match;
- direct and indirect calls;
- calling conventions;
- function pointer fields;
- arrays by value;
- array-to-slice aliasing;
- slice bounds success and failure;
- struct and explicit layout offsets;
- packed struct size and alignment;
- error-union success and error returns;
- catch bodies that return, break, continue, and fall through;
- generic recursion and duplicate instantiation;
- globals and string pooling;
- hosted and freestanding entry points;
- Linux x86_64 and aarch64 syscalls;
- Windows ABI and symbol names;
- bare-metal sections, linker script, and entry symbol;
- inline assembly constraints and multiple outputs;
- repeated backend invocation without state leakage.

### LLVM verification

Always run module verification in tests. In debug builds, verify after each
function to localize failures.

### Layout parity

For every aggregate fixture, compare:

- frontend expected offsets;
- current C compiler `sizeof`, `_Alignof`, and `offsetof` results;
- LLVM target data ABI size, alignment, and offsets.

Do this for every supported target, not only the host.

### ABI parity

Create small C harnesses that call exported LLVM-generated functions and small
Zorb programs that call C functions. Cover scalars, pointers, structs, slices,
unions, error unions, and function pointers. Aggregate failures should block
declaring C ABI support for that shape.

## Staged Migration Plan

### Stage 0: Freeze and describe current behavior

- identify all codegen fixtures;
- add missing runtime tests for ambiguous behavior;
- record target triples and native toolchains;
- decide boolean, aggregate, and external ABI compatibility goals.

### Stage 1: Define backend IR

- design versioned schema;
- emit it from the C# frontend;
- build a reader and validator in Zig;
- round-trip representative programs without LLVM emission.

### Stage 2: LLVM module skeleton

- integrate generated LLVM C bindings;
- initialize target machine and data layout;
- create module;
- emit empty object;
- verify and dispose all LLVM resources.

### Stage 3: Scalars and functions

- scalar types;
- constants;
- function declarations;
- parameters and returns;
- locals, loads, stores;
- arithmetic, comparisons, casts;
- direct calls.

### Stage 4: Structured control flow

- if/else;
- while and for;
- break and continue;
- short-circuit operators;
- switch;
- PHI handling and terminator discipline.

### Stage 5: Aggregates

- arrays;
- ordinary structs;
- field access;
- aggregate literals and copies;
- slices and bounds checks;
- target-data layout validation.

### Stage 6: Language-specific aggregates

- enums;
- tagged unions;
- match;
- error unions;
- catch.

### Stage 7: Generics and indirect calls

- canonical type interning;
- generic struct queue;
- generic function queue;
- function pointers;
- recursive instantiations.

### Stage 8: Targets and runtime

- hosted Linux;
- freestanding Linux x86_64;
- freestanding Linux aarch64;
- Windows ABI choice;
- bare-metal x86_64;
- inline assembly and runtime objects.

### Stage 9: Optimization and debug information

- optimization pipelines;
- source file and line metadata;
- type debug metadata;
- reproducible object output where feasible.

### Stage 10: Default switch and removal

- LLVM backend is the default;
- the legacy C backend has been removed;
- remaining cleanup should focus on deleting stale migration assumptions and
  any leftover compatibility language.

## `CGenerator.cs` Method-Family Traceability

This table maps the current implementation surface to the proposed owner. It is
intended as a rewrite checklist, not a requirement to preserve the current
method boundaries.

| Current method or state family | Proposed destination | Main change |
| --- | --- | --- |
| `Generate`, `ResetGenerationState` | `backend.zig`, `module_emitter.zig` | One compilation object per invocation; explicit phased pipeline |
| `CollectNodes`, `_parsedFilesByPath`, `_allNodes` | C# frontend and backend IR writer | Flatten imports before invoking Zig |
| `_includes`, `_cHeaders` | backend IR extern metadata or binding tooling | Headers do not participate directly in LLVM IR |
| `BuildCNameMaps`, `AddUniqueCNames`, `AllocateCName` | `names.zig` | Mangle by symbol ID; remove C keyword constraints |
| `FlattenName`, `EncodeNameSegment` | `names.zig` | Length-prefixed, versioned symbol encoding |
| `GetFunctionCName*`, `GetTypeCName*`, `GetMemberCName*` | `names.zig` plus declaration maps | Cache names by stable symbol/type/member ID |
| `GetGlobalVariableCName`, `GetVariableCName` | `names.zig` | Separate source identity, linkage name, and local debug name |
| `RegisterLocalCName`, `GetLocalCName` | `function_emitter.zig` | Locals keyed by symbol ID; LLVM names are optional diagnostics |
| `MapType`, `MapTypeForSizeof` | `types.zig` | Return LLVM type plus source metadata, not C text |
| `GetResultTypeName`, `GenerateResultStruct` | `types.zig` | Intern error-union type by canonical inner type ID |
| `GetSliceStructName`, `GenerateSliceStruct` | `types.zig` | Intern slice representation by source element type |
| `PreGenerateSliceType*` | type discovery work queue | Discovery reaches a fixed point before body emission |
| `PreGenerateGenericStructType*` | `generics.zig` | Canonical instantiation queue |
| `GenerateGenericStruct`, `GenerateStructDependency` | `generics.zig`, `types.zig` | Declare named shells first, then complete bodies |
| `BuildTypeSubstitutions`, `SubstituteTypeParameters` | `generics.zig` | Resolve through an immutable substitution environment |
| `InstantiateStruct`, `InstantiateFunction` | removed as cloning operations | Lower original declaration under a concrete environment |
| `CloneStatement`, `CloneExpr`, `CopyNode` | removed | Backend IR nodes remain immutable |
| `IsConcreteType` | backend IR validation and type interner | Concrete type status is explicit or canonical |
| `ResolveConcreteStructForLayout` | `layout.zig` | Resolve by type ID and validate through target data |
| `GenerateStruct` | `types.zig`, `layout.zig` | Named struct body plus source-field index map |
| `GenerateEnum` | `types.zig`, `module_emitter.zig` | Nominal source type over an LLVM integer type |
| `GenerateUnion`, union tag helpers | `types.zig`, `layout.zig` | Explicit tag and aligned payload storage |
| `GenerateFunction`, `GenerateParameterList` | declaration phase plus `function_emitter.zig` | Declare first; emit body in a fresh local context |
| `GenerateStatement`, `GenerateStatementBlock` | `function_emitter.zig` | Tagged-union dispatch with direct instruction emission |
| `GenerateExpression*`, `WrapStatementExpression` | `expression.zig` | Return typed value/address; no C prelude text |
| `GeneratedExpression.Prelude` | explicit basic blocks | Control flow becomes part of the CFG |
| `GetExprType`, `GetVarType`, `GetStructFieldType` | backend IR typed nodes and type table | No backend type rediscovery |
| `GetCallFunctionType`, `GetCallParameterTypes` | resolved call record in backend IR | Direct reference to checked function type |
| `TryGetDirectFunctionSymbol` | backend IR symbol ID | No string-based fallback lookup |
| `CoerceExpressionToTargetType` | typed coercion emitter | Select exact LLVM extension, truncation, GEP, or aggregate operation |
| `AppendArrayCopyStatements` | aggregate store or `LLVMBuildMemCpy` | Target-data size/alignment and explicit alias rules |
| `GenerateMatchStatement`, case helpers | CFG lowering | LLVM switch or ordered branches |
| `TryGetPlatformPreprocessorCondition` | frontend target folding | No preprocessor in LLVM mode |
| `AppendBuiltinDefine` | target constants | Constants come from target configuration |
| `GenerateAsmOperands` | inline assembly lowering | Build constraint string and aggregate result |
| `GenerateHostedMainShim` | `runtime.zig` | Generate LLVM function and calls |
| `GenerateFreestandingMainShim` | target runtime module | Prefer tested target runtime object |
| `HasHostedStartShim`, `HasFreestandingMainShim` | entry policy | Enum-based target/entry configuration |
| `GetHostedStartFunction`, `GetFreestandingMainFunction` | backend IR entry symbol IDs | Resolve before code generation |
| `GetVariableAttributeSuffix` | attribute lowering | Set LLVM section/alignment/volatility directly |
| `EscapeCString` | diagnostic/name/string literal utilities | Escape only textual output; literals remain byte arrays |
| `AppendIndentedBlock*` | removed | No generated C indentation |
| `FormatConditionExpression` | removed | Conditions are normalized to `i1` |
| `NewTemp` | optional IR-readable naming | Correctness must not depend on generated names |
| `CloneLocalVars`, `RestoreLocalVars` | lexical scope stack | Push/pop binding ranges with `defer` |
| `_continueTargets` | loop target stack | Store LLVM break and continue blocks |
| `_catchErrorVars` | explicit binding kind in local symbol table | Return of error code uses typed value metadata |
| `_dynamicTypeDefinitions` | named LLVM type cache | No textual deferred definitions |
| `_dynamicGenericFunctionPrototypes` | declaration cache | Function declaration created on first instantiation request |
| `_dynamicGenericFunctions` | generic body work queue | Bodies emitted after declarations exist |
| `_generated*` sets | canonical caches keyed by IDs | Avoid generated-name strings as semantic keys |
| `_insideFunctionBody` | type-level separation | Global and function emitters expose different APIs |
| `_tempCounter` | function-local optional counter | Reset naturally with each function emitter |
| `LinuxSyscallWrapper` | target runtime object or LLVM inline asm | Remove C macro overloading and preprocessor branches |
| `RuntimeFailureHelpers` | target runtime object | Declare one noreturn failure function in generated IR |

## Risks and Decisions That Must Be Resolved

### Aggregate ABI

This is the largest technical risk. C currently handles target ABI lowering.
LLVM IR function signatures using aggregates by value may not match C ABI
without target-specific classification and attributes.

### Boolean ABI

The current backend represents `bool` as `int32_t`. Moving to `i1` across
function boundaries changes ABI.

### Tagged-union layout

LLVM has no C union type. Payload size, alignment, and access rules need an
explicit representation and parity tests.

### Error-union fallthrough after catch

Current generated C can expose a placeholder value after a catch body falls
through. Confirm whether this is intentional.

### External types

An `extern type` used by value cannot be emitted correctly without size and
alignment. Restrict opaque extern types to pointers unless layout is supplied.

### Windows target identity

`host-windows` is insufficient to select MSVC versus MinGW ABI and runtime.

### Inline assembly

GCC-style constraints are not automatically portable to LLVM on every target.

### Undefined behavior

Direct LLVM emission removes the C compiler as a semantic buffer. Incorrect
`inbounds`, `nsw`, `nuw`, alignment, aliasing, lifetime, or poison handling can
miscompile optimized programs.

### Debug information

Source locations exist on AST nodes, but full debug information requires
preserving lexical scopes, variables, types, and file tables in backend IR.

## Concrete Design Rules

1. Do not port `CGenerator.cs` line by line.
2. Keep source types and LLVM types as separate abstractions.
3. Require checked expression types in backend IR.
4. Distinguish addresses from values.
5. Create named aggregate shells before completing bodies.
6. Declare every function before emitting any body.
7. Use explicit basic blocks instead of expression preludes.
8. Stop emitting into a block after a terminator.
9. Never attach LLVM no-wrap or inbounds guarantees without language proof.
10. Derive platform builtins from the requested target.
11. Validate layout through LLVM target data.
12. Keep external linking separate from object emission initially.
13. Do not reintroduce a second code-generation backend without a clear product
    requirement.
14. Treat LLVM verifier failures as backend defects.
15. Use Zig `defer` for every owned LLVM handle and message.
16. Use Zig 0.16 `addTranslateC`, not new `@cImport` usage.
17. Pin and validate the LLVM major version.
18. Do not expose C# object layout through the backend boundary.
19. Use canonical IDs and interning instead of repeated string lookup.
20. Make ABI changes explicit, documented, and fixture-tested.

## Initial Deliverable Definition

The first implementation milestone should not be "all of `CGenerator.cs` in
Zig." It should be:

- a versioned typed backend IR for one semantically checked module;
- a Zig 0.16 executable that reads it;
- LLVM 20 C bindings generated by the Zig build;
- verified LLVM IR for scalar functions;
- target object emission for host Linux x86_64;
- LLVM verifier and runtime tests for the migrated feature set;
- complete ownership cleanup under a leak-checking allocator.

That milestone establishes the architecture and the riskiest integration
points without prematurely committing to every language feature or target ABI.
