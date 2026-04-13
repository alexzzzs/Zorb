# Changelog

All notable changes to this project will be documented in this file.

## [0.1.0] - 2026-04-13

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

- `build` and `run` are currently Linux-host-only workflows.
- Zorb is still an early compiler with a deliberately small supported language subset.
- The current semantic source of truth is `SEMANTICS.md`.
