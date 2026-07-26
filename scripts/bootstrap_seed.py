#!/usr/bin/env python3
"""Resolve and cache verified integrated Zorb compiler seeds."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import shutil
import stat
import sys
import tempfile
import urllib.parse
import urllib.request
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Mapping, Sequence


MANIFEST_SCHEMA_VERSION = 2
MANIFEST_PURPOSE = "integrated-compiler"
DOWNLOAD_TIMEOUT_SECONDS = 120
SHA256_HEX_LENGTH = 64


class SeedError(RuntimeError):
    """Raised when a bootstrap seed cannot be resolved safely."""


@dataclass(frozen=True)
class SeedArtifact:
    target: str
    version: str
    archive_format: str
    executable: str
    url: str
    sha256: str


def repository_root() -> Path:
    return Path(__file__).resolve().parent.parent


def executable_name_for_target(target: str) -> str:
    names = {
        "host-linux": "zorb",
        "host-linux-aarch64": "zorb",
        "host-windows": "zorb.exe",
    }
    try:
        return names[target]
    except KeyError as error:
        raise SeedError(
            f"Bootstrap seeds are only defined for hosted compiler targets; got {target}."
        ) from error


def current_host_target() -> str:
    system = platform.system().lower()
    machine = platform.machine().lower()
    if system == "linux" and machine in {"x86_64", "amd64"}:
        return "host-linux"
    if system == "linux" and machine in {"aarch64", "arm64"}:
        return "host-linux-aarch64"
    if system == "windows" and machine in {"x86_64", "amd64"}:
        return "host-windows"
    raise SeedError(f"Unsupported compiler host: {platform.system()} {platform.machine()}.")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def verify_sha256(path: Path, expected_sha256: str) -> bool:
    return (
        len(expected_sha256) == SHA256_HEX_LENGTH
        and all(character in "0123456789abcdefABCDEF" for character in expected_sha256)
        and sha256_file(path).lower() == expected_sha256.lower()
    )


def _required_string(entry: Mapping[str, Any], key: str, target: str) -> str:
    value = entry.get(key)
    if not isinstance(value, str) or not value:
        raise SeedError(f"Bootstrap artifact {target} has an invalid {key} value.")
    return value


def load_seed_artifact(manifest_path: Path, target: str) -> SeedArtifact:
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except FileNotFoundError as error:
        raise SeedError(f"Bootstrap manifest does not exist: {manifest_path}") from error
    except json.JSONDecodeError as error:
        raise SeedError(f"Bootstrap manifest is not valid JSON: {error}") from error

    if manifest.get("schemaVersion") != MANIFEST_SCHEMA_VERSION:
        raise SeedError(
            f"Unsupported bootstrap manifest schema: {manifest.get('schemaVersion')}."
        )
    if manifest.get("purpose") != MANIFEST_PURPOSE:
        raise SeedError(f"Unsupported bootstrap manifest purpose: {manifest.get('purpose')}.")
    artifacts = manifest.get("artifacts")
    if not isinstance(artifacts, list):
        raise SeedError("Bootstrap manifest artifacts must be an array.")

    matches = [
        entry
        for entry in artifacts
        if isinstance(entry, dict) and entry.get("target") == target
    ]
    if not matches:
        raise SeedError(
            f"No published integrated compiler seed is available for {target}. "
            "Use an explicit C# recovery bootstrap for the first release on a new host target."
        )
    if len(matches) != 1:
        raise SeedError(f"Bootstrap manifest contains duplicate entries for {target}.")

    entry = matches[0]
    artifact = SeedArtifact(
        target=target,
        version=_required_string(entry, "version", target),
        archive_format=_required_string(entry, "format", target),
        executable=_required_string(entry, "executable", target),
        url=_required_string(entry, "url", target),
        sha256=_required_string(entry, "sha256", target).lower(),
    )
    if artifact.archive_format != "zip":
        raise SeedError(f"Unsupported bootstrap archive format: {artifact.archive_format}.")
    if artifact.executable != executable_name_for_target(target):
        raise SeedError(f"Bootstrap artifact {target} has an unexpected executable name.")
    version_parts = artifact.version.split(".")
    if len(version_parts) != 3 or not all(part.isdigit() for part in version_parts):
        raise SeedError(f"Bootstrap artifact {target} has an invalid semantic version.")
    if len(artifact.sha256) != SHA256_HEX_LENGTH or not all(
        character in "0123456789abcdef" for character in artifact.sha256
    ):
        raise SeedError(f"Bootstrap artifact {target} has an invalid SHA-256 digest.")
    scheme = urllib.parse.urlparse(artifact.url).scheme
    if scheme not in {"https", "file"}:
        raise SeedError(f"Bootstrap artifact {target} uses an unsupported URL scheme.")
    return artifact


def resolve_local_seed(artifact_dir: Path, target: str) -> Path | None:
    candidate = artifact_dir / target / executable_name_for_target(target)
    if not candidate.is_file():
        return None
    checksum_path = candidate.with_name(candidate.name + ".sha256")
    if not checksum_path.is_file():
        raise SeedError(f"Local bootstrap seed checksum is missing for {candidate}.")
    checksum_parts = checksum_path.read_text(encoding="utf-8").split(maxsplit=1)
    if not checksum_parts:
        raise SeedError(f"Local bootstrap seed checksum is empty for {candidate}.")
    expected_sha256 = checksum_parts[0]
    if not verify_sha256(candidate, expected_sha256):
        raise SeedError(f"Local bootstrap seed checksum verification failed for {candidate}.")
    if target == "host-windows":
        runtime_library = candidate.parent / "LLVM-C.dll"
        runtime_checksum = runtime_library.with_name(runtime_library.name + ".sha256")
        if not runtime_library.is_file() or not runtime_checksum.is_file():
            raise SeedError(
                f"Local Windows bootstrap seed is missing LLVM-C.dll or its checksum in {candidate.parent}."
            )
        runtime_checksum_parts = runtime_checksum.read_text(encoding="utf-8").split(maxsplit=1)
        if not runtime_checksum_parts or not verify_sha256(
            runtime_library, runtime_checksum_parts[0]
        ):
            raise SeedError(
                f"Local bootstrap runtime checksum verification failed for {runtime_library}."
            )
    make_executable(candidate)
    return candidate.resolve()


def make_executable(path: Path) -> None:
    if os.name != "nt":
        path.chmod(path.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)


def download_file(url: str, destination: Path) -> None:
    request = urllib.request.Request(url, headers={"User-Agent": "zorb-bootstrap"})
    try:
        with urllib.request.urlopen(request, timeout=DOWNLOAD_TIMEOUT_SECONDS) as response:
            with destination.open("wb") as output:
                shutil.copyfileobj(response, output)
    except OSError as error:
        raise SeedError(f"Failed to download bootstrap seed from {url}: {error}") from error


def safe_extract_zip(archive_path: Path, destination: Path) -> None:
    destination_resolved = destination.resolve()
    try:
        with zipfile.ZipFile(archive_path) as archive:
            for member in archive.infolist():
                member_path = (destination / member.filename).resolve()
                try:
                    contained = (
                        os.path.commonpath((destination_resolved, member_path))
                        == str(destination_resolved)
                    )
                except ValueError:
                    contained = False
                if not contained:
                    raise SeedError(f"Bootstrap package contains an unsafe path: {member.filename}")
            archive.extractall(destination)
    except zipfile.BadZipFile as error:
        raise SeedError(f"Bootstrap package is not a valid ZIP archive: {archive_path}") from error


def extracted_package_matches_archive(archive_path: Path, package_dir: Path) -> bool:
    try:
        with zipfile.ZipFile(archive_path) as archive:
            for member in archive.infolist():
                if member.is_dir():
                    continue
                extracted_file = package_dir / member.filename
                if not extracted_file.is_file():
                    return False
                archived_digest = hashlib.sha256()
                with archive.open(member) as source:
                    for chunk in iter(lambda: source.read(1024 * 1024), b""):
                        archived_digest.update(chunk)
                if sha256_file(extracted_file) != archived_digest.hexdigest():
                    return False
    except (OSError, zipfile.BadZipFile):
        return False
    return True


def resolve_published_seed(artifact: SeedArtifact, cache_dir: Path) -> Path:
    cache_entry = cache_dir / artifact.target / artifact.sha256
    archive_path = cache_entry / "seed.zip"
    package_dir = cache_entry / "package"
    executable_path = package_dir / artifact.executable

    archive_is_valid = archive_path.is_file() and verify_sha256(archive_path, artifact.sha256)
    if executable_path.is_file() and archive_is_valid and extracted_package_matches_archive(
        archive_path, package_dir
    ):
        require_windows_runtime(package_dir, artifact.target)
        make_executable(executable_path)
        return executable_path.resolve()

    cache_entry.mkdir(parents=True, exist_ok=True)
    if not archive_is_valid:
        with tempfile.NamedTemporaryFile(
            prefix="seed-download-", suffix=".zip", dir=cache_entry, delete=False
        ) as temporary_file:
            download_path = Path(temporary_file.name)
        try:
            download_file(artifact.url, download_path)
            if not verify_sha256(download_path, artifact.sha256):
                raise SeedError(
                    f"Bootstrap seed checksum verification failed for {artifact.target}."
                )
            download_path.replace(archive_path)
        finally:
            download_path.unlink(missing_ok=True)

    with tempfile.TemporaryDirectory(prefix="seed-extract-", dir=cache_entry) as temporary_dir:
        extracted_package = Path(temporary_dir) / "package"
        extracted_package.mkdir()
        safe_extract_zip(archive_path, extracted_package)
        extracted_executable = extracted_package / artifact.executable
        if not extracted_executable.is_file():
            raise SeedError(
                f"Published bootstrap package for {artifact.target} does not contain "
                f"{artifact.executable}."
            )
        require_windows_runtime(extracted_package, artifact.target)
        if package_dir.exists():
            shutil.rmtree(package_dir)
        shutil.move(str(extracted_package), package_dir)

    make_executable(executable_path)
    return executable_path.resolve()


def require_windows_runtime(package_dir: Path, target: str) -> None:
    if target != "host-windows":
        return
    runtime_library = package_dir / "LLVM-C.dll"
    if not runtime_library.is_file():
        raise SeedError(
            f"Published Windows bootstrap package is missing required runtime: {runtime_library.name}."
        )


def resolve_seed(
    target: str,
    manifest_path: Path,
    artifact_dir: Path,
    cache_dir: Path,
) -> Path:
    local_seed = resolve_local_seed(artifact_dir, target)
    if local_seed is not None:
        return local_seed
    artifact = load_seed_artifact(manifest_path, target)
    return resolve_published_seed(artifact, cache_dir)


def cache_local_seed(compiler: Path, target: str, artifact_dir: Path) -> Path:
    if not compiler.is_file():
        raise SeedError(f"Compiler seed does not exist: {compiler}")
    destination_dir = artifact_dir / target
    destination_dir.mkdir(parents=True, exist_ok=True)
    destination = destination_dir / executable_name_for_target(target)
    shutil.copy2(compiler, destination)
    make_executable(destination)
    if target == "host-windows":
        runtime_library = compiler.parent / "LLVM-C.dll"
        if not runtime_library.is_file():
            raise SeedError(
                f"Windows compiler seed is missing its sibling LLVM-C.dll: {runtime_library}"
            )
        cached_runtime = destination_dir / runtime_library.name
        shutil.copy2(runtime_library, cached_runtime)
        runtime_digest = sha256_file(cached_runtime)
        cached_runtime.with_name(cached_runtime.name + ".sha256").write_text(
            f"{runtime_digest}  {cached_runtime.name}\n", encoding="utf-8"
        )
    digest = sha256_file(destination)
    destination.with_name(destination.name + ".sha256").write_text(
        f"{digest}  {destination.name}\n", encoding="utf-8"
    )
    return destination.resolve()


def parse_args(arguments: Sequence[str]) -> argparse.Namespace:
    root = repository_root()
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    resolve_parser = subparsers.add_parser("resolve", help="resolve a local or published seed")
    resolve_parser.add_argument(
        "target", nargs="?", help="host-linux, host-linux-aarch64, or host-windows"
    )
    resolve_parser.add_argument(
        "--manifest",
        type=Path,
        default=Path(
            os.environ.get("ZORB_BOOTSTRAP_MANIFEST", root / "bootstrap/manifest.json")
        ),
    )
    resolve_parser.add_argument("--artifact-dir", type=Path, default=root / "bootstrap/artifacts")
    resolve_parser.add_argument(
        "--cache-dir",
        type=Path,
        default=Path(os.environ.get("ZORB_BOOTSTRAP_CACHE_DIR", root / "build/bootstrap")),
    )
    cache_parser = subparsers.add_parser(
        "cache-local", help="cache an existing integrated compiler as a local seed"
    )
    cache_parser.add_argument("--target", default=None)
    cache_parser.add_argument("--compiler", type=Path)
    cache_parser.add_argument("--output-dir", type=Path, default=root / "bootstrap/artifacts")
    return parser.parse_args(arguments)


def main(arguments: Sequence[str] | None = None) -> int:
    options = parse_args(sys.argv[1:] if arguments is None else arguments)
    try:
        target = options.target or current_host_target()
        if options.command == "resolve":
            seed = resolve_seed(target, options.manifest, options.artifact_dir, options.cache_dir)
        else:
            default_name = executable_name_for_target(target)
            compiler = options.compiler or repository_root() / "build" / default_name
            seed = cache_local_seed(compiler.resolve(), target, options.output_dir)
    except SeedError as error:
        print(error, file=sys.stderr)
        return 69
    print(seed)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
