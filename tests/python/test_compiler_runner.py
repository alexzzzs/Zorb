from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


PROJECT_ROOT = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = PROJECT_ROOT / "scripts"
sys.path.insert(0, str(SCRIPTS_DIR))

from project_version import read_project_version  # noqa: E402
from test_compiler import (  # noqa: E402
    COMMAND_TIMEOUT_ENVIRONMENT_VARIABLE,
    ExclusionTracker,
    NativeCompilerSuite,
    SuiteFailure,
    expected_phase_from_code,
    load_fixture_manifest,
    llvm_expectations,
    load_runtime_expectation,
    load_suite_exclusions,
    parse_arguments,
)


class CompilerRunnerTests(unittest.TestCase):
    def test_repository_manifest_classifies_the_complete_native_corpus(self) -> None:
        cases = load_fixture_manifest(PROJECT_ROOT)
        self.assertGreater(len(cases), 300)
        self.assertEqual(len(cases), len({case.name for case in cases}))

    def test_repository_exclusions_are_named_and_reference_real_cases(self) -> None:
        cases = load_fixture_manifest(PROJECT_ROOT)
        exclusions = load_suite_exclusions(PROJECT_ROOT, cases)
        self.assertTrue(exclusions.llvm)
        self.assertTrue(all(exclusions.llvm.values()))
        self.assertFalse(exclusions.runtime)
        self.assertFalse(exclusions.runtime_by_target)

    def test_llvm_expectations_prefer_the_exact_target(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            fixture_dir = Path(temp_dir)
            (fixture_dir / "expect-llvm.txt").write_text("generic\n", encoding="utf-8")
            (fixture_dir / "expect-llvm-linux.txt").write_text("linux\n", encoding="utf-8")
            (fixture_dir / "expect-llvm-host-linux-aarch64.txt").write_text(
                "aarch64\n", encoding="utf-8"
            )

            self.assertEqual(
                llvm_expectations(fixture_dir, "host-linux-aarch64"), ["aarch64"]
            )

    def test_structured_diagnostic_codes_map_to_manifest_outcomes(self) -> None:
        self.assertEqual(expected_phase_from_code("lex.invalid-token"), "lexical-failure")
        self.assertEqual(expected_phase_from_code("parse.expected-token"), "parse-failure")
        self.assertEqual(expected_phase_from_code("import.not-found"), "import-failure")
        self.assertEqual(expected_phase_from_code("type.not-assignable"), "semantic-failure")
        self.assertEqual(expected_phase_from_code("name.unknown"), "semantic-failure")
        self.assertEqual(expected_phase_from_code("flow.missing-return"), "semantic-failure")
        with self.assertRaisesRegex(SuiteFailure, "unrecognized structured diagnostic code"):
            expected_phase_from_code("backend.invalid")

    def test_command_timeout_uses_environment_and_cli_override(self) -> None:
        environment = {COMMAND_TIMEOUT_ENVIRONMENT_VARIABLE: "75"}
        self.assertEqual(parse_arguments([], environment).command_timeout_seconds, 75)
        self.assertEqual(
            parse_arguments(
                ["--command-timeout-seconds", "90"], environment
            ).command_timeout_seconds,
            90,
        )

    def test_suite_command_uses_resolved_timeout(self) -> None:
        suite = NativeCompilerSuite(
            project_root=PROJECT_ROOT,
            compiler=Path("zorb"),
            environment={},
            target="host-linux",
            runtime_targets=[],
            command_timeout_seconds=123,
            frontend_only=True,
            selected_case=None,
        )
        with patch("test_compiler.run_command") as run_command:
            suite._run_command(["zorb", "check", "input.zorb"])
        self.assertEqual(run_command.call_args.args[3], 123)

    def test_unconsumed_exclusion_is_stale(self) -> None:
        tracker = ExclusionTracker()
        tracker.register("llvm:case", "reason")
        with self.assertRaisesRegex(SuiteFailure, "stale native-suite exclusions"):
            tracker.require_no_stale_exclusions()
        tracker.consume("llvm:case")
        tracker.require_no_stale_exclusions()

    def test_aarch64_runtime_expectations_fall_back_to_generic_values(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            fixture = Path(temp)
            (fixture / "expect-stdout.txt").write_text("ok\n", encoding="utf-8")
            expectation = load_runtime_expectation(fixture, "host-linux-aarch64")
            self.assertIsNotNone(expectation)
            assert expectation is not None
            self.assertEqual(expectation.stdout, "ok\n")
            self.assertEqual(expectation.exit_code, 0)

    def test_windows_runtime_requires_a_windows_expectation(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            fixture = Path(temp)
            (fixture / "expect-stdout.txt").write_text("linux\n", encoding="utf-8")
            self.assertIsNone(load_runtime_expectation(fixture, "host-windows"))
            (fixture / "expect-exit-windows.txt").write_text("0\n", encoding="utf-8")
            expectation = load_runtime_expectation(fixture, "host-windows")
            self.assertIsNotNone(expectation)
            assert expectation is not None
            self.assertEqual(expectation.stdout, "linux\n")

    def test_invalid_runtime_exit_names_the_expectation_file(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            fixture = Path(temp)
            expectation_path = fixture / "expect-exit.txt"
            expectation_path.write_text("not-a-number\n", encoding="utf-8")
            with self.assertRaisesRegex(RuntimeError, "expect-exit.txt"):
                load_runtime_expectation(fixture, "host-linux")

    def test_project_version_is_read_without_msbuild(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            project = Path(temp) / "project.csproj"
            project.write_text(
                '<Project><PropertyGroup><Version>1.2.3-dev</Version></PropertyGroup></Project>',
                encoding="utf-8",
            )
            self.assertEqual(read_project_version(project), "1.2.3-dev")

    def test_unknown_exclusion_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "tests").mkdir()
            (root / "tests/native-suite-exclusions.json").write_text(
                json.dumps(
                    {
                        "version": 1,
                        "llvm": {"missing": "reason"},
                        "llvm_by_target": {},
                        "llvm_assertions": {},
                        "runtime": {},
                        "warnings": {},
                    }
                ),
                encoding="utf-8",
            )
            with self.assertRaisesRegex(RuntimeError, "not a fixture case"):
                load_suite_exclusions(root, [])

    def test_unknown_exclusion_section_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "tests").mkdir()
            (root / "tests/native-suite-exclusions.json").write_text(
                json.dumps(
                    {
                        "version": 1,
                        "llvm": {},
                        "llvm_by_target": {},
                        "llvm_assertions": {},
                        "runtime": {},
                        "warnings": {},
                        "runtimes": {},
                    }
                ),
                encoding="utf-8",
            )
            with self.assertRaisesRegex(RuntimeError, "unknown section 'runtimes'"):
                load_suite_exclusions(root, [])

    def test_target_specific_exclusion_is_loaded_for_windows(self) -> None:
        cases = load_fixture_manifest(PROJECT_ROOT)
        exclusions = load_suite_exclusions(PROJECT_ROOT, cases)
        self.assertIn(
            "self_check_builtin_compile_error",
            exclusions.llvm_by_target["host-windows"],
        )


if __name__ == "__main__":
    unittest.main()
