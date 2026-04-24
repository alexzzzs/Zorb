# Zorb Roadmap

This document turns the current state of Zorb into a concrete `0.2` plan.

The goal for `0.2` is not "more syntax". The goal is to make Zorb feel like a usable small systems language with tighter semantics, better diagnostics, and one real dogfood program that exercises the language end to end.

## Guiding Rule

For the `0.2` cycle, new features should be added only if they unblock:

- the dogfood target in this document
- semantic consistency with [SEMANTICS.md](./SEMANTICS.md)
- materially better diagnostics or portability

Work that does not help one of those three should usually wait until after `0.2`.

## Dogfood Target

Build a standalone Zorb lexer demo in Zorb.

Why this target:

- It fits the current standard library and runtime model.
- It exercises structs, loops, slices, arrays, error handling, numeric parsing, and string handling.
- It is directly relevant to the compiler project.
- It does not require filesystem APIs, command-line argument parsing, or stdin support before the language is ready for them.

Scope:

- tokenize a source string embedded in the program
- produce a token stream into a caller-owned buffer or allocator-backed array
- report token kind, span, and simple lexer errors
- include one example program and one end-to-end regression test

Non-goals for the first dogfood version:

- full self-hosting
- parsing files from disk
- Unicode-heavy identifier handling beyond current compiler behavior
- reproducing every compiler token kind on day one

## Milestone Plan

### M1. Spec Audit And Freeze

Goal: stop semantic drift for one cycle.

Tasks:

- Audit [SEMANTICS.md](./SEMANTICS.md) against the implementation.
- Mark each mismatch as one of:
  - compiler bug
  - spec bug
  - postponed behavior
- Add or tighten fixtures for every resolved mismatch.
- Avoid new surface syntax unless the dogfood target forces it.

Exit criteria:

- every known spec mismatch is tracked by a fixture or a documentation update
- the semantics document no longer relies on implementation-shaped shortcuts for current behavior

### M2. Constant Evaluation And Numeric Cleanup

Goal: remove the current early-compiler limits around integer constants.

Tasks:

- implement constant evaluation for integer expressions
- allow named `const` values in array sizes where semantically valid
- support constant folding for global initializers that already lower cleanly to C
- document integer-literal typing rules
- document overflow behavior for compile-time evaluation
- harden signed/unsigned comparison and cast diagnostics

Exit criteria:

- array sizes are no longer limited to raw numeric literals in common cases
- constant-expression behavior is specified and covered by fixtures
- the numeric model in [SEMANTICS.md](./SEMANTICS.md) matches the implementation

### M3. Dogfood Implementation

Goal: build the first real Zorb program that stresses the language instead of isolated fixtures.

Tasks:

- create `examples/dogfood/lexer/` with a small standalone lexer demo
- define token structs and token-kind constants in Zorb
- implement scanning for identifiers, integers, punctuation, strings, and comments
- return lexer errors through existing error-union flows
- exercise slices and allocator-backed storage instead of ad hoc globals where practical
- add a runtime fixture or example validation case that compiles and runs the lexer demo

Exit criteria:

- the lexer demo builds and runs on the supported hosted target path
- the demo is readable enough to serve as an example of real Zorb code
- at least one missing language/runtime ergonomic issue discovered by dogfooding is fixed in the cycle

### M4. Diagnostics And Ergonomics

Goal: make the compiler feel more deliberate under failure.

Tasks:

- improve type mismatch messages with actual and expected types
- improve import/path diagnostics with the failing path and importer location
- improve target-specific errors so unsupported target combinations are obvious
- add "declared here" or equivalent source-location notes where feasible
- review parser errors that still collapse into generic failures

Exit criteria:

- common type and import failures produce specific, localizable messages
- newly added diagnostics are covered by fixture expectations

### M5. Release Hardening

Goal: ship `0.2` as a stability release.

Tasks:

- keep the full fixture suite green on normal development hosts
- add one explicit end-to-end regression lane for the dogfood lexer example
- review examples for outdated syntax or style drift
- update [README.md](./README.md), [SEMANTICS.md](./SEMANTICS.md), and [CHANGELOG.md](./CHANGELOG.md) together near release
- tag the release only after docs and behavior line up

Exit criteria:

- fixture suite passes
- dogfood example passes
- docs reflect the shipped behavior

## Priority Order

If time is limited, do the work in this order:

1. Spec audit.
2. Constant evaluation for integer expressions and array sizes.
3. Dogfood lexer implementation.
4. Diagnostics pass driven by dogfood pain points.
5. Release polish.

## Immediate Backlog

These are the next concrete tasks to open or start now:

1. Add fixtures for constant integer expressions in array sizes, global initializers, and attribute arguments where allowed.
2. Design the compile-time integer model in `SEMANTICS.md`, including overflow and literal typing.
3. Add a `examples/dogfood/lexer/main.zorb` skeleton with a fixed input string and token-printing output.
4. Decide whether the dogfood lexer stores tokens in a fixed buffer first or uses `std.mem.HeapAllocator`.
5. Add one tracking issue or checklist for spec mismatches discovered during the audit.

## Explicitly Deferred Until After 0.2

These are important, but they are likely the wrong tradeoff before the language is tighter:

- generics
- traits or interfaces
- macro systems
- another backend besides C
- package management
- optimizer work beyond obvious correctness fixes
- broad stdlib expansion unrelated to the dogfood target

## 0.2 Release Criteria

Call `0.2` done when all of the following are true:

- Zorb can build and run the dogfood lexer example cleanly.
- Constant integer evaluation is implemented for normal systems-language use cases.
- The numeric model is documented and matches reality.
- The semantics document and fixture suite agree on current behavior.
- Diagnostics are noticeably sharper for common failures.

