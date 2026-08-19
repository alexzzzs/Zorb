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

## Release packaging and provenance

Release ZIPs are created by the deterministic, standard-library-only helper:

```bash
python scripts/package_release.py <package-dir> <release.zip> \
  --target <target> --version <version> --commit <commit>
```

The helper sorts all archive paths as POSIX paths and fixes ZIP timestamps,
permissions, and metadata. It writes the canonical
`<release>.provenance.json` sidecar with the target, version, commit, and
SHA-256 digest of every packaged file. Release CI also publishes `SHA256SUMS`,
`SHA256SUMS.minisig`, and a `.minisig` signature beside each provenance file;
signing uses the protected GitHub Actions `MINISIGN_SECRET_KEY`,
`MINISIGN_PUBLIC_KEY`, and `MINISIGN_PUBLIC_KEY_FINGERPRINT` secrets. CI fails
closed unless the SHA-256 of the published public key matches the protected
fingerprint, and publishes `MINISIGN_PUBLIC_KEY` with its
`MINISIGN_PUBLIC_KEY.sha256` checksum. No private key is stored in this
repository.

After downloading the release metadata, verify the public key and signatures:

```bash
sha256sum -c MINISIGN_PUBLIC_KEY.sha256
minisign -Vm SHA256SUMS -x SHA256SUMS.minisig -p MINISIGN_PUBLIC_KEY
for manifest in *.provenance.json; do
  minisign -Vm "$manifest" -x "$manifest.minisig" -p MINISIGN_PUBLIC_KEY
done
```

The v0.2.4 entries in `manifest.json` remain SHA-256-only because bootstrap
currently verifies the archive digest directly. The corresponding release also
publishes signed checksum and provenance metadata for independent verification.

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

Linux x64, Linux ARM64, and Windows x64 use the pinned v0.2.4 release packages.
That release completed the one-time recovery transition for portable Windows
and ARM64 seeds, so normal bootstrap and publishing on every supported host no
longer require .NET.
