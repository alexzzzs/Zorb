# Bootstrap compiler seeds

This directory defines the verified seed contract for integrated Zorb compiler
packages. A seed is the preceding released `zorb` compiler, including any
runtime library shipped beside it. It can compile the current production driver;
it is not the frontend-only `zorb-self-check` probe.

`manifest.json` pins each package by immutable release URL and SHA-256 digest.
The cross-platform Python 3.10+ resolver downloads the ZIP, verifies it before
extraction, rejects unsafe archive paths, and caches the complete package by
digest:

```bash
python scripts/bootstrap_seed.py resolve
python scripts/bootstrap_seed.py resolve host-windows
```

Local seed packages live under `bootstrap/artifacts/<target>/` and remain
ignored by Git. Cache an already-built integrated compiler for offline use:

```bash
python scripts/bootstrap_seed.py cache-local \
  --target host-linux \
  --compiler build/zorb
```

The compatibility wrapper `scripts/resolve-bootstrap-seed.sh` invokes the same
Python implementation. The former frontend-only
`scripts/build-bootstrap-seeds.sh` entry point was removed so it cannot be
mistaken for the integrated compiler seed contract.

Linux x64, Linux ARM64, and Windows x64 use the pinned v0.2.3 release packages.
That release completed the one-time recovery transition for portable Windows
and ARM64 seeds, so normal bootstrap and publishing on every supported host no
longer require .NET.
