from __future__ import annotations

import subprocess
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch


SCRIPTS_DIR = Path(__file__).resolve().parents[2] / "scripts"
sys.path.insert(0, str(SCRIPTS_DIR))

from bootstrap_compiler import (  # noqa: E402
    BuildError,
    validate_seed_selection,
    verify_linux_static_binary,
)


class BootstrapCompilerTests(unittest.TestCase):
    def test_seed_and_recovery_are_mutually_exclusive(self) -> None:
        options = SimpleNamespace(seed=Path("seed"), recovery_csharp=True)
        with self.assertRaisesRegex(BuildError, "mutually exclusive"):
            validate_seed_selection(options)

    def test_static_ldd_exit_is_accepted(self) -> None:
        result = subprocess.CompletedProcess(
            args=("ldd", "zorb"),
            returncode=1,
            stdout="",
            stderr="not a dynamic executable\n",
        )
        with patch("bootstrap_compiler.require_command", return_value="ldd"):
            with patch("bootstrap_compiler.subprocess.run", return_value=result):
                verify_linux_static_binary(Path("zorb"))

    def test_shared_llvm_dependency_is_rejected(self) -> None:
        result = subprocess.CompletedProcess(
            args=("ldd", "zorb"),
            returncode=0,
            stdout="libLLVM.so.21 => /usr/lib/libLLVM.so.21\n",
            stderr="",
        )
        with patch("bootstrap_compiler.require_command", return_value="ldd"):
            with patch("bootstrap_compiler.subprocess.run", return_value=result):
                with self.assertRaisesRegex(BuildError, "shared LLVM"):
                    verify_linux_static_binary(Path("zorb"))


if __name__ == "__main__":
    unittest.main()
