#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_PATH="$ROOT_DIR/seed/csharp/Zorb.Compiler.csproj"
DRIVER_ENTRY="$ROOT_DIR/compiler/driver/main.zorb"
STAGE0="$ROOT_DIR/seed/csharp/bin/Release/net8.0/Zorb.Compiler"
BACKEND_DIR="$ROOT_DIR/backend/llvm"
ZIG="${ZIG:-zig}"
LLVM_PREFIX="${LLVM_PREFIX:-/usr/lib/llvm-21}"
LLVM_CONFIG="${LLVM_CONFIG:-llvm-config-21}"
CXX_RUNTIME="${CXX_RUNTIME:-}"
HOST_ARCH="$(uname -m)"

case "$HOST_ARCH" in
  x86_64)
    COMPILER_TARGET="host-linux"
    PACKAGE_ARCH="x64"
    ;;
  aarch64|arm64)
    COMPILER_TARGET="host-linux-aarch64"
    PACKAGE_ARCH="arm64"
    ;;
  *)
    echo "Unsupported Linux compiler host architecture: $HOST_ARCH" >&2
    exit 1
    ;;
esac

OUTPUT_DIR="${1:-$ROOT_DIR/artifacts/compiler/linux-$PACKAGE_ARCH}"

mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(cd "$OUTPUT_DIR" && pwd)"

if [[ -z "$CXX_RUNTIME" ]]; then
  if ! command -v g++ >/dev/null 2>&1; then
    echo "g++ is required to locate libstdc++, or set CXX_RUNTIME explicitly." >&2
    exit 1
  fi
  CXX_RUNTIME="$(g++ -print-file-name=libstdc++.so)"
fi
if [[ ! -f "$CXX_RUNTIME" ]]; then
  echo "CXX_RUNTIME does not point to an existing file: $CXX_RUNTIME" >&2
  exit 1
fi

if ! command -v "$LLVM_CONFIG" >/dev/null 2>&1; then
  echo "LLVM_CONFIG is required (default: llvm-config-21)." >&2
  exit 1
fi

dotnet build "$PROJECT_PATH" --configuration Release --nologo
pushd "$BACKEND_DIR" >/dev/null
trap 'popd >/dev/null' EXIT

"$ZIG" build \
  --cache-dir .zig-cache \
  --prefix zig-out \
  -Doptimize=ReleaseSafe \
  -Dstatic-llvm=true \
  -Dllvm-prefix="$LLVM_PREFIX" \
  -Dcxx-runtime="$CXX_RUNTIME"

popd >/dev/null
trap - EXIT

LLVM_LIBS="$($LLVM_CONFIG --link-static --libs \
  core target nativecodegen aarch64 x86 passes bitwriter irreader)"
LLVM_SYSTEM_LIBS="$($LLVM_CONFIG --link-static --system-libs)"
QUADMATH_LINK_ARGS=()
QUADMATH_LIBRARY="$(g++ -print-file-name=libquadmath.so)"
if [[ "$QUADMATH_LIBRARY" != "libquadmath.so" && -f "$QUADMATH_LIBRARY" ]]; then
  QUADMATH_LINK_ARGS=(-lquadmath)
fi
NATIVE_FLAGS="$BACKEND_DIR/zig-out/lib/libzorb-llvm.a \
-L$LLVM_PREFIX/lib -Wl,--start-group $LLVM_LIBS -Wl,--end-group \
$LLVM_SYSTEM_LIBS -lpthread ${QUADMATH_LINK_ARGS[*]} -lstdc++"

read -r -a LLVM_LIB_ARGS <<< "$LLVM_LIBS"
read -r -a LLVM_SYSTEM_LIB_ARGS <<< "$LLVM_SYSTEM_LIBS"
NATIVE_LINK_ARGS=(
  "$BACKEND_DIR/zig-out/lib/libzorb-llvm.a"
  "-L$LLVM_PREFIX/lib"
  -Wl,--start-group
  "${LLVM_LIB_ARGS[@]}"
  -Wl,--end-group
  "${LLVM_SYSTEM_LIB_ARGS[@]}"
  -lpthread
  "${QUADMATH_LINK_ARGS[@]}"
  -lstdc++
)

VERIFICATION_DIR="$(mktemp -d "${TMPDIR:-/tmp}/zorb-release-fixed-point.XXXXXX")"
trap 'rm -rf -- "$VERIFICATION_DIR"' EXIT
GENERATION_1="$VERIFICATION_DIR/zorb-generation-1"
GENERATION_2="$VERIFICATION_DIR/zorb-generation-2"
GENERATION_3="$VERIFICATION_DIR/zorb-generation-3"

"$STAGE0" build "$DRIVER_ENTRY" --target "$COMPILER_TARGET" -o "$GENERATION_1" \
  --native-flags "$NATIVE_FLAGS"
"$GENERATION_1" build "$DRIVER_ENTRY" --target "$COMPILER_TARGET" -o "$GENERATION_2" \
  --native-link-args "${NATIVE_LINK_ARGS[@]}"
"$GENERATION_2" build "$DRIVER_ENTRY" --target "$COMPILER_TARGET" -o "$GENERATION_3" \
  --native-link-args "${NATIVE_LINK_ARGS[@]}"

if ! cmp -s "$GENERATION_2" "$GENERATION_3"; then
  echo "Generation-2 and generation-3 compilers are not byte-identical." >&2
  exit 1
fi
install -m 0755 "$GENERATION_2" "$OUTPUT_DIR/zorb"

if ldd "$OUTPUT_DIR/zorb" | grep -q 'libLLVM'; then
  echo "Published Linux compiler still depends on a shared LLVM library." >&2
  exit 1
fi

printf 'Verified byte-identical generation-2 and generation-3 compilers.\n'
printf 'Published native Linux %s compiler to %s\n' "$PACKAGE_ARCH" "$OUTPUT_DIR"
