# Editor Support

Zorb ships a language server named `zorb-lsp`. The first version provides
compiler diagnostics while a document is open or being edited.

## Start the server

The standalone compiler archives include both `zorb` and `zorb-lsp`. Configure
your editor to launch `zorb-lsp` over standard input and output. By default, it
looks for `zorb` beside the server executable. To use a different compiler,
pass its path explicitly:

```text
zorb-lsp --compiler /path/to/zorb
```

When building from the repository, `zorb-lsp` is installed at
`backend/llvm/zig-out/bin/zorb-lsp` (or `zorb-lsp.exe` on Windows). The compiler
path can be the bootstrapped `build/zorb` executable.

## Supported features

- Publishes errors and warnings from `zorb check` as the document changes.
- Handles full-document synchronization when files open, change, or close.
- Accepts local `file:` URIs and preserves relative import resolution for
  unsaved document contents.

Completion, hover, navigation, formatting, and workspace-wide diagnostics are
not implemented yet.
