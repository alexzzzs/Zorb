# Bootstrapping Zorb

## Normal workflow

The normal bootstrap resolves the preceding integrated compiler release from
`bootstrap/manifest.json`, verifies its pinned SHA-256 digest, and uses it to
build the current driver with the local Zig/LLVM backend:

```bash
python scripts/bootstrap_compiler.py bootstrap
./build/zorb check compiler/self-check/fixtures/simple.zorb
./build/zorb build compiler/self-check/fixtures/simple.zorb -o ./build/simple
./build/zorb run compiler/self-check/fixtures/simple.zorb
```

The implementation requires Python 3.10 or newer, uses only the standard
library, and supports Linux x64, Linux ARM64, and Windows x64. The shell and
PowerShell scripts in `scripts/` are compatibility wrappers around the Python
commands.

## Release provenance

Release jobs package Linux and Windows output with the same deterministic
standard-library helper:

```bash
python scripts/package_release.py <package-dir> <release.zip> \
  --target <target> --version <version> --commit <commit>
```

It recursively records sorted POSIX paths, fixed ZIP timestamps and file
permissions, and no host-specific archive metadata. The adjacent canonical
`<release>.provenance.json` records the target, version, commit, and SHA-256
digest for every packaged file. The release then publishes `SHA256SUMS` and
signatures for both that checksum file and every provenance manifest. It also
publishes `MINISIGN_PUBLIC_KEY` and `MINISIGN_PUBLIC_KEY.sha256`. Release CI
requires the protected `MINISIGN_SECRET_KEY`, `MINISIGN_PUBLIC_KEY`, and
`MINISIGN_PUBLIC_KEY_FINGERPRINT` secrets, and fails closed unless the
published public key's SHA-256 matches the protected fingerprint. No private
key is checked into the repository.

Verify downloaded release metadata with:

```bash
sha256sum -c MINISIGN_PUBLIC_KEY.sha256
minisign -Vm SHA256SUMS -x SHA256SUMS.minisig -p MINISIGN_PUBLIC_KEY
for manifest in *.provenance.json; do
  minisign -Vm "$manifest" -x "$manifest.minisig" -p MINISIGN_PUBLIC_KEY
done
```

The v0.2.4 bootstrap manifest remains SHA-256-only because bootstrap currently
verifies those archive digests directly. The corresponding release also
publishes signed checksum and provenance metadata for independent verification.

The resulting `zorb` executable implements `check`, `build`, and `run` and does
not invoke a separate backend executable. `--target`, `--output-kind`, and
`-O0` through `-O3` are accepted by `build`; `run` accepts target and
optimization selection. Development bootstrap links the local shared LLVM
installation. Release publication uses static LLVM on Linux and the packaged
LLVM C API DLL on Windows.

Use an explicit local compiler instead of the manifest seed when needed:

```bash
python scripts/bootstrap_compiler.py bootstrap --seed /path/to/zorb
```

## Seed resolution

Resolve a target-specific compiler package directly with:

```bash
python scripts/bootstrap_seed.py resolve
python scripts/bootstrap_seed.py resolve host-windows
```

The resolver checks `bootstrap/artifacts/<target>/` first, requiring a matching
`.sha256` file. Otherwise it downloads the immutable release ZIP declared in
the manifest, verifies the archive before extraction, and caches the complete
package under `build/bootstrap/<target>/<digest>/`. Keeping the whole package
is required on Windows because `LLVM-C.dll` lives beside `zorb.exe`.

Cache an existing integrated compiler for offline bootstrap:

```bash
python scripts/bootstrap_seed.py cache-local \
  --target host-linux \
  --compiler build/zorb
```

`ZORB_BOOTSTRAP_SEED`, `ZORB_BOOTSTRAP_MANIFEST`, and
`ZORB_BOOTSTRAP_CACHE_DIR` override the compiler, manifest, and cache paths for
automation. Command-line options take precedence where both are supplied.

## Explicit C# recovery

A source checkout should use C# only when there is no released compiler for a
new host target or the release chain must be repaired:

```bash
python scripts/bootstrap_compiler.py bootstrap --recovery-csharp
```

The recovery option is mutually exclusive with `--seed`. Normal bootstrap and
publishing fail with a clear error when a target has no manifest entry; they do
not silently fall back to C#.

The pinned v0.2.4 packages provide portable seeds for Linux x64, Linux ARM64,
and Windows x64. Normal bootstrap and publishing on those hosts do not invoke
C# or require .NET. `--recovery-csharp` remains available only for a new host
target or explicit repair of the release chain.

The native frontend checker remains available as a bootstrap probe:

```bash
python scripts/bootstrap_compiler.py self-check
./build/zorb-self-check --json compiler/self-check/fixtures/simple.zorb
```

It validates source, emits structured diagnostics, and can emit Backend IR, but
it is not the packaged end-user entry point.

## Fixed-point release workflow

Publish a standalone compiler for the current host with:

```bash
python scripts/bootstrap_compiler.py publish
```

The cross-platform publisher performs this chain:

```text
released zorb N → generation 1 → generation 2 → generation 3
```

Each release proves:

1. the pinned seed builds the candidate;
2. generation 1 rebuilds the production driver;
3. generation 2 rebuilds it once more; and
4. generation 2 and generation 3 are byte-identical.

The comparison covers the frontend, native Backend IR lowering, LLVM emission,
embedded backend library, and linker orchestration. A hash mismatch is a release
failure even when both binaries compile ordinary fixtures.

Linux publishing detects x86_64 versus AArch64 and statically links LLVM.
Windows publishing uses the MSVC ABI and copies the matching `LLVM-C.dll` into
the package. CI places deliberately failing `dotnet` shims around normal seeded
bootstrap and publishing on Linux x64, Linux ARM64, and Windows x64.

The C# recovery source need not implement new language features. It should stay
frozen to the pinned bridge needed to repair the bootstrap chain.

## Native test workflow

Run the production compiler regression suite without .NET on every supported
host:

```bash
python scripts/test_compiler.py
```

The command resolves or builds `build/zorb`, validates the fixture catalog,
checks structured diagnostic outcomes, emits LLVM IR, executes host runtime
fixtures, and verifies CLI target behavior. CI passes explicit target profiles
for Windows and AArch64. `tests/native-suite-exclusions.json` is the reviewed,
machine-validated list of remaining recovery-to-native migration work.
