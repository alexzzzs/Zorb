# Notes

This project is a hobby compiler.
These are rough notes, not a formal roadmap.

## Current Focus

- keep the compiler, examples, and fixture suite green
- keep `SEMANTICS.md` aligned with actual behavior
- keep dogfooding the lexer example and fix annoyances it reveals
- improve diagnostics when the current errors feel too vague

## Rough Next Steps

- tighten parser errors that still read like generic failures
- keep examples in sync with the current language style
- do a docs cleanup pass before the next tagged release

## Spec Mismatch Checklist

When the implementation and docs disagree:

- decide whether it is a compiler bug, a docs bug, or something postponed
- add or update a fixture so the behavior is pinned down
- update `SEMANTICS.md` if the shipped behavior is intentional

## Explicitly Not A Priority Right Now

- generics
- traits or interfaces
- package management
- another backend
- broad stdlib expansion that does not help current examples
