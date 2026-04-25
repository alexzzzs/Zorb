# Zorb Semantics

This document describes the target semantics for the current Zorb compiler line in this repository.

## Status

- This spec is the source of truth for semantic tightening work.
- The compiler should move toward this document.
- Current language behavior should be described here directly rather than deferred to implementation-shaped shortcuts.

## Source Files

- A Zorb program is compiled from one entry source file.
- Additional source files may be loaded through `import`.
- Source locations are tracked as file, line, column, and token-length metadata on AST nodes.

## Lexical Structure

### Whitespace

- Any Unicode whitespace recognized by `char.IsWhiteSpace` is ignored except inside string literals.
- Newlines are not statement terminators.

### Comments

- `//` starts a line comment.
- A line comment continues until the next newline.
- There are no block comments.

### Identifiers

- Identifiers start with a letter or `_`.
- Remaining characters may be letters, digits, or `_`.
- Qualified names are written with `.` between identifiers, for example `std.io.println`.

### Keywords

The lexer recognizes these reserved words:

- `fn`
- `import`
- `as`
- `if`
- `else`
- `while`
- `for`
- `switch`
- `case`
- `return`
- `struct`
- `cast`
- `extern`
- `abi`
- `align`
- `noinline`
- `noclone`
- `volatile`
- `catch`
- `const`
- `continue`
- `break`
- `true`
- `false`
- `error`
- `export`
- `builtin` (case-insensitive in the lexer)

Special note:

- `error Name = <integer-literal>` declares an error code.
- `error.Name` refers to a visible error declaration named `Name`.

### Punctuation And Operators

The lexer recognizes:

- Braces: `{` `}`
- Parentheses: `(` `)`
- Brackets: `[` `]`
- Separators: `,` `:` `;` `.`
- Assignment: `=`
- Arithmetic and bitwise operators: `+` `-` `*` `/` `%` `&` `|` `^`
- Logical operators: `&&` `||`
- Shifts: `<<` `>>`
- Comparisons: `>` `<` `>=` `<=` `==` `!=`
- Type arrow: `->`
- Error-union marker: `!`

Unary operators:

- `&expr` takes the address of an expression.
- `-expr` negates a numeric expression.
- `!expr` negates a boolean expression.

### Number Literals

- Decimal integer literals are supported.
- Hex integer literals of the form `0x...` or `0X...` are supported.
- All parsed numeric literals are currently represented as signed 64-bit integers in the AST.

### Constant Integer Evaluation

- Constant integer evaluation applies in every compile-time-only integer context.
- Those contexts currently include:
  - array size expressions
  - struct field array sizes
  - `align(...)` attribute arguments
  - `offset(...)` attribute arguments
  - folded global integer initializers
- Constant integer evaluation rejects division by zero and `i64` overflow.

### String Literals

- Double-quoted string literals are supported.
- Supported escape sequences are `\"`, `\\`, `\n`, `\r`, `\t`, and `\0`.
- Any other `\x`-style escape is a lexer error.
- Unterminated strings are a lexer error.

## Top-Level Items

A source file may contain:

- `import`
- `export` declarations
- `error` declarations
- global variable declarations
- `const` global variable declarations
- `struct` declarations
- function declarations
- `extern fn` declarations
- top-level attributes applied to functions or variable declarations

Empty top-level semicolons and stray `}` are skipped by the parser.

## Imports

### Syntax

```text
import "path/to/file.zorb"
import "path/to/file.zorb" as alias
import c "header.h"
```

### Meaning

- `import "..."` loads another Zorb source file.
- Import paths are resolved relative to the importing file unless already absolute.
- Imports are deduplicated by canonical full path during semantic checking and code generation.
- `import "path.zorb" as alias` creates a synthetic outer namespace for that file's exports.
- Alias qualification preserves the exported name beneath the alias:
  `answer` becomes `alias.answer`, and `math.add` becomes `alias.math.add`.
- Aliased imports do not inject unqualified exported names into the importing file.
- `import c "header.h"` registers a C header for code generation and does not participate in semantic symbol loading.

### Visibility

- Only exported functions, structs, and global variables from an imported file become visible in the importing file.
- For `import "path.zorb" as alias`, imported exports are visible only through the alias-qualified form.
- Visibility is file-oriented, not package-oriented.
- Imported names are not re-exported transitively through another imported file.
- Imported parse and semantic errors are reported during semantic processing of the importing file.

## Types

### Built-In Scalar Type Names

The compiler treats these names as built-in scalar types:

- `i8`
- `i16`
- `i32`
- `i64`
- `u8`
- `u16`
- `u32`
- `u64`
- `bool`
- `void`
- `string`

Notes:

- `bool` exists semantically and boolean literals have type `bool`.
- `string` is a distinct source-level type. It is not implicitly interchangeable with pointer or integer types.

### User Types

- A `struct` declaration introduces a nominal type.
- Struct names may be qualified with namespace path segments, for example `std.io.Writer`.

### Pointer Types

- `*T` is a pointer to `T`.
- Multiple levels are allowed: `**T`, `***T`, and so on.
- Pointer level is preserved semantically and in code generation.

### Array Types

- `[N]T` is a fixed-size array type.
- `N` must be a constant integer expression that resolves at semantic-check time.
- See [Constant Integer Evaluation](#constant-integer-evaluation) for the shared compile-time rules that govern `N`.
- Arrays may appear in variable declarations and struct fields.
- Struct field array sizes are resolved through the same constant-integer rules as variable declarations.
- Arrays do not implicitly decay to pointers in general expression or assignment contexts.
- Arrays decay to `*T` only when passed to a function parameter of type `*T`.
- Arrays also coerce to `[]T` when assigned, initialized, returned, used in struct literals, or passed to a function expecting that matching slice type.
- `&array` is an explicit way to obtain a pointer to the first element, with type `*T`.
- Indexing an array or pointer produces an element value of type `T`.

### Slice Types

- `[]T` is a non-owning slice of contiguous elements of type `T`.
- A slice value exposes `.ptr` with type `*T` and `.len` with type `i64`.
- Indexing a slice with `slice[index]` produces an element value of type `T`.
- Slice indexing performs runtime bounds checks and traps on out-of-bounds access before reading or writing the backing storage.
- Slices alias their backing storage, so writes through `slice[index]` or `slice.ptr[...]` mutate the underlying array or buffer view.
- Slice fields are ordinary writable fields today, including `.len` and `.ptr`.
- Slices currently lower to generated C structs containing `ptr` and `len` fields.

### Function Types

```text
fn(T1, T2) -> R
fn(T1, T2)
```

- Function types are first-class type nodes in the compiler.
- Omitted return type means `void`.
- Function types may be used in variable declarations and struct fields.

Function declarations may spell error-union returns in either of these equivalent forms:

```text
fn name(...) !T
fn name(...) -> !T
```

### Error Unions

```text
!T
```

- `!T` represents a result-like type carrying either a success value of `T` or an error code.
- Error unions are nominally represented in generated C as `struct Result_<...> { T value; int32_t error; }`.
- Pointer depth inside `T` is preserved.

## Declarations

### Variable Declarations

Syntax:

```text
export fn name(...) -> T { ... }
export struct Name { ... }
export error Name = 5
error Fail = 5
name: Type
name: Type = expr
const name: Type = expr
```

### Loop Control

- `continue` skips to the next iteration of the nearest enclosing `while` or `for`.
- `break` exits the nearest enclosing `while` or `for`.
- `continue` is valid only inside a `while` or `for` body.
- `break` is valid only inside a `while` or `for` body.
- In a `for` loop, `continue` still runs the update clause before the next condition check.

Meaning:

- Error declarations define globally visible error codes.
- `error Name = value` lowers through the implementation to a generated symbol named `Error_Name`.
- Variables are explicitly typed.
- There is no local type inference.
- A variable may be declared without an initializer.
- `const` declarations must include an initializer.
- Global initializers may not contain `catch` expressions.
- `const` marks the generated C declaration as `const`.

Qualified names:

- Global and local declarations may use dotted names, for example `std.io.stdout: i32`.
- Code generation flattens dotted variable names to C identifiers by replacing `.` with `_` where needed.

### Struct Declarations

Syntax:

```text
struct Name {
    field: Type,
    other: Type,
}

[packed]
struct PackedName {
    field: Type,
}

[layout(explicit)]
struct Table {
    [offset(0)] header: u32,
    [offset(8)] ptr: u64,
}
```

Meaning:

- Struct fields are named and typed.
- Trailing commas are effectively tolerated because field parsing stops at `}`.
- Field types must name built-in numeric types, `void`, `string`, function types, or known structs.
- Struct declarations also accept attributes.
- Recognized struct attributes are `packed`, `align(N)`, and `layout(explicit)`.
- Recognized struct-field attributes are `offset(N)`.
- `layout(explicit)` requires every field to declare `offset(N)`.
- `layout(explicit)` currently means byte-precise packed layout; code generation inserts explicit padding fields as needed and emits `_Static_assert` checks for the final field offsets.
- Byte-precise layout currently rejects field types that do not have a stable compile-time C layout in the compiler model, such as function types, slices, and error unions.

### Function Declarations

Syntax:

```text
fn name(p: T, q: U) -> R { ... }
extern fn name(p: T, q: U) -> R
```

Meaning:

- Functions are declared by name, parameter list, and optional return type.
- Omitted return type means `void`.
- `extern fn` declares a function without a body.
- Names may be qualified with dotted paths.

### Attributes

Attributes are written in square brackets:

```text
[noinline]
[noclone]
[align(16)]
[section(".text.boot")]
[abi(sysv)]
[volatile]
[packed]
[layout(explicit)]
```

Current behavior:

- Attributes may appear before functions.
- Attributes may also appear before variable declarations.
- Attributes may also appear before struct declarations.
- Struct field attributes appear before the field name inside a struct body.
- Recognized function attributes are `noinline`, `noclone`, `align(N)`, `section("name")`, and `abi(name)`.
- Recognized variable attributes are `align(N)`, `section("name")`, and `volatile`.
- Recognized struct attributes are `packed`, `align(N)`, and `layout(explicit)`.
- Recognized struct-field attributes are `offset(N)`.
- `N` in `align(N)` and `offset(N)` may be any constant integer expression that resolves during semantic checking. See [Constant Integer Evaluation](#constant-integer-evaluation).
- `abi(name)` currently accepts `sysv`, `sysv64`, `ms`, and `win64`.
- `section("name")` is currently intended for functions and global variables.
- Unknown attributes are parser errors.
- Attributes are lowered to GCC-style `__attribute__` annotations in generated C.

## Statements

The current statement forms are:

- variable declaration
- assignment
- expression statement
- `if`
- `while`
- `for`
- `switch`
- `return`
- `continue`
- `break`
- inline `asm`

`break` exits the nearest enclosing `while` or `for`.

There is no dedicated block statement syntax beyond the bodies of `if`, `else`, `while`, `for`, `switch` cases, and functions.

### For Statements

Syntax:

```text
for init; condition; update { ... }
for ; condition; update { ... }
for init; ; update { ... }
for ; ; { ... }
```

Meaning:

- `for` headers are written without parentheses.
- The initializer and update clauses may be a variable declaration, assignment, or expression statement.
- The initializer clause runs once before the loop starts.
- The condition clause is evaluated before each iteration. If omitted, the loop behaves as if the condition were always `true`.
- The update clause runs after each completed iteration and also after `continue` before the next condition check.

### Switch Statements

Syntax:

```text
switch expr {
    case value { ... }
    case other { ... }
    else { ... }
}
```

Meaning:

- `switch` compares the controlling expression against each `case` in source order.
- The controlling expression is evaluated once.
- `else` is optional and runs only when no earlier case matches.
- Case bodies do not fall through into later cases.
- `switch` currently accepts numeric and `bool` controlling expressions.
- Case expressions must be equality-comparable to the controlling expression type.
- `break` retains its loop-only meaning and is not required to exit a `switch` case.

## Expressions

### Primary Expressions

- identifier
- number literal
- string literal
- parenthesized expression
- unary `&expr`
- unary `-expr`
- unary `!expr`
- `cast(Type, expr)`
- boolean literals `true` and `false`
- typed struct literals `Type{ field: expr, ... }`
- typed array literals `[N]T{ expr, ... }`
- builtins `Builtin.IsLinux` and `Builtin.IsWindows`
- error literals `error.Name`

### Postfix Expressions

Postfix parsing supports:

- field access: `expr.field`
- indexing: `expr[index]`
- call: `expr(...)`

Special case:

- `error.Name` parses as an `ErrorExpr`, not ordinary field access.
- `error.Name` refers to the visible declaration `error Name = ...`.

### Binary Operators

The parser supports these binary operators:

- multiplicative: `*` `/` `%`
- additive: `+` `-`
- shifts: `<<` `>>`
- bitwise-and: `&`
- bitwise-xor: `^`
- bitwise-or: `|`
- comparisons: `>` `<` `>=` `<=` `==` `!=`
- logical-and: `&&`
- logical-or: `||`

### Operator Precedence

From highest to lowest:

1. postfix: call, field access, indexing
2. unary: `&`, unary `-`, unary `!`
3. `*` `/` `%`
4. `+` `-`
5. `<<` `>>`
6. `&`
7. `^`
8. `|`
9. comparisons and equality: `>` `<` `>=` `<=` `==` `!=`
10. logical-and: `&&`
11. logical-or: `||`
12. postfix `catch`

Parsing notes:

- Binary parsing is precedence-based and left-grouping in practice for supported operators.
- `catch` binds after parsing the expression on its left.

### Catch Expressions

Syntax:

```text
expr catch |err| { ... }
```

Meaning:

- The left side must produce an error union in any meaningful program.
- The catch body is parsed as a list of statements.
- The error variable is introduced inside the catch body as an `i32`.
- Inside function bodies, catch expressions may appear in general expression position.
- Global initializers may not contain `catch` expressions.

## Builtins

Current builtins:

- `Builtin.IsLinux`
- `Builtin.IsWindows`
- `Builtin.IsBareMetal`
- `Builtin.IsX86_64`
- `Builtin.IsAArch64`

Meaning:

- These are compile-time-known source constructs lowered to C preprocessor-backed constants.
- They describe the target platform selected by the generated C compilation environment, not the host OS running the Zorb compiler.
- Semantically they behave as `bool` values.

## Name Resolution And Visibility

### Scopes

- The symbol table has a global scope and nested local scopes.
- Lookup now prefers the innermost active scope.
- Function parameters live in the function-local scope.
- Nested statement blocks created by `if`, `else`, `while`, `for`, and `switch` cases create nested scopes in semantic analysis.

### Shadowing

- A local declaration may shadow an outer declaration.
- The innermost binding is the one used for semantic lookup.

### Visibility Checks

- A symbol may exist in the symbol table but still fail visibility checks if it was not made visible in the current file/import scope.
- Imported symbols are made visible by semantic import processing.
- Builtins like `syscall`, `Builtin.IsLinux`, `Builtin.IsWindows`, `Builtin.IsBareMetal`, `Builtin.IsX86_64`, and `Builtin.IsAArch64` are inserted as visible built-in symbols.

## Type Checking

The type system is nominal and explicit. Implicit conversions are intentionally limited.

### Numeric Types

The checker treats these as numeric:

- `i8`
- `i16`
- `i32`
- `i64`
- `u8`
- `u16`
- `u32`
- `u64`

Notes:

- `bool` is built in but is not included in the current numeric-type set used by arithmetic checks.
- Number literals have type `i64`.

### Conditions

- `if`, `while`, and `for` conditions must have type `bool`.
- Numeric and pointer values are not implicitly truthy in conditions.
- `Builtin.IsLinux`, `Builtin.IsWindows`, `Builtin.IsX86_64`, and `Builtin.IsAArch64` may be used directly as conditions because they are `bool`.
- To branch on a numeric or pointer expression, compare it explicitly, for example `value != 0`.

### Binary Operator Checking

For arithmetic and bitwise operators:

- both operands must be numeric types

For logical operators:

- `&&` and `||` require `bool` on both sides
- code generation preserves short-circuit evaluation order

For relational comparisons:

- `>` `<` `>=` `<=` require numeric operands on both sides

For equality:

- `==` and `!=` allow:
  - numeric vs numeric
  - `bool` vs `bool`
  - `string` vs `string`
  - pointer vs pointer when the pointer types match exactly

Other equality comparisons are rejected.

### Variable Initialization

When a variable has an initializer:

- the initializer expression is checked
- the initializer type must be assignable to the declared type
- local fixed-size arrays may be initialized from another value of the same array type
- global fixed-size arrays currently require array-literal initializers so code generation can emit static C initializers
- global integer initializers that are constant integer expressions may be folded during semantic checking

### Numeric Conversions

- Number literals have type `i64`.
- Integer literals may initialize or assign to any built-in integer type that can represent the literal value.
- Non-literal integer values may convert implicitly only through exact-match or widening conversions.
- Widening means:
  - signed-to-signed widening, for example `i32 -> i64`
  - unsigned-to-unsigned widening, for example `u8 -> u32`
  - unsigned-to-signed widening when the target is strictly wider, for example `u32 -> i64`
- Narrowing or potentially lossy integer conversions require an explicit `cast(...)`.
- Pointer-to-integer and integer-to-pointer conversions require an explicit `cast(...)`.

### Assignment

For `target = value`:

- both sides are checked
- `value` must be assignable to `target`
- assigning a non-error-union value to an error-union target is rejected with a dedicated diagnostic
- fixed-size arrays assign by value when the source and target have the same array type

### Returns

- A `return` without an expression is valid in a `void`-returning context.
- A `return expr` must be assignable to the function return type.
- Returning `error.Name` is only valid from a function whose return type is an error union.
- For a function returning `!T`, returning a non-error expression requires that expression to be assignable to `T`.

## Assignability

The current assignability relation is:

1. `null` source type is treated as assignable to anything for error-recovery purposes.
2. A source error union is not assignable to a non-error-union target.
3. Same types are assignable.
4. Any pointer source is assignable to `*void`.
5. A non-pointer numeric source is assignable to a non-pointer numeric target only when the conversion is an allowed implicit numeric conversion.
6. Two non-numeric, non-pointer, non-built-in nominal types are assignable only if name and namespace path match.
7. Function types are assignable only if return type and parameter types match structurally.

Important consequence:

- Pointer-to-integer and integer-to-pointer conversion require an explicit `cast(...)`.
- `string` does not implicitly convert to pointer or integer types.
- Numeric conversions are intentionally stricter than earlier compiler behavior.

## Pointer Rules

### Address-Of

- `&expr` produces a pointer to the operand type.
- Taking the address of a pointer increases pointer depth by one.
- Taking the address of an array expression yields an element pointer `*T`, not a distinct pointer-to-array type.
- `&array` is therefore equivalent to the same element-pointer view used for call-position decay.

### Pointer Compatibility

- Pointer argument passing checks pointer level equality when both argument and parameter are pointers.
- Pointer level mismatches in calls are rejected.
- Pointer assignment compatibility is otherwise mostly delegated to the general assignability rules.

### Pointer Arithmetic

- `pointer + integer` and `integer + pointer` produce a pointer of the same type as the pointer operand.
- `pointer - integer` produces a pointer of the same type as the left operand.
- Pointer-to-pointer arithmetic is not supported.
- Other arithmetic operators require numeric operands.

## Arrays

### Declaration

- Arrays are declared with `[N]T`.
- Local arrays may be initialized from array literals or copied from another value of the same array type.
- Global arrays currently require array-literal initializers.

### Indexing

- `expr[index]` is parsed for any postfix expression.
- If the target type is an array, indexing yields the element type.
- If the target type is a slice, indexing yields the slice element type.
- If the target type is a pointer, indexing yields the pointed-to type, decreasing pointer level by one when necessary.

### Field Access

- Normal field access on structs uses the declared struct fields.
- Slice values additionally expose `.ptr` and `.len` as built-in fields.
- No other built-in field names currently exist.

### Decay

- Arrays decay to pointers only in function-call argument position.
- That decay applies only when the parameter type is exactly `*T` for the array element type `T`.
- Arrays do not decay to `*void` implicitly.
- Outside call position, arrays remain arrays unless explicitly addressed or indexed.

### Slice Coercions

- A fixed-size array `[N]T` may coerce to `[]T` when the element types match exactly.
- That coercion keeps a pointer to the original array storage and initializes the slice length to `N`.
- There is currently no implicit pointer-to-slice or string-to-slice coercion.

### Literals

- An array literal is written as `[N]T{ expr, ... }`.
- The literal must contain exactly `N` elements.
- Each element must be assignable to `T`.
- In declaration initializers, array literals lower to C array initializer lists.

### Copy Semantics

- Arrays assign by value only when source and target types match exactly, including length and element type.
- Array copies lower to explicit element-by-element C loops rather than raw C array assignment.
- Copying an array does not create an alias to the original storage.

## Strings

- A string literal has source-level type `string`.
- In generated C, `string` maps to `char*`.
- A variable initialized directly from a string literal may be emitted as a C character array depending on the declared type and codegen path.
- `string` is only implicitly assignable to `string`.
- Converting `string` to pointer or integer representations requires an explicit `cast(...)`.

## Error Unions

### Source-Level Meaning

- `!T` represents either a success value of `T` or an error code.
- `error.Name` constructs an error code expression with type `i32`.
- `error.Name` is backed by a visible declaration of the form `error Name = <integer-literal>`.

### Semantic Rules

- `!T` is not assignable to `T`.
- Error declarations must be written as `error Name = <integer-literal>`.
- Distinct error declarations must use distinct integer values across the visible program.
- Using `error.Name` without a visible matching declaration is rejected.
- Returning `error.Name` from a non-error-union function is rejected.
- Casting an error union to a non-pointer, non-error-union target is rejected.
- The checker encourages explicit unwrapping through `.value`.

### Code Generation Model

- `!T` lowers to `struct Result_<T>`.
- The generated struct always contains:
  - `value`
  - `error`
- `error` is an `int32_t`.
- For `return error.Name`, the generated result has `.error = Error_Name`.
- For success returns, the generated result has `.error = 0`.

### Catch

- In `x: T = call() catch |err| { ... }`, code generation stores the result, binds `err` to the `.error` field, executes the catch body when nonzero, and then assigns `.value` into `x`.

## Function Calls

### Direct Calls

- `name(...)` resolves to a function by name.
- Qualified names may resolve to imported functions or namespaced declarations.

### Indirect Calls

- `expr(...)` may call a function-typed expression.
- Struct fields containing function types are callable through field access.

### Arity

- Calls must match parameter count exactly.
- `syscall` is a special built-in exception and may accept fewer arguments than its declared maximum arity check path uses.

### Argument Checking

- Each argument must be assignable to the corresponding parameter type.
- If both parameter and argument are pointers, pointer levels must match exactly.

## Inline Assembly

Syntax:

```text
asm {
    "code"
    : "constraint"(expr), ...
    : "constraint"(expr), ...
    : "clobber", ...
}
```

Meaning:

- Inline assembly is parsed as an `AsmStatementNode`.
- Constraint strings are preserved verbatim and attached to typed operand expressions.
- Input operands must type-check and have scalar or pointer type.
- Output operands must type-check, have scalar or pointer type, and be assignable expressions.
- Arrays, function values, error unions, and `void` values are not valid asm operands.
- Catch expressions are not valid inside asm operands.
- Clobbers are string literals emitted verbatim in generated C.

## Built-In Symbols

The semantic checker injects:

- `syscall: fn(i64, i64, i64, i64, i64, i64) -> i64`
- `Builtin.IsLinux: bool`
- `Builtin.IsWindows: bool`
- `Builtin.IsBareMetal: bool`
- `Builtin.IsX86_64: bool`
- `Builtin.IsAArch64: bool`

It also seeds built-in scalar type names into the symbol table for visibility and lookup purposes.

## Code Generation Model

The current compiler lowers Zorb to C.

### Name Lowering

- Qualified names are flattened with `_`.
- Dotted variable references used as global-like qualified names are flattened when emitted in C.

### Platform Lowering

- Linux syscall support code is emitted only for targets that use the Linux syscall ABI.
- On Linux syscall targets, the generated syscall wrapper currently has x86_64 and AArch64 inline-assembly implementations.
- `Builtin.IsLinux`, `Builtin.IsWindows`, `Builtin.IsBareMetal`, `Builtin.IsX86_64`, and `Builtin.IsAArch64` lower to preprocessor-defined boolean-like constants (`0` or `1` in generated C).

### Entry Point

- On hosted targets, a top-level `_start` becomes the program entry via a generated `main` shim.
- If hosted source defines both `_start` and `main`, the compiler keeps both by renaming the user functions internally and reserving the real C `main` for the generated shim.
- Passing `-nostdlib` preserves `_start` for the `freestanding-linux` target.
- `bare-metal-x86_64` also preserves `_start`, but it is a separate target with no Linux syscall shim, no host `run` support, and a linker-script-driven kernel link step.

## Error Handling And Recovery

- Parser expectation failures report diagnostics and continue by advancing.
- Top-level parser recovery uses synchronization on declaration and statement-start tokens.
- Semantic checking accumulates diagnostics rather than stopping immediately.

## Resolved Current-Line Decisions

The current compiler line treats these behaviors as settled semantics:

- Conditions require `bool`; numeric and pointer truthiness is rejected.
- Arrays decay only to exact `*T` in call position and do not implicitly decay to `*void`.
- `&array` yields an element pointer `*T`, not a pointer-to-array type.
- `error.Name` resolves through visible `error Name = <integer-literal>` declarations.
- Distinct visible error declarations must use distinct integer values.
- Import aliases are file-oriented and do not inject unqualified imported names.
- Imported names are not re-exported transitively.

## Suggested Process For Evolving This File

For each semantic change:

1. Update this file first or in the same change.
2. Add at least one positive fixture.
3. Add at least one negative fixture when the rule rejects code.
4. Keep implementation shortcuts called out explicitly until they are redesigned.

That discipline is what turns a compiler project into a language.
