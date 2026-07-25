#!/usr/bin/env bash
# Compatibility entry point. Cross-platform implementation:
#   python scripts/bootstrap_seed.py resolve
set -euo pipefail

readonly ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
exec "${PYTHON:-python3}" "$ROOT_DIR/scripts/bootstrap_seed.py" resolve "$@"
