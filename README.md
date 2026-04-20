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
- globals and `const` globals
- `if`, `else`, `while`, `continue`, `break`, and `return`
- pointers, fixed-size arrays, function types, and error unions
- imports, including `import "file.zorb" as alias`
- inline assembly
- builtins such as `Builtin.IsLinux`, `Builtin.IsWindows`, and `Builtin.sizeof(...)`

The current semantic source of truth is [SEMANTICS.md](./SEMANTICS.md).

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
VERSION=0.1.1-dev.42 INFORMATIONAL_VERSION=0.1.1-dev.42+abcdef12 ./scripts/publish-compiler-linux.sh
```

On Windows PowerShell:

```powershell
./scripts/publish-compiler-windows.ps1
```

Publish a version-stamped standalone Windows compiler build:

```powershell
$env:VERSION="0.1.1-dev.42"
$env:INFORMATIONAL_VERSION="0.1.1-dev.42+abcdef12"
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

Keep the generated C while building or running:

```bash
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- build main.zorb -o out --keep-c out.c
```

On Linux, `build` and `run` default to freestanding output and preserve `_start`.
For freestanding output, compile the generated C with the required Linux x86_64 flags:

```bash
gcc -O2 -nostdlib -fno-pie -no-pie -z execstack -fno-builtin out.c -o out
```

Flag rationale is documented in [CFLAGS.md](./CFLAGS.md).

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

Notes:

- Windows `build` and `run` currently use hosted output and map `_start` to `main`.
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

Representative larger examples live in [`examples/`](./examples) and the executable fixture corpus under [`Zorb.Compiler.Tests/fixtures/`](./Zorb.Compiler.Tests/fixtures).

Current checked-in examples:

- [`examples/basics/import_alias/main.zorb`](./examples/basics/import_alias/main.zorb): import aliasing with a sibling module.
- [`examples/basics/error_catch.zorb`](./examples/basics/error_catch.zorb): error unions with `catch`, `std.io`, and `std.os`.
- [`examples/basics/platform_info.zorb`](./examples/basics/platform_info.zorb): portable stdlib output that branches on the active host platform.
- [`examples/advanced/threads.zorb`](./examples/advanced/threads.zorb): lower-level task/thread setup using inline assembly and Linux syscalls.

## Project Shape

- `Zorb.Compiler/`: lexer, parser, semantic checker, and C codegen
- `Zorb.Compiler.Tests/`: fixture runner and regression fixtures
- `std/`: standard library modules used by runtime-oriented examples
- `SEMANTICS.md`: language behavior and current design constraints
