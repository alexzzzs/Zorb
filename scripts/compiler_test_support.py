"""Fixture catalog and command helpers for the native compiler suite."""


from __future__ import annotations

import json
import os
import platform
import re
import shutil
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Sequence


MANIFEST_VERSION = 2
EXCLUSIONS_VERSION = 1
EXCLUSION_SECTIONS = (
    "llvm",
    "llvm_by_target",
    "llvm_assertions",
    "runtime",
    "runtime_by_target",
    "warnings",
)
SUPPORTED_TARGETS = {
    "host-linux",
    "freestanding-linux",
    "host-linux-aarch64",
    "freestanding-linux-aarch64",
    "host-windows",
    "bare-metal-x86_64",
}
DEFAULT_COMMAND_TIMEOUT_SECONDS = 60
COMMAND_TIMEOUT_ENVIRONMENT_VARIABLE = "ZORB_TEST_COMMAND_TIMEOUT_SECONDS"
BOOTSTRAP_TIMEOUT_SECONDS = 600
CONCURRENT_RUN_COUNT = 8
CHECK_JSON_OPTION = "--json"
USAGE_EXIT_CODE = 64
JSON_DIAGNOSTIC_FIELDS = frozenset(
    {"kind", "severity", "phase", "code", "file", "line", "column", "length", "message"}
)
VALID_CLASSIFICATIONS = {"deferred", "native-verified", "differential"}
VALID_OUTCOMES = {
    "success",
    "lexical-failure",
    "parse-failure",
    "import-failure",
    "semantic-failure",
}
DIAGNOSTIC_PATTERN = re.compile(r"error\[(?P<code>[a-z0-9.-]+)\]")
SEMANTIC_DIAGNOSTIC_PREFIXES = ("name.", "type.", "flow.", "pointer.")


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
class SuiteExclusion:
    key: str
    reason: str


@dataclass(frozen=True)
class SuiteExclusions:
    llvm: dict[str, str]
    llvm_by_target: dict[str, dict[str, str]]
    llvm_assertions: dict[str, str]
    runtime: dict[str, str]
    runtime_by_target: dict[str, dict[str, str]]
    warnings: dict[str, str]


class SuiteFailure(RuntimeError):
    pass


class SuiteSkip(RuntimeError):
    pass


class ExclusionTracker:
    def __init__(self) -> None:
        self._applicable: dict[str, str] = {}
        self._consumed: set[str] = set()

    def register(self, key: str, reason: str) -> None:
        self._applicable[key] = reason

    def consume(self, key: str) -> None:
        self._consumed.add(key)

    def require_no_stale_exclusions(self) -> None:
        stale = sorted(set(self._applicable) - self._consumed)
        if stale:
            details = "; ".join(
                f"{key}: {self._applicable[key]}" for key in stale
            )
            raise SuiteFailure(f"stale native-suite exclusions: {details}")


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
    if code.startswith(SEMANTIC_DIAGNOSTIC_PREFIXES):
        return "semantic-failure"
    raise SuiteFailure(f"unrecognized structured diagnostic code {code!r}")


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
        runtime_by_target=read_target_section("runtime_by_target"),
        warnings=read_section("warnings"),
    )


def run_command(
    arguments: Sequence[str | Path],
    cwd: Path,
    environment: dict[str, str],
    timeout_seconds: int = DEFAULT_COMMAND_TIMEOUT_SECONDS,
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
    target_path = fixture_dir / f"expect-llvm-{target}.txt"
    platform_name = "windows" if target == "host-windows" else "linux"
    platform_path = fixture_dir / f"expect-llvm-{platform_name}.txt"
    generic_path = fixture_dir / "expect-llvm.txt"
    if target_path.is_file():
        return read_expectation_lines(target_path)
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


def parse_json_diagnostics(result: CommandResult) -> list[dict[str, object]]:
    if result.stderr:
        raise SuiteFailure(f"JSON check wrote to stderr: {result.stderr.strip()}")

    diagnostics: list[dict[str, object]] = []
    for line_number, line in enumerate(normalize_newlines(result.stdout).splitlines(), 1):
        try:
            payload = json.loads(line)
        except json.JSONDecodeError as error:
            raise SuiteFailure(f"invalid JSON diagnostic line {line_number}: {error}") from error
        if not isinstance(payload, dict):
            raise SuiteFailure(f"JSON diagnostic line {line_number} is not an object")
        if set(payload) != JSON_DIAGNOSTIC_FIELDS:
            raise SuiteFailure(
                f"JSON diagnostic line {line_number} has unstable fields: {sorted(payload)}"
            )
        if payload["kind"] != "diagnostic":
            raise SuiteFailure(f"JSON diagnostic line {line_number} has an invalid kind")
        if payload["severity"] not in {"error", "warning"}:
            raise SuiteFailure(f"JSON diagnostic line {line_number} has an invalid severity")
        for field in ("phase", "code", "file", "message"):
            if not isinstance(payload[field], str):
                raise SuiteFailure(f"JSON diagnostic line {line_number} has a non-string {field}")
        for field in ("line", "column", "length"):
            value = payload[field]
            if not isinstance(value, int) or isinstance(value, bool) or value < 1:
                raise SuiteFailure(f"JSON diagnostic line {line_number} has an invalid {field}")
        diagnostics.append(payload)
    return diagnostics


def format_command_failure(context: str, result: CommandResult) -> str:
    details = result.output.strip()
    if len(details) > 4000:
        details = details[-4000:]
    return f"{context} (exit {result.returncode})" + (f"\n{details}" if details else "")


__all__ = [
    'CHECK_JSON_OPTION',
    'COMMAND_TIMEOUT_ENVIRONMENT_VARIABLE',
    'CommandResult',
    'CONCURRENT_RUN_COUNT',
    'DEFAULT_COMMAND_TIMEOUT_SECONDS',
    'DIAGNOSTIC_PATTERN',
    'EXCLUSION_SECTIONS',
    'EXCLUSIONS_VERSION',
    'ExclusionTracker',
    'bootstrap_compiler',
    'copy_runtime_data',
    'default_compiler_path',
    'default_runtime_targets',
    'default_target',
    'diagnostic_phase',
    'expected_phase_from_code',
    'enumerate_parity_sources',
    'execution_command',
    'FixtureCase',
    'format_command_failure',
    'has_runtime_expectation',
    'JSON_DIAGNOSTIC_FIELDS',
    'load_fixture_manifest',
    'load_json_object',
    'load_runtime_expectation',
    'load_suite_exclusions',
    'llvm_expectations',
    'MANIFEST_VERSION',
    'normalize_newlines',
    'parse_json_diagnostics',
    'read_expectation_lines',
    'read_optional_exit',
    'read_optional_text',
    'run_command',
    'RuntimeExpectation',
    'SEMANTIC_DIAGNOSTIC_PREFIXES',
    'SuiteExclusion',
    'SuiteExclusions',
    'SuiteFailure',
    'SuiteSkip',
    'SUPPORTED_TARGETS',
    'USAGE_EXIT_CODE',
    'VALID_CLASSIFICATIONS',
    'VALID_OUTCOMES',
]
