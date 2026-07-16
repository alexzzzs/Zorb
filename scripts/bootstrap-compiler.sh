#!/usr/bin/env bash
set -euo pipefail

readonly ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly BACKEND_DIR="$ROOT_DIR/backend/llvm"
readonly DRIVER_ENTRY="$ROOT_DIR/compiler/driver/main.zorb"
readonly STAGE0="$ROOT_DIR/seed/csharp/bin/Release/net8.0/Zorb.Compiler"

OUTPUT_PATH="${1:-$ROOT_DIR/build/zorb}"
ZIG="${ZIG:-zig}"
LLVM_PREFIX="${LLVM_PREFIX:-/usr/lib/llvm-21}"

mkdir -p "$(dirname "$OUTPUT_PATH")"
dotnet build "$ROOT_DIR/seed/csharp/Zorb.Compiler.csproj" --configuration Release --nologo

pushd "$BACKEND_DIR" >/dev/null
"$ZIG" build --cache-dir .zig-cache --prefix zig-out -Doptimize=ReleaseSafe \
    -Dllvm-prefix="$LLVM_PREFIX"
popd >/dev/null

NATIVE_FLAGS="$BACKEND_DIR/zig-out/lib/libzorb-llvm.a \
-L$LLVM_PREFIX/lib -lLLVM-21 -Wl,-rpath,$LLVM_PREFIX/lib \
-ldl -lpthread -lm -lquadmath -lz -lzstd -lxml2 -lstdc++"

"$STAGE0" build "$DRIVER_ENTRY" --target host-linux -o "$OUTPUT_PATH" \
    --native-flags "$NATIVE_FLAGS"

printf 'Bootstrapped integrated Zorb compiler at %s\n' "$OUTPUT_PATH"
