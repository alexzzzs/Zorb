#!/usr/bin/env bash
set -euo pipefail

ZIG_VERSION="0.16.0"
ZIG_SIGNING_KEY="RWSGOq2NVecA2UPNdBUZykf1CCb147pkmdtYxgb3Ti+JO/wCYvhbAb/U"

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <x86_64|aarch64>" >&2
  exit 2
fi

case "$1" in
  x86_64|aarch64)
    zig_arch="$1"
    ;;
  *)
    echo "Unsupported Zig Linux architecture: $1" >&2
    exit 2
    ;;
esac

archive="zig-$zig_arch-linux-$ZIG_VERSION.tar.xz"
signature="$archive.minisig"
download_url="https://ziglang.org/download/$ZIG_VERSION/$archive"

curl -fsSLo "$archive" "$download_url"
curl -fsSLo "$signature" "$download_url.minisig"
minisign -Vm "$archive" -x "$signature" -P "$ZIG_SIGNING_KEY"

if ! grep -Eq "^trusted comment: .*[[:space:]]file:$archive([[:space:]]|$)" "$signature"; then
  echo "Zig signature trusted comment does not match $archive." >&2
  exit 1
fi

tar -xf "$archive"
