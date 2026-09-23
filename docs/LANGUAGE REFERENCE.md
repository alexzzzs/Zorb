# Zorb Language Reference

This is a compact syntax guide. [Zorb Semantics](SEMANTICS.md) defines the
current language behavior; the [standard library reference](STANDARD%20LIBRARY.md)
covers library APIs.

## Compilation and targets

Zorb compiles one entry file and loads additional files through `import`.
The frontend checks the program and lowers it to the versioned
[Backend IR](BACKEND%20IR.md), which the Zig/LLVM backend verifies and emits.

Stable build targets are `host-linux`, `freestanding-linux`,
`host-linux-aarch64`, `freestanding-linux-aarch64`, `host-windows`, and
`bare-metal-x86_64`. The bare-metal target builds kernel ELF files and does not
support `run`. Hosted Windows uses the MSVC ABI; MinGW is unsupported. Host
and toolchain requirements are listed in the
[backend target matrix](../backend/llvm/README.md#supported-output-targets).

## Imports and visibility

```zorb
import "std/io.zorb"
import "math.zorb" as math
import c "windows.h"
```

Paths are relative to the importing file unless absolute. Only `export`
declarations are visible to an importer. Imports are not transitive. An alias
qualifies the imported declarations, so `math.answer` is visible while
`answer` is not. C header imports register a native dependency.

## Declarations

```zorb
export const limit: i64 = 64
export answer: i64 = 42
export error Missing = 100

export struct Point {
    x: i64,
    y: i64
}

export enum Mode: i32 { Idle, Run = 4 }
export union Value { Number: i64, Flag: bool }

export fn add(a: i64, b: i64) -> i64 {
    return a + b
}

extern fn write(fd: i32, data: []u8) -> i64
```

Local declarations require explicit types. The language also supports globals,
function values, namespaced declarations, tagged unions, and explicit generic
functions and types.

## Types and generics

Built-in types include signed and unsigned integers (`i8` through `i64`, `u8`
through `u64`), `bool`, `void`, and `string`. User-defined types are nominal.

```zorb
struct Box<T = i64> {
    value: T,
}

fn mirror<T, U: T = T>(left: T, right: U) -> U {
    return right
}

fn use_box() -> Box<i64> {
    return Box{ value: mirror<i64>(1, 2) }
}
```

Type parameters can have exact-type constraints and trailing defaults. Generic
calls may infer types from arguments; concrete uses are monomorphized. The
constraint syntax is not a trait system.

Other type forms:

| Form | Example |
| --- | --- |
| Pointer | `*i64` |
| Fixed-size array | `[4]u8` |
| Slice | `[]u8` |
| Function type | `fn(i64) -> bool` |
| Error union | `!i64` |

## Statements and expressions

```zorb
if ready { run() } else { wait() }
while count > 0 { count = count - 1 }

for i: i64 = 0; i < 4; i = i + 1 {
    total = total + i
}

match value {
    case Value.Number(number) { return number }
    case Value.Flag(flag) { if flag { return 1 } else { return 0 } }
}
```

The language supports `if`, `while`, `for`, `switch`, exhaustive `match`,
`return`, `break`, `continue`, and inline assembly. Expressions include
function calls, indexing, field access, casts, `catch`, `sizeof`, arithmetic,
comparisons, bitwise operators, and boolean `&&`, `||`, and `!`.

String literals use double quotes. Supported escapes are `\"`, `\\`, `\n`,
`\r`, `\t`, and `\0`. `//` starts a line comment; there are no block comments.

## Attributes and builtins

Attributes configure functions, variables, and structs. Current forms include
`abi(...)`, `align(...)`, `noinline`, `noclone`, `section("...")`, `packed`,
`layout(explicit)`, `offset(N)`, and `volatile`.

`Builtin.IsLinux`, `Builtin.IsWindows`, `Builtin.IsBareMetal`, architecture
queries, and `Builtin.sizeof(...)` expose compile-time target information.
`Builtin.CompileError(...)` produces a compile-time diagnostic.

## Example

```zorb
import "std/io.zorb"
import "std/os.zorb"

fn _start() {
    std.io.println("hello from Zorb")
    std.os.exit(0)
}
```

See [`examples/`](../examples/) for runnable programs and
[semantics](SEMANTICS.md) for import rules, type checking, pointer behavior,
error handling, and code generation details.
