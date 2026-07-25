#!/usr/bin/env bash
# Compatibility entry point. Cross-platform implementation:
#   python scripts/bootstrap_compiler.py publish
set -euo pipefail

readonly ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
exec "${PYTHON:-python3}" "$ROOT_DIR/scripts/bootstrap_compiler.py" publish "$@"
