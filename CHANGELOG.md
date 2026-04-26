# Changelog

All notable changes to this project will be documented in this file.

## [0.1.5] - April 24, 2026

### Added

- `for` loops with C-style headers written as `for init; condition; update { ... }`.
- `switch` statements with ordered `case` branches plus an optional `else` branch.
- New hardening fixtures covering `continue`, local `const` declarations, and exported `const` globals.
- Additional hardening fixtures covering imports of exported vs. private symbols, and unknown-attribute parsing.
- Direct CLI argument-regression coverage for help/version paths plus invalid option combinations.
- Slice types written as `[]T`, including `.ptr` and `.len` field access plus slice indexing.
- Array-to-slice coercions for assignment, initialization, struct literals, returns, and function-call arguments when the element types match.
- New slice-focused fixtures covering postfix-syntax rejection, slice parameter lowering, and runtime slice indexing.
- A new `bare-metal-x86_64` target for kernel-oriented builds, including `Builtin.IsBareMetal`, preserved `_start`, and a bundled default linker script.
- Bare-metal CLI support for `--linker-script` and `--emit-linker-script`, plus regression coverage for default-script and custom-script builds.
- Early bare-metal stdlib support including x86_64 debug-port output, bare-metal capability checks, and a minimal example kernel fixture.
- Low-level codegen attributes for `abi(sysv|sysv64|ms|win64)` and `section("...")`.
- Struct layout attributes for `packed`, `layout(explicit)`, and field-level `offset(N)` for hardware-table-style definitions.
- `volatile` as a real type qualifier, in addition to declaration-level attribute syntax, so it can appear in pointer, field, parameter, and cast target types.
- New fixtures covering bare-metal codegen, volatile and ABI attributes, section placement, explicit struct layout, and invalid explicit-layout usage.

### Changed

- The compiler version now advances to the `0.1.6-dev` line after the `0.1.5` release.
- `std.io.write` now takes a `[]u8` buffer instead of raw pointer-and-length arguments, and the string and integer formatting helpers now build on slice-based buffer flows.
- `std.str.reverse` and `std.str.from_i64` now consume `[]u8` buffers instead of separate pointer-and-length arguments.
- The documentation now distinguishes `freestanding-linux` from true bare-metal output instead of using "freestanding" loosely for both.
- Hosted `_start` entry handling now uses a generated `main` shim instead of renaming `_start` directly to `main`, so hosted programs can define both `_start` and `main` without symbol collisions.
- Bare-metal builds now produce a linker-script-driven kernel ELF by default instead of stopping at an object file.

### Fixed

- `continue` is now rejected semantically outside `while` loops, matching the documented loop-control rules.
- Local `const` declarations now parse in statement position instead of being skipped as unsupported syntax.
- `const` declarations now require an initializer, matching the documented syntax instead of falling through to invalid C output.
- The CLI now rejects ignored or contradictory option combinations such as `--keep-c` outside build/run, `-o` with `run`, and `--check` with build/run.
- The generated C backend no longer assumes Linux syscall wrappers or hosted entrypoint lowering for every target.
- Explicit-layout structs now fail during semantic checking when their offsets are incomplete or incompatible with the compiler's byte-precise layout model.

## [Unreleased]

### Added

- A concrete `0.2` milestone roadmap in `ROADMAP.md`, centered on semantic tightening, constant evaluation, and a first dogfood lexer target.
- Constant integer expression support for fixed-size array types, so array sizes can now resolve from expressions such as local or global `const` values instead of raw numeric literals only.
- Constant integer expression support for compile-time-only attribute arguments, including `align(...)` on variables, functions, and structs plus `offset(...)` on explicit-layout struct fields.
- Constant-folding support for global integer initializers, with semantic rejection for division by zero and `i64` overflow in those constant-evaluated contexts.
- New fixture coverage for constant-expression array sizes, folded global integer initializers, constant-expression `align(...)` and `offset(...)` attributes, and the matching non-constant rejection paths.
- New diagnostic fixture coverage for missing import files with file, line, and column reporting.
- A non-fatal compiler warning system, including fixture support for expected warnings.
- New diagnostics fixtures covering unreachable-code warnings, duplicate declaration errors, const assignment, invalid assignment targets, duplicate struct fields, and duplicate switch cases.
- Adds codegen fixtures covering struct returns through error unions, nested struct returns, and return-through-local struct values.
- Includes numeric fixtures for negative literals assigned to unsigned targets, explicit narrowing casts, unsigned-to-signed widening, signedness-mismatch call failures, narrowing return failures, and mixed signed/unsigned comparison warnings.

### Changed

- Semantic analysis and code generation now reuse the shared parsed import graph instead of reparsing imported files during later phases.
- Struct field array sizes now resolve through the same constant-integer rules as variable declarations.
- Function signatures now go through the same pass-2 type-validation path as other declared types, keeping type-resolution behavior more consistent across declarations.
- The semantics documentation now reflects constant-expression array sizes, constant-expression attribute arguments, and folded global integer initializer behavior.
- Semantic diagnostics now consistently report file, line, and column information for more expression and statement failures.
- Type mismatch, operator, visibility, and import diagnostics now include more specific context, including actual operand types and private or non-re-exported symbol wording.
- Pointer-alignment diagnostics that were previously reported as semantic errors now emit warnings and allow compilation to continue.
- The semantic checker now warns about unreachable statements after `return`, `break`, or `continue`.
- The semantic checker now rejects duplicate switch case values, duplicate struct fields, duplicate local declarations, duplicate parameters, invalid assignment targets, and assignments to `const` declarations.
- Numeric-conversion diagnostics now explain whether rejection came from literal range overflow, narrowing, or signedness changes, and they point at `cast(...)` when an explicit conversion is required.
- Mixed signed/unsigned numeric comparisons now emit warnings except in the common literal-fit cases such as comparing a `u8` value with `48`.
- Duplicate declaration diagnostics now include the first declaration location for top-level declarations, locals, parameters, struct fields, and duplicate switch cases.
- Target-host diagnostics now include the current host platform, and CLI target-validation failures consistently reach stderr.

### Fixed

- Imported modules no longer lose semantic normalization work during code generation because the backend now emits from the same parsed import graph the checker already consumed.
- Constant-expression failures in compile-time-only contexts now surface during semantic checking instead of falling through toward invalid generated C.
- Missing import-file diagnostics now point at the import declaration and are reported once.
- Duplicate top-level declarations, duplicate parameters, and duplicate local declarations are now rejected instead of silently overwriting earlier symbols.
- Assignments to `const` declarations, assignments to non-assignable expressions, duplicate struct fields, and duplicate constant switch cases are now rejected during semantic checking.
- Recent semantics and roadmap updates now line up with the landed numeric model, struct-return coverage, and diagnostics behavior instead of leaving those improvements undocumented.

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
