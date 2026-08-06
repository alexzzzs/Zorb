# Zorb

Zorb is a small ahead-of-time compiler for a systems language. Native
compilation lowers through a Zig 0.16 backend over LLVM 22.

The normal compiler is a single `zorb` executable: a Zorb-written frontend
paired with the Zig/LLVM backend through a static C ABI library. A pinned
preceding release is the normal bootstrap seed; the C# project is retained only
as an explicit stage-0 recovery bootstrap. See
[Compiler architecture](docs/ARCHITECTURE.md) and
[Bootstrapping Zorb](docs/BOOTSTRAPPING.md) for the roles and migration plan.
Target-specific integrated compiler seeds are resolved by the cross-platform
[`scripts/bootstrap_seed.py`](scripts/bootstrap_seed.py).

The project has:

- a self-compiling Zorb frontend and in-process Zig/LLVM backend
- a fixture-based regression suite with runtime tests
- a draft language spec in `SEMANTICS.md`
- a small standard library under `runtime/std/`

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

- `host-linux` and `freestanding-linux` on Linux hosts
- `freestanding-linux-aarch64` and `host-linux-aarch64` from Linux hosts with an AArch64 cross-toolchain
- `host-windows` on Windows hosts using the MSVC ABI toolchain path
- `bare-metal-x86_64` for kernel ELF builds

It does not currently mean:

- hosted Windows GNU/MinGW support
- `run` support for bare-metal output

## Stability Contract

The current stable contract for this repository is:

- The language subset listed in [Current Status](#current-status) is expected to type-check, lower through the LLVM backend, and stay covered by the fixture suite.
- `build` is stable for `host-linux`, `freestanding-linux`, `host-linux-aarch64`, `freestanding-linux-aarch64`, `host-windows`, and `bare-metal-x86_64`.
- `run` is stable for the hosted runtime paths covered by the fixture suite: `host-linux` and `freestanding-linux` on Linux hosts, `host-windows` on Windows hosts, and the optional AArch64 Linux runtime lane when the documented cross-toolchain and QEMU prerequisites are present.
- `bare-metal-x86_64` is a build-only target. It guarantees ELF kernel output plus linker-script support; it does not guarantee hosted runtime behavior or a `run` workflow.

The standard library support contract is intentionally module-specific:

- `std.os`, `std.io`, `std.str`, and `std.mem` are the base portable layer and are expected to work across the repo's main hosted targets, with target-specific behavior documented for bare-metal where applicable.
- `std.fs` is a hosted-only module and is currently stable on Linux and Windows hosted targets.
- `std.net` is stable on Linux and Windows hosted targets for low-level TCP sockets and readiness polling.
- `std.task` is stable on Linux and Windows x86_64 and AArch64 hosted targets when `std.task.is_supported()` reports `true`.
- `std.async` is stable only where both `std.task` and `std.net` are supported, and portable code should continue to gate use with `std.async.is_supported()`.

## Generics

Structs, enums, unions, and non-extern functions may declare one or more type parameters.
Type parameters may optionally declare an exact-type constraint with `: Type`
and a trailing default type with `= Type`:

```zorb
struct Box<T = i64> {
    value: T,
}

fn mirror<T, U: T = T>(left: T, right: U) -> U {
    return right
}

fn make() -> Box {
    return Box{ value: mirror<i64>(0, 42) }
}
```

In `mirror`, `T` comes from the first argument, while `U` must exactly match `T`
and defaults to `T` when omitted.

Generic calls may provide explicit type arguments such as `identity<i64>(42)`, or omit them when the parameter types make the concrete instantiation obvious, such as `identity(42)`. Generic types such as `Box<i64>`, `Mode<i64>`, and `Result<i64, bool>` may provide explicit type arguments, and declarations with trailing defaults may omit those trailing positions. Nested forms such as `Box<Box<i64>>`, imported generic declarations, pointers, slices, arrays, error unions, and generic struct layout attributes are supported.

Each concrete use is monomorphized into a distinct backend function or concrete nominal type. Generic unions also monomorphize their generated `.Tag` enums per concrete use, so expressions such as `Result<i64, bool>.Tag.Ok` remain type-safe.
Constraints are exact-type requirements after substituting any earlier type
arguments; they are not a trait or interface system. Generic `extern fn`
declarations monomorphize per concrete use. First-class values for
uninstantiated generic functions are still not supported.

Cross-platform stdlib helpers currently include:

- `std.os.is_linux()`, `std.os.is_windows()`, `std.os.platform_name()`
- `std.os.is_x86_64()`, `std.os.is_aarch64()`, `std.os.arch_name()`
- `std.os.monotonic_millis()` for timeout/deadline-oriented runtime code on hosted targets
- `std.io.print(...)`, `std.io.println(...)`, `std.io.eprint(...)`, `std.io.eprintln(...)`, boolean and integer print helpers, slice-based `std.io.write(fd, buf)`, and `std.io.read(fd, buf)`
- hosted-target `std.fs.open_read(...)`, `std.fs.open_write(...)`, `std.fs.exists(...)`, `std.fs.size(...)`, `std.fs.read_all(...)`, `std.fs.write_all(...)`, `std.fs.rename(...)`, and `std.fs.delete(...)`
- low-level hosted `std.net` helpers for raw TCP socket setup, IPv4 socket addresses, send/recv, and readiness polling on Linux and Windows
- `std.task.is_supported()` and `std.async.is_supported()` for checking runtime capability before using task or async features, plus async readiness waits with optional timeouts and exact send/recv helpers where supported
- `std.str.eql(...)`, `std.str.starts_with(...)`, `std.str.ends_with(...)`, `std.str.copy(...)`, and `std.str.from_u64(...)`
- `std.mem.zero(...)` and `std.mem.copy(...)` for slice-oriented memory helpers

## Build

```bash
python scripts/bootstrap_compiler.py bootstrap
./build/zorb check compiler/self-check/fixtures/simple.zorb
```

Linux x64, Linux ARM64, and Windows x64 normally use the pinned preceding
release. The legacy shell entry point `./scripts/bootstrap-compiler.sh` invokes
the same Python implementation.

Backend development requires Zig 0.16 and LLVM 22 development headers and
libraries. The development bootstrap links the local shared LLVM library. The
Linux publisher statically links LLVM into one `zorb` executable; neither path
launches a separate backend process.

Executable `build` and `run` still use the host linker driver: `cc` on Linux and
`clang-cl` on Windows. Those tools are development/runtime prerequisites and
are not copied into compiler packages. LLVM IR, bitcode, assembly, and object
output do not require a linker.

Publish a standalone compiler package for the current Linux host architecture:

```bash
python scripts/bootstrap_compiler.py publish
```

On Windows PowerShell:

```powershell
python scripts/bootstrap_compiler.py publish
# Legacy wrapper:
./scripts/publish-compiler-windows.ps1
```

Normal Windows publishing resolves the pinned portable compiler seed and does
not require .NET. Use `-RecoveryCSharp` only for explicit release-chain repair.

The Linux publisher supports native x86_64 and AArch64 hosts and selects
`host-linux` or `host-linux-aarch64` automatically. The GitHub Actions workflow
builds and tests the recovery seed, native frontend, Zig backend, and packaged
toolchain on Linux x86_64, Linux ARM64, and Windows x86_64. Pushes to `master`
publish standalone artifacts. Version tags create a GitHub Release with zipped
compiler packages for all three hosts.

## Run The Compiler

Check a file without emitting output:

```bash
./build/zorb check main.zorb
```

Emit verified LLVM IR:

```bash
./build/zorb build main.zorb --output-kind llvm-ir -o out.ll
```

Build a native executable on the current host:

```bash
./build/zorb build main.zorb -o out
```

Compile and run a program on the current host:

```bash
./build/zorb run main.zorb
```

Select an explicit build target:

```bash
./build/zorb build main.zorb --target freestanding-linux -o out
```

Pass additional host-linker arguments (this terminal option consumes every
remaining argument without shell parsing):

```bash
./build/zorb build main.zorb -o out --native-link-args path/to/library.a -lm
```

The integrated driver owns the link policies for all supported targets. Exact
LLVM triples remain available for `llvm-ir`, `object`, `assembly`, and
`bitcode` output; use the stable target names below when building executables.

### Target workflows

Supported `--target` values are `host-linux`, `freestanding-linux`,
`host-linux-aarch64`, `freestanding-linux-aarch64`, `bare-metal-x86_64`, and
`host-windows`. Linux and Windows default to their native hosted target.
Freestanding Linux preserves `_start` and links without the host startup files.
AArch64 builds on x86_64 Linux use `aarch64-linux-gnu-gcc`; override it with
`ZORB_AARCH64_LINUX_GCC`. On an AArch64 Linux host, the compiler uses native
`gcc` and runs AArch64 programs directly without QEMU. Only cross-host AArch64
`run` uses `qemu-aarch64` plus the `/usr/aarch64-linux-gnu` sysroot by default;
override those with `ZORB_QEMU_AARCH64` and `ZORB_AARCH64_LINUX_SYSROOT`.

Build a bare-metal x86_64 kernel ELF with the bundled linker script:

```bash
./build/zorb build main.zorb --target bare-metal-x86_64 -o kernel.elf
```

Use a custom linker script instead of the bundled one:

```bash
./build/zorb build main.zorb --target bare-metal-x86_64 --linker-script kernel.ld -o kernel.elf
```

Emit the linker script used for the build so you can inspect or customize it:

```bash
./build/zorb build main.zorb --target bare-metal-x86_64 --emit-linker-script kernel.ld -o kernel.elf
```

`bare-metal-x86_64` preserves `_start`, sets `Builtin.IsBareMetal`, routes
`std.io.write(...)` to the x86_64 debug port `0xE9`, emits an ELF object through
LLVM, and links a kernel ELF with an available `ld.lld`. The target can be built
from x86_64 Linux or Windows hosts with either the bundled linker script or the
script passed to `--linker-script`.
`run` is intentionally unsupported for bare-metal output.

## Windows Host Builds

For native Windows builds, the recommended hosted linker driver is `clang-cl`.
It integrates with the normal Windows/MSVC link environment, which makes it
the most convenient path for Zorb programs that use the Windows-facing
standard-library bindings in `runtime/std/io.zorb` and `runtime/std/os.zorb`.

Build a native Windows executable:

```powershell
./build/zorb.exe build main.zorb -o out.exe
```

Compile and run a program on the current Windows host:

```powershell
./build/zorb.exe run main.zorb
```

Select the hosted Windows target explicitly:

```powershell
./build/zorb.exe build main.zorb --target host-windows -o out.exe
```

Notes:

- Windows `build` and `run` default to `host-windows` and use a generated hosted `main` shim when source defines `_start`.
- `clang-cl` is the recommended Windows toolchain.
- Windows GNU/MinGW hosted output is not supported. LLVM removes the old C
  compiler restriction for bare-metal ELF output, but it does not implicitly
  provide a MinGW runtime, ABI, or standard-library binding layer.

## Test

Run the full fixture suite:

```bash
python scripts/test_compiler.py
```

The Python runner bootstraps the native compiler from the pinned preceding
release when needed; it never invokes .NET. Every catalog input is checked
against its declared native outcome and structured diagnostic phase. Supported
successful inputs are emitted through LLVM, and runtime fixtures are built and
executed through the native driver. Focused `expect-llvm.txt` files may assert
stable IR details where verifier and runtime coverage are not specific enough.

Known recovery-to-native migration gaps are explicit in
`tests/native-suite-exclusions.json` and are printed as `SKIP` records. The
runner rejects stale exclusions that do not name a catalog input. The C# project
is retained solely for explicit recovery work and is not part of normal tests
or CI.

Current runtime coverage is strongest on Linux and on Windows host targets in
CI. An AArch64 Linux lane is available on Linux hosts with `aarch64-linux-gnu-gcc`
plus `qemu-aarch64`:

```bash
python scripts/test_compiler.py \
  --target host-linux-aarch64 \
  --runtime-target host-linux-aarch64
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

Representative larger examples live in [`examples/`](./examples) and the executable fixture corpus under [`tests/csharp/fixtures/`](./tests/csharp/fixtures).

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

- `seed/csharp/`: lexer, parser, semantic checker, CLI, and LLVM backend IR emission
- `tests/csharp/`: fixture runner and regression fixtures
- `std/`: standard library modules used by runtime-oriented examples
- `SEMANTICS.md`: language behavior and current design constraints
