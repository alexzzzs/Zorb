# Self-hosted frontend parity

This document covers the current parity implementation. For the intended
one-binary compiler architecture and bootstrap chain, see
[Compiler architecture](ARCHITECTURE.md) and
[Bootstrapping Zorb](BOOTSTRAPPING.md).

`zorb-self-check` is an experimental, Zorb-written frontend checker.  The C# compiler remains the stage-0 compiler and release frontend.  This milestone explicitly excludes backend IR emission, compiler replacement, and changes to language syntax.

## Parity contract

For each applicable fixture, both frontends must agree on success or failure, phase (`lexical`, `parse`, `import`, or `semantic`), diagnostic category, source file, and an overlapping source span. Diagnostic prose is not part of the contract. Stable diagnostic codes use `phase.category`, for example `lex.invalid-token`, `parse.expected-token`, `import.not-found`, `name.unknown`, `type.not-assignable`, and `flow.missing-return`.

## Current feature matrix

| Stable feature group | C# frontend | Zorb lexer | Zorb parser/AST | Zorb semantics | Differential coverage |
| --- | --- | --- | --- | --- | --- |
| Source ownership and spans | complete | line/column only | partial | partial | none |
| Diagnostics and phase codes | prose diagnostics | coarse errors | coarse errors | coarse errors | none |
| Hosted command-line entry | complete | n/a | n/a | n/a | native Linux slice |
| Imports, aliases, visibility, cycles | complete | keywords | relative graph walk | canonical relative loading, direct exported symbols, aliases, and cycle detection | native bootstrap fixtures |
| Declarations and primitive types | complete | partial | qualified names, error-union returns, and structural function types | partial | function-value baseline |
| Expressions and calls | complete | partial | calls, postfixes, array/struct literals, and generic function values partial | callback return typing and direct function-value validation partial | array, generic, and imported callback baselines |
| Control flow and flow analysis | complete | keyword inventory | partial | partial | none |
| Generics and constraints | complete | punctuation/keywords | parameter lists, nested multi-argument type references, explicit calls, generic literals, and delimiter-terminated function values partial | direct explicit-call result substitution, nominal literals, callback values, direct union bindings, and wrapped struct-member element substitution partial | function, struct, nested struct, multi-argument type reference, local/global callback, and imported callback baselines |
| Tagged unions, match, switch, catch | complete | keyword inventory | boolean/enum match, union cases, and direct pattern bindings partial | boolean/enum/union exhaustiveness and direct union binding substitution partial | boolean, enum, generic-union, union-binding, and union-exhaustiveness baselines |
| Attributes, layouts, assembly, extern types | complete | partial | partial | not implemented | none |

The initial executable slice creates a compilation-owned session, retains source buffers for the check, canonically walks relative imports, detects active import cycles, projects only direct exported symbols (including aliases), invokes the existing native lexer/parser/checker, and reports stable phase-prefixed diagnostics. `zorb-self-check --json <entry.zorb>` emits exactly one JSON result or diagnostic object on stdout, while `--dump-tokens` and `--dump-ast` use the same JSON-lines protocol for stable source-order records. Rich spans, parser recovery, and differential fixture gating remain tracked work rather than implied parity.

## Fixture classification

`tests/csharp/frontend-parity.json` is the machine-readable fixture catalog. It deliberately gives every stage-0 fixture, checked-in example, and native bootstrap input a classification: `deferred`, `native-verified`, or `differential`. Directory scopes classify the unadmitted inventory; explicit records override those defaults with a feature group, expected outcome, rationale, and optional `frontend` gate membership. The executable `fixture_parity_classification` test rejects malformed entries, missing paths, duplicate records, and source inputs left outside every scope.

Only explicit `differential` records with `gate: "frontend"` may enter the parity harness. This makes promotion a reviewable metadata change: first add or retain a `native-verified` record, verify normalized output, then promote it to `differential`. Runtime, LLVM-emission, target, linker, and CLI workflow assertions are outside this frontend gate.

The Linux bootstrap gate is `frontend_differential`. Its current cases mirror the catalog's deliberately narrow, verified set of success, aliased-import, parse, and missing-import inputs; `LoadFrontendParityCases` is the catalog API for making that membership data-driven. For a failure it normalizes and compares phase, stable code, canonical file path, and overlapping one-based spans. Semantic fixtures remain classified but are not enabled until native semantic spans and category coverage are complete. Set `ZORB_FRONTEND_PARITY_CASE=<catalog-name>` locally to run one enabled case while debugging; CI leaves it unset and runs the complete enabled set.

GitHub Actions runs this contract in the dedicated `linux-native-frontend-parity` job. It sets `ZORB_FRONTEND_PARITY_ONLY=1`, builds stage 0 and the native backend, then runs catalog validation, repeated-session bootstrap coverage, and the differential gate without waiting on the wider runtime suite.

## Session contract

Every invocation owns a fresh heap, source manager, token/interner allocations, AST allocations, and diagnostics. Source buffers remain alive for the full compilation. Operational failures (file I/O and allocation) do not become semantic diagnostics; they are reported at the session boundary. No mutable frontend state may cross between sessions.
