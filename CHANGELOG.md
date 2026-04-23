# Changelog

All notable changes to this project will be documented in this file.

## [Unreleased]

## [0.1.4] - 2026-04-23

### Added

- Explicit `--target` CLI selection for compiler output modes, with `host-linux`, `freestanding-linux`, and `host-windows` targets.
- Hosted Windows runtime fixture coverage for platform-branching, catch flows, imported-module execution, stderr writes, and nonzero exits.
- New hosted-runtime fixtures for `runtime_host_platform_branch`, `runtime_host_platform_catch`, `runtime_host_import_alias`, `runtime_host_stderr_write`, and `runtime_host_nonzero_exit`.
- Cross-platform stdlib helpers including `std.io.eprint`, `std.io.eprintln`, `std.io.eprint_i64`, `std.os.is_linux`, `std.os.is_windows`, and `std.os.platform_name`.
- Additional stdlib capability helpers including `std.os.is_x86_64`, `std.os.is_aarch64`, `std.os.arch_name`, `std.task.is_supported`, and `std.async.is_supported`.

### Changed

- `build` and `run` now resolve through explicit compilation targets instead of host-only implicit mode selection, while preserving `-nostdlib` as legacy shorthand for `--target freestanding-linux`.
- CLI workflow tests now pass explicit `--target` values and cover freestanding Linux plus hosted Linux and Windows build-run paths.
- `std.io` now routes `print` and `println` through a shared cross-platform write path, and the platform example now uses stdlib helpers instead of raw builtins.
- `std.async` now exposes an explicit support check and exits early on unsupported targets, and `std.task` now exposes a matching support query for fiber-based scheduling.
- The compiler fixture runner now supports Windows-specific runtime expectation files, stderr assertions, and target-aware runtime compilation for hosted Windows versus freestanding Linux paths.
- Windows CLI workflow coverage now includes the hosted platform-branching, catch, import, stderr, and nonzero-exit fixtures in addition to the existing smoke fixtures.
- Windows CI now runs the normal fixture suite instead of a separate smoke script, keeping Linux and Windows fixture coverage aligned.

## [0.1.3] - 2026-04-21

### Added

- Typed struct literals and typed fixed-size array literals, with semantic validation for struct fields and array element counts.
- Local fixed-size array value-copy semantics for declarations and assignments when types match exactly.
- Boolean language features: unary `!`, `==`, `!=`, and short-circuit `&&` / `||` with `bool`-only checking.
- `break` support for exiting the nearest enclosing `while` loop, with semantic rejection outside loop bodies.
- Windows host support for `build` and `run` using hosted output, with `clang-cl` as the recommended toolchain.

### Changed

- Linux `build` and `run` remain freestanding by default, while Windows `build` and `run` now default to hosted output.
- The checked-in examples and language docs were expanded to better cover import aliasing, error handling, literals, cross-platform stdlib usage, and array value-copy semantics.
- The advanced threads example now reflects the current array syntax and pointer-cast and inline-asm operand rules.
- CI now includes broader Windows-hosted smoke coverage and compilation checks for checked-in examples.

### Fixed

- `catch` expressions are now rejected in global initializers before code generation.
- Invalid postfix array type syntax now reports that arrays must be written as `[N]T`, for example `[4]u8`.
- Declared `bool` types are treated consistently as built-in types during semantic validation and C code generation.
- Version metadata and release workflow defaults now advance from the released `0.1.2` line to the `0.1.3-dev` line consistently.

## [0.1.2] - 2026-04-13

Initial public release of Zorb.

### Added

- Ahead-of-time `net8.0` compiler that lowers Zorb source to C.
- Core language support for:
  - functions and `extern fn`
  - structs
  - globals and `const` globals
  - `if`, `else`, `while`, `continue`, and `return`
  - pointers, fixed-size arrays, function types, and error unions
  - imports, including `import "file.zorb" as alias`
  - inline assembly
  - builtins including `Builtin.IsLinux`, `Builtin.IsWindows`, and `Builtin.sizeof(...)`
- Draft language semantics in `SEMANTICS.md`.
- Small standard library under `std/`.
- Fixture-based regression suite with parser, semantic, codegen, and runtime coverage.
- CLI support for:
  - semantic checking with `--check`
  - token dumping with `--dump-tokens`
  - C emission
  - native Linux `build`
  - native Linux `run`
  - optional `--keep-c` output retention for `build` and `run`
- GitHub Actions workflow that builds the compiler and runs the fixture suite on pushes and pull requests.

### Notes

- At the `0.1.2` release, `build` and `run` were Linux-host-only workflows.
- Zorb is still an early compiler with a deliberately small supported language subset.
- The current semantic source of truth is `SEMANTICS.md`.
