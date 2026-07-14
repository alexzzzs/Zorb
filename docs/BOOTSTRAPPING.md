# Bootstrapping Zorb

## Current workflow

The C# stage-0 compiler builds the native Zorb frontend checker:

```bash
./scripts/bootstrap-native-frontend.sh
./build/zorb-self-check --json compiler/self-check/fixtures/simple.zorb
```

Pass an output path as the script's first argument to choose another location.
The checker currently validates source and emits structured diagnostics; it is
not yet the packaged end-user compiler because backend IR emission remains in
the C# pipeline.

## Seed artifacts

Build a target-specific local seed artifact and checksum:

```bash
./scripts/build-bootstrap-seeds.sh --target host-linux
./scripts/resolve-bootstrap-seed.sh host-linux
```

Local artifacts are cached under `bootstrap/artifacts/<target>/` and are not
committed. `bootstrap/manifest.json` is the checked-in contract for published
artifacts; once release automation supplies an artifact URL and SHA-256, the
resolver downloads and verifies it when no local seed exists. See
[`bootstrap/README.md`](../bootstrap/README.md) for the artifact format.

## Self-hosted release workflow

The eventual normal release chain is:

```text
released zorb N → builds zorb N+1 → rebuilds/verifies zorb N+1
```

Each release should prove:

1. the bootstrap compiler builds the candidate;
2. the candidate checks the admitted differential corpus;
3. the candidate rebuilds the compiler source; and
4. the rebuilt candidate checks the same corpus.

## Recovery bootstrap

A source checkout without a released `zorb` binary needs a pinned bootstrap
toolchain. Until the native compiler is fully self-hosted, that toolchain is
the C# seed in `seed/csharp/`. After self-hosting, releases should publish a
verified seed binary (or a bootstrap manifest that downloads one) for every
supported host target. Keep the C# seed source and build instructions as a
recovery path, but do not require it to implement new language features.

The seed must only compile a pinned bridge compiler source; that bridge then
builds the current compiler. This prevents future compiler source from being
limited by the seed language subset.
