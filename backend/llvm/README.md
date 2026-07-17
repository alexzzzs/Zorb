# Zorb LLVM Backend

This project is the Zig 0.16/LLVM 21 code-generation backend for Zorb. It reads
the versioned JSON backend IR emitted by the native `zorb` driver or recovery
seed and writes LLVM IR, bitcode, assembly, or a target object file.

The build also installs `libzorb-llvm.a`. Its C ABI exports
`zorb_llvm_emit_file` and `zorb_llvm_link_object`; the production Zorb driver
links this archive so backend emission and build/run orchestration happen from
one compiler executable. `zorb-llvm-backend` remains available as a protocol
test and debugging tool.

## Development Build

Install Zig 0.16 and LLVM 21 development headers and libraries, then run:

```bash
zig build test
zig build -Dllvm-prefix=/usr/lib/llvm-21
```

The default build links against the shared LLVM C API for quick local
iteration.

## Static LLVM Build

Linux release packages statically link LLVM component archives:

```bash
zig build \
  -Doptimize=ReleaseSafe \
  -Dstatic-llvm=true \
  -Dllvm-prefix=/usr/lib/llvm-21 \
  -Dcxx-runtime="$(g++ -print-file-name=libstdc++.so)"
```

`-Dcxx-runtime` must point at the C++ runtime ABI used to build LLVM. The
resulting backend has no shared `libLLVM` dependency, though it still uses
ordinary platform runtime libraries such as libc, libstdc++, zlib, and zstd.

## Windows Build

Windows development builds use the shared LLVM C API. The integrated driver
links the generated static Zorb backend API archive and packages the LLVM DLL
beside `zorb.exe`:

```powershell
zig build `
  "-Doptimize=ReleaseSafe" `
  "-Dllvm-prefix=$env:ProgramFiles\LLVM" `
  "-Dllvm-library=LLVM-C"
```

The runtime package contains one user-facing compiler plus LLVM's shared
library:

```text
zorb.exe
LLVM-C.dll
```

Executable linking uses `clang-cl` from `PATH`, matching the MSVC target ABI;
the compiler package does not bundle a host C/C++ toolchain. The standalone
`zorb-llvm-backend.exe` is a development protocol tool and is not required by
the integrated compiler.

## Supported Output Targets

- Linux GNU x86_64 and AArch64 on Linux hosts, including AArch64 cross-linking
  through `aarch64-linux-gnu-gcc` and QEMU-backed execution.
- Windows MSVC x86_64 and AArch64 on matching Windows hosts.
- Bare-metal x86_64 ELF from x86_64 Linux or Windows hosts.

The integrated API receives a stable target policy name separately from the
LLVM triple. This keeps target selection in the Zorb driver while the backend
owns platform linker argv, bundled bare-metal linker scripts, environment
overrides, and process execution.

Hosted Windows GNU/MinGW output is not currently supported.

## Parity Scope

This backend is intended to be feature-complete for the implemented Zorb
language subset on the supported targets above. That does not imply hosted
Windows GNU/MinGW support or bare-metal `run` support.
