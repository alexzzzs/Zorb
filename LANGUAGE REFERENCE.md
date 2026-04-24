# Zorb Language Reference

This reference describes the Zorb language implemented by this repository's current compiler. It is based on the lexer, parser, semantic checker, C generator, standard library, and fixture suite, rather than only on examples.

Zorb is a small ahead-of-time systems language that currently lowers to C. Its current design favors explicit types, limited implicit conversion, direct target access, and a small standard library implemented in Zorb itself.

## Source Files And Compilation Model

A program is compiled from one entry `.zorb` file. Additional Zorb files are loaded through `import`.

The compiler pipeline is:

1. Lex the entry file and imported files.
2. Parse the import graph.
3. Run semantic checking, including import visibility and type checking.
4. Lower the checked AST to C.
5. Optionally compile and run/build the generated C through the compiler driver.

The compiler supports these output targets:

```text
host-linux
freestanding-linux
bare-metal-x86_64
host-windows
```

`build` and `run` default to `freestanding-linux` on Linux and `host-windows` on Windows. Plain C emission defaults to `host-linux`.

## Lexical Structure

### Whitespace

Whitespace is ignored outside string literals. The lexer uses C# `char.IsWhiteSpace`, so ordinary spaces, tabs, and newlines are all separators.

Newlines are not statement terminators. Most statements are separated by their own syntax. Semicolons are only meaningful in `for` loop headers and are skipped at top level.

### Comments

Line comments start with `//` and continue to the next newline.

```zorb
// this is a comment
value: i64 = 10 // this is also a comment
```

There are no block comments.

### Identifiers

Identifiers begin with a letter or `_`. Remaining characters may be letters, digits, or `_`.

```text
name
_private
value_123
```

Qualified names are written by joining identifiers with `.`:

```zorb
std.io.print("hello\n")
std.mem.HeapAllocator
```

### Keywords

The lexer reserves:

```text
fn import as if else while for switch case return struct cast extern align catch
const error export continue break true false
```

`builtin` is recognized case-insensitively as the `Builtin` namespace token.

These words are contextual attributes when they appear inside attribute lists:

```text
abi section packed layout offset noinline noclone volatile
```

### Literals

Integer literals are decimal or hexadecimal:

```zorb
decimal: i64 = 123
hex: i64 = 0x7B
```

The parser stores numeric literals as signed 64-bit integers. Decimal and hexadecimal literals outside `i64` range are rejected by parsing or semantic checking.

String literals are double-quoted:

```zorb
msg: string = "hello\n"
```

Supported escapes are:

```text
\"  quote
\\  backslash
\n  newline
\r  carriage return
\t  tab
\0  nul byte
```

Any other escape sequence is a lexer error. A newline before the closing quote is an unterminated string error.

Boolean literals are:

```zorb
true
false
```

## Program Items

A source file may contain:

```text
import declarations
error declarations
global variable declarations
global const declarations
struct declarations
function declarations
extern function declarations
attributes before functions, globals, and structs
export declarations
```

Top-level stray semicolons and stray `}` tokens are skipped by the parser for recovery.

## Imports And Visibility

### Zorb Imports

```zorb
import "std/io.zorb"
import "math.zorb" as math
```

Import paths are resolved relative to the importing file unless absolute. The import graph is canonicalized by full path to avoid duplicate processing.

Only exported declarations from an imported file become visible to the importing file. Imports are not transitive: if `main.zorb` imports `middle.zorb`, and `middle.zorb` imports `leaf.zorb`, `main.zorb` does not automatically see `leaf.zorb` exports through `middle.zorb`.

### Import Aliases

An alias creates an outer namespace for the imported file's exports:

```zorb
import "math.zorb" as math

fn main() {
    x: i64 = math.answer
}
```

Aliased imports do not inject unqualified names. If `math.zorb` exports `answer`, `answer` is not visible directly; `math.answer` is visible.

For already-qualified exported names, the alias preserves the exported name beneath the alias. For example, exported `util.add` is addressed as `alias.util.add`.

### C Header Imports

```zorb
import c "windows.h"
```

This registers a C header for generated C. It does not load Zorb symbols.

## Declarations

### Export

`export` may be used before functions, structs, error declarations, const globals, and global variables:

```zorb
export fn add(a: i64, b: i64) -> i64 {
    return a + b
}

export struct Point {
    x: i64,
    y: i64
}

export error Missing = 100
export answer: i64 = 42
export const limit: i64 = 64
```

`export import ...` is rejected.

### Variables

Variables are explicitly typed:

```zorb
name: Type
name: Type = expr
```

There is no local type inference.

Global variables and local variables use the same declaration shape. Globals may use dotted names:

```zorb
std.io.stdout: i32 = 1
```

Dotted global names are flattened in generated C by replacing `.` with `_`.

### Constants

```zorb
const count: i64 = 4
export const page_size: i64 = 4096
```

`const` declarations must include an initializer. In generated C, constants are emitted with C `const`.

Integer `const` declarations whose initializer is a constant integer expression can be used in compile-time integer contexts such as array sizes, alignments, offsets, and folded global integer initializers.

### Error Declarations

```zorb
error MissingConfig = 200
export error OutOfMemory = 1
```

An error declaration is internally represented as a constant `i32` symbol named `Error_<Name>`. Use errors in expressions with `error.Name`:

```zorb
return error.OutOfMemory
```

Error declarations must use a numeric literal initializer or a unary-negative numeric literal initializer. Visible error declarations must use distinct integer values.

### Structs

```zorb
struct Pair {
    left: i32,
    right: i32
}
```

Structs are nominal types. Field declarations are `name: Type`. Commas separate fields, and a trailing comma is accepted.

Struct names may be qualified:

```zorb
export struct std.mem.HeapAllocator {
    buffer: *u8,
    len: i64,
    pos: i64
}
```

### Functions

```zorb
fn name(param: Type, other: Type) -> ReturnType {
    return value
}

fn no_return_value() {
}
```

The return type is optional. Omitted return type means `void`.

Function names may be qualified:

```zorb
fn std.io.print(msg: string) {
}
```

### Extern Functions

```zorb
extern fn ExitProcess(uExitCode: u32) -> void
extern fn WriteFile(h: i64, buf: *i8, len: u32, written: *u32, overlapped: i64) -> i32
```

`extern fn` declares a function without a body and emits an `extern` C declaration.

## Attributes

Attributes are written before declarations in square brackets:

```zorb
[noinline]
[noinline, noclone]
[align(16)]
[section(".text.boot")]
[abi(sysv)]
[volatile]
[packed]
[layout(explicit)]
```

Unknown attributes are parser errors.

### Function Attributes

Allowed on functions:

```text
noinline
noclone
align(N)
section("name")
abi(sysv)
abi(sysv64)
abi(ms)
abi(win64)
```

`sysv` and `sysv64` lower to C `sysv_abi`. `ms` and `win64` lower to C `ms_abi`.

`noclone` is omitted on Windows codegen because it is GCC-specific.

### Variable Attributes

Allowed on variable declarations:

```text
align(N)
section("name")
volatile
```

`section("name")` is only supported on global variables. Local variables with `section` are rejected.

`volatile` can also be written as a type qualifier:

```zorb
port: volatile *u8
```

### Struct Attributes

Allowed on structs:

```text
packed
align(N)
layout(explicit)
```

Allowed on struct fields:

```text
offset(N)
```

`layout(explicit)` requires every field to declare `[offset(N)]`. It uses byte-precise packed layout and generates C padding fields and `_Static_assert` offset checks.

Explicit byte-precise layout rejects field types whose stable C layout is not modeled by the compiler, including function types, slices, error unions, and `void`.

### Constant Attribute Arguments

`N` in `align(N)` and `offset(N)` is a constant integer expression. It may refer to visible integer constants.

```zorb
const boundary: i64 = 8 * 2

[align(boundary)]
buffer: [64]u8
```

Alignment must be positive and fit in the compiler-supported integer range. Offsets must be non-negative and fit in the compiler-supported byte offset range.

## Types

### Built-In Scalar Types

```text
i8 i16 i32 i64
u8 u16 u32 u64
bool
void
string
```

`bool` is a first-class type. Conditions require `bool`; integers and pointers are not implicitly truthy.

`string` is a source-level type. It maps to `char*` in generated C but does not implicitly convert to pointer or integer types.

### Pointers

```zorb
ptr: *u8
ptr_to_ptr: **u8
```

`*T` is a pointer to `T`. Multiple pointer levels are allowed.

`&expr` takes the address of an expression. Taking the address of a pointer increases pointer depth by one.

Special case: `&array` produces `*T`, a pointer to the first element, not a pointer-to-array type.

### Arrays

```zorb
buf: [32]u8
```

`[N]T` is a fixed-size array. `N` must be a constant integer expression and must be non-negative.

Arrays may be used in variables, fields, parameters, returns, and literals. Postfix array syntax is rejected:

```zorb
// rejected
buf: u8[32]
slice: u8[]
```

Arrays assign by value only when the full array type matches exactly, including length and element type. Local array copies lower to element-by-element loops.

Global arrays must be initialized with array literals.

### Slices

```zorb
view: []u8
```

`[]T` is a non-owning view over contiguous `T` elements. It lowers to a generated C struct with:

```zorb
ptr: *T
len: i64
```

Slice values expose only `.ptr` and `.len`.

Indexing a slice performs runtime bounds checks. If `index < 0` or `index >= slice.len`, generated code calls the runtime trap helper. On Linux that exits with code `1`; on bare metal it halts; otherwise it uses a trap or platform exit.

Fixed arrays coerce to matching slices in assignment, initialization, return, struct literals, and function arguments:

```zorb
buf: [4]u8 = [4]u8{ 1, 2, 3, 4 }
view: []u8 = buf
```

The slice aliases the original array storage.

### Function Types

```zorb
fn(*void) -> void
fn(i64, i64) -> i64
fn(i64)          // returns void
```

Function types may be used in variables, parameters, and struct fields.

```zorb
struct Handler {
    callback: fn(*void) -> void,
    arg: *void
}
```

Function type assignment requires structural equality of parameter and return types.

### Error Unions

```zorb
!T
```

`!T` represents either a success value of type `T` or an error code.

Function declarations may use either spelling:

```zorb
fn open() !i64 {
    return 1
}

fn open2() -> !i64 {
    return 1
}
```

Generated C represents `!T` as a struct containing:

```text
value: T
error: int32_t
```

Returning a success expression from a `!T` function sets `.error = 0`. Returning `error.Name` sets `.error = Error_Name`.

## Statements

Supported statements are:

```text
variable declaration
assignment
expression statement
if / else / else if
while
for
switch
return
continue
break
inline asm
```

There is no standalone block statement outside function and control-flow bodies.

### Assignment

```zorb
x = y
point.x = 10
buf[0] = 1
```

The target and value are type checked. The value must be assignable to the target.

Fixed-size arrays are assignable by value only when source and target array types match exactly:

```zorb
a: [4]u8 = [4]u8{ 1, 2, 3, 4 }
b: [4]u8 = a
```

### If

```zorb
if condition {
    ...
} else if other {
    ...
} else {
    ...
}
```

The condition must have type `bool`.

When an `if` condition is a platform builtin expression, codegen may lower it to C preprocessor conditionals.

### While

```zorb
while condition {
    ...
}
```

The condition must have type `bool`.

### For

```zorb
for init; condition; update {
    ...
}

for ; condition; update {
    ...
}

for ; ; {
    ...
}
```

For loop headers do not use parentheses. The initializer and update may be a variable declaration, assignment, or expression statement. The condition is optional; omitted condition means an infinite loop.

The initializer is scoped to the loop. The update runs after each completed iteration and also after `continue`.

### Switch

```zorb
switch value {
    case 0 {
        return 10
    }
    case 1 {
        return 20
    }
    else {
        return 30
    }
}
```

The switch expression is evaluated once. Cases are tested in source order. There is no fallthrough. `else` is optional and must appear after all `case` branches. Only one `else` branch is allowed.

Switch operands must be numeric or `bool`. Case expressions must be equality-comparable with the switch expression.

`break` keeps its loop-only meaning; it is not a switch-case terminator.

### Return

```zorb
return
return expr
```

`return` without a value is valid for `void` functions.

Non-`void` functions must return a value on all checked paths. If both branches of an `if` or all cases plus `else` of a `switch` return, the compiler treats that control flow as returning.

For `!T` functions:

```zorb
return success_value
return error.Name
```

Returning `error.Name` from a non-error-union function is rejected.

### Continue And Break

```zorb
continue
break
```

Both are valid only inside `while` or `for`. `continue` in a `for` loop runs the update clause before rechecking the condition.

### Inline Assembly

```zorb
asm {
    "instruction"
    : "output_constraint"(target)
    : "input_constraint"(expr)
    : "clobber", "memory"
}
```

Assembly strings are concatenated and emitted into C `__asm__ volatile`.

Input operands must have scalar or pointer type. Output operands must have scalar or pointer type and be assignable expressions: identifier, field expression, or index expression.

Invalid asm operand types include arrays, slices, function values, error unions, and `void`. Catch expressions are not allowed inside asm operands.

## Expressions

### Primary Expressions

Primary forms include:

```text
identifier
integer literal
string literal
true
false
error.Name
Builtin.Name
Builtin.sizeof(Type)
Builtin.CompileError("message")
cast(Type, expr)
typed struct literal
typed array literal
parenthesized expression
unary expression
```

### Postfix Expressions

```zorb
call(args)
expr.field
expr[index]
```

Calls work for named functions, qualified functions, and function-typed expressions.

Field access works on structs, pointers to structs, error-union result structs, global qualified names, and slice `.ptr` / `.len`.

### Struct Literals

```zorb
pair: Pair = Pair{ left: 1, right: 2 }
```

Struct literals are typed. Every field must be initialized exactly once, and no unknown fields are allowed.

A trailing comma is allowed:

```zorb
pair: Pair = Pair{
    left: 1,
    right: 2,
}
```

### Array Literals

```zorb
mask: [4]u8 = [4]u8{ 1, 1, 0, 0 }
```

Array literals are typed. The element count must exactly match the declared array length. Each element must be assignable to the array element type.

### Casts

```zorb
small: i32 = cast(i32, value)
ptr: *u8 = cast(*u8, address)
```

`cast` is required for narrowing numeric conversions, pointer-to-integer conversions, integer-to-pointer conversions, and string-to-pointer or string-to-integer representation conversions.

Casts from error unions to non-pointer, non-error-union types are rejected by semantic checking; unwrap explicitly with `.value` or use `catch`.

### Unary Operators

```text
&expr   address-of
-expr   numeric negation
!expr   boolean negation
```

`!expr` requires `bool` and produces `bool`.

### Binary Operators

Operators from highest to lowest precedence:

```text
postfix: call, field access, indexing
unary: &, -, !
* / %
+ -
<< >>
&
^
|
> < >= <= == !=
&&
||
catch
```

Binary operators are left-grouping in practice for the supported operator set.

Arithmetic and bitwise operators require numeric operands:

```text
+ - * / % << >> & | ^
```

Relational operators require numeric operands:

```text
> < >= <=
```

Equality operators allow:

```text
numeric == numeric
bool == bool
string == string
matching_pointer == matching_pointer
```

Logical operators require `bool` operands and preserve short-circuit evaluation:

```zorb
ready: bool = is_open && has_data
```

### Catch Expressions

```zorb
value: T = fallible() catch |err| {
    // err has type i32
    return err
}
```

The left side must have error-union type `!T`. The catch expression itself has type `T`.

The catch body runs only if the error code is nonzero. The named error variable is an `i32` scoped to the catch body.

Global initializers may not contain `catch`.

## Builtins

### Platform Builtins

```zorb
Builtin.IsLinux
Builtin.IsWindows
Builtin.IsBareMetal
Builtin.IsX86_64
Builtin.IsAArch64
```

These are `bool` values representing the selected compilation target and generated C target environment. They are not the host OS running the Zorb compiler unless the selected target matches the host.

### Builtin.sizeof

```zorb
size: i64 = Builtin.sizeof(Type)
```

Returns the generated C `sizeof(Type)` as `i64`. Unknown types are rejected.

Examples:

```zorb
bytes: i64 = Builtin.sizeof(i64)
fiber_bytes: i64 = Builtin.sizeof(Fiber)
```

### Builtin.CompileError

```zorb
Builtin.CompileError("message")
```

This expects a string literal and lowers to a generated C preprocessor `#error`. It is used primarily in platform-specific branches that must fail if compiled for an unsupported target.

## Type Checking And Assignability

Zorb has explicit, nominal type checking with limited implicit conversions.

### Numeric Types

Numeric types are:

```text
i8 i16 i32 i64 u8 u16 u32 u64
```

`bool` is not numeric.

Integer literals have type `i64`, but literal assignment is allowed to smaller integer types if the literal value fits.

```zorb
small: u8 = 255      // accepted
bad: u8 = 256        // rejected
```

Non-literal integer values allow exact match or widening conversions only:

```text
i8  -> i16 -> i32 -> i64
u8  -> u16 -> u32 -> u64
u8  -> i16/i32/i64
u16 -> i32/i64
u32 -> i64
```

Potentially lossy conversions require `cast`.

### Conditions

`if`, `while`, and `for` conditions must be `bool`.

```zorb
if value != 0 {
    ...
}
```

Numeric and pointer truthiness is rejected.

### Pointer Assignability

Pointer types must generally match exactly. Any pointer source is assignable to `*void`.

Function call arguments additionally reject pointer-level mismatches when both argument and parameter are pointer types.

Arrays decay to `*T` only in function-call argument position, and only when the parameter type is exactly `*T` for the array element type. Arrays do not decay to `*void`.

### Volatile Qualification

An unqualified value can be assigned to a matching volatile-qualified target. Volatile qualification is represented in the type and in generated C.

### Function Types

Function types are assignable only when return type and parameter types match structurally.

### Nominal Types

Non-built-in, non-numeric, non-pointer types are assignable only when their names and namespace paths match.

## Constant Integer Expressions

Constant integer expressions are evaluated in compile-time-only integer contexts:

```text
array sizes
struct field array sizes
align(...)
offset(...)
folded global integer initializers
```

Supported constant operations:

```text
integer literals
integer const identifiers
unary -
+ - * / % << >> & | ^
```

Division by zero and `i64` overflow are rejected.

## Name Resolution And Scope

The compiler has a global scope and nested local scopes. Function parameters are in the function-local scope. Bodies of `if`, `else`, `while`, `for`, and `switch` cases create nested semantic scopes.

Local declarations may shadow outer declarations. Lookup prefers the innermost active scope.

Visibility is separate from existence. A symbol may exist in the global symbol table but not be visible from the current file unless it is local, declared in the same file, builtin, or exported by a directly imported file.

## Generated C Model

Zorb lowers to C.

Primitive mappings:

```text
i8     int8_t
i16    int16_t
i32    int32_t
i64    int64_t
u8     uint8_t
u16    uint16_t
u32    uint32_t
u64    uint64_t
bool   int32_t
string char*
void   void
```

Structs lower to C `struct` declarations. Qualified names are flattened with `_`.

Error unions lower to generated result structs. Slices lower to generated slice structs.

On hosted targets, a source `_start` is renamed internally and called from a generated C `main`. On freestanding Linux and bare-metal targets, `_start` is preserved.

## Complete Example

```zorb
import "std/io.zorb"
import "std/os.zorb"

error MissingConfig = 200

struct Pair {
    left: i64,
    right: i64
}

fn load_value(ok: bool) !Pair {
    if !ok {
        return error.MissingConfig
    }

    return Pair{ left: 10, right: 20 }
}

fn _start() {
    pair: Pair = load_value(true) catch |err| {
        std.io.print("load failed\n")
        std.os.exit(err)
        return
    }

    total: i64 = pair.left + pair.right
    std.io.print_i64(total)
    std.io.print("\n")
    std.os.exit(0)
}
```
