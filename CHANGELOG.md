# Changelog

All notable changes to this project will be documented in this file.

## [Unreleased]

### Added

- A production compiler driver written in Zorb with `check`, `build`, and
  `run` commands, backed by the in-process Zig/LLVM backend through a static C
  ABI library.
- Native Zorb implementations of frontend sessions, Backend IR modeling,
  serialization, and AST-to-IR lowering for the full admitted fixture corpus,
  including control flow, aggregates, generics, error unions, function values,
  inline assembly, globals, and target builtins.
- A native fixture harness that bootstraps the integrated compiler and runs the
  normal fixture suite through native frontend and IR lowering paths.
- Generation-2/generation-3 fixed-point verification for byte-identical native
  compiler rebuilds.
- Standalone compiler publishers for Linux and Windows. Linux release builds
  statically link LLVM; Windows packages include the required LLVM C API DLL.
- Native Backend IR envelope and scalar-function runtime fixtures, plus
  expanded self-check coverage for the native lowerer.
- Native driver link-policy support for hosted and freestanding Linux,
  cross-compiled AArch64 Linux, hosted Windows, and bare-metal x86_64,
  including bundled or custom linker scripts.

### Changed

- The compiler version now advances to the `0.2.2-dev` line after the
  `0.2.1` release.
- The Zorb-written frontend and IR lowerer are now the normal compiler path;
  the C# compiler remains as the checked-in recovery stage used to bootstrap a
  native compiler from a source checkout.
- The LLVM backend can now be embedded as a library instead of requiring a
  separate backend process.
- Compiler bootstrap, release packaging, CI smoke tests, and self-hosting
  documentation now target the integrated native `zorb` executable.
- Bootstrap seed generation now emits portable checksums without performing an
  unrelated LLVM backend build, and seed resolution accepts a custom artifact
  directory.
- Cross-target and bare-metal executable linking now runs through the native
  driver and embedded backend instead of requiring the C# recovery CLI.

### Fixed

- Native IR lowering now passes the complete fixture suite, including runtime
  inline-assembly execution and the previously unsupported aggregate and
  control-flow cases.
- Native compiler rebuilds now clean up intermediate object files and temporary
  output directories instead of leaving build artifacts behind.
- CI jobs now restore and run the actual C# test project instead of the removed
  `tests/csharp/tests/csharp.csproj` path.
- Windows fixture CI now builds and configures the integrated native
  `zorb.exe` before starting the cross-compiler test harness.
- Local bootstrap seeds are now rejected when their checksum is missing,
  malformed, or does not match the artifact; downloaded seeds use the same
  verification path.
- Native inline-assembly lowering now promotes narrow integer inputs and
  normalizes fixed-register constraints consistently with the recovery seed.

### Removed

- Obsolete standalone LLVM smoke, legacy parser-demo, sample-source, and
  redundant slice self-check files that were not imported, tested, documented,
  or packaged.

## [0.2.1] - July 5, 2026

### Guarantees

- The language subset documented in `README.md` and `docs/SEMANTICS.md`
  remains the stable frontend contract for this release.
- `build` is supported for `host-linux`, `freestanding-linux`,
  `host-linux-aarch64`, `freestanding-linux-aarch64`, `host-windows`, and
  `bare-metal-x86_64`.
- `run` is supported for the hosted runtime paths covered by the fixture
  suite: `host-linux` and `freestanding-linux` on Linux hosts,
  `host-windows` on Windows hosts, and the optional AArch64 Linux runtime
  lane when the documented cross-toolchain and QEMU prerequisites are
  available.
- `bare-metal-x86_64` remains a build-only target with linker-script support.
- `std.os`, `std.io`, `std.str`, and `std.mem` remain the base standard
  library surface across the repo's supported targets. `std.fs` is stable on
  hosted Linux and Windows targets. `std.net`, `std.task`, and `std.async`
  remain target-sensitive and should be gated with their `is_supported()`
  checks in portable code.

### Non-Goals

- Hosted Windows GNU/MinGW output is still not supported.
- `run` remains unsupported for bare-metal output.
- Higher-level networking, process, and general hosted-OS abstractions remain
  outside the current standard-library scope.

### Added

- Generic enum and tagged-union support, including exact nominal typing per
  concrete instantiation, generic union `.Tag` enum members such as
  `Result<i64, bool>.Tag.Ok`, import-alias support, and payload-binding
  `match` over concrete generic unions.
- Additional fixture and runtime coverage for generic enums and unions,
  including import aliases, nominal mismatch diagnostics, non-generic misuse,
  and arity validation.
- Explicit Linux AArch64 CLI targets: `host-linux-aarch64` and
  `freestanding-linux-aarch64`.
- Linux AArch64 cross-build verification in the fixture suite, including LLVM
  target-triple checks, binary build checks, and QEMU-backed CLI runtime tests
  for a focused AArch64 lane.
- Ubuntu CI coverage for the AArch64 Linux lane using `aarch64-linux-gnu-gcc`
  and `qemu-aarch64`.
- Representative frontend runtime-fixture coverage for both
  `freestanding-linux-aarch64` and `host-linux-aarch64`, reusing Linux
  expectations by default while still allowing target-specific overrides.
- Generic function type-argument inference for direct, obvious call sites,
  including imported generic declarations and parameter shapes involving
  pointers, slices, arrays, function types, and error unions.
- Generic type-parameter constraints and trailing default type arguments for
  structs, enums, unions, and non-extern functions, plus fixture and runtime
  coverage for partial explicit instantiations and constraint rejection.
- Additional fixture coverage for inferred generic calls, import-alias generic
  calls, and runtime coercion flows across arrays, pointers, and slices.
- Scalar `match` support for numeric and `bool` expressions, with shared
  ordered-case lowering alongside enum `match` and tagged-union payload
  matching.
- Cross-platform `std.fs` growth with Windows support for `open_read`,
  `open_write`, `close`, `exists`, `size`, `delete`, `rename`, `read_all`,
  and `write_all` through descriptor-backed helpers, plus expanded Linux and
  Windows runtime coverage.
- Async wait timeouts through `std.async.wait_readable_timeout(...)` and
  `std.async.wait_writable_timeout(...)`, plus higher-level
  `std.async.send_exact(...)` and `std.async.recv_exact(...)` helpers.
- Additional async runtime coverage for timeout expiry, helper-based socket
  ping-pong, and waiter fairness beyond the per-poll 64-fiber batch size.
- Generic `extern fn` support, generic function values inferred from expected
  concrete function types, and explicit-layout support for function, slice,
  and error-union fields.

### Changed

- The compiler version now advances to the `0.2.1-dev` line after the
  `0.2.0` release.
- Arrays now decay to exact element pointers in any context that expects `*T`,
  instead of only in function-call argument position.
- Generic calls no longer require explicit type arguments when the concrete
  instantiation can be inferred directly from the argument types.
- Generic function values now infer their concrete instantiation from the
  expected function type in assignability contexts.
- `match` now acts as the general ordered-case branching form for scalars and
  enums, while retaining tagged-union payload binding as its richer extension.
- `std.net`, `std.os`, and `std.task` now report `UnsupportedPlatform`
  consistently for unsupported target paths instead of exposing stale
  `NotImplemented` fallback branches.
- `std.task.spawn(...)` now reserves task budget from the caller allocator
  while backing runtime fiber records and stacks with reclaimable OS pages, so
  completed tasks release their native allocations without changing existing
  out-of-memory behavior.

### Fixed

- Direct expression-statement `catch` bodies may now fall through without a
  fallback value when the result is discarded, while value-producing `catch`
  expressions still require a fallback or control transfer.
- LLVM lowering now applies contextual array-to-pointer coercions consistently
  for returns, stores, and calls, so semantic acceptance and verified backend
  output stay aligned.
- Finished tasks now release their owned runtime stack and fiber-record
  allocations instead of leaking native memory for long-running async/task
  workloads.

## [0.2.0] - June 12, 2026

### Breaking

- The legacy C backend was removed. `--emit-c` and `--keep-c` no longer exist,
  native `build` and `run` are LLVM-only, and downstream workflows must stop
  depending on generated C output.

### Added

- A Zig 0.16 backend that consumes versioned backend IR and emits verified LLVM
  IR, assembly, bitcode, or native object files through LLVM 21.
- `--emit-llvm` for writing verified LLVM IR.
- Production packaging for the LLVM backend and LLD, with static LLVM linkage
  on Linux and a bundled `LLVM-C.dll` on Windows.
- Linux and Windows CI coverage for the Zig backend, packaged release smoke
  tests, and cross-host `bare-metal-x86_64` ELF builds.
- LLVM verifier coverage for every semantically successful fixture and example,
  plus LLVM-built runtime execution for the full host-supported runtime suite.
- Explicit generic structs and functions with multiple type parameters, nested concrete type arguments, imported generic declarations, and monomorphized native output.
- Generic type support across pointers, slices, fixed arrays, function signatures, struct literals, `Builtin.sizeof(...)`, and error unions.
- Generic struct support for `packed`, `align(N)`, `layout(explicit)`, and field `offset(N)` attributes.
- Parser, semantic, code-generation, import, layout, diagnostic, and runtime fixtures covering generic success paths and invalid arity, duplicate parameters, unknown arguments, non-generic misuse, nominal mismatches, and unsupported generic extern declarations.
- `match` statements for enum and tagged-union branching, including exhaustiveness checking and tagged-union payload binding via patterns like `Value.Number(n)`.
- Fixture, runtime, and example coverage for enum matching, union matching, and non-exhaustive match diagnostics.
- `enum` declarations with explicit built-in integer backing types, auto-incremented members, and constant-expression member initializers.
- Qualified enum member references such as `Mode.Run`, including import-alias flows like `pkg.Mode.Run`.
- Enum-aware `switch` checking, including duplicate-case detection by resolved value and exhaustiveness checking when no `else` branch is present.
- Fixture, runtime, and example coverage for enum code generation, import-alias usage, invalid underlying types, duplicate values, and exhaustive switching.
- Tagged `union` declarations with generated `Union.Tag` enums plus literals such as `Value{ Number: 7 }`.
- Union field access through `.tag` and payload fields, including import-alias support for tag members like `pkg.Value.Tag.Number`.
- Fixture, runtime, and example coverage for tagged unions, invalid union declarations, and tagged-union switching.
- Additional stdlib helpers including string equality and prefix/suffix checks, slice copying and zeroing, unsigned integer formatting, boolean and integer print helpers, and slice-based `std.io.read(...)`.
- A Linux-first low-level `std.net` module with raw TCP socket operations, IPv4 socket-address helpers, byte-order helpers, and runtime coverage for socket-create/bind/listen/close flows.
- New runtime fixtures covering repeated async initialization, yielding outside a fiber, and the new stdlib helper set.

### Changed

- Native `build`, `run`, and default emission now use the Zig/LLVM backend.
- C-fragment snapshots and the GCC/QEMU runtime harness were replaced by LLVM
  emission checks, focused LLVM IR assertions, and LLVM-built runtime tests.
- The backend project now lives under `backend/llvm/`.
- Bare-metal x86_64 builds use LLD and are supported from x86_64 Linux and
  Windows hosts. Hosted Windows output remains MSVC ABI; Windows GNU/MinGW is
  not supported.
- Concrete generic struct types now participate in exact nominal type compatibility, and type substitution preserves pointer, slice, array, and error-union wrappers.
- The compiler entrypoint and parser are now split across smaller partial-class source files so CLI/build logic and parsing logic are easier to work on in isolation.

### Fixed

- `std.task.spawn(...)` now rejects unsupported targets up front instead of falling into architecture-specific code paths.
- `std.task.yield()` now returns safely when called outside an active fiber.
- `std.async.init()` now resets its internal polling state on re-entry, and `std.async.loop()` no longer assumes polling was initialized successfully.
- Mixed-width integer operands are explicitly coerced before LLVM arithmetic
  and comparisons, preventing invalid mismatched LLVM instruction types.
- Slice bounds failures preserve the documented exit code `1` on Linux and
  Windows instead of lowering unconditionally to `llvm.trap`.
- Catch fallback expressions now lower through an explicit LLVM phi, catch
  bodies without a fallback or control transfer are rejected, and hosted entry
  shim generation no longer mutates the checked AST.

## [0.1.6] - May 2, 2026

### Added

- Constant integer expression support for fixed-size array types, so array sizes can now resolve from expressions such as local or global `const` values instead of raw numeric literals only.
- Constant integer expression support for compile-time-only attribute arguments, including `align(...)` on variables, functions, and structs plus `offset(...)` on explicit-layout struct fields.
- Constant-folding support for global integer initializers, with semantic rejection for division by zero and `i64` overflow in those constant-evaluated contexts.
- New fixture coverage for constant-expression array sizes, folded global integer initializers, constant-expression `align(...)` and `offset(...)` attributes, and the matching non-constant rejection paths.
- New diagnostic fixture coverage for missing import files with file, line, and column reporting.
- A non-fatal compiler warning system, including fixture support for expected warnings.
- New diagnostics fixtures covering unreachable-code warnings, duplicate declaration errors, const assignment, invalid assignment targets, duplicate struct fields, and duplicate switch cases.
- Adds codegen fixtures covering struct returns through error unions, nested struct returns, and return-through-local struct values.
- Includes numeric fixtures for negative literals assigned to unsigned targets, explicit narrowing casts, unsigned-to-signed widening, signedness-mismatch call failures, narrowing return failures, and mixed signed/unsigned comparison warnings.
- Parser diagnostics for unexpected top-level tokens and malformed expressions now include clearer expected-context wording, with matching parser fixtures.
- Parser diagnostics now give clearer guidance for invalid `export` usage, malformed switch bodies, and missing commas inside attribute lists, with matching fixture coverage.

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
- Recent semantics and project-note updates now line up with the landed numeric model, struct-return coverage, and diagnostics behavior instead of leaving those improvements undocumented.
- README example coverage now calls out the dogfood lexer and bare-metal kernel examples more directly.

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
