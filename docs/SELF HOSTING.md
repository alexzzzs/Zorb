# Self-hosting and frontend parity

This document covers the current parity implementation. For the intended
one-binary compiler architecture and bootstrap chain, see
[Compiler architecture](ARCHITECTURE.md) and
[Bootstrapping Zorb](BOOTSTRAPPING.md).

`zorb-self-check` is the bootstrap verification entry for the same Zorb-written
frontend used by the production driver. It emits the complete versioned backend
IR surface needed by the compiler graph. The C# compiler is a recovery stage 0,
not the release frontend.

The fixed-point gate uses the recovery seed or preceding release to build the
production driver, uses that candidate to rebuild `compiler/driver/main.zorb`,
then uses the rebuilt compiler to build the driver once more. The generation-2
and generation-3 compiler executables must be byte-identical; there is no
normalization step.

The ordinary fixture run is now `python scripts/test_compiler.py`. It creates
the integrated native `zorb` driver from the pinned release seed, validates the
complete catalog directly, emits supported successful inputs through native
Backend IR and LLVM lowering, and executes the applicable runtime corpus. No
.NET runtime participates in this production gate on Linux x64, Linux ARM64,
Linux-to-ARM64 cross tests, or Windows x64.

The remaining recovery-to-native gaps are machine-readable in
`tests/native-suite-exclusions.json`. They are reported as explicit skips and
must reference an existing catalog case with a non-empty reason.

## Parity contract

For each applicable fixture, both frontends must agree on success or failure, phase (`lexical`, `parse`, `import`, or `semantic`), diagnostic category, source file, and an overlapping source span. Diagnostic prose is not part of the contract. Stable diagnostic codes use `phase.category`, for example `lex.invalid-token`, `parse.expected-token`, `import.not-found`, `name.unknown`, `type.not-assignable`, and `flow.missing-return`.

## Current verification scope

The native production gate covers every fixture directory. Successful inputs
must pass parsing, semantic checking, Backend IR lowering, Backend IR
validation, and LLVM IR emission. Negative inputs must fail native checking
with a phase-prefixed structured diagnostic. The compiler graph additionally
has focused native self-check fixtures for aggregate types, control flow,
errors, generics, globals, casts, function values, builtins, and platform
branches.

Backend IR failures attributable to checked source use the same structured
record and source ownership as frontend diagnostics. Unsupported source
constructs use `lower.unsupported`; compiler invariant failures discovered near
a source construct use `lower.internal`. Allocation failures and failures that
cannot be tied to source remain operational errors instead of being presented
as user mistakes.

The differential gate covers all 374 catalog inputs that are eligible for
cross-frontend comparison, including successful programs and lexical, parse,
import, and semantic failures. For failures it compares phase, stable code,
canonical source path, and overlapping source span. `zorb-self-check --json`
emits one result or diagnostic object, while `--dump-tokens` and `--dump-ast`
use the same JSON-lines protocol for stable source-order records.

## Fixture classification

`tests/csharp/frontend-parity.json` is the machine-readable fixture catalog. It
deliberately gives every stage-0 fixture, checked-in example, and native
bootstrap input an explicit feature group, expected outcome, classification,
and optional gate membership. The current catalog has 374 `differential`
entries, seven focused `native-verified` bootstrap probes, and no `deferred`
entries. The executable `fixture_parity_classification` test rejects malformed
entries, missing paths, duplicate records, and source inputs missing from the
catalog.

Only explicit `differential` records with `gate: "frontend"` enter the parity
harness. The seven `native-verified` records are compiler self-check probes
whose purpose is native parser or Backend IR coverage rather than equivalent
stage-0 frontend behavior. Runtime, LLVM-emission, target, linker, and CLI
workflow assertions are also outside this frontend gate.

The Linux bootstrap gate is `frontend_differential`.
`LoadFrontendParityCases` reads its 374 cases directly from the catalog. Set
`ZORB_FRONTEND_PARITY_CASE=<catalog-name>` locally to run one enabled case while
debugging; CI leaves it unset and runs the complete differential set.

GitHub Actions runs the focused contract in the dedicated
`linux-native-frontend-parity` job with
`python scripts/test_compiler.py --frontend-only`. Wider Linux, AArch64, and
Windows jobs use the same Python runner for native LLVM and runtime coverage.
Cross-frontend comparison remains available for explicit recovery maintenance,
but it is not part of the production CI path.

## Session contract

Every invocation owns a fresh heap, source manager, token/interner allocations, AST allocations, and diagnostics. Source buffers remain alive for the full compilation. Operational failures (file I/O and allocation) do not become semantic diagnostics; they are reported at the session boundary. No mutable frontend state may cross between sessions.
