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

Linux ARM64 is new in the 0.2.3 development line. Because v0.2.2 did not publish
an ARM64 compiler, v0.2.3 performs one explicit C# recovery build on its native
ARM64 runner. The resulting v0.2.3 package is the seed for subsequent ARM64
releases.

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
the package. CI includes a Linux x64 lane with a deliberately failing `dotnet`
shim, proving the normal seed bootstrap does not touch the recovery compiler.

The C# recovery source need not implement new language features. It should stay
frozen to the pinned bridge needed to repair the bootstrap chain.
