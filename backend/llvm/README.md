# Zorb LLVM Backend

This project is the Zig 0.16/LLVM 21 code-generation backend for Zorb. It reads
the versioned JSON backend IR emitted by `Zorb.Compiler` and writes LLVM IR,
bitcode, assembly, or a target object file.

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

The Windows release uses the shared LLVM C API and packages the DLL beside the
backend:

```powershell
zig build `
  "-Doptimize=ReleaseSafe" `
  "-Dllvm-prefix=$env:ProgramFiles\LLVM" `
  "-Dllvm-library=LLVM-C"
```

The release package must contain:

```text
Zorb.Compiler.exe
zorb-llvm-backend.exe
LLVM-C.dll
ld.lld.exe
```

The compiler searches beside its own executable first. Development overrides
are available through `ZORB_LLVM_BACKEND` and `ZORB_LLD`.

## Supported Output Targets

- Linux GNU x86_64 and AArch64 on matching Linux hosts.
- Windows MSVC x86_64 and AArch64 on matching Windows hosts.
- Bare-metal x86_64 ELF from x86_64 Linux or Windows hosts.

Hosted Windows GNU/MinGW output is not currently supported.

## Parity Scope

This backend is intended to be feature-complete for the implemented Zorb
language subset on the supported targets above. That does not imply hosted
Windows GNU/MinGW support or bare-metal `run` support.
