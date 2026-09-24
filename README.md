# Zorb

Zorb is an ahead-of-time systems language compiler. The production compiler
combines a Zorb frontend with an in-process Zig 0.16 and LLVM 22 backend. A
pinned Zorb release is the normal bootstrap seed; the C# compiler is kept for
explicit recovery only.

## Quick start

Bootstrap the compiler, check a source file, then build or run it:

```bash
python scripts/bootstrap_compiler.py bootstrap
./build/zorb check main.zorb
./build/zorb build main.zorb -o main
./build/zorb run main.zorb
```

Bootstrap verifies a pinned compiler seed. Building the backend from source
requires Zig 0.16 and LLVM 22; executable output also needs the host linker
(`cc` on Linux or `clang-cl` on Windows). See the
[bootstrap guide](docs/BOOTSTRAPPING.md) for offline seeds and recovery builds.

## Targets

`build` supports `host-linux`, `freestanding-linux`, `host-linux-aarch64`,
`freestanding-linux-aarch64`, `host-windows`, and `bare-metal-x86_64`.
`bare-metal-x86_64` produces a kernel ELF and does not support `run`. Hosted
Windows uses the MSVC ABI; MinGW output is not supported. See the
[backend target matrix](backend/llvm/README.md#supported-output-targets) for
host and toolchain requirements.

Emit LLVM IR, assembly, object files, or bitcode without linking:

```bash
./build/zorb build main.zorb --output-kind llvm-ir -o out.ll
```

## Tests and releases

Run the native fixture suite with:

```bash
python scripts/test_compiler.py
```

The runner bootstraps the production compiler as needed and does not invoke
.NET. Remaining recovery-to-native gaps are listed in
[`tests/native-suite-exclusions.json`](tests/native-suite-exclusions.json).

Publish a standalone compiler package for the current host with:

```bash
python scripts/bootstrap_compiler.py publish
```

## Examples

Runnable examples are in [`examples/`](examples/). Start with
[import aliasing](examples/basics/import_alias/main.zorb),
[generics](examples/basics/generics.zorb), or the
[bare-metal kernel](examples/baremetal/hello_kernel.zorb).

## Documentation

| Guide | Covers |
| --- | --- |
| [Editor support](docs/EDITOR%20SUPPORT.md) | Language server setup and current editor features |
| [Language reference](docs/LANGUAGE%20REFERENCE.md) | Syntax and supported language features |
| [Semantics](docs/SEMANTICS.md) | Type checking and runtime behavior |
| [Standard library](docs/STANDARD%20LIBRARY.md) | Public library APIs and target support |
| [Compiler architecture](docs/ARCHITECTURE.md) | Frontend, backend, and bootstrap roles |
| [Backend IR](docs/BACKEND%20IR.md) | Frontend/backend contract |
| [Bootstrapping](docs/BOOTSTRAPPING.md) | Seed verification, recovery, and publishing |
| [Self-hosting](docs/SELF%20HOSTING.md) | Frontend parity and verification |

## Repository layout

- `compiler/`: production Zorb frontend and driver
- `runtime/std/`: standard library
- `backend/llvm/`: Zig and LLVM backend
- `seed/csharp/`: recovery compiler
- `tests/`: native fixtures and test harnesses
- `scripts/`: bootstrap, build, test, and release tools
