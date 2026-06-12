#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_PATH="$ROOT_DIR/Zorb.Compiler/Zorb.Compiler.csproj"
BACKEND_DIR="$ROOT_DIR/Zorb.LlvmBackend"
OUTPUT_DIR="${1:-$ROOT_DIR/artifacts/compiler/linux-x64}"
VERSION="${VERSION:-}"
INFORMATIONAL_VERSION="${INFORMATIONAL_VERSION:-}"
ZIG="${ZIG:-zig}"
LLVM_PREFIX="${LLVM_PREFIX:-/usr/lib/llvm-20}"
CXX_RUNTIME="${CXX_RUNTIME:-}"
LLD="${LLD:-}"

find_versioned_tool() {
  local exact_name="$1"
  local prefix_name="$2"
  local candidate=""

  candidate="$(command -v "$exact_name" 2>/dev/null || true)"
  if [[ -n "$candidate" ]]; then
    printf '%s\n' "$candidate"
    return 0
  fi

  local path_dir
  IFS=':' read -r -a path_dirs <<< "$PATH"
  for path_dir in "${path_dirs[@]}"; do
    [[ -d "$path_dir" ]] || continue
    local match
    for match in "$path_dir"/"$prefix_name"*; do
      [[ -x "$match" && ! -d "$match" ]] || continue
      printf '%s\n' "$match"
    done
  done | sort -V | tail -n 1
}

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

if [[ -z "$LLD" ]]; then
  LLD="$(find_versioned_tool "ld.lld" "ld.lld-" || true)"
fi
if [[ -z "$LLD" || ! -x "$LLD" ]]; then
  echo "ld.lld is required, or set LLD to an executable path." >&2
  exit 1
fi

PUBLISH_ARGS=(
  -c Release
  -r linux-x64
  --self-contained true
  /p:PublishSingleFile=true
  /p:PublishTrimmed=false
  -o "$OUTPUT_DIR"
)

if [[ -n "$VERSION" ]]; then
  PUBLISH_ARGS+=("/p:Version=$VERSION")
fi

if [[ -n "$INFORMATIONAL_VERSION" ]]; then
  PUBLISH_ARGS+=("/p:InformationalVersion=$INFORMATIONAL_VERSION")
fi

dotnet publish "$PROJECT_PATH" "${PUBLISH_ARGS[@]}"
"$ZIG" build \
  --build-file "$BACKEND_DIR/build.zig" \
  --cache-dir "$BACKEND_DIR/.zig-cache" \
  --prefix "$BACKEND_DIR/zig-out" \
  -Doptimize=ReleaseSafe \
  -Dstatic-llvm=true \
  -Dllvm-prefix="$LLVM_PREFIX" \
  -Dcxx-runtime="$CXX_RUNTIME"

install -m 0755 "$BACKEND_DIR/zig-out/bin/zorb-llvm-backend" "$OUTPUT_DIR/zorb-llvm-backend"
install -m 0755 "$LLD" "$OUTPUT_DIR/ld.lld"

if ldd "$OUTPUT_DIR/zorb-llvm-backend" | grep -q 'libLLVM'; then
  echo "Published Linux backend still depends on a shared LLVM library." >&2
  exit 1
fi

printf 'Published Linux compiler to %s\n' "$OUTPUT_DIR"
