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

The ordinary managed fixture run now also creates (or accepts through
`ZORB_NATIVE_FIXTURE_COMPILER`) the integrated native `zorb` driver. Every
successful fixture must complete a native `build --output-kind llvm-ir`; every
negative fixture must be rejected by native `check` with a structured
diagnostic. This makes all fixture directories a native frontend-and-lowering
gate rather than limiting native validation to the differential subset.

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

Exact cross-frontend diagnostic equivalence is intentionally narrower. Rich
span agreement, recovery ordering, and diagnostic-category parity are admitted
case-by-case through `tests/csharp/frontend-parity.json`; they should not be
confused with the broader native compilation gate. `zorb-self-check --json`
emits one result or diagnostic object, while `--dump-tokens` and `--dump-ast`
use the same JSON-lines protocol for stable source-order records.

## Fixture classification

`tests/csharp/frontend-parity.json` is the machine-readable fixture catalog. It deliberately gives every stage-0 fixture, checked-in example, and native bootstrap input a classification: `deferred`, `native-verified`, or `differential`. Directory scopes classify the unadmitted inventory; explicit records override those defaults with a feature group, expected outcome, rationale, and optional `frontend` gate membership. The executable `fixture_parity_classification` test rejects malformed entries, missing paths, duplicate records, and source inputs left outside every scope.

Only explicit `differential` records with `gate: "frontend"` may enter the parity harness. This makes promotion a reviewable metadata change: first add or retain a `native-verified` record, verify normalized output, then promote it to `differential`. Runtime, LLVM-emission, target, linker, and CLI workflow assertions are outside this frontend gate.

The Linux bootstrap gate is `frontend_differential`. Its current cases mirror the catalog's deliberately narrow, verified set of success, aliased-import, parse, and missing-import inputs; `LoadFrontendParityCases` is the catalog API for making that membership data-driven. For a failure it normalizes and compares phase, stable code, canonical file path, and overlapping one-based spans. Semantic fixtures remain classified but are not enabled until native semantic spans and category coverage are complete. Set `ZORB_FRONTEND_PARITY_CASE=<catalog-name>` locally to run one enabled case while debugging; CI leaves it unset and runs the complete enabled set.

GitHub Actions runs this contract in the dedicated `linux-native-frontend-parity` job. It sets `ZORB_FRONTEND_PARITY_ONLY=1`, builds stage 0 and the native backend, then runs catalog validation, repeated-session bootstrap coverage, and the differential gate without waiting on the wider runtime suite.

## Session contract

Every invocation owns a fresh heap, source manager, token/interner allocations, AST allocations, and diagnostics. Source buffers remain alive for the full compilation. Operational failures (file I/O and allocation) do not become semantic diagnostics; they are reported at the session boundary. No mutable frontend state may cross between sessions.
