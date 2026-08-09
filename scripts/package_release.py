#!/usr/bin/env python3
"""Create a deterministic ZIP release asset and its provenance manifest."""

from __future__ import annotations

import argparse
import hashlib
import json
import stat
import sys
import tempfile
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Sequence


CHUNK_SIZE = 1024 * 1024
ZIP_TIMESTAMP = (1980, 1, 1, 0, 0, 0)
ZIP_FILE_MODE = stat.S_IFREG | 0o644
PROVENANCE_SUFFIX = ".provenance.json"


@dataclass(frozen=True)
class PackageFile:
    """A regular source file and its stable path inside the release archive."""

    relative_path: str
    filesystem_path: Path


def iter_source_files(source_dir: Path) -> tuple[PackageFile, ...]:
    """Return regular files in POSIX path order, rejecting links and specials."""

    source_dir = Path(source_dir)
    if source_dir.is_symlink() or not source_dir.is_dir():
        raise ValueError(f"source directory does not exist: {source_dir}")

    files: list[PackageFile] = []
    for path in source_dir.rglob("*"):
        if path.is_symlink():
            raise ValueError(f"release source contains a symbolic link: {path}")
        if path.is_file():
            relative_path = path.relative_to(source_dir).as_posix()
            files.append(PackageFile(relative_path, path))
        elif not path.is_dir():
            raise ValueError(f"release source contains a non-regular file: {path}")

    return tuple(sorted(files, key=lambda item: item.relative_path))


def sha256_file(path: Path) -> str:
    """Return the lowercase SHA-256 digest of a file."""

    digest = hashlib.sha256()
    with Path(path).open("rb") as source:
        for chunk in iter(lambda: source.read(CHUNK_SIZE), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _require_metadata(name: str, value: str) -> str:
    if not isinstance(value, str) or not value:
        raise ValueError(f"{name} must be a non-empty string")
    return value


def _manifest_for_files(
    files: Iterable[PackageFile], target: str, version: str, commit: str
) -> dict[str, object]:
    return {
        "target": _require_metadata("target", target),
        "version": _require_metadata("version", version),
        "commit": _require_metadata("commit", commit),
        "files": [
            {"path": item.relative_path, "sha256": sha256_file(item.filesystem_path)}
            for item in files
        ],
    }


def build_provenance_manifest(
    source_dir: Path, target: str, version: str, commit: str
) -> dict[str, object]:
    """Build the provenance object for a source directory without writing it."""

    return _manifest_for_files(iter_source_files(source_dir), target, version, commit)


def canonical_json_bytes(value: object) -> bytes:
    """Serialize JSON with stable key ordering, separators, and UTF-8 encoding."""

    return (
        json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")) + "\n"
    ).encode("utf-8")


def provenance_path_for_archive(output_zip: Path) -> Path:
    """Return the sidecar path for an archive, replacing its final suffix."""

    output_zip = Path(output_zip)
    if output_zip.suffix.lower() == ".zip":
        return output_zip.with_suffix(PROVENANCE_SUFFIX)
    return Path(f"{output_zip}{PROVENANCE_SUFFIX}")


def _reject_outputs_inside_source(
    source_root: Path, output_zip: Path, provenance_path: Path
) -> None:
    for label, output_path in (
        ("ZIP output", output_zip),
        ("provenance output", provenance_path),
    ):
        resolved_output_path = output_path.resolve()
        if resolved_output_path == source_root or source_root in resolved_output_path.parents:
            raise ValueError(f"{label} must not be inside source directory: {output_path}")


def _zip_info(relative_path: str) -> zipfile.ZipInfo:
    info = zipfile.ZipInfo(relative_path)
    info.date_time = ZIP_TIMESTAMP
    info.compress_type = zipfile.ZIP_DEFLATED
    info.create_system = 3
    info.create_version = 20
    info.extract_version = 20
    info.flag_bits = 0
    info.volume = 0
    info.internal_attr = 0
    info.external_attr = ZIP_FILE_MODE << 16
    info.extra = b""
    info.comment = b""
    return info


def create_deterministic_zip(files: Iterable[PackageFile], output_zip: Path) -> None:
    """Write regular files to a deterministic ZIP in sorted POSIX path order."""

    output_zip = Path(output_zip)
    output_zip.parent.mkdir(parents=True, exist_ok=True)
    ordered_files = tuple(sorted(files, key=lambda item: item.relative_path))

    temporary_path: Path | None = None
    try:
        with tempfile.NamedTemporaryFile(
            dir=output_zip.parent,
            prefix=f".{output_zip.name}.",
            suffix=".tmp",
            delete=False,
        ) as temporary:
            temporary_path = Path(temporary.name)

        with zipfile.ZipFile(
            temporary_path,
            mode="w",
            compression=zipfile.ZIP_DEFLATED,
            compresslevel=9,
            allowZip64=True,
        ) as archive:
            archive.comment = b""
            for item in ordered_files:
                archive.writestr(
                    _zip_info(item.relative_path), item.filesystem_path.read_bytes()
                )
        temporary_path.replace(output_zip)
    finally:
        if temporary_path is not None:
            temporary_path.unlink(missing_ok=True)


def write_canonical_json(value: object, output_path: Path) -> None:
    """Write canonical JSON atomically as UTF-8 text."""

    output_path = Path(output_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    temporary_path: Path | None = None
    try:
        with tempfile.NamedTemporaryFile(
            dir=output_path.parent,
            prefix=f".{output_path.name}.",
            suffix=".tmp",
            mode="wb",
            delete=False,
        ) as temporary:
            temporary_path = Path(temporary.name)
            temporary.write(canonical_json_bytes(value))
        temporary_path.replace(output_path)
    finally:
        if temporary_path is not None:
            temporary_path.unlink(missing_ok=True)


def package_release(
    source_dir: Path, output_zip: Path, target: str, version: str, commit: str
) -> tuple[Path, Path]:
    """Create an archive and adjacent provenance manifest; return both paths."""

    source_dir = Path(source_dir)
    source_root = source_dir.resolve()
    output_zip = Path(output_zip)
    provenance_path = provenance_path_for_archive(output_zip)
    _reject_outputs_inside_source(source_root, output_zip, provenance_path)

    source_files = iter_source_files(source_dir)
    manifest = _manifest_for_files(source_files, target, version, commit)
    create_deterministic_zip(source_files, output_zip)
    write_canonical_json(manifest, provenance_path)
    return output_zip, provenance_path


def parse_args(arguments: Sequence[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source_dir", type=Path, help="directory to package")
    parser.add_argument("output_zip", type=Path, help="ZIP path to create")
    parser.add_argument("--target", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--commit", required=True)
    return parser.parse_args(arguments)


def main(arguments: Sequence[str] | None = None) -> int:
    options = parse_args(sys.argv[1:] if arguments is None else arguments)
    try:
        archive_path, provenance_path = package_release(
            options.source_dir,
            options.output_zip,
            options.target,
            options.version,
            options.commit,
        )
    except (OSError, ValueError, zipfile.BadZipFile) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    print(f"created {archive_path}")
    print(f"created {provenance_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
