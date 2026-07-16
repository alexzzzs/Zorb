# Compiler Fixtures

Each fixture is a directory containing `main.zorb`. Imported `.zorb` files and
runtime data files may live beside it.

Optional expectation files:

- `expect-phase.txt`
- `expect-errors.txt`
- `expect-warnings.txt`
- `expect-llvm.txt`
- `expect-llvm-linux.txt`
- `expect-llvm-windows.txt`
- `expect-stdout.txt`
- `expect-stderr.txt`
- `expect-exit.txt`
- `expect-stdout-windows.txt`
- `expect-stderr-windows.txt`
- `expect-exit-windows.txt`
- `expect-stdout-linux-aarch64.txt`
- `expect-stderr-linux-aarch64.txt`
- `expect-exit-linux-aarch64.txt`
- `expect-stdout-host-linux-aarch64.txt`
- `expect-stderr-host-linux-aarch64.txt`
- `expect-exit-host-linux-aarch64.txt`

## Default Behavior

Without expectation files, parsing and semantic analysis must succeed. The
compiler must then emit non-empty, verified LLVM IR containing a target triple.

## Diagnostics

`expect-phase.txt` may contain `success`, `parse`, or `semantic`. It defaults to
`success`.

`expect-errors.txt` and `expect-warnings.txt` contain diagnostic substrings, one
per line. Blank lines and lines beginning with `#` are ignored.

## LLVM Assertions

`expect-llvm.txt` contains stable substrings that must appear in verified
textual LLVM IR:

```text
icmp slt i64
```

Host-specific `expect-llvm-linux.txt` or `expect-llvm-windows.txt` files
override the generic file. Use these sparingly for lowering details that
verifier and runtime checks do not pin down. Avoid full-module snapshots because
harmless LLVM upgrades can change textual IR.

## Runtime Expectations

Any generic stdout, stderr, or exit expectation enables a
`freestanding-linux` runtime pass on Linux. The runner builds the fixture
through LLVM, copies non-source fixture data into an isolated directory, and
executes the resulting binary.

- `expect-stdout.txt` matches stdout exactly after newline normalization.
- `expect-stderr.txt` matches stderr exactly after newline normalization.
- `expect-exit.txt` contains the expected process exit code and defaults to `0`.

Windows-specific expectation files enable a `host-windows` runtime pass when
the suite runs on Windows. Missing Windows values fall back to their generic
counterparts.

When the AArch64 Linux lane is enabled, generic runtime expectations may also
be reused for:

- `freestanding-linux-aarch64`
- `host-linux-aarch64`

Add explicit `*-linux-aarch64.txt` expectation files only when AArch64 behavior
intentionally differs from the generic Linux result.

The current parity bar for the LLVM backend is:

- verified LLVM emission for every semantically successful fixture and example
- Linux runtime execution for fixtures with generic runtime expectations
- AArch64 Linux cross-build verification plus QEMU runtime execution for the
  focused AArch64 lane when enabled
- Windows runtime execution when Windows-specific host expectations are present
- bare-metal build validation through CLI coverage rather than runtime execution

## Running The Suite

From the repository root:

```bash
dotnet run --project tests/csharp/Zorb.Compiler.Tests.csproj
```

The suite requires a built `backend/llvm`. Set `ZORB_LLVM_BACKEND` when the
backend is not discoverable beside the compiler or under the repository build
directory.

## Adding A Fixture

1. Create a directory under `tests/csharp/fixtures/`.
2. Add `main.zorb` and any imported files.
3. Add only the diagnostic, focused LLVM, or runtime expectations needed.
4. Run the complete suite.

Prefer runtime assertions when behavior is observable. Use separate fixtures
for successful behavior and each diagnostic contract.

Imported files are lexed and parsed during the initial import-graph walk, so
malformed imported source is classified as `parse`.
