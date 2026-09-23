#!/usr/bin/env python3
"""Cross-platform production compiler test runner.

This runner deliberately exercises only the released/native compiler path.  The
C# project remains an explicit recovery artifact and is not needed to execute
the frontend, LLVM, runtime, target, or CLI regression gates.
"""

from __future__ import annotations

import argparse
import os
import platform
import shutil
import sys
import tempfile
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import Callable, Iterable, Mapping, Sequence


if __package__:
    # Keep the runner's historical import surface available to its callers.
    from .compiler_test_support import *  # noqa: F403
else:
    from compiler_test_support import *  # noqa: F403


class NativeCompilerSuite:
    def __init__(
        self,
        project_root: Path,
        compiler: Path,
        environment: dict[str, str],
        target: str,
        runtime_targets: Sequence[str],
        command_timeout_seconds: int,
        frontend_only: bool,
        selected_case: str | None,
    ) -> None:
        self.project_root = project_root
        self.compiler = compiler
        self.environment = environment
        self.target = target
        self.runtime_targets = list(runtime_targets)
        self.command_timeout_seconds = command_timeout_seconds
        self.frontend_only = frontend_only
        self.selected_case = selected_case
        self.failures: list[str] = []
        self.exclusion_tracker = ExclusionTracker()

    def run(self) -> int:
        cases = load_fixture_manifest(self.project_root)
        exclusions = load_suite_exclusions(self.project_root, cases)
        if self.selected_case is not None:
            cases = [case for case in cases if case.name == self.selected_case]
            if len(cases) != 1:
                raise SuiteFailure(f"no fixture named {self.selected_case!r}")

        with tempfile.TemporaryDirectory(prefix="zorb-native-suite-") as temp:
            output_root = Path(temp)
            for index, case in enumerate(cases):
                self._run_named(
                    case.name,
                    lambda case=case, index=index: self._test_case(
                        case, output_root, index, exclusions
                    ),
                )

            if self.selected_case is None:
                self._run_named("cli_json_diagnostics", self._test_cli_json_diagnostics)
                if not self.frontend_only:
                    self._run_runtime_tests(cases, output_root, exclusions)
                    self._run_named("cli_contract", lambda: self._test_cli_contract(output_root))

        try:
            self.exclusion_tracker.require_no_stale_exclusions()
        except SuiteFailure as error:
            self.failures.append(f"exclusions: {error}")

        if self.failures:
            print(file=sys.stderr)
            for failure in self.failures:
                print(failure, file=sys.stderr)
            return 1
        return 0

    def _run_command(
        self,
        arguments: Sequence[str | Path],
        cwd: Path | None = None,
        environment: dict[str, str] | None = None,
    ) -> CommandResult:
        return run_command(
            arguments,
            cwd or self.project_root,
            environment or self.environment,
            self.command_timeout_seconds,
        )

    def _run_named(self, name: str, action: Callable[[], None]) -> None:
        try:
            action()
        except SuiteSkip as skip:
            print(f"SKIP {name}: {skip}")
        except (OSError, ValueError, SuiteFailure) as error:
            self.failures.append(f"{name}: {error}")
            print(f"FAIL {name}")
        else:
            print(f"PASS {name}")

    def _test_case(
        self,
        case: FixtureCase,
        output_root: Path,
        index: int,
        exclusions: SuiteExclusions,
    ) -> None:
        checked = self._check_frontend_outcome(case)
        if case.expected != "success":
            return

        self._validate_warning_expectations(case, checked, exclusions)
        if self.frontend_only:
            return

        exclusion = self._resolve_llvm_exclusion(case, exclusions)
        llvm_ir = self._emit_llvm(case, output_root, index, exclusion)
        self._assert_llvm_expectations(case, llvm_ir, exclusions)

    def _check_frontend_outcome(self, case: FixtureCase) -> CommandResult:
        checked = self._run_command([self.compiler, "check", case.path])
        if case.expected == "success":
            if checked.returncode != 0:
                raise SuiteFailure(
                    format_command_failure("native check rejected successful input", checked)
                )
            return checked

        phase = diagnostic_phase(checked)
        if checked.returncode == 0:
            raise SuiteFailure(f"native check accepted input expecting {case.expected}")
        if phase is None:
            raise SuiteFailure(
                format_command_failure("native check emitted no structured diagnostic", checked)
            )
        if phase != case.expected:
            raise SuiteFailure(
                f"expected {case.expected}, got {phase}\n{checked.output.strip()}"
            )
        expected_codes = read_expectation_lines(case.path.parent / "expect-diagnostic-code.txt")
        if expected_codes:
            if len(expected_codes) != 1:
                raise SuiteFailure("fixture must specify exactly one expected diagnostic code")
            json_checked = self._run_command(
                [self.compiler, "check", case.path, CHECK_JSON_OPTION]
            )
            if json_checked.returncode == 0:
                raise SuiteFailure("JSON check accepted input expecting a diagnostic")
            diagnostics = parse_json_diagnostics(json_checked)
            actual_codes = [
                str(diagnostic["code"])
                for diagnostic in diagnostics
                if diagnostic["severity"] == "error"
            ]
            if actual_codes != expected_codes:
                raise SuiteFailure(
                    f"expected diagnostic codes {expected_codes!r}, got {actual_codes!r}\n"
                    f"{json_checked.output.strip()}"
                )
        return checked

    def _validate_warning_expectations(
        self, case: FixtureCase, checked: CommandResult, exclusions: SuiteExclusions
    ) -> None:
        warning_expectations = read_expectation_lines(case.path.parent / "expect-warnings.txt")
        native_warning_codes = read_expectation_lines(
            case.path.parent / "expect-native-warning-codes.txt"
        )
        warning_assertions = [*warning_expectations, *native_warning_codes]
        if not warning_assertions:
            return

        exclusion_reason = exclusions.warnings.get(case.name)
        if exclusion_reason is None:
            self._assert_warnings(case, checked, warning_assertions)
            return

        exclusion = SuiteExclusion(f"warnings:{case.name}", exclusion_reason)
        self.exclusion_tracker.register(exclusion.key, exclusion.reason)
        diagnostics = self._normalized_diagnostics(case, checked)
        missing_warnings = [
            expected for expected in warning_assertions if expected not in diagnostics
        ]
        if missing_warnings:
            self.exclusion_tracker.consume(exclusion.key)
            print(f"SKIP warning/{case.name}: {exclusion.reason}")

    def _resolve_llvm_exclusion(
        self, case: FixtureCase, exclusions: SuiteExclusions
    ) -> SuiteExclusion | None:
        target_exclusions = exclusions.llvm_by_target.get(self.target, {})
        exclusion_reason = exclusions.llvm.get(case.name)
        if exclusion_reason is not None:
            return SuiteExclusion(f"llvm:{case.name}", exclusion_reason)
        if case.name in target_exclusions:
            return SuiteExclusion(
                f"llvm_by_target:{self.target}:{case.name}", target_exclusions[case.name]
            )
        return None

    def _emit_llvm(
        self,
        case: FixtureCase,
        output_root: Path,
        index: int,
        exclusion: SuiteExclusion | None,
    ) -> str:
        if exclusion is not None:
            self.exclusion_tracker.register(exclusion.key, exclusion.reason)

        output = output_root / f"fixture-{index}.ll"
        built = self._run_command(
            [
                self.compiler,
                "build",
                case.path,
                "--target",
                self.target,
                "--output-kind",
                "llvm-ir",
                "-o",
                output,
            ],
        )
        if built.returncode != 0 or not output.is_file() or output.stat().st_size == 0:
            if exclusion is not None:
                self.exclusion_tracker.consume(exclusion.key)
                raise SuiteSkip(exclusion.reason)
            raise SuiteFailure(format_command_failure("LLVM IR emission failed", built))

        llvm_ir = output.read_text(encoding="utf-8")
        if "target triple =" not in llvm_ir:
            raise SuiteFailure("LLVM output did not contain a target triple")
        return llvm_ir

    def _assert_llvm_expectations(
        self, case: FixtureCase, llvm_ir: str, exclusions: SuiteExclusions
    ) -> None:
        expectations = llvm_expectations(case.path.parent, self.target)
        exclusion_reason = exclusions.llvm_assertions.get(case.name)
        if exclusion_reason is None:
            for expected in expectations:
                if expected not in llvm_ir:
                    raise SuiteFailure(f"LLVM output did not contain {expected!r}")
            return

        exclusion = SuiteExclusion(f"llvm_assertions:{case.name}", exclusion_reason)
        self.exclusion_tracker.register(exclusion.key, exclusion.reason)
        missing_assertions = [expected for expected in expectations if expected not in llvm_ir]
        if missing_assertions:
            self.exclusion_tracker.consume(exclusion.key)
            print(f"SKIP llvm-assertion/{case.name}: {exclusion.reason}")

    def _assert_warnings(
        self, case: FixtureCase, result: CommandResult, expectations: Sequence[str]
    ) -> None:
        diagnostics = self._normalized_diagnostics(case, result)
        for expected in expectations:
            if expected not in diagnostics:
                raise SuiteFailure(f"diagnostics did not contain warning {expected!r}")

    @staticmethod
    def _normalized_diagnostics(case: FixtureCase, result: CommandResult) -> str:
        diagnostics = result.output.replace(os.fspath(case.path), case.path.name)
        return diagnostics.replace(case.path.as_posix(), case.path.name)

    def _run_runtime_tests(
        self,
        cases: Iterable[FixtureCase],
        output_root: Path,
        exclusions: SuiteExclusions,
    ) -> None:
        fixture_cases = [
            case
            for case in cases
            if case.expected == "success" and "tests/csharp/fixtures" in case.path.as_posix()
        ]
        for target in self.runtime_targets:
            for case in fixture_cases:
                if not has_runtime_expectation(case.path.parent, target):
                    continue
                name = f"runtime/{target}/{case.name}"
                exclusion_reason = exclusions.runtime.get(case.name) or (
                    exclusions.runtime_by_target.get(target, {}).get(case.name)
                )
                self._run_named(
                    name,
                    lambda case=case, target=target, exclusion_reason=exclusion_reason: self._test_runtime_from_files(
                        case, target, output_root, exclusion_reason
                    ),
                )

    def _test_runtime_from_files(
        self,
        case: FixtureCase,
        target: str,
        output_root: Path,
        exclusion_reason: str | None,
    ) -> None:
        expectation = load_runtime_expectation(case.path.parent, target)
        if expectation is None:
            raise SuiteFailure("runtime expectation disappeared while running the suite")
        exclusion_key = f"runtime:{target}:{case.name}"
        if exclusion_reason is not None:
            self.exclusion_tracker.register(exclusion_key, exclusion_reason)
        try:
            self._test_runtime(case, expectation, output_root)
        except SuiteFailure:
            if exclusion_reason is None:
                raise
            self.exclusion_tracker.consume(exclusion_key)
            raise SuiteSkip(exclusion_reason)

    def _test_runtime(
        self, case: FixtureCase, expectation: RuntimeExpectation, output_root: Path
    ) -> None:
        runtime_dir = Path(tempfile.mkdtemp(prefix=f"runtime-{case.name}-", dir=output_root))
        copy_runtime_data(case.path.parent, runtime_dir)
        binary = runtime_dir / ("out.exe" if expectation.target == "host-windows" else "out")
        built = self._run_command(
            [self.compiler, "build", case.path, "--target", expectation.target, "-o", binary],
        )
        if built.returncode != 0 or not binary.is_file():
            raise SuiteFailure(format_command_failure("runtime build failed", built))

        executed = self._run_command(
            execution_command(binary, expectation.target, self.environment),
            runtime_dir,
        )
        if executed.returncode != expectation.exit_code:
            raise SuiteFailure(
                format_command_failure(
                    f"expected runtime exit {expectation.exit_code}", executed
                )
            )
        actual_stdout = normalize_newlines(executed.stdout)
        actual_stderr = normalize_newlines(executed.stderr)
        if expectation.stdout is not None and actual_stdout != expectation.stdout:
            raise SuiteFailure(
                f"stdout mismatch\nexpected: {expectation.stdout!r}\nactual:   {actual_stdout!r}"
            )
        if expectation.stderr is not None and actual_stderr != expectation.stderr:
            raise SuiteFailure(
                f"stderr mismatch\nexpected: {expectation.stderr!r}\nactual:   {actual_stderr!r}"
            )

    def _test_cli_json_diagnostics(self) -> None:
        cases = (
            (
                "semantic",
                self.project_root / "tests/csharp/fixtures/bool_condition_required/main.zorb",
                1,
                {
                    "severity": "error",
                    "phase": "semantic",
                    "code": "type.condition-not-bool",
                    "file": "main.zorb",
                    "line": 3,
                    "column": 8,
                    "message": "condition must be bool",
                },
            ),
            (
                "parse",
                self.project_root / "tests/csharp/fixtures/parse_parameter_missing_colon/main.zorb",
                1,
                {
                    "severity": "error",
                    "phase": "parse",
                    "code": "parse.invalid-syntax",
                    "file": "main.zorb",
                    "line": 1,
                    "column": 17,
                    "message": "invalid syntax",
                },
            ),
            (
                "import",
                self.project_root / "compiler/self-check/fixtures/import_missing/main.zorb",
                1,
                {
                    "severity": "error",
                    "phase": "import",
                    "code": "import.not-found",
                    "file": "main.zorb",
                    "line": 1,
                    "column": 1,
                    "message": "unable to read source file",
                },
            ),
            (
                "warning",
                self.project_root / "tests/csharp/fixtures/warning_unreachable_after_return/main.zorb",
                0,
                {
                    "severity": "warning",
                    "phase": "semantic",
                    "code": "flow.unreachable",
                    "file": "main.zorb",
                    "line": 3,
                    "column": 5,
                    "message": "Unreachable statement.",
                },
            ),
            (
                "success",
                self.project_root / "tests/csharp/fixtures/runtime_hello_world/main.zorb",
                0,
                None,
            ),
        )
        for name, source, expected_exit, expected in cases:
            result = self._run_command([self.compiler, "check", source, CHECK_JSON_OPTION])
            if result.returncode != expected_exit:
                raise SuiteFailure(
                    format_command_failure(f"JSON check/{name} returned the wrong exit code", result)
                )
            diagnostics = parse_json_diagnostics(result)
            if expected is None:
                if diagnostics:
                    raise SuiteFailure(f"JSON check/{name} emitted unexpected diagnostics")
                continue
            if len(diagnostics) != 1:
                raise SuiteFailure(
                    f"JSON check/{name} emitted {len(diagnostics)} diagnostics, expected one"
                )
            actual = diagnostics[0]
            for field, value in expected.items():
                actual_value = actual[field]
                if field == "file":
                    actual_value = Path(str(actual_value)).name
                if actual_value != value:
                    raise SuiteFailure(
                        f"JSON check/{name} field {field!r}: expected {value!r}, got {actual_value!r}"
                    )

        malformed = self._run_command(
            [
                self.compiler,
                "check",
                self.project_root / "tests/csharp/fixtures/runtime_hello_world/main.zorb",
                "--unknown-check-option",
            ]
        )
        if malformed.returncode != USAGE_EXIT_CODE or malformed.stdout:
            raise SuiteFailure(
                format_command_failure("malformed JSON check option was not rejected as usage", malformed)
            )
        if "usage: zorb check" not in malformed.stderr:
            raise SuiteFailure("malformed JSON check option did not print check usage")

    def _test_advanced_examples_lowering(self, output_root: Path) -> None:
        cases = (
            "stress_pipeline.zorb",
            "threads.zorb",
        )
        for source_name in cases:
            source = self.project_root / "examples/advanced" / source_name
            output = output_root / f"advanced-{source.stem}.ll"
            result = self._run_command(
                [self.compiler, "build", source, "--output-kind", "llvm-ir", "-o", output],
            )
            if (
                result.returncode != 0
                or result.stdout
                or not output.is_file()
                or output.stat().st_size == 0
            ):
                raise SuiteFailure(
                    format_command_failure(
                        f"native lowering failed for {source_name}", result
                    )
                )

    def _test_cli_contract(self, output_root: Path) -> None:
        simple = self.project_root / "tests/csharp/fixtures/runtime_hello_world/main.zorb"
        invalid_output = output_root / "invalid-native-link-args.ll"
        invalid = self._run_command(
            [
                self.compiler,
                "build",
                simple,
                "--output-kind",
                "llvm-ir",
                "-o",
                invalid_output,
                "--native-link-args",
                "-lm",
            ],
        )
        if invalid.returncode != 64:
            raise SuiteFailure("native linker arguments were accepted for non-executable output")

        def run_hello(_: int) -> CommandResult:
            return self._run_command(
                [self.compiler, "run", simple, "--target", self.target],
            )

        with ThreadPoolExecutor(max_workers=CONCURRENT_RUN_COUNT) as executor:
            concurrent = list(executor.map(run_hello, range(CONCURRENT_RUN_COUNT)))
        failed = [result for result in concurrent if result.returncode != 0 or result.stdout != "ok\n"]
        if failed:
            raise SuiteFailure(format_command_failure("concurrent native run failed", failed[0]))

        self._test_named_target_triples(simple, output_root)
        self._test_bare_metal_linking(output_root)
        self._test_advanced_examples_lowering(output_root)

    def _test_named_target_triples(self, source: Path, output_root: Path) -> None:
        arm_host = platform.machine().lower() in {"aarch64", "arm64"}
        targets = {
            "host-linux": "aarch64-unknown-linux-gnu" if arm_host else "x86_64-pc-linux-gnu",
            "freestanding-linux": "aarch64-unknown-linux-gnu" if arm_host else "x86_64-pc-linux-gnu",
            "host-linux-aarch64": "aarch64-unknown-linux-gnu",
            "freestanding-linux-aarch64": "aarch64-unknown-linux-gnu",
            "bare-metal-x86_64": "x86_64-unknown-none-elf",
            "host-windows": "aarch64-pc-windows-msvc" if arm_host else "x86_64-pc-windows-msvc",
        }
        for target, triple in targets.items():
            output = output_root / f"target-{target}.ll"
            built = self._run_command(
                [
                    self.compiler,
                    "build",
                    source,
                    "--target",
                    target,
                    "--output-kind",
                    "llvm-ir",
                    "-o",
                    output,
                ],
            )
            if built.returncode != 0 or not output.is_file():
                raise SuiteFailure(format_command_failure(f"named target {target} failed", built))
            if f'target triple = "{triple}"' not in output.read_text(encoding="utf-8"):
                raise SuiteFailure(f"named target {target} did not emit triple {triple}")

    def _test_bare_metal_linking(self, output_root: Path) -> None:
        if platform.machine().lower() not in {"x86_64", "amd64"}:
            return
        linker = next(
            (path for name in ("ld.lld-22", "ld.lld") if (path := shutil.which(name))),
            None,
        )
        if linker is None:
            return
        source = self.project_root / "tests/csharp/fixtures/bare_metal_debug_port/main.zorb"
        output = output_root / "kernel.elf"
        script = output_root / "kernel.ld"
        environment = dict(self.environment)
        environment["ZORB_LLD"] = linker
        built = self._run_command(
            [
                self.compiler,
                "build",
                source,
                "--target",
                "bare-metal-x86_64",
                "-o",
                output,
                "--emit-linker-script",
                script,
            ],
            environment=environment,
        )
        if built.returncode != 0 or not output.is_file() or not script.is_file():
            raise SuiteFailure(format_command_failure("bare-metal linking failed", built))
        if "ENTRY(_start)" not in script.read_text(encoding="utf-8"):
            raise SuiteFailure("bare-metal linker script did not preserve _start")


def positive_timeout_seconds(value: str) -> int:
    try:
        seconds = int(value)
    except ValueError as error:
        raise argparse.ArgumentTypeError("timeout must be an integer") from error
    if seconds <= 0:
        raise argparse.ArgumentTypeError("timeout must be greater than zero")
    return seconds


def parse_arguments(
    arguments: Sequence[str], environment: Mapping[str, str] | None = None
) -> argparse.Namespace:
    environment = os.environ if environment is None else environment
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--compiler", type=Path, help="native compiler to test")
    parser.add_argument("--target", default=default_target(), help="LLVM emission target")
    parser.add_argument(
        "--runtime-target",
        action="append",
        dest="runtime_targets",
        help="runtime target to execute; repeat for multiple targets",
    )
    parser.add_argument("--frontend-only", action="store_true", help="only check frontend outcomes")
    parser.add_argument("--case", help="run one manifest case and skip runtime/CLI tests")
    parser.add_argument(
        "--command-timeout-seconds",
        type=positive_timeout_seconds,
        default=environment.get(
            COMMAND_TIMEOUT_ENVIRONMENT_VARIABLE,
            str(DEFAULT_COMMAND_TIMEOUT_SECONDS),
        ),
        help=(
            "per-command compiler/runtime timeout; defaults to 60 seconds or "
            f"${COMMAND_TIMEOUT_ENVIRONMENT_VARIABLE}"
        ),
    )
    parser.add_argument(
        "--no-bootstrap",
        action="store_true",
        help="fail instead of bootstrapping when the default compiler is absent",
    )
    return parser.parse_args(arguments)


def main(arguments: Sequence[str] | None = None) -> int:
    options = parse_arguments(sys.argv[1:] if arguments is None else arguments)
    project_root = Path(__file__).resolve().parents[1]
    compiler = (options.compiler or default_compiler_path(project_root)).resolve()
    environment = dict(os.environ)

    if not compiler.is_file():
        if options.compiler is not None or options.no_bootstrap:
            raise SuiteFailure(f"native compiler does not exist: {compiler}")
        bootstrap_compiler(project_root, compiler, environment)

    runtime_targets = options.runtime_targets
    if runtime_targets is None:
        runtime_targets = default_runtime_targets()
    suite = NativeCompilerSuite(
        project_root=project_root,
        compiler=compiler,
        environment=environment,
        target=options.target,
        runtime_targets=runtime_targets,
        command_timeout_seconds=options.command_timeout_seconds,
        frontend_only=options.frontend_only,
        selected_case=options.case,
    )
    return suite.run()


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except SuiteFailure as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1)
