# Compiler Fixtures

This directory contains fixture-based regression tests for the Zorb compiler.

Each fixture is one subdirectory with at least:

- `main.zorb`

Optional files control what the test expects:

- `expect-phase.txt`
- `expect-errors.txt`
- `expect-generated.txt`
- `expect-generated-windows.txt`
- `expect-generated-counts.txt`
- `expect-generated-counts-windows.txt`
- `expect-generated-full.c`
- `expect-generated-full-windows.c`
- `expect-stdout.txt`
- `expect-stderr.txt`
- `expect-exit.txt`
- `expect-stdout-windows.txt`
- `expect-stderr-windows.txt`
- `expect-exit-windows.txt`
- `expect-stdout-aarch64.txt`
- `expect-stderr-aarch64.txt`
- `expect-exit-aarch64.txt`

## Default Behavior

If a fixture only contains `main.zorb`, the test expects:

- parsing succeeds
- semantic analysis succeeds
- code generation succeeds

## `expect-phase.txt`

This declares the phase where the fixture is expected to stop.

Allowed values:

- `success`
- `parse`
- `semantic`
- `codegen`

Examples:

```text
semantic
```

```text
codegen
```

If `expect-phase.txt` is omitted, the runner assumes `success`.

## `expect-errors.txt`

This file contains substrings that must appear in the collected diagnostics for a failing fixture.

Rules:

- one expected substring per line
- blank lines are ignored
- lines starting with `#` are ignored

Example:

```text
Cannot cast Error Union to non-pointer type
```

## `expect-generated.txt`

This file contains substrings that must appear in generated C output for a successful fixture.

Use this when you want lightweight assertions without snapshotting the full file.

Example:

```text
uint8_t buf[4];
buf[1] = item;
```

### `expect-generated-windows.txt`

If present and the suite is running on a Windows host, this file overrides `expect-generated.txt`.

Use it when generated C intentionally differs between Windows-hosted and Linux-hosted output, for example when a GCC-only attribute is omitted on Windows.

## `expect-generated-counts.txt`

This file asserts that a generated substring appears an exact number of times.

Format:

```text
<substring> => <count>
```

Example:

```text
int64_t util_answer() => 2
```

### `expect-generated-counts-windows.txt`

If present and the suite is running on a Windows host, this file overrides `expect-generated-counts.txt`.

## `expect-generated-full.c`

This file is a full snapshot of the generated C output.

Use this for representative programs where exact output stability matters.

The runner compares the generated text to this file exactly, after newline normalization.

### `expect-generated-full-windows.c`

If present and the suite is running on a Windows host, this file overrides `expect-generated-full.c`.

## Runtime Expectations

If either Linux host runtime expectation file is present, the runner also:

1. regenerates the fixture with `_start` preserved and `-nostdlib` semantics enabled
2. compiles the generated C with:

```bash
gcc -O2 -nostdlib -fno-pie -no-pie -z execstack -fno-builtin out.c -o out
```

3. executes the resulting binary

### `expect-stdout.txt`

This file is matched against the program's stdout exactly after newline normalization.

### `expect-stderr.txt`

This file is matched against the program's stderr exactly after newline normalization.

### `expect-exit.txt`

This file contains the expected integer process exit code.

If `expect-exit.txt` is omitted but runtime expectations are enabled, the runner assumes exit code `0`.

### `expect-stdout-windows.txt`

This enables an additional hosted Windows runtime pass for the fixture when the suite is run on a Windows host.

The runner will:

1. regenerate the fixture with hosted output enabled
2. compile the generated C with `clang-cl` or `cl`
3. execute the resulting `.exe`

Stdout is matched exactly after newline normalization.

### `expect-stderr-windows.txt`

This file is matched against the hosted Windows runtime pass stderr exactly after newline normalization.

### `expect-exit-windows.txt`

This contains the expected integer process exit code for the hosted Windows runtime pass.

If `expect-exit-windows.txt` is omitted but hosted Windows runtime expectations are enabled, the runner falls back to `expect-exit.txt` when present, otherwise it assumes exit code `0`.

### `expect-stdout-aarch64.txt`

This enables an additional Linux AArch64 runtime pass for the fixture.

The runner will:

1. cross-compile the generated C with:

```bash
aarch64-linux-gnu-gcc -O2 -nostdlib -static -fno-pie -no-pie -z execstack -fno-builtin out.c -o out-aarch64
```

2. execute it with:

```bash
qemu-aarch64 ./out-aarch64
```

Stdout is matched exactly after newline normalization.

### `expect-stderr-aarch64.txt`

This file is matched against the Linux AArch64 runtime pass stderr exactly after newline normalization.

### `expect-exit-aarch64.txt`

This contains the expected integer process exit code for the Linux AArch64 runtime pass.

If `expect-exit-aarch64.txt` is omitted but AArch64 runtime expectations are enabled, the runner assumes exit code `0`.

## Running The Suite

From the repo root:

```bash
dotnet run --project Zorb.Compiler.Tests/Zorb.Compiler.Tests.csproj
```

## Updating Snapshots

To refresh every `expect-generated-full.c` snapshot from current codegen output:

```bash
dotnet run --project Zorb.Compiler.Tests/Zorb.Compiler.Tests.csproj -- --update-snapshots
```

Only use this after reviewing that the codegen change is intentional.

## Adding A New Fixture

1. Create a new directory under `Zorb.Compiler.Tests/fixtures/`.
2. Add `main.zorb`.
3. Add any imported `.zorb` files the fixture needs.
4. Add expectation files only for the checks you care about.
5. Run the suite.

For a new snapshot fixture:

1. Add `main.zorb`.
2. Add `expect-generated-full.c` with an initial placeholder or copied output.
3. Run:

```bash
dotnet run --project Zorb.Compiler.Tests/Zorb.Compiler.Tests.csproj -- --update-snapshots
```

4. Review the snapshot diff.

## Current Design Note

Imported files are now lexed and parsed during the initial parse walk of the import graph, so malformed imported source is classified as `parse`.
