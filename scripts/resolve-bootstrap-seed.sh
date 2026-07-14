#!/usr/bin/env bash
# Resolve a verified bootstrap seed from the local artifact cache or the
# repository manifest. This is intentionally separate from build logic so the
# future self-hosted compiler can reuse the same acquisition contract.

set -euo pipefail

readonly REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly MANIFEST_PATH="$REPOSITORY_ROOT/bootstrap/manifest.json"
readonly LOCAL_ARTIFACT_DIR="$REPOSITORY_ROOT/bootstrap/artifacts"
readonly DEFAULT_CACHE_DIR="$REPOSITORY_ROOT/build/bootstrap"

usage() {
    printf 'usage: %s <target> [--cache-dir <directory>]\n' "$0"
}

(($# >= 1)) || { usage >&2; exit 64; }
target="$1"
shift
cache_dir="$DEFAULT_CACHE_DIR"

while (($# > 0)); do
    case "$1" in
        --cache-dir)
            (($# >= 2)) || { usage >&2; exit 64; }
            cache_dir="$2"
            shift 2
            ;;
        *)
            usage >&2
            exit 64
            ;;
    esac
done

for candidate in "$LOCAL_ARTIFACT_DIR/$target/zorb-self-check" "$LOCAL_ARTIFACT_DIR/$target/zorb-self-check.exe"; do
    if [[ -f "$candidate" ]]; then
        printf '%s\n' "$candidate"
        exit 0
    fi
done

command -v jq >/dev/null || { printf 'jq is required to resolve published bootstrap seeds.\n' >&2; exit 69; }
url="$(jq -r --arg target "$target" '.artifacts[] | select(.target == $target) | .url' "$MANIFEST_PATH" | head -n 1)"
sha256="$(jq -r --arg target "$target" '.artifacts[] | select(.target == $target) | .sha256' "$MANIFEST_PATH" | head -n 1)"

if [[ -z "$url" || "$url" == null || -z "$sha256" || "$sha256" == null ]]; then
    printf 'No local or published bootstrap seed is available for %s.\n' "$target" >&2
    exit 69
fi

command -v curl >/dev/null || { printf 'curl is required to download bootstrap seeds.\n' >&2; exit 69; }
mkdir -p "$cache_dir"
artifact_path="$cache_dir/zorb-bootstrap-$target"
curl --fail --location --silent --show-error "$url" --output "$artifact_path"
printf '%s  %s\n' "$sha256" "$artifact_path" | sha256sum --check --status || {
    rm -f "$artifact_path"
    printf 'Bootstrap seed checksum verification failed for %s.\n' "$target" >&2
    exit 65
}
chmod +x "$artifact_path"
printf '%s\n' "$artifact_path"
