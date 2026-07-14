# Compiler architecture

Zorb is transitioning to a compiler with a Zorb-written frontend and a
Zig/LLVM backend. The intended distributable is one `zorb` executable; users
will not choose between C# and native frontend modes.

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

`seed/csharp/` is the C# stage-0 seed compiler. It is currently required to
build the native frontend checker and remains the release frontend until the
native frontend can emit the backend contract. It is not a competing
user-facing compiler mode.

The transition has three stages:

1. C# stage 0 builds the native Zorb frontend checker.
2. The native frontend reaches differential parity for the admitted corpus and
   emits the stable backend contract.
3. A released `zorb` binary builds the next release. The C# seed is retained
   only for recovery and bootstrapping.

## Repository map

| Path | Responsibility | Long-term role |
| --- | --- | --- |
| `compiler/` | Authoritative Zorb compiler source | Frontend core |
| `compiler/frontend/` | Native session, diagnostics, import graph | Frontend core |
| `compiler/self-check/` | Hosted native checker entry and probes | Bootstrap verification |
| `runtime/std/` | Zorb standard library/runtime-facing modules | Runtime core |
| `backend/llvm/` | Zig/LLVM backend integration | Backend core |
| `seed/csharp/` | C# stage-0 seed compiler | Frozen recovery seed after self-hosting |
| `tests/csharp/` | Differential and runtime corpus | Test harness and corpus |
| `scripts/` | Build, publish, and bootstrap entry points | Developer interface |

The physical layout now follows these roles. Future moves should preserve the
stable bootstrap-script interface and update import paths in the same change.
