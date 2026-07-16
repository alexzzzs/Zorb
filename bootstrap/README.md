# Bootstrap seed artifacts

This directory defines the verified seed-artifact contract. Local binaries are
written under `bootstrap/artifacts/` and intentionally ignored by Git; release
automation publishes them separately and updates `manifest.json` with their
target, URL, and SHA-256 digest.

Build local seed checkers with:

```bash
./scripts/build-bootstrap-seeds.sh --target host-linux
```

Resolve a cached or published seed with:

```bash
./scripts/resolve-bootstrap-seed.sh host-linux
```

For a custom seed output directory, pass that directory back to the resolver:

```bash
./scripts/build-bootstrap-seeds.sh --target host-linux --output-dir /tmp/zorb-seeds
./scripts/resolve-bootstrap-seed.sh host-linux --artifact-dir /tmp/zorb-seeds
```

The resolver verifies the SHA-256 checksum for both local and downloaded
artifacts before returning a seed path.

The current artifacts are native frontend checkers used by the recovery and
fixed-point workflow. They include native Backend IR emission, but intentionally
do not contain the LLVM backend or the end-user `check`/`build`/`run` driver.
Build the integrated end-user compiler with `scripts/bootstrap-compiler.sh`.
