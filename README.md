# Zorb

Zorb is a small ahead-of-time compiler for a systems language. Native
compilation lowers through a Zig 0.16 backend over LLVM 21.

The project already has:

- a working `net8.0` frontend and Zig/LLVM native backend
- a fixture-based regression suite with runtime tests
- a draft language spec in `SEMANTICS.md`
- a small standard library under `std/`

## Current Status

The compiler supports a focused language subset:

- functions, `extern fn`, and namespaced declarations
- `struct`, `enum`, and tagged `union` types plus explicit generic structs, enums, unions, and functions, monomorphized per concrete use
- `enum` types with explicit integer backing types
- tagged `union` types with generated tag enums
- globals, `const` declarations, and error declarations
- `if`, `else`, `while`, `for`, `switch`, `match`, `continue`, `break`, and `return`
- logical `&&`, `||`, and unary `!` on `bool`
- pointers, fixed-size arrays, slice types, function types, and error unions
- typed struct literals, typed array literals, and local array value copies
- imports, including `import "file.zorb" as alias`
- inline assembly
- target-facing attributes such as `section("...")`, `packed`, `layout(explicit)`, `offset(N)`, `abi(...)`, and `volatile`
- builtins such as `Builtin.IsLinux`, `Builtin.IsWindows`, `Builtin.IsBareMetal`, and `Builtin.sizeof(...)`

The current semantic source of truth is [SEMANTICS.md](docs/SEMANTICS.md).

## Supported Parity

The LLVM backend is the only production backend and is the parity target for
the implemented language subset described in this repository.

That parity claim currently covers:

- `freestanding-linux` on Linux hosts
- `freestanding-linux-aarch64` and `host-linux-aarch64` from Linux hosts with an AArch64 cross-toolchain
- `host-windows` on Windows hosts using the MSVC ABI toolchain path
- `bare-metal-x86_64` for kernel ELF builds

It does not currently mean:

- hosted Windows GNU/MinGW support
- `run` support for bare-metal output

## Generics

Structs and non-extern functions may declare one or more type parameters:

```zorb
struct Box<T> {
    value: T,
}

fn identity<T>(value: T) -> T {
    return value
}

fn make() -> Box<i64> {
    return Box<i64>{ value: identity<i64>(42) }
}
```

Generic calls may provide explicit type arguments such as `identity<i64>(42)`, or omit them when the parameter types make the concrete instantiation obvious, such as `identity(42)`. Generic types such as `Box<i64>`, `Mode<i64>`, and `Result<i64, bool>` still provide explicit type arguments. Nested forms such as `Box<Box<i64>>`, imported generic declarations, pointers, slices, arrays, error unions, and generic struct layout attributes are supported.

Each concrete use is monomorphized into a distinct backend function or concrete nominal type. Generic unions also monomorphize their generated `.Tag` enums per concrete use, so expressions such as `Result<i64, bool>.Tag.Ok` remain type-safe.
Zorb does not currently provide constraints, default type arguments, generic
extern functions, or first-class values for uninstantiated generic functions.

Cross-platform stdlib helpers currently include:

- `std.os.is_linux()`, `std.os.is_windows()`, `std.os.platform_name()`
- `std.os.is_x86_64()`, `std.os.is_aarch64()`, `std.os.arch_name()`
- `std.os.monotonic_millis()` for timeout/deadline-oriented runtime code on hosted targets
- `std.io.print(...)`, `std.io.println(...)`, `std.io.eprint(...)`, `std.io.eprintln(...)`, boolean and integer print helpers, slice-based `std.io.write(fd, buf)`, and `std.io.read(fd, buf)`
- `std.fs.open_read(...)`, `std.fs.open_write(...)`, `std.fs.exists(...)`, `std.fs.size(...)`, `std.fs.read_all(...)`, `std.fs.write_all(...)`, `std.fs.rename(...)`, and `std.fs.delete(...)`
- low-level Linux-first networking helpers in `std.net` for raw TCP socket setup, IPv4 socket addresses, send/recv, and close
- `std.task.is_supported()` and `std.async.is_supported()` for checking runtime capability before using task or async features, plus async readiness waits with optional timeouts and exact send/recv helpers
- `std.str.eql(...)`, `std.str.starts_with(...)`, `std.str.ends_with(...)`, `std.str.copy(...)`, and `std.str.from_u64(...)`
- `std.mem.zero(...)` and `std.mem.copy(...)` for slice-oriented memory helpers

## Build

```bash
dotnet build Zorb.Compiler/Zorb.Compiler.csproj -c Release
cd Zorb.LlvmBackend
zig build test
zig build
```

Backend development requires Zig 0.16 and LLVM 21 development headers and
libraries. The release scripts package the backend and LLD with the compiler,
so released archives do not require a separate Zig installation. Linux release
builds statically link LLVM; Windows release builds bundle `LLVM-C.dll`.

Publish a standalone compiler package:

```bash
./scripts/publish-compiler-linux.sh
```

Publish a version-stamped standalone Linux compiler build:

```bash
VERSION=0.2.0 INFORMATIONAL_VERSION=0.2.0 ./scripts/publish-compiler-linux.sh
```

On Windows PowerShell:

```powershell
./scripts/publish-compiler-windows.ps1
```

Publish a version-stamped standalone Windows compiler build:

```powershell
$env:VERSION="0.2.0"
$env:INFORMATIONAL_VERSION="0.2.0"
./scripts/publish-compiler-windows.ps1
```

The GitHub Actions workflow builds and tests the .NET frontend, Zig backend, and
packaged toolchain on Linux and Windows. Pushes to `master` publish standalone
artifacts. Version tags such as `v0.1.0` create a GitHub Release with zipped
compiler packages.

## Run The Compiler

Check a file without emitting output:

```bash
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- main.zorb --check
```

Print the compiler version:

```bash
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- --version
```

Dump the token stream before parsing:

```bash
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- main.zorb --dump-tokens --check
```

Emit verified LLVM IR:

```bash
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- main.zorb -o out.ll
```

Build a native executable on the current host:

```bash
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- build main.zorb -o out
```

Compile and run a program on the current host:

```bash
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- run main.zorb
```

Select an explicit build target:

```bash
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- build main.zorb --target host-linux -o out
```

Supported `--target` values are `host-linux`, `freestanding-linux`, `host-linux-aarch64`, `freestanding-linux-aarch64`, `bare-metal-x86_64`, and `host-windows`.
On Linux, `build` and `run` default to `freestanding-linux`, which preserves `_start` and links a Linux executable without the usual C runtime startup files.
The legacy `-nostdlib` flag remains available as shorthand for `--target freestanding-linux`.

Build a bare-metal x86_64 kernel ELF with the bundled linker script:

```bash
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- build main.zorb --target bare-metal-x86_64 -o kernel.elf
```

Use a custom linker script instead of the bundled one:

```bash
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- build main.zorb --target bare-metal-x86_64 --linker-script kernel.ld -o kernel.elf
```

Emit the linker script used for the build so you can inspect or customize it:

```bash
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- build main.zorb --target bare-metal-x86_64 --emit-linker-script kernel.ld -o kernel.elf
```

`bare-metal-x86_64` preserves `_start`, sets `Builtin.IsBareMetal`, routes
`std.io.write(...)` to the x86_64 debug port `0xE9`, emits an ELF object through
LLVM, and links a kernel ELF with packaged `ld.lld`. The target can be built
from x86_64 Linux or Windows hosts with either the bundled linker script or the
script passed to `--linker-script`.
`run` is intentionally unsupported for bare-metal output.

## Windows Host Builds

On Windows, the recommended hosted linker driver is `clang-cl`.
It integrates with the normal Windows/MSVC link environment, which makes it the most convenient path for Zorb programs that use the Windows-facing standard library bindings in `std/io.zorb` and `std/os.zorb`.

Build a native Windows executable:

```powershell
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- build main.zorb -o out.exe
```

Compile and run a program on the current Windows host:

```powershell
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- run main.zorb
```

Select the hosted Windows target explicitly:

```powershell
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- build main.zorb --target host-windows -o out.exe
```

Notes:

- Windows `build` and `run` default to `host-windows` and use a generated hosted `main` shim when source defines `_start`.
- `-nostdlib` build and run remain Linux-oriented and are not currently supported on Windows hosts.
- `clang-cl` is the recommended Windows toolchain.
- `cl.exe` may also work, but `clang-cl` is the preferred default.
- Windows GNU/MinGW hosted output is not supported. LLVM removes the old C
  compiler restriction for bare-metal ELF output, but it does not implicitly
  provide a MinGW runtime, ABI, or standard-library binding layer.

## Test

Run the full fixture suite:

```bash
dotnet run --project Zorb.Compiler.Tests/Zorb.Compiler.Tests.csproj
```

Every semantically successful fixture and example is emitted through LLVM and
verified. Runtime fixtures are built and executed through the LLVM backend.
Focused `expect-llvm.txt` files may assert stable IR details where verifier and
runtime coverage are not specific enough.

Current runtime coverage is strongest on Linux and on Windows host targets in
CI. An AArch64 Linux lane is available on Linux hosts with `aarch64-linux-gnu-gcc`
plus `qemu-aarch64`; set `ZORB_RUN_AARCH64_TESTS=1` to require that lane locally.

## Examples

Minimal program:

```zorb
import "std/io.zorb"
import "std/os.zorb"

fn _start() {
    std.io.print("hello from zorb\n")
    std.os.exit(0)
}
```

Import aliasing:

```zorb
import "math.zorb" as math

fn main() {
    answer: i64 = math.answer
}
```

Explicit numeric casts and string escapes:

```zorb
fn demo(value: i64) {
    small: i32 = cast(i32, value)
    message: string = "line 1\nline 2\t\"quoted\"\\done"
}
```

Typed literals and logical operators:

```zorb
struct Pair {
    left: i32,
    right: i32
}

fn main() {
    pair: Pair = Pair{ left: 1, right: 2 }
    mask: [4]u8 = [4]u8{ 1, 1, 0, 0 }
    copy: [4]u8 = mask
    ready: bool = (pair.left == 1 && copy[0] == 1) || false
}
```

Enums and exhaustive match:

```zorb
enum Mode: i32 {
    Idle,
    Run = 4,
    Stop
}

fn score(mode: Mode) -> i64 {
    match mode {
        case Mode.Idle { return 1 }
        case Mode.Run { return 4 }
        case Mode.Stop { return 9 }
    }
}
```

Tagged unions with payload binding:

```zorb
union Value {
    Number: i64,
    Flag: bool
}

fn score(value: Value) -> i64 {
    match value {
        case Value.Number(number) { return number }
        case Value.Flag(flag) {
            if flag { return 1 }
            return 0
        }
    }
}
```

`for` loops and `switch` branches:

```zorb
import "std/os.zorb"

fn classify(value: i64) -> i64 {
    switch value {
        case 0 {
            return 10
        }
        else {
            return 20
        }
    }
}

fn _start() {
    total: i64 = 0
    for i: i64 = 0; i < 3; i = i + 1 {
        total = total + classify(i)
    }

    std.os.exit(0)
}
```

Slice-backed buffer flow:

```zorb
import "std/io.zorb"

fn main() {
    buf: [4]u8 = [4]u8{ 79, 75, 10, 0 }
    view: []u8 = buf
    view.len = 3
    std.io.write(1, view)
}
```

Slice indexing is runtime-bounds-checked before reads or writes.

Representative larger examples live in [`examples/`](./examples) and the executable fixture corpus under [`Zorb.Compiler.Tests/fixtures/`](./Zorb.Compiler.Tests/fixtures).

Current checked-in examples:

- [`examples/basics/import_alias/main.zorb`](./examples/basics/import_alias/main.zorb): import aliasing with a sibling module.
- [`examples/basics/error_catch.zorb`](./examples/basics/error_catch.zorb): error unions with `catch`, `std.io`, and `std.os`.
- [`examples/basics/tagged_union.zorb`](./examples/basics/tagged_union.zorb): tagged unions with payload-binding `match`.
- [`examples/basics/platform_info.zorb`](./examples/basics/platform_info.zorb): cross-platform stdlib helpers for platform detection, stdout, and stderr.
- [`examples/basics/net_socket.zorb`](./examples/basics/net_socket.zorb): low-level TCP socket setup using the Linux-first `std.net` APIs.
- [`examples/basics/stdlib_helpers.zorb`](./examples/basics/stdlib_helpers.zorb): string, memory, and formatted output helpers from the standard library.
- [`examples/basics/literals.zorb`](./examples/basics/literals.zorb): typed struct and array literals combined with logical operators.
- [`examples/basics/generics.zorb`](./examples/basics/generics.zorb): explicit generic structs and functions with nested concrete instantiations.
- [`examples/basics/generic_adts.zorb`](./examples/basics/generic_adts.zorb): generic enums and tagged unions, including phantom-type stage markers, concrete tag comparisons, and payload-binding `match`.
- [`examples/basics/switch_for.zorb`](./examples/basics/switch_for.zorb): `for` loops and `switch` with an `else` branch.
- [`examples/dogfood/lexer/main.zorb`](./examples/dogfood/lexer/main.zorb): a small lexer demo written in Zorb that exercises real control flow, slices, and token handling.
- [`examples/advanced/threads.zorb`](./examples/advanced/threads.zorb): lower-level task/thread setup using inline assembly and Linux syscalls.
- [`examples/baremetal/hello_kernel.zorb`](./examples/baremetal/hello_kernel.zorb): a tiny x86_64 bare-metal kernel example using the debug port output path.

## Project Shape

- `Zorb.Compiler/`: lexer, parser, semantic checker, CLI, and LLVM backend IR emission
- `Zorb.Compiler.Tests/`: fixture runner and regression fixtures
- `std/`: standard library modules used by runtime-oriented examples
- `SEMANTICS.md`: language behavior and current design constraints
