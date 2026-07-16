# Bootstrapping Zorb

## Current workflow

The C# recovery stage builds the native Zorb frontend checker:

```bash
./scripts/bootstrap-native-frontend.sh
./build/zorb-self-check --json compiler/self-check/fixtures/simple.zorb
```

Pass an output path as the script's first argument to choose another location.
The checker validates source, emits structured diagnostics, and can emit the
versioned Backend IR used for fixed-point verification. It is a bootstrap probe,
not the packaged end-user entry point.

Build the integrated compiler driver and in-process LLVM backend with:

```bash
./scripts/bootstrap-compiler.sh
./build/zorb check compiler/self-check/fixtures/simple.zorb
./build/zorb build compiler/self-check/fixtures/simple.zorb -o ./build/simple
./build/zorb run compiler/self-check/fixtures/simple.zorb
```

The resulting `zorb` executable implements `check`, `build`, and `run` and does
not invoke a separate backend executable. `--target`, `--output-kind`, and
`-O0` through `-O3` are accepted by `build`; `run` accepts target and
optimization selection. The development bootstrap dynamically links the local
LLVM installation, while release packaging may statically link the same API
library and LLVM component archives.

Compiler or runtime sources that declare additional native symbols can append
exact linker argv entries with the terminal `--native-link-args` option. It is
terminal by design, so every following argument is passed directly to the host
linker without shell parsing. The self-hosting gate uses it to link the
integrated LLVM API while rebuilding `compiler/driver/main.zorb`.

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
When seeds are built in another directory, pass the same location to the
resolver with `--artifact-dir <directory>`. Both local and downloaded seeds are
verified before use.

## Self-hosted release workflow

The normal release chain is:

```text
released zorb N → builds zorb N+1 → rebuilds/verifies zorb N+1
```

Each release should prove:

1. the bootstrap compiler builds the candidate;
2. the candidate passes the native fixture and differential corpus;
3. the candidate rebuilds `compiler/driver/main.zorb`; and
4. that rebuilt compiler rebuilds the driver byte-for-byte.

The generation-2/generation-3 binary comparison covers the frontend, native
Backend IR lowering, LLVM emission, embedded backend library, and linker
orchestration. A hash mismatch is a release failure even when both binaries can
compile ordinary fixtures.

## Recovery bootstrap

A source checkout without a released `zorb` binary needs a pinned bootstrap
toolchain. The C# seed in `seed/csharp/` is the checked-in recovery path;
releases should publish a verified seed binary (or a bootstrap manifest that
downloads one) for every supported host target. The C# seed need not implement
new language features beyond the pinned bridge required to build current Zorb.

The seed must only compile a pinned bridge compiler source; that bridge then
builds the current compiler. This prevents future compiler source from being
limited by the seed language subset.
