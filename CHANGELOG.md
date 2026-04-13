# Changelog

All notable changes to this project will be documented in this file.

## [Unreleased]

### Added

- Semantic validation that rejects `catch` expressions inside global initializers before code generation.
- Regression coverage for rejected global-initializer `catch` expressions.
- Expanded Windows hosted CI smoke coverage across multiple runtime fixtures.
- Additional checked-in examples for import aliasing, error handling, and cross-platform stdlib usage.
- Unary boolean negation with `!`.
- Boolean equality and inequality comparisons with `==` and `!=`.
- `break` statement support for exiting the nearest enclosing `while` loop.
- Semantic validation that rejects `break` outside loop bodies.
- Regression coverage for valid loop-breaking runtime behavior and invalid out-of-loop `break` usage.
- Windows host support for `build` and `run`, with hosted output that recommends `clang-cl` and can also use `cl.exe`.
- CLI host defaults that keep Linux `build` and `run` freestanding while using hosted output on Windows.
- Windows build guidance in the README, including toolchain and linker expectations for standard-library-based programs.
- GitHub Actions Windows smoke coverage that builds and runs the hello-world fixture through the hosted Windows CLI path.

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
