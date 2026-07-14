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

The current artifacts are native frontend checkers. They are not yet the
end-user `zorb` compiler because native backend IR emission has not landed.
The builder compiles the Zig backend before invoking the C# seed; use
`--skip-backend` only when that backend is already built for local iteration.
