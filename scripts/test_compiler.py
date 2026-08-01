#!/usr/bin/env python3
"""Cross-platform production compiler test runner.

This runner deliberately exercises only the released/native compiler path.  The
C# project remains an explicit recovery artifact and is not needed to execute
the frontend, LLVM, runtime, target, or CLI regression gates.
"""

from __future__ import annotations

import argparse
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import tempfile
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Iterable, Sequence


MANIFEST_VERSION = 2
EXCLUSIONS_VERSION = 1
EXCLUSION_SECTIONS = ("llvm", "llvm_by_target", "llvm_assertions", "runtime", "warnings")
SUPPORTED_TARGETS = {
    "host-linux",
    "freestanding-linux",
    "host-linux-aarch64",
    "freestanding-linux-aarch64",
    "host-windows",
    "bare-metal-x86_64",
}
COMMAND_TIMEOUT_SECONDS = 60
BOOTSTRAP_TIMEOUT_SECONDS = 600
CONCURRENT_RUN_COUNT = 8
VALID_CLASSIFICATIONS = {"deferred", "native-verified", "differential"}
VALID_OUTCOMES = {
    "success",
    "lexical-failure",
    "parse-failure",
    "import-failure",
    "semantic-failure",
}
DIAGNOSTIC_PATTERN = re.compile(r"error\[(?P<code>[a-z0-9.-]+)\]")


@dataclass(frozen=True)
class FixtureCase:
    name: str
    path: Path
    classification: str
    feature: str
    expected: str
    gate: str | None
    reason: str


@dataclass(frozen=True)
class CommandResult:
    returncode: int
    stdout: str
    stderr: str

    @property
    def output(self) -> str:
        return normalize_newlines(self.stdout + self.stderr)


@dataclass(frozen=True)
class RuntimeExpectation:
    target: str
    stdout: str | None
    stderr: str | None
    exit_code: int


@dataclass(frozen=True)
class SuiteExclusions:
    llvm: dict[str, str]
    llvm_by_target: dict[str, dict[str, str]]
    llvm_assertions: dict[str, str]
    runtime: dict[str, str]
    warnings: dict[str, str]


class SuiteFailure(RuntimeError):
    pass


class SuiteSkip(RuntimeError):
    pass


def normalize_newlines(value: str) -> str:
    return value.removeprefix("\ufeff").replace("\r\n", "\n").replace("\r", "\n")


def read_expectation_lines(path: Path) -> list[str]:
    if not path.is_file():
        return []
    return [
        line.strip()
        for line in normalize_newlines(path.read_text(encoding="utf-8")).splitlines()
        if line.strip() and not line.lstrip().startswith("#")
    ]


def expected_phase_from_code(code: str) -> str:
    if code.startswith("lex."):
        return "lexical-failure"
    if code.startswith("parse."):
        return "parse-failure"
    if code.startswith("import."):
        return "import-failure"
    return "semantic-failure"


def enumerate_parity_sources(project_root: Path) -> set[Path]:
    sources = set((project_root / "tests/csharp/fixtures").glob("**/main.zorb"))

    native_root = project_root / "compiler/self-check/fixtures"
    for path in native_root.glob("**/*.zorb"):
        relative = path.relative_to(native_root)
        if len(relative.parts) == 1 or path.name == "main.zorb":
            sources.add(path)

    examples_root = project_root / "examples"
    for path in examples_root.glob("**/*.zorb"):
        if path.name == "main.zorb" or not (path.parent / "main.zorb").is_file():
            sources.add(path)
    return {path.resolve() for path in sources}


def load_json_object(path: Path, description: str) -> dict[str, object]:
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise SuiteFailure(f"cannot read {description} {path}: {error}") from error
    if not isinstance(payload, dict):
        raise SuiteFailure(f"{description} must be an object")
    return payload


def load_fixture_manifest(project_root: Path) -> list[FixtureCase]:
    manifest_path = project_root / "tests/csharp/frontend-parity.json"
    payload = load_json_object(manifest_path, "fixture manifest")
    if payload.get("version") != MANIFEST_VERSION:
        raise SuiteFailure(f"unsupported fixture manifest version {payload.get('version')!r}")

    cases: list[FixtureCase] = []
    names: set[str] = set()
    paths: set[Path] = set()
    entries = payload.get("entries", [])
    if not isinstance(entries, list):
        raise SuiteFailure("fixture manifest 'entries' must be an array")
    for raw in entries:
        if not isinstance(raw, dict):
            raise SuiteFailure("fixture manifest entry must be an object")
        required = ("name", "path", "classification", "feature", "expected", "reason")
        if any(not isinstance(raw.get(key), str) or not raw[key].strip() for key in required):
            raise SuiteFailure("fixture manifest contains an incomplete entry")
        if raw["classification"] not in VALID_CLASSIFICATIONS:
            raise SuiteFailure(
                f"fixture {raw['name']!r} has unknown classification {raw['classification']!r}"
            )
        if raw["expected"] not in VALID_OUTCOMES:
            raise SuiteFailure(f"fixture {raw['name']!r} has unknown outcome {raw['expected']!r}")
        gate = raw.get("gate")
        if gate not in (None, "frontend"):
            raise SuiteFailure(f"fixture {raw['name']!r} has unknown gate {gate!r}")
        if gate == "frontend" and raw["classification"] != "differential":
            raise SuiteFailure(f"gated fixture {raw['name']!r} must be differential")

        input_path = (project_root / raw["path"]).resolve()
        if raw["name"] in names:
            raise SuiteFailure(f"fixture manifest contains duplicate name {raw['name']!r}")
        if input_path in paths:
            raise SuiteFailure(f"fixture manifest contains duplicate path {raw['path']!r}")
        if not input_path.is_file():
            raise SuiteFailure(f"fixture {raw['name']!r} references missing input {raw['path']!r}")
        names.add(raw["name"])
        paths.add(input_path)
        cases.append(
            FixtureCase(
                name=raw["name"],
                path=input_path,
                classification=raw["classification"],
                feature=raw["feature"],
                expected=raw["expected"],
                gate=gate,
                reason=raw["reason"],
            )
        )

    sources = enumerate_parity_sources(project_root)
    missing = sorted(sources - paths)
    outside = sorted(paths - sources)
    if missing:
        raise SuiteFailure(f"fixture manifest does not classify {missing[0].relative_to(project_root)}")
    if outside:
        raise SuiteFailure(f"fixture manifest path is outside the corpus: {outside[0]}")
    if not any(case.gate == "frontend" for case in cases):
        raise SuiteFailure("fixture manifest has no enabled frontend cases")
    return sorted(cases, key=lambda case: case.name)


def load_suite_exclusions(project_root: Path, cases: Sequence[FixtureCase]) -> SuiteExclusions:
    path = project_root / "tests/native-suite-exclusions.json"
    payload = load_json_object(path, "native-suite exclusions")
    if payload.get("version") != EXCLUSIONS_VERSION:
        raise SuiteFailure(f"unsupported native-suite exclusions version {payload.get('version')!r}")
    unknown_sections = sorted(set(payload) - {"version", *EXCLUSION_SECTIONS})
    if unknown_sections:
        raise SuiteFailure(
            f"native-suite exclusions has unknown section {unknown_sections[0]!r}"
        )
    case_names = {case.name for case in cases}

    def read_section(name: str) -> dict[str, str]:
        section = payload.get(name)
        if not isinstance(section, dict):
            raise SuiteFailure(f"native-suite exclusions section {name!r} must be an object")
        for case_name, reason in section.items():
            if case_name not in case_names:
                raise SuiteFailure(f"native-suite exclusion {case_name!r} is not a fixture case")
            if not isinstance(reason, str) or not reason.strip():
                raise SuiteFailure(f"native-suite exclusion {case_name!r} has no reason")
        return section

    def read_target_section(name: str) -> dict[str, dict[str, str]]:
        section = payload.get(name)
        if not isinstance(section, dict):
            raise SuiteFailure(f"native-suite exclusions section {name!r} must be an object")
        result: dict[str, dict[str, str]] = {}
        for target, target_cases in section.items():
            if target not in SUPPORTED_TARGETS:
                raise SuiteFailure(f"native-suite exclusion has unknown target {target!r}")
            if not isinstance(target_cases, dict):
                raise SuiteFailure(f"native-suite exclusions target {target!r} must be an object")
            for case_name, reason in target_cases.items():
                if case_name not in case_names:
                    raise SuiteFailure(f"native-suite exclusion {case_name!r} is not a fixture case")
                if not isinstance(reason, str) or not reason.strip():
                    raise SuiteFailure(f"native-suite exclusion {case_name!r} has no reason")
            result[target] = target_cases
        return result

    return SuiteExclusions(
        llvm=read_section("llvm"),
        llvm_by_target=read_target_section("llvm_by_target"),
        llvm_assertions=read_section("llvm_assertions"),
        runtime=read_section("runtime"),
        warnings=read_section("warnings"),
    )


def run_command(
    arguments: Sequence[str | Path],
    cwd: Path,
    environment: dict[str, str],
    timeout_seconds: int = COMMAND_TIMEOUT_SECONDS,
) -> CommandResult:
    command = [os.fspath(argument) for argument in arguments]
    try:
        completed = subprocess.run(
            command,
            cwd=cwd,
            env=environment,
            text=True,
            encoding="utf-8",
            errors="replace",
            capture_output=True,
            timeout=timeout_seconds,
            check=False,
        )
    except subprocess.TimeoutExpired as error:
        raise SuiteFailure(f"command timed out after {timeout_seconds}s: {' '.join(command)}") from error
    return CommandResult(completed.returncode, completed.stdout, completed.stderr)


def default_compiler_path(project_root: Path) -> Path:
    return project_root / "build" / ("zorb.exe" if os.name == "nt" else "zorb")


def bootstrap_compiler(project_root: Path, compiler: Path, environment: dict[str, str]) -> None:
    compiler.parent.mkdir(parents=True, exist_ok=True)
    result = run_command(
        [sys.executable, project_root / "scripts/bootstrap_compiler.py", "bootstrap", compiler],
        project_root,
        environment,
        BOOTSTRAP_TIMEOUT_SECONDS,
    )
    if result.returncode != 0 or not compiler.is_file():
        raise SuiteFailure(f"native compiler bootstrap failed\n{result.output}".rstrip())


def default_target() -> str:
    if os.name == "nt":
        return "host-windows"
    if platform.machine().lower() in {"aarch64", "arm64"}:
        return "host-linux-aarch64"
    return "host-linux"


def default_runtime_targets() -> list[str]:
    if os.name == "nt":
        return ["host-windows"]
    if platform.machine().lower() in {"aarch64", "arm64"}:
        return ["host-linux-aarch64"]
    return ["host-linux"]


def expectation_suffix(target: str) -> str | None:
    return {
        "host-windows": "windows",
        "freestanding-linux-aarch64": "linux-aarch64",
        "host-linux-aarch64": "host-linux-aarch64",
    }.get(target)


def read_optional_text(primary: Path, fallback: Path | None) -> str | None:
    selected = primary if primary.is_file() else fallback
    if selected is None or not selected.is_file():
        return None
    return normalize_newlines(selected.read_text(encoding="utf-8"))


def read_optional_exit(primary: Path, fallback: Path | None) -> int:
    selected = primary if primary.is_file() else fallback
    if selected is None or not selected.is_file():
        return 0
    contents = selected.read_text(encoding="utf-8").strip()
    try:
        return int(contents)
    except ValueError as error:
        raise SuiteFailure(
            f"invalid runtime exit code {contents!r} in {selected}"
        ) from error


def has_runtime_expectation(fixture_dir: Path, target: str) -> bool:
    suffix = expectation_suffix(target)
    generic = [fixture_dir / f"expect-{kind}.txt" for kind in ("stdout", "stderr", "exit")]
    specific = [
        fixture_dir / f"expect-{kind}-{suffix}.txt" if suffix else path
        for kind, path in zip(("stdout", "stderr", "exit"), generic, strict=True)
    ]
    if target == "host-windows":
        return any(path.is_file() for path in specific)
    return any(path.is_file() for path in generic + specific)


def load_runtime_expectation(fixture_dir: Path, target: str) -> RuntimeExpectation | None:
    suffix = expectation_suffix(target)
    generic_paths = {
        kind: fixture_dir / f"expect-{kind}.txt" for kind in ("stdout", "stderr", "exit")
    }
    specific_paths = {
        kind: fixture_dir / f"expect-{kind}-{suffix}.txt" if suffix else path
        for (kind, path) in generic_paths.items()
    }

    if not has_runtime_expectation(fixture_dir, target):
        return None

    return RuntimeExpectation(
        target=target,
        stdout=read_optional_text(specific_paths["stdout"], generic_paths["stdout"]),
        stderr=read_optional_text(specific_paths["stderr"], generic_paths["stderr"]),
        exit_code=read_optional_exit(specific_paths["exit"], generic_paths["exit"]),
    )


def llvm_expectations(fixture_dir: Path, target: str) -> list[str]:
    platform_name = "windows" if target == "host-windows" else "linux"
    platform_path = fixture_dir / f"expect-llvm-{platform_name}.txt"
    generic_path = fixture_dir / "expect-llvm.txt"
    return read_expectation_lines(platform_path if platform_path.is_file() else generic_path)


def copy_runtime_data(fixture_dir: Path, destination: Path) -> None:
    for path in fixture_dir.iterdir():
        if path.is_file() and path.name != "main.zorb" and not path.name.startswith("expect-"):
            shutil.copy2(path, destination / path.name)


def execution_command(binary: Path, target: str, environment: dict[str, str]) -> list[str]:
    machine = platform.machine().lower()
    is_cross_aarch64 = target in {
        "freestanding-linux-aarch64",
        "host-linux-aarch64",
    } and machine not in {"aarch64", "arm64"}
    if not is_cross_aarch64:
        return [os.fspath(binary)]
    qemu = environment.get("ZORB_QEMU_AARCH64", "qemu-aarch64")
    sysroot = environment.get("ZORB_AARCH64_LINUX_SYSROOT", "/usr/aarch64-linux-gnu")
    return [qemu, "-L", sysroot, os.fspath(binary)]


def diagnostic_phase(result: CommandResult) -> str | None:
    match = DIAGNOSTIC_PATTERN.search(result.output)
    return expected_phase_from_code(match.group("code")) if match else None


def format_command_failure(context: str, result: CommandResult) -> str:
    details = result.output.strip()
    if len(details) > 4000:
        details = details[-4000:]
    return f"{context} (exit {result.returncode})" + (f"\n{details}" if details else "")


class NativeCompilerSuite:
    def __init__(
        self,
        project_root: Path,
        compiler: Path,
        environment: dict[str, str],
        target: str,
        runtime_targets: Sequence[str],
        frontend_only: bool,
        selected_case: str | None,
    ) -> None:
        self.project_root = project_root
        self.compiler = compiler
        self.environment = environment
        self.target = target
        self.runtime_targets = list(runtime_targets)
        self.frontend_only = frontend_only
        self.selected_case = selected_case
        self.failures: list[str] = []

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

            if not self.frontend_only and self.selected_case is None:
                self._run_runtime_tests(cases, output_root, exclusions)
                self._run_named("cli_contract", lambda: self._test_cli_contract(output_root))

        if self.failures:
            print(file=sys.stderr)
            for failure in self.failures:
                print(failure, file=sys.stderr)
            return 1
        return 0

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
        checked = run_command(
            [self.compiler, "check", case.path], self.project_root, self.environment
        )
        if case.expected == "success":
            if checked.returncode != 0:
                raise SuiteFailure(format_command_failure("native check rejected successful input", checked))
            warning_expectations = read_expectation_lines(case.path.parent / "expect-warnings.txt")
            if warning_expectations:
                if case.name in exclusions.warnings:
                    print(f"SKIP warning/{case.name}: {exclusions.warnings[case.name]}")
                else:
                    self._assert_warnings(case, checked, warning_expectations)
            if self.frontend_only:
                return
            target_exclusions = exclusions.llvm_by_target.get(self.target, {})
            exclusion_reason = exclusions.llvm.get(case.name) or target_exclusions.get(case.name)
            if exclusion_reason is not None:
                raise SuiteSkip(exclusion_reason)
            output = output_root / f"fixture-{index}.ll"
            built = run_command(
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
                self.project_root,
                self.environment,
            )
            if built.returncode != 0 or not output.is_file() or output.stat().st_size == 0:
                raise SuiteFailure(format_command_failure("LLVM IR emission failed", built))
            llvm_ir = output.read_text(encoding="utf-8")
            if "target triple =" not in llvm_ir:
                raise SuiteFailure("LLVM output did not contain a target triple")
            if case.name in exclusions.llvm_assertions:
                print(
                    f"SKIP llvm-assertion/{case.name}: "
                    f"{exclusions.llvm_assertions[case.name]}"
                )
            else:
                for expected in llvm_expectations(case.path.parent, self.target):
                    if expected not in llvm_ir:
                        raise SuiteFailure(f"LLVM output did not contain {expected!r}")
            return

        phase = diagnostic_phase(checked)
        if checked.returncode == 0:
            raise SuiteFailure(f"native check accepted input expecting {case.expected}")
        if phase is None:
            raise SuiteFailure(format_command_failure("native check emitted no structured diagnostic", checked))
        if phase != case.expected:
            raise SuiteFailure(
                f"expected {case.expected}, got {phase}\n{checked.output.strip()}"
            )

    def _assert_warnings(
        self, case: FixtureCase, result: CommandResult, expectations: Sequence[str]
    ) -> None:
        diagnostics = result.output.replace(os.fspath(case.path), case.path.name)
        diagnostics = diagnostics.replace(case.path.as_posix(), case.path.name)
        for expected in expectations:
            if expected not in diagnostics:
                raise SuiteFailure(f"diagnostics did not contain warning {expected!r}")

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
                if case.name in exclusions.runtime:
                    print(f"SKIP {name}: {exclusions.runtime[case.name]}")
                    continue
                self._run_named(
                    name,
                    lambda case=case, target=target: self._test_runtime_from_files(
                        case, target, output_root
                    ),
                )

    def _test_runtime_from_files(
        self, case: FixtureCase, target: str, output_root: Path
    ) -> None:
        expectation = load_runtime_expectation(case.path.parent, target)
        if expectation is None:
            raise SuiteFailure("runtime expectation disappeared while running the suite")
        self._test_runtime(case, expectation, output_root)

    def _test_runtime(
        self, case: FixtureCase, expectation: RuntimeExpectation, output_root: Path
    ) -> None:
        runtime_dir = Path(tempfile.mkdtemp(prefix=f"runtime-{case.name}-", dir=output_root))
        copy_runtime_data(case.path.parent, runtime_dir)
        binary = runtime_dir / ("out.exe" if expectation.target == "host-windows" else "out")
        built = run_command(
            [self.compiler, "build", case.path, "--target", expectation.target, "-o", binary],
            self.project_root,
            self.environment,
        )
        if built.returncode != 0 or not binary.is_file():
            raise SuiteFailure(format_command_failure("runtime build failed", built))

        executed = run_command(
            execution_command(binary, expectation.target, self.environment),
            runtime_dir,
            self.environment,
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

    def _test_cli_contract(self, output_root: Path) -> None:
        simple = self.project_root / "tests/csharp/fixtures/runtime_hello_world/main.zorb"
        invalid_output = output_root / "invalid-native-link-args.ll"
        invalid = run_command(
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
            self.project_root,
            self.environment,
        )
        if invalid.returncode != 64:
            raise SuiteFailure("native linker arguments were accepted for non-executable output")

        def run_hello(_: int) -> CommandResult:
            return run_command(
                [self.compiler, "run", simple, "--target", self.target],
                self.project_root,
                self.environment,
            )

        with ThreadPoolExecutor(max_workers=CONCURRENT_RUN_COUNT) as executor:
            concurrent = list(executor.map(run_hello, range(CONCURRENT_RUN_COUNT)))
        failed = [result for result in concurrent if result.returncode != 0 or result.stdout != "ok\n"]
        if failed:
            raise SuiteFailure(format_command_failure("concurrent native run failed", failed[0]))

        self._test_named_target_triples(simple, output_root)
        self._test_bare_metal_linking(output_root)

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
            built = run_command(
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
                self.project_root,
                self.environment,
            )
            if built.returncode != 0 or not output.is_file():
                raise SuiteFailure(format_command_failure(f"named target {target} failed", built))
            if f'target triple = "{triple}"' not in output.read_text(encoding="utf-8"):
                raise SuiteFailure(f"named target {target} did not emit triple {triple}")

    def _test_bare_metal_linking(self, output_root: Path) -> None:
        if platform.machine().lower() not in {"x86_64", "amd64"}:
            return
        linker = next(
            (path for name in ("ld.lld-21", "ld.lld") if (path := shutil.which(name))),
            None,
        )
        if linker is None:
            return
        source = self.project_root / "tests/csharp/fixtures/bare_metal_debug_port/main.zorb"
        output = output_root / "kernel.elf"
        script = output_root / "kernel.ld"
        environment = dict(self.environment)
        environment["ZORB_LLD"] = linker
        built = run_command(
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
            self.project_root,
            environment,
        )
        if built.returncode != 0 or not output.is_file() or not script.is_file():
            raise SuiteFailure(format_command_failure("bare-metal linking failed", built))
        if "ENTRY(_start)" not in script.read_text(encoding="utf-8"):
            raise SuiteFailure("bare-metal linker script did not preserve _start")


def parse_arguments(arguments: Sequence[str]) -> argparse.Namespace:
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
