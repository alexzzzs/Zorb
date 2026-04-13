#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_PATH="$ROOT_DIR/Zorb.Compiler/Zorb.Compiler.csproj"
OUTPUT_DIR="${1:-$ROOT_DIR/artifacts/compiler/linux-x64}"
VERSION="${VERSION:-}"
INFORMATIONAL_VERSION="${INFORMATIONAL_VERSION:-}"

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

printf 'Published Linux compiler to %s\n' "$OUTPUT_DIR"
