#!/usr/bin/env python3
"""Build or publish Zorb from a verified preceding compiler seed."""

from __future__ import annotations

import argparse
import os
import shlex
import shutil
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Sequence

from bootstrap_seed import SeedError, current_host_target, repository_root, resolve_seed, sha256_file


LLVM_COMPONENTS = (
    "core",
    "target",
    "nativecodegen",
    "aarch64",
    "x86",
    "passes",
    "bitwriter",
    "irreader",
)
WINDOWS_SYSTEM_LIBRARIES = ("ntdll.lib",)
PORTABLE_BACKEND_CPU = "baseline"


class BuildError(RuntimeError):
    """Raised when compiler bootstrap or publication fails."""


@dataclass(frozen=True)
class BuildEnvironment:
    root: Path
    backend_dir: Path
    driver_entry: Path
    target: str
    zig: str
    llvm_prefix: Path
    llvm_config: str
    llvm_lib_dir: Path | None
    llvm_runtime_dir: Path | None


@dataclass(frozen=True)
class BackendArtifacts:
    link_args: tuple[str, ...]
    runtime_library: Path | None


def require_command(command: str, purpose: str) -> str:
    resolved = shutil.which(command)
    if resolved is None:
        raise BuildError(f"{command} is required to {purpose}.")
    return resolved


def run_checked(description: str, command: Sequence[str], cwd: Path | None = None) -> None:
    print(
        f"{description}: {shlex.join(str(argument) for argument in command)}",
        flush=True,
    )
    try:
        result = subprocess.run(
            tuple(str(argument) for argument in command), cwd=cwd, check=False
        )
    except OSError as error:
        raise BuildError(f"{description} could not start: {error}") from error
    if result.returncode != 0:
        raise BuildError(f"{description} failed with exit code {result.returncode}.")


def capture_checked(description: str, command: Sequence[str]) -> str:
    try:
        return subprocess.check_output(
            tuple(str(argument) for argument in command), text=True, stderr=subprocess.STDOUT
        ).strip()
    except (OSError, subprocess.CalledProcessError) as error:
        raise BuildError(f"{description} failed: {error}") from error


def default_llvm_prefix(target: str) -> Path:
    if target == "host-windows":
        program_files = os.environ.get("ProgramFiles")
        if not program_files:
            raise BuildError("ProgramFiles is not set; pass --llvm-prefix explicitly.")
        return Path(program_files) / "LLVM"
    return Path("/usr/lib/llvm-21")


def create_environment(options: argparse.Namespace) -> BuildEnvironment:
    root = repository_root()
    target = current_host_target()
    llvm_prefix_value = options.llvm_prefix or os.environ.get("LLVM_PREFIX")
    llvm_prefix = Path(llvm_prefix_value) if llvm_prefix_value else default_llvm_prefix(target)
    llvm_prefix = llvm_prefix.resolve()
    if not llvm_prefix.is_dir():
        raise BuildError(
            f"LLVM prefix does not exist: {llvm_prefix}. Install LLVM 21 or pass --llvm-prefix."
        )
    llvm_lib_dir = options.llvm_lib_dir or (
        Path(os.environ["LLVM_LIB_DIR"]) if "LLVM_LIB_DIR" in os.environ else None
    )
    llvm_runtime_dir = options.llvm_runtime_dir or (
        Path(os.environ["LLVM_RUNTIME_DIR"]) if "LLVM_RUNTIME_DIR" in os.environ else None
    )
    return BuildEnvironment(
        root=root,
        backend_dir=root / "backend/llvm",
        driver_entry=root / "compiler/driver/main.zorb",
        target=target,
        zig=options.zig or os.environ.get("ZIG", "zig"),
        llvm_prefix=llvm_prefix,
        llvm_config=options.llvm_config or os.environ.get("LLVM_CONFIG", "llvm-config-21"),
        llvm_lib_dir=llvm_lib_dir.resolve() if llvm_lib_dir else None,
        llvm_runtime_dir=llvm_runtime_dir.resolve() if llvm_runtime_dir else None,
    )


def find_first_file(root: Path, name: str) -> Path | None:
    direct = root / name
    if direct.is_file():
        return direct
    return next((candidate for candidate in root.rglob(name) if candidate.is_file()), None)


def resolve_windows_llvm_paths(environment: BuildEnvironment) -> tuple[Path, Path]:
    preferred_lib_dir = environment.llvm_lib_dir or environment.llvm_prefix / "lib"
    import_library = preferred_lib_dir / "LLVM-C.lib"
    if not import_library.is_file():
        discovered = find_first_file(environment.llvm_prefix, "LLVM-C.lib")
        if discovered is None:
            raise BuildError(
                f"Unable to find LLVM-C.lib under {environment.llvm_prefix}; pass --llvm-lib-dir."
            )
        import_library = discovered

    runtime_dir = environment.llvm_runtime_dir or environment.llvm_prefix / "bin"
    runtime_library = runtime_dir / "LLVM-C.dll"
    if not runtime_library.is_file():
        raise BuildError(
            f"Unable to find LLVM-C.dll at {runtime_library}; pass --llvm-runtime-dir."
        )
    return import_library.resolve(), runtime_library.resolve()


def optional_quadmath_link_args() -> tuple[str, ...]:
    gxx = shutil.which("g++")
    if gxx is None:
        return ()
    library = capture_checked("Locate libquadmath", (gxx, "-print-file-name=libquadmath.so"))
    if library == "libquadmath.so" or not Path(library).is_file():
        return ()
    return ("-lquadmath",)


def build_backend(environment: BuildEnvironment, publish: bool) -> BackendArtifacts:
    zig = require_command(environment.zig, "build the Zig/LLVM backend")
    zig_args = [
        zig,
        "build",
        "--cache-dir",
        ".zig-cache",
        "--prefix",
        "zig-out",
        "-Doptimize=ReleaseSafe",
        f"-Dcpu={PORTABLE_BACKEND_CPU}",
        f"-Dllvm-prefix={environment.llvm_prefix}",
    ]

    if environment.target == "host-windows":
        import_library, runtime_library = resolve_windows_llvm_paths(environment)
        zig_args.extend(
            (
                f"-Dllvm-lib-dir={import_library.parent}",
                "-Dllvm-library=LLVM-C",
            )
        )
        run_checked("Build the Windows LLVM backend", zig_args, environment.backend_dir)
        backend_candidates = (
            environment.backend_dir / "zig-out/lib/zorb-llvm.lib",
            environment.backend_dir / "zig-out/lib/libzorb-llvm.a",
        )
        backend_api = next((path for path in backend_candidates if path.is_file()), None)
        if backend_api is None:
            raise BuildError("Zig did not produce the static zorb-llvm API library.")
        return BackendArtifacts(
            link_args=tuple(str(path) for path in (backend_api, import_library))
            + WINDOWS_SYSTEM_LIBRARIES,
            runtime_library=runtime_library,
        )

    if publish:
        llvm_config = require_command(environment.llvm_config, "locate static LLVM libraries")
        gxx = require_command("g++", "locate the LLVM C++ runtime")
        cxx_runtime = os.environ.get("CXX_RUNTIME") or capture_checked(
            "Locate libstdc++", (gxx, "-print-file-name=libstdc++.so")
        )
        if not Path(cxx_runtime).is_file():
            raise BuildError(f"CXX_RUNTIME does not point to an existing file: {cxx_runtime}")
        zig_args.extend(("-Dstatic-llvm=true", f"-Dcxx-runtime={cxx_runtime}"))
        run_checked("Build the static Linux LLVM backend", zig_args, environment.backend_dir)
        llvm_libs = shlex.split(
            capture_checked(
                "Resolve static LLVM components",
                (llvm_config, "--link-static", "--libs", *LLVM_COMPONENTS),
            )
        )
        llvm_system_libs = shlex.split(
            capture_checked(
                "Resolve LLVM system libraries",
                (llvm_config, "--link-static", "--system-libs"),
            )
        )
        link_args = (
            str(environment.backend_dir / "zig-out/lib/libzorb-llvm.a"),
            f"-L{environment.llvm_prefix / 'lib'}",
            "-Wl,--start-group",
            *llvm_libs,
            "-Wl,--end-group",
            *llvm_system_libs,
            "-lpthread",
            *optional_quadmath_link_args(),
            "-lstdc++",
        )
        return BackendArtifacts(link_args=link_args, runtime_library=None)

    run_checked("Build the shared Linux LLVM backend", zig_args, environment.backend_dir)
    link_args = (
        str(environment.backend_dir / "zig-out/lib/libzorb-llvm.a"),
        f"-L{environment.llvm_prefix / 'lib'}",
        "-lLLVM-21",
        f"-Wl,-rpath,{environment.llvm_prefix / 'lib'}",
        "-ldl",
        "-lpthread",
        "-lm",
        *optional_quadmath_link_args(),
        "-lz",
        "-lzstd",
        "-lxml2",
        "-lstdc++",
    )
    return BackendArtifacts(link_args=link_args, runtime_library=None)


def validate_seed_selection(options: argparse.Namespace) -> None:
    seed_override = options.seed or os.environ.get("ZORB_BOOTSTRAP_SEED")
    if options.recovery_csharp and seed_override:
        raise BuildError(
            "--seed/ZORB_BOOTSTRAP_SEED and --recovery-csharp are mutually exclusive."
        )


def resolve_seed_command(
    options: argparse.Namespace, root: Path, target: str
) -> tuple[str, ...]:
    seed_override = options.seed or os.environ.get("ZORB_BOOTSTRAP_SEED")
    if seed_override:
        seed_path = Path(seed_override).resolve()
        if not seed_path.is_file():
            raise BuildError(f"Bootstrap seed does not exist: {seed_path}")
        if os.name != "nt" and not os.access(seed_path, os.X_OK):
            raise BuildError(f"Bootstrap seed is not executable: {seed_path}")
        return (str(seed_path),)
    manifest_path = options.manifest or Path(
        os.environ.get("ZORB_BOOTSTRAP_MANIFEST", root / "bootstrap/manifest.json")
    )
    artifact_dir = options.artifact_dir or root / "bootstrap/artifacts"
    cache_dir = options.cache_dir or Path(
        os.environ.get("ZORB_BOOTSTRAP_CACHE_DIR", root / "build/bootstrap")
    )
    seed_path = resolve_seed(target, manifest_path, artifact_dir, cache_dir)
    return (str(seed_path),)


def prepare_recovery_command(root: Path, workspace: Path) -> tuple[str, ...]:
    dotnet = require_command("dotnet", "run the explicit C# recovery bootstrap")
    recovery_output = workspace / "recovery"
    recovery_output.mkdir()
    recovery_project = root / "seed/csharp/Zorb.Compiler.csproj"
    run_checked(
        "Build the C# recovery compiler",
        (
            dotnet,
            "build",
            recovery_project,
            "--configuration",
            "Release",
            "--nologo",
            "--output",
            recovery_output,
        ),
    )
    recovery_assembly = recovery_output / "Zorb.Compiler.dll"
    if not recovery_assembly.is_file():
        raise BuildError(f"C# recovery compiler was not produced at {recovery_assembly}.")
    return (dotnet, str(recovery_assembly))


def native_flags_for_recovery(link_args: Sequence[str]) -> str:
    if os.name == "nt":
        return subprocess.list2cmdline(tuple(link_args))
    return shlex.join(link_args)


def build_generation(
    description: str,
    compiler_command: Sequence[str],
    environment: BuildEnvironment,
    backend: BackendArtifacts,
    output_path: Path,
    recovery: bool = False,
) -> None:
    arguments = [
        *compiler_command,
        "build",
        str(environment.driver_entry),
        "--target",
        environment.target,
        "-o",
        str(output_path),
    ]
    if recovery:
        arguments.extend(("--native-flags", native_flags_for_recovery(backend.link_args)))
    else:
        arguments.append("--native-link-args")
        arguments.extend(backend.link_args)
    run_checked(description, arguments)
    if not output_path.is_file():
        raise BuildError(f"{description} did not produce {output_path}.")


def prepare_workspace_runtime(backend: BackendArtifacts, workspace: Path) -> None:
    if backend.runtime_library is not None:
        shutil.copy2(backend.runtime_library, workspace / backend.runtime_library.name)


def bootstrap(options: argparse.Namespace, environment: BuildEnvironment) -> None:
    output_path = (
        options.output
        or environment.root / ("build/zorb.exe" if os.name == "nt" else "build/zorb")
    ).resolve()
    output_path.parent.mkdir(parents=True, exist_ok=True)
    backend = build_backend(environment, publish=False)
    with tempfile.TemporaryDirectory(prefix="zorb-bootstrap-") as temporary_dir:
        workspace = Path(temporary_dir)
        prepare_workspace_runtime(backend, workspace)
        if options.recovery_csharp:
            seed_command = prepare_recovery_command(environment.root, workspace)
            recovery = True
        else:
            seed_command = resolve_seed_command(
                options, environment.root, environment.target
            )
            recovery = False
        build_generation(
            "Build integrated Zorb compiler",
            seed_command,
            environment,
            backend,
            output_path,
            recovery,
        )
    if backend.runtime_library is not None:
        shutil.copy2(backend.runtime_library, output_path.parent / backend.runtime_library.name)
    print(f"Bootstrapped integrated Zorb compiler at {output_path}")


def bootstrap_self_check(options: argparse.Namespace) -> None:
    root = repository_root()
    target = current_host_target()
    output_path = (
        options.output
        or root / ("build/zorb-self-check.exe" if target == "host-windows" else "build/zorb-self-check")
    ).resolve()
    output_path.parent.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="zorb-self-check-bootstrap-") as temporary_dir:
        workspace = Path(temporary_dir)
        if options.recovery_csharp:
            compiler_command = prepare_recovery_command(root, workspace)
        else:
            compiler_command = resolve_seed_command(options, root, target)
        run_checked(
            "Build native frontend self-check",
            (
                *compiler_command,
                "build",
                root / "compiler/self-check/main.zorb",
                "--target",
                target,
                "-o",
                output_path,
            ),
        )
    if not output_path.is_file():
        raise BuildError(f"Native frontend self-check build did not produce {output_path}.")
    print(f"Native frontend checker built at {output_path}")


def verify_linux_static_binary(compiler_path: Path) -> None:
    ldd = require_command("ldd", "verify the published compiler dependencies")
    try:
        result = subprocess.run(
            (ldd, str(compiler_path)), text=True, capture_output=True, check=False
        )
    except OSError as error:
        raise BuildError(f"Inspect published compiler dependencies could not start: {error}") from error
    dependencies = result.stdout + result.stderr
    if "libLLVM" in dependencies:
        raise BuildError("Published Linux compiler still depends on a shared LLVM library.")


def publish(options: argparse.Namespace, environment: BuildEnvironment) -> None:
    package_arch = "arm64" if environment.target == "host-linux-aarch64" else "x64"
    default_output = (
        environment.root / f"artifacts/compiler/linux-{package_arch}"
        if environment.target != "host-windows"
        else environment.root / "artifacts/compiler/win-x64"
    )
    output_dir = (options.output or default_output).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    backend = build_backend(environment, publish=True)

    with tempfile.TemporaryDirectory(prefix="zorb-release-fixed-point-") as temporary_dir:
        workspace = Path(temporary_dir)
        prepare_workspace_runtime(backend, workspace)
        if options.recovery_csharp:
            seed_command = prepare_recovery_command(environment.root, workspace)
            recovery = True
        else:
            seed_command = resolve_seed_command(
                options, environment.root, environment.target
            )
            recovery = False

        executable_suffix = ".exe" if environment.target == "host-windows" else ""
        generation_1 = workspace / f"zorb-generation-1{executable_suffix}"
        generation_2 = workspace / f"zorb-generation-2{executable_suffix}"
        generation_3 = workspace / f"zorb-generation-3{executable_suffix}"
        build_generation(
            "Build generation-1 compiler",
            seed_command,
            environment,
            backend,
            generation_1,
            recovery,
        )
        build_generation(
            "Build generation-2 compiler",
            (str(generation_1),),
            environment,
            backend,
            generation_2,
        )
        build_generation(
            "Build generation-3 compiler",
            (str(generation_2),),
            environment,
            backend,
            generation_3,
        )
        if sha256_file(generation_2) != sha256_file(generation_3):
            raise BuildError("Generation-2 and generation-3 compilers are not byte-identical.")

        compiler_name = "zorb.exe" if environment.target == "host-windows" else "zorb"
        compiler_output = output_dir / compiler_name
        shutil.copy2(generation_2, compiler_output)
        if backend.runtime_library is not None:
            shutil.copy2(backend.runtime_library, output_dir / backend.runtime_library.name)

    if environment.target != "host-windows":
        verify_linux_static_binary(output_dir / "zorb")
    print("Verified byte-identical generation-2 and generation-3 compilers.")
    print(f"Published {environment.target} compiler to {output_dir}")


def add_common_arguments(parser: argparse.ArgumentParser) -> None:
    parser.add_argument(
        "output", nargs="?", type=Path, help="compiler output path or package directory"
    )
    parser.add_argument("--seed", type=Path, help="use an explicit integrated compiler seed")
    parser.add_argument(
        "--recovery-csharp",
        action="store_true",
        help="explicitly use the checked-in C# recovery compiler",
    )
    parser.add_argument("--manifest", type=Path)
    parser.add_argument("--artifact-dir", type=Path)
    parser.add_argument("--cache-dir", type=Path)
    parser.add_argument("--zig")
    parser.add_argument("--llvm-prefix", type=Path)
    parser.add_argument("--llvm-config")
    parser.add_argument("--llvm-lib-dir", type=Path)
    parser.add_argument("--llvm-runtime-dir", type=Path)


def parse_args(arguments: Sequence[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    bootstrap_parser = subparsers.add_parser("bootstrap", help="build one development compiler")
    publish_parser = subparsers.add_parser("publish", help="build and verify a release compiler package")
    self_check_parser = subparsers.add_parser("self-check", help="build the native frontend checker")
    add_common_arguments(bootstrap_parser)
    add_common_arguments(publish_parser)
    add_common_arguments(self_check_parser)
    return parser.parse_args(arguments)


def main(arguments: Sequence[str] | None = None) -> int:
    options = parse_args(sys.argv[1:] if arguments is None else arguments)
    try:
        validate_seed_selection(options)
        if options.command == "self-check":
            bootstrap_self_check(options)
            return 0
        environment = create_environment(options)
        if options.command == "bootstrap":
            bootstrap(options, environment)
        else:
            publish(options, environment)
    except (BuildError, SeedError) as error:
        print(error, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
