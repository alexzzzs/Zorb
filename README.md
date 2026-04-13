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
- `if`, `else`, `while`, `continue`, and `return`
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

Build a native Linux executable:

```bash
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- build main.zorb -o out
```

Compile and run a program on the current Linux host:

```bash
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- run main.zorb
```

Keep the generated C while building or running:

```bash
dotnet run --project Zorb.Compiler/Zorb.Compiler.csproj -- build main.zorb -o out --keep-c out.c
```

For "freestanding" output, compile the generated C with the required Linux x86_64 flags:

```bash
gcc -O2 -nostdlib -fno-pie -no-pie -z execstack -fno-builtin out.c -o out
```

Flag rationale is documented in [CFLAGS.md](./CFLAGS.md).

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

## Project Shape

- `Zorb.Compiler/`: lexer, parser, semantic checker, and C codegen
- `Zorb.Compiler.Tests/`: fixture runner and regression fixtures
- `std/`: standard library modules used by runtime-oriented examples
- `SEMANTICS.md`: language behavior and current design constraints
