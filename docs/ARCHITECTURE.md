# Compiler architecture

Zorb's normal compiler has a Zorb-written frontend and a Zig/LLVM backend. The
distributable is one `zorb` executable; users do not choose between frontend
modes or launch a separate backend process.

## Target architecture

```text
zorb executable
├── Zorb frontend
│   ├── lexer
│   ├── parser and AST
│   ├── import graph
│   ├── semantic analysis
│   └── diagnostics
└── Zig/LLVM backend integration
    ├── backend IR contract
    ├── object emission
    └── target linking
```

The frontend source lives in `compiler/`. Its native compilation session and
machine-readable diagnostics live in `compiler/frontend/`; the current hosted
checker entry point is `compiler/self-check/main.zorb`. The standard library
is `runtime/std/`. `backend/llvm/` owns LLVM object emission and platform linking.

## Bootstrap roles

`seed/csharp/` is the C# stage-0 recovery compiler. It can build the native
compiler from a source checkout without a released seed, but it is not the
normal release frontend or a competing user-facing compiler mode. The native
driver in `compiler/driver/` owns `check`, `build`, and `run`; it calls the
static backend API in `backend/llvm/src/api.zig`.

The versioned frontend/backend boundary is documented in
[Backend IR contract](BACKEND%20IR.md).

The bootstrap chain is:

1. C# stage 0 builds a native Zorb compiler when no released seed is present.
2. The native compiler emits the stable backend contract and builds programs
   through the in-process LLVM API.
3. The candidate recompiles the compiler graph until generation-2 and
   generation-3 driver executables are byte-identical.
4. Releases use the preceding verified `zorb`; C# remains only for recovery.

## Repository map

| Path | Responsibility | Long-term role |
| --- | --- | --- |
| `compiler/` | Authoritative Zorb compiler source | Frontend core |
| `compiler/frontend/` | Native session, diagnostics, import graph | Frontend core |
| `compiler/self-check/` | Hosted native checker entry and probes | Bootstrap verification |
| `compiler/driver/` | Production `check`, `build`, and `run` entry | User-facing compiler |
| `runtime/std/` | Zorb standard library/runtime-facing modules | Runtime core |
| `backend/llvm/` | Zig/LLVM backend integration | Backend core |
| `seed/csharp/` | C# stage-0 seed compiler | Frozen recovery seed after self-hosting |
| `tests/csharp/` | Differential and runtime corpus | Test harness and corpus |
| `scripts/` | Build, publish, and bootstrap entry points | Developer interface |

The physical layout now follows these roles. Future moves should preserve the
stable bootstrap-script interface and update import paths in the same change.
