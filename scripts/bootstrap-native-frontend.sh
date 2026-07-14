#!/usr/bin/env bash
# Build the native Zorb frontend checker with the C# stage-0 compiler.
#
# This is a developer/bootstrap command, not the end-user compiler driver.
# The resulting executable owns lexing, parsing, import loading, semantic
# checking, and structured diagnostics; code generation remains stage 0 work
# until the frontend-to-backend contract is completed.

set -euo pipefail

readonly REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly STAGE0_PROJECT="$REPOSITORY_ROOT/seed/csharp/Zorb.Compiler.csproj"
readonly FRONTEND_ENTRY="$REPOSITORY_ROOT/compiler/self-check/main.zorb"
readonly DEFAULT_OUTPUT="$REPOSITORY_ROOT/build/zorb-self-check"

output_path="${1:-$DEFAULT_OUTPUT}"

mkdir -p "$(dirname "$output_path")"
dotnet build "$STAGE0_PROJECT" --configuration Release --nologo
"$REPOSITORY_ROOT/seed/csharp/bin/Release/net8.0/Zorb.Compiler" \
    build "$FRONTEND_ENTRY" --target host-linux -o "$output_path"

printf 'Native frontend checker built at %s\n' "$output_path"
