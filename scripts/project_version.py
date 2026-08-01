#!/usr/bin/env python3
"""Print the recovery project version without requiring the .NET SDK."""

from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path


VERSION_PATTERN = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$")


def read_project_version(project_path: Path) -> str:
    root = ElementTree.parse(project_path).getroot()
    version = root.findtext("./PropertyGroup/Version")
    if version is None or not VERSION_PATTERN.fullmatch(version.strip()):
        raise ValueError(f"missing or invalid Version in {project_path}")
    return version.strip()


def main() -> int:
    project_root = Path(__file__).resolve().parents[1]
    print(read_project_version(project_root / "seed/csharp/Zorb.Compiler.csproj"))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ElementTree.ParseError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1)
