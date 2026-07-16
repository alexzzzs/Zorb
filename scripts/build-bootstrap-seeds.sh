#!/usr/bin/env bash
# Build locally cached native frontend seed artifacts with the C# stage-0
# compiler. Release automation can publish the resulting files and manifest.

set -euo pipefail

readonly REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly STAGE0_PROJECT="$REPOSITORY_ROOT/seed/csharp/Zorb.Compiler.csproj"
readonly STAGE0_BINARY="$REPOSITORY_ROOT/seed/csharp/bin/Release/net8.0/Zorb.Compiler"
readonly FRONTEND_ENTRY="$REPOSITORY_ROOT/compiler/self-check/main.zorb"
readonly DEFAULT_OUTPUT_DIR="$REPOSITORY_ROOT/bootstrap/artifacts"

targets=()
output_dir="$DEFAULT_OUTPUT_DIR"

usage() {
    printf 'usage: %s [--target <target>]... [--output-dir <directory>]\n' "$0"
}

while (($# > 0)); do
    case "$1" in
        --target)
            (($# >= 2)) || { usage >&2; exit 64; }
            targets+=("$2")
            shift 2
            ;;
        --output-dir)
            (($# >= 2)) || { usage >&2; exit 64; }
            output_dir="$2"
            shift 2
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            usage >&2
            exit 64
            ;;
    esac
done

if ((${#targets[@]} == 0)); then
    case "$(uname -s)-$(uname -m)" in
        Linux-x86_64) targets=(host-linux) ;;
        Linux-aarch64|Linux-arm64) targets=(host-linux-aarch64) ;;
        *)
            printf 'Specify --target explicitly for this host.\n' >&2
            exit 64
            ;;
    esac
fi

dotnet build "$STAGE0_PROJECT" --configuration Release --nologo

for target in "${targets[@]}"; do
    case "$target" in
        host-linux|freestanding-linux|host-linux-aarch64|freestanding-linux-aarch64|bare-metal-x86_64)
            output_name=zorb-self-check
            ;;
        host-windows)
            output_name=zorb-self-check.exe
            ;;
        *)
            printf 'Unsupported seed target: %s\n' "$target" >&2
            exit 64
            ;;
    esac

    artifact_dir="$output_dir/$target"
    artifact_path="$artifact_dir/$output_name"
    mkdir -p "$artifact_dir"
    "$STAGE0_BINARY" build "$FRONTEND_ENTRY" --target "$target" -o "$artifact_path"
    (cd "$artifact_dir" && sha256sum "$output_name" > "$output_name.sha256")
    printf 'Built %s (%s)\n' "$artifact_path" "$target"
done
