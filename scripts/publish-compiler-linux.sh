#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_PATH="$ROOT_DIR/seed/csharp/Zorb.Compiler.csproj"
DRIVER_ENTRY="$ROOT_DIR/compiler/driver/main.zorb"
STAGE0="$ROOT_DIR/seed/csharp/bin/Release/net8.0/Zorb.Compiler"
BACKEND_DIR="$ROOT_DIR/backend/llvm"
OUTPUT_DIR="${1:-$ROOT_DIR/artifacts/compiler/linux-x64}"
ZIG="${ZIG:-zig}"
LLVM_PREFIX="${LLVM_PREFIX:-/usr/lib/llvm-21}"
LLVM_CONFIG="${LLVM_CONFIG:-llvm-config-21}"
CXX_RUNTIME="${CXX_RUNTIME:-}"

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
NATIVE_FLAGS="$BACKEND_DIR/zig-out/lib/libzorb-llvm.a \
-L$LLVM_PREFIX/lib -Wl,--start-group $LLVM_LIBS -Wl,--end-group \
$LLVM_SYSTEM_LIBS -lpthread -lquadmath -lstdc++"

"$STAGE0" build "$DRIVER_ENTRY" --target host-linux -o "$OUTPUT_DIR/zorb" \
  --native-flags "$NATIVE_FLAGS"

if ldd "$OUTPUT_DIR/zorb" | grep -q 'libLLVM'; then
  echo "Published Linux compiler still depends on a shared LLVM library." >&2
  exit 1
fi

printf 'Published Linux compiler to %s\n' "$OUTPUT_DIR"
