from __future__ import annotations

import hashlib
import json
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = PROJECT_ROOT / "scripts"
sys.path.insert(0, str(SCRIPTS_DIR))

from bootstrap_seed import (  # noqa: E402
    SeedError,
    cache_local_seed,
    load_seed_artifact,
    resolve_local_seed,
    resolve_seed,
    safe_extract_zip,
)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


class BootstrapSeedTests(unittest.TestCase):
    def test_repository_manifest_covers_supported_hosts(self) -> None:
        manifest = PROJECT_ROOT / "bootstrap/manifest.json"
        expected_executables = {
            "host-linux": "zorb",
            "host-linux-aarch64": "zorb",
            "host-windows": "zorb.exe",
        }

        for target, executable in expected_executables.items():
            with self.subTest(target=target):
                artifact = load_seed_artifact(manifest, target)
                self.assertEqual("0.2.4", artifact.version)
                self.assertEqual(executable, artifact.executable)

    def test_local_seed_requires_matching_checksum(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_dir:
            artifact_dir = Path(temporary_dir)
            compiler = artifact_dir / "host-linux/zorb"
            compiler.parent.mkdir(parents=True)
            compiler.write_bytes(b"compiler")
            compiler.with_name("zorb.sha256").write_text(
                f"{sha256(compiler)}  zorb\n", encoding="utf-8"
            )

            self.assertEqual(compiler.resolve(), resolve_local_seed(artifact_dir, "host-linux"))

            compiler.write_bytes(b"tampered")
            with self.assertRaisesRegex(SeedError, "checksum verification failed"):
                resolve_local_seed(artifact_dir, "host-linux")

    def test_published_zip_is_verified_extracted_and_cached(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_dir:
            root = Path(temporary_dir)
            package = root / "seed.zip"
            with zipfile.ZipFile(package, "w") as archive:
                archive.writestr("zorb", b"published compiler")
                archive.writestr("version.txt", "version=0.2.2\n")
            manifest = root / "manifest.json"
            manifest.write_text(
                json.dumps(
                    {
                        "schemaVersion": 2,
                        "purpose": "integrated-compiler",
                        "artifacts": [
                            {
                                "target": "host-linux",
                                "version": "0.2.2",
                                "format": "zip",
                                "executable": "zorb",
                                "url": package.as_uri(),
                                "sha256": sha256(package),
                            }
                        ],
                    }
                ),
                encoding="utf-8",
            )

            cache_dir = root / "cache"
            resolved = resolve_seed("host-linux", manifest, root / "local", cache_dir)
            self.assertEqual(b"published compiler", resolved.read_bytes())
            self.assertEqual("version=0.2.2\n", (resolved.parent / "version.txt").read_text())

            package.unlink()
            self.assertEqual(
                resolved,
                resolve_seed("host-linux", manifest, root / "local", cache_dir),
            )

            resolved.write_bytes(b"tampered cache")
            repaired = resolve_seed("host-linux", manifest, root / "local", cache_dir)
            self.assertEqual(b"published compiler", repaired.read_bytes())

    def test_unsafe_archive_path_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_dir:
            root = Path(temporary_dir)
            archive_path = root / "unsafe.zip"
            with zipfile.ZipFile(archive_path, "w") as archive:
                archive.writestr("../outside", b"bad")
            with self.assertRaisesRegex(SeedError, "unsafe path"):
                safe_extract_zip(archive_path, root / "output")

    def test_published_windows_package_requires_llvm_runtime(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_dir:
            root = Path(temporary_dir)
            package = root / "seed.zip"
            with zipfile.ZipFile(package, "w") as archive:
                archive.writestr("zorb.exe", b"compiler")
            manifest = root / "manifest.json"
            manifest.write_text(
                json.dumps(
                    {
                        "schemaVersion": 2,
                        "purpose": "integrated-compiler",
                        "artifacts": [
                            {
                                "target": "host-windows",
                                "version": "0.2.2",
                                "format": "zip",
                                "executable": "zorb.exe",
                                "url": package.as_uri(),
                                "sha256": sha256(package),
                            }
                        ],
                    }
                ),
                encoding="utf-8",
            )

            with self.assertRaisesRegex(SeedError, "missing required runtime: LLVM-C.dll"):
                resolve_seed("host-windows", manifest, root / "local", root / "cache")

    def test_manifest_rejects_duplicate_target_entries(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_dir:
            manifest = Path(temporary_dir) / "manifest.json"
            entry = {
                "target": "host-linux",
                "version": "0.2.2",
                "format": "zip",
                "executable": "zorb",
                "url": "https://example.invalid/zorb.zip",
                "sha256": "a" * 64,
            }
            manifest.write_text(
                json.dumps(
                    {
                        "schemaVersion": 2,
                        "purpose": "integrated-compiler",
                        "artifacts": [entry, entry],
                    }
                ),
                encoding="utf-8",
            )
            with self.assertRaisesRegex(SeedError, "duplicate entries"):
                load_seed_artifact(manifest, "host-linux")

    def test_cache_local_seed_writes_resolvable_artifact(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_dir:
            root = Path(temporary_dir)
            compiler = root / "compiler"
            compiler.write_bytes(b"local compiler")
            artifact_dir = root / "artifacts"

            cached = cache_local_seed(compiler, "host-linux", artifact_dir)

            self.assertEqual(cached, resolve_local_seed(artifact_dir, "host-linux"))
            self.assertEqual(b"local compiler", cached.read_bytes())


if __name__ == "__main__":
    unittest.main()
