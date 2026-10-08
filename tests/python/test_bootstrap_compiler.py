from __future__ import annotations

import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch


SCRIPTS_DIR = Path(__file__).resolve().parents[2] / "scripts"
sys.path.insert(0, str(SCRIPTS_DIR))

from bootstrap_compiler import (  # noqa: E402
    BuildEnvironment,
    BuildError,
    build_backend,
    prepare_seed_entry,
    validate_seed_selection,
    verify_linux_static_binary,
)


class BootstrapCompilerTests(unittest.TestCase):
    def test_hosted_seed_sources_preserve_lowering_and_checkout(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_dir:
            root = Path(temporary_dir) / "repo"
            entry = root / "compiler/driver/main.zorb"
            entry.parent.mkdir(parents=True)
            lowering = 'fn builtin_name() -> string { return "IsFreestanding" }\n'
            entry.write_text(lowering, encoding="utf-8")
            runtime = root / "runtime/std/os.zorb"
            runtime.parent.mkdir(parents=True)
            original = 'fn hosted() -> bool { return !Builtin.IsFreestanding }\n'
            runtime.write_text(original, encoding="utf-8")

            for target in ("host-linux", "host-linux-aarch64", "host-windows"):
                with self.subTest(target=target):
                    workspace = Path(temporary_dir) / target
                    adapted_entry = prepare_seed_entry(root, entry, workspace, target)
                    self.assertEqual(adapted_entry.read_text(encoding="utf-8"), lowering)
                    adapted_runtime = workspace / "seed-sources/runtime/std/os.zorb"
                    self.assertIn("return !false", adapted_runtime.read_text(encoding="utf-8"))
                    self.assertEqual(runtime.read_text(encoding="utf-8"), original)

    def test_seed_folding_rejects_freestanding_targets(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_dir:
            root = Path(temporary_dir)
            with self.assertRaisesRegex(BuildError, "requires a hosted target"):
                prepare_seed_entry(root, root / "main.zorb", root / "workspace", "freestanding-linux")
            self.assertFalse((root / "workspace").exists())

    def test_backend_build_targets_portable_baseline_cpu(self) -> None:
        environment = BuildEnvironment(
            root=Path("/repo"),
            backend_dir=Path("/repo/backend/llvm"),
            driver_entry=Path("/repo/compiler/driver/main.zorb"),
            target="host-linux",
            zig="zig",
            llvm_prefix=Path("/usr/lib/llvm-22"),
            llvm_config="llvm-config-22",
            llvm_lib_dir=None,
            llvm_runtime_dir=None,
        )
        with patch("bootstrap_compiler.require_command", return_value="/tools/zig"):
            with patch("bootstrap_compiler.run_checked") as run_checked:
                with patch("bootstrap_compiler.optional_quadmath_link_args", return_value=()):
                    build_backend(environment, publish=False)

        command = run_checked.call_args.args[1]
        self.assertIn("-Dcpu=baseline", command)

    def test_shared_backend_uses_configured_llvm_library_directory(self) -> None:
        environment = BuildEnvironment(
            root=Path("/repo"),
            backend_dir=Path("/repo/backend/llvm"),
            driver_entry=Path("/repo/compiler/driver/main.zorb"),
            target="host-linux",
            zig="zig",
            llvm_prefix=Path("/usr/lib/llvm-22"),
            llvm_config="llvm-config-22",
            llvm_lib_dir=Path("/custom/llvm/lib"),
            llvm_runtime_dir=None,
        )
        with patch("bootstrap_compiler.require_command", return_value="/tools/zig"):
            with patch("bootstrap_compiler.run_checked"):
                with patch("bootstrap_compiler.optional_quadmath_link_args", return_value=()):
                    backend = build_backend(environment, publish=False)

        self.assertIn("-L/custom/llvm/lib", backend.link_args)
        self.assertIn("-Wl,-rpath,/custom/llvm/lib", backend.link_args)

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
            stdout="libLLVM.so.22 => /usr/lib/libLLVM.so.22\n",
            stderr="",
        )
        with patch("bootstrap_compiler.require_command", return_value="ldd"):
            with patch("bootstrap_compiler.subprocess.run", return_value=result):
                with self.assertRaisesRegex(BuildError, "shared LLVM"):
                    verify_linux_static_binary(Path("zorb"))

    def test_unexpected_ldd_failure_is_rejected(self) -> None:
        result = subprocess.CompletedProcess(
            args=("ldd", "zorb"),
            returncode=2,
            stdout="",
            stderr="permission denied\n",
        )
        with patch("bootstrap_compiler.require_command", return_value="ldd"):
            with patch("bootstrap_compiler.subprocess.run", return_value=result):
                with self.assertRaisesRegex(BuildError, "exit code 2: permission denied"):
                    verify_linux_static_binary(Path("zorb"))


if __name__ == "__main__":
    unittest.main()
