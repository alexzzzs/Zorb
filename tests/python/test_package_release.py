from __future__ import annotations

import hashlib
import json
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path


SCRIPTS_DIR = Path(__file__).resolve().parents[2] / "scripts"
sys.path.insert(0, str(SCRIPTS_DIR))

from package_release import (  # noqa: E402
    ZIP_TIMESTAMP,
    canonical_json_bytes,
    package_release,
)


class PackageReleaseTests(unittest.TestCase):
    def _create_source(self, root: Path, names: list[str]) -> None:
        for name in names:
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(f"contents for {name}\n".encode("utf-8"))

    def test_creation_order_does_not_change_archive_or_manifest(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            first_source = root / "first"
            second_source = root / "second"
            names = ["zorb.exe", "nested/config.txt", "README.md", "nested/data.bin"]
            self._create_source(first_source, names)
            self._create_source(second_source, list(reversed(names)))

            first_zip, first_manifest = package_release(
                first_source, root / "first.zip", "host-windows", "0.3.0", "abc123"
            )
            second_zip, second_manifest = package_release(
                second_source, root / "second.zip", "host-windows", "0.3.0", "abc123"
            )

            self.assertEqual(first_zip.read_bytes(), second_zip.read_bytes())
            self.assertEqual(first_manifest.read_bytes(), second_manifest.read_bytes())
            with zipfile.ZipFile(first_zip) as archive:
                infos = archive.infolist()
                self.assertEqual([info.filename for info in infos], sorted(names))
                for info in infos:
                    self.assertEqual(info.date_time, ZIP_TIMESTAMP)
                    self.assertEqual(info.create_system, 3)
                    self.assertEqual(info.flag_bits, 0)
                    self.assertEqual(info.internal_attr, 0)
                    self.assertEqual(info.external_attr >> 16 & 0o777, 0o644)
                    self.assertEqual(info.extra, b"")
                    self.assertEqual(info.comment, b"")

    def test_provenance_records_hashes_for_packaged_files(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "release"
            names = ["zorb", "runtime/LLVM-C.dll"]
            self._create_source(source, names)
            archive_path, manifest_path = package_release(
                source, root / "zorb.zip", "host-linux", "0.3.0", "deadbeef"
            )

            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            expected_files = [
                {
                    "path": name,
                    "sha256": hashlib.sha256((source / name).read_bytes()).hexdigest(),
                }
                for name in sorted(names)
            ]
            expected = {
                "target": "host-linux",
                "version": "0.3.0",
                "commit": "deadbeef",
                "files": expected_files,
            }
            self.assertEqual(manifest, expected)
            self.assertEqual(manifest_path.read_bytes(), canonical_json_bytes(expected))

            with zipfile.ZipFile(archive_path) as archive:
                self.assertEqual(
                    {name: archive.read(name) for name in names},
                    {name: (source / name).read_bytes() for name in names},
                )

    def test_outputs_inside_source_directory_are_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "release"
            self._create_source(source, ["zorb"])
            output_zip = source / "zorb.zip"

            with self.assertRaisesRegex(ValueError, "inside source directory"):
                package_release(source, output_zip, "host-linux", "0.3.0", "deadbeef")

            self.assertFalse(output_zip.exists())
            self.assertFalse((source / "zorb.provenance.json").exists())


if __name__ == "__main__":
    unittest.main()
