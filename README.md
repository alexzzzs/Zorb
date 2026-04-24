# Zorb

Zorb is a small ahead-of-time compiler for a systems language that currently lowers to C.

The project already has:

- a working `net8.0` compiler
- a fixture-based regression suite with runtime tests
- a draft language spec in `SEMANTICS.md`
- a small standard library under `std/`

## Current Status

The compiler supports a focused language subset:

- functions, `extern fn`, and namespaced declarations
- `struct` types
- globals, `const` declarations, and error declarations
- `if`, `else`, `while`, `for`, `switch`, `continue`, `break`, and `return`
- logical `&&`, `||`, and unary `!` on `bool`
- pointers, fixed-size arrays, slice types, function types, and error unions
- typed struct literals, typed array literals, and local array value copies
- imports, including `import "file.zorb" as alias`
- inline assembly
- target-facing attributes such as `section("...")`, `packed`, `layout(explicit)`, `offset(N)`, `abi(...)`, and `volatile`
- builtins such as `Builtin.IsLinux`, `Builtin.IsWindows`, `Builtin.IsBareMetal`, and `Builtin.sizeof(...)`

The current semantic source of truth is [SEMANTICS.md](./SEMANTICS.md).

Cross-platform stdlib helpers currently include:

- `std.os.is_linux()`, `std.os.is_windows()`, `std.os.platform_name()`
- `std.os.is_x86_64()`, `std.os.is_aarch64()`, `std.os.arch_name()`
- `std.io.print(...)`, `std.io.println(...)`, `std.io.eprint(...)`, `std.io.eprintln(...)`, and slice-based `std.io.write(fd, buf)`
- `std.task.is_supported()` and `std.async.is_supported()` for checking runtime capability before using task or async features

## Build

```bash
dotnet build Zorb.Compiler/Zorb.Compiler.csproj -c Release
```

Publish a standalone compiler binary:

```bash
./scripts/publish-compiler-linux.sh
```

Publish a version-stamped standalone Linux compiler build:

```bash
VERSION=0.1.5-dev INFORMATIONAL_VERSION=0.1.5-dev ./scripts/publish-compiler-linux.sh
```

On Windows PowerShell:

```powershell
./scripts/publish-compiler-windows.ps1
```

Publish a version-stamped standalone Windows compiler build:

```powershell
$env:VERSION="0.1.5-dev"
$env:INFORMATIONAL_VERSION="0.1.5-dev"
./scripts/publish-compiler-windows.ps1
```

The GitHub Actions workflow publishes standalone Linux and Windows compiler artifacts automatically on pushes to `master` and on version tags such as `v0.1.0`.
Version tags such as `v0.1.0` also create a GitHub Release and attach zipped standalone compiler binaries as release assets.

## Run The Compiler

Check a file without emitting C:

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

Emit C:

```bash
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- main.zorb -o out.c
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

Keep the generated C while building or running:

```bash
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- build main.zorb -o out --keep-c out.c
```

Supported `--target` values are `host-linux`, `freestanding-linux`, `bare-metal-x86_64`, and `host-windows`.
On Linux, `build` and `run` default to `freestanding-linux`, which preserves `_start` and links a Linux executable without the usual C runtime startup files.
The legacy `-nostdlib` flag remains available as shorthand for `--target freestanding-linux`.

Build a bare-metal x86_64 kernel ELF with the bundled linker script:

```bash
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- build main.zorb --target bare-metal-x86_64 -o kernel.elf --keep-c kernel.c
```

Use a custom linker script instead of the bundled one:

```bash
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- build main.zorb --target bare-metal-x86_64 --linker-script kernel.ld -o kernel.elf
```

Emit the linker script used for the build so you can inspect or customize it:

```bash
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- build main.zorb --target bare-metal-x86_64 --emit-linker-script kernel.ld -o kernel.elf
```

`bare-metal-x86_64` preserves `_start`, sets `Builtin.IsBareMetal`, routes `std.io.write(...)` to the x86_64 debug port `0xE9`, links a kernel ELF with either the bundled linker script or the script passed to `--linker-script`, and can write that exact script to disk with `--emit-linker-script`.
`run` is intentionally unsupported for bare-metal output.

For freestanding Linux output, compile the generated C with the required Linux x86_64 flags:

```bash
gcc -O2 -nostdlib -fno-pie -no-pie -z execstack -fno-builtin out.c -o out
```

Flag rationale is documented in [CFLAGS.md](./CFLAGS.md).
Those flags are for Linux freestanding binaries, not for true bare-metal kernels.

## Windows Host Builds

On Windows, the recommended compiler driver is `clang-cl`.
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
- Plain `clang` and MinGW-style flows may work, but currently require manual linker setup for the Windows API libraries used by the standard library modules.

## Test

Run the full fixture suite:

```bash
dotnet run --project Zorb.Compiler.Tests/Zorb.Compiler.Tests.csproj
```

Update full-codegen snapshots after reviewing intentional output changes:

```bash
dotnet run --project Zorb.Compiler.Tests/Zorb.Compiler.Tests.csproj -- --update-snapshots
```

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
- [`examples/basics/platform_info.zorb`](./examples/basics/platform_info.zorb): cross-platform stdlib helpers for platform detection, stdout, and stderr.
- [`examples/basics/literals.zorb`](./examples/basics/literals.zorb): typed struct and array literals combined with logical operators.
- [`examples/basics/switch_for.zorb`](./examples/basics/switch_for.zorb): `for` loops and `switch` with an `else` branch.
- [`examples/advanced/threads.zorb`](./examples/advanced/threads.zorb): lower-level task/thread setup using inline assembly and Linux syscalls.

## Project Shape

- `Zorb.Compiler/`: lexer, parser, semantic checker, and C codegen
- `Zorb.Compiler.Tests/`: fixture runner and regression fixtures
- `std/`: standard library modules used by runtime-oriented examples
- `SEMANTICS.md`: language behavior and current design constraints
