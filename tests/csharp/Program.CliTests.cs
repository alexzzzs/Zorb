using System.Runtime.InteropServices;
using System.Text;
using Zorb.Compiler.Utils;

internal static partial class Program
{
    private static void RunWindowsLinkerSelectionTests()
    {
        var clangArguments = ExternalTools.GetWindowsCompileAndLinkArgumentList(
            "clang-cl",
            "input.c",
            ["backend.lib"],
            "output.exe").ToList();
        var linkerSelectionIndex = clangArguments.IndexOf("-fuse-ld=lld");
        var linkerArgumentsIndex = clangArguments.IndexOf("/link");
        if (linkerSelectionIndex < 0 || linkerArgumentsIndex < 0 ||
            linkerSelectionIndex >= linkerArgumentsIndex)
        {
            throw new Exception(
                "clang-cl must select lld before the /link argument separator.");
        }

        var msvcArguments = ExternalTools.GetWindowsCompileAndLinkArgumentList(
            "cl",
            "input.c",
            ["backend.lib"],
            "output.exe");
        if (msvcArguments.Contains("-fuse-ld=lld", StringComparer.Ordinal))
            throw new Exception("MSVC cl.exe must not receive a Clang linker-selection flag.");
    }

    private static void RunCliWorkflowTests(string fixtureRoot)
    {
        if (OperatingSystem.IsLinux())
        {
            EnsureToolAvailable("timeout");
            EnsureToolAvailable("gcc");
        }
        else if (OperatingSystem.IsWindows())
        {
            EnsureToolAvailable("clang-cl", "cl");
        }
        else
        {
            throw new Exception("CLI workflow tests currently require a Linux or Windows host.");
        }

        var projectRoot = GetProjectRoot();
        var compilerInvocation = GetCompilerInvocation(projectRoot);

        WithTempDirectory("zorb-cli-tests", tempDir =>
        {
            foreach (var workflowCase in GetCliWorkflowCases())
                RunCliWorkflowFixture(compilerInvocation, projectRoot, fixtureRoot, tempDir, workflowCase);
        });
    }

    private static void RunCliArgumentValidationTests(string fixtureRoot)
    {
        var projectRoot = GetProjectRoot();
        var compilerInvocation = GetCompilerInvocation(projectRoot);
        var sampleInput = Path.Combine(fixtureRoot, "runtime_hello_world", "main.zorb");
        var hasWindowsHostToolchain = OperatingSystem.IsWindows() && IsAnyToolAvailable("clang-cl", "cl");

        WithTempDirectory("zorb-cli-args", tempDir =>
        {
            foreach (var testCase in GetCliArgumentCases(sampleInput, tempDir, hasWindowsHostToolchain))
            {
                var result = RunProcessWithTimeoutArgs(
                    compilerInvocation.FileName,
                    CombineCommandArguments(compilerInvocation.ArgumentsPrefix, testCase.Arguments),
                    projectRoot,
                    TimeSpan.FromSeconds(30));

                if (result.ExitCode != testCase.ExpectedExitCode)
                    throw new Exception($"CLI arg case '{testCase.Name}' exit code mismatch. Expected {testCase.ExpectedExitCode}, got {result.ExitCode}.");

                var actualStdOut = NormalizeNewlines(result.StdOut);
                var actualStdErr = NormalizeNewlines(result.StdErr);

                if (!string.IsNullOrEmpty(testCase.ExpectedStdOutSubstring) &&
                    !actualStdOut.Contains(testCase.ExpectedStdOutSubstring, StringComparison.Ordinal))
                {
                    throw new Exception(
                        $"CLI arg case '{testCase.Name}' stdout did not contain expected text '{testCase.ExpectedStdOutSubstring}'.{Environment.NewLine}Actual stdout:{Environment.NewLine}{actualStdOut}");
                }

                if (!string.IsNullOrEmpty(testCase.ExpectedStdErrSubstring) &&
                    !actualStdErr.Contains(testCase.ExpectedStdErrSubstring, StringComparison.Ordinal))
                {
                    throw new Exception(
                        $"CLI arg case '{testCase.Name}' stderr did not contain expected text '{testCase.ExpectedStdErrSubstring}'.{Environment.NewLine}Actual stderr:{Environment.NewLine}{actualStdErr}");
                }
            }
        });
    }

    private static CliWorkflowCase[] GetCliWorkflowCases()
    {
        if (OperatingSystem.IsWindows())
        {
            return
            [
                new CliWorkflowCase("runtime_hello_world", "host-windows"),
                new CliWorkflowCase("runtime_string_escapes", "host-windows"),
                new CliWorkflowCase("runtime_condition_catch", "host-windows"),
                new CliWorkflowCase("runtime_host_platform_branch", "host-windows"),
                new CliWorkflowCase("runtime_host_platform_catch", "host-windows"),
                new CliWorkflowCase("runtime_host_import_alias", "host-windows"),
                new CliWorkflowCase("runtime_host_stderr_write", "host-windows"),
                new CliWorkflowCase("runtime_host_nonzero_exit", "host-windows"),
                new CliWorkflowCase("runtime_stdlib_cross_platform", "host-windows"),
                new CliWorkflowCase("runtime_stdlib_support_checks", "host-windows")
            ];
        }

        if (OperatingSystem.IsLinux())
        {
            return
            [
                new CliWorkflowCase("runtime_hello_world", "freestanding-linux"),
                new CliWorkflowCase("runtime_host_platform_branch", "host-linux"),
                new CliWorkflowCase("runtime_host_platform_catch", "host-linux"),
                new CliWorkflowCase("runtime_host_import_alias", "host-linux"),
                new CliWorkflowCase("runtime_host_stderr_write", "host-linux"),
                new CliWorkflowCase("runtime_host_nonzero_exit", "host-linux"),
                new CliWorkflowCase("runtime_stdlib_cross_platform", "host-linux"),
                new CliWorkflowCase("runtime_stdlib_support_checks", "host-linux")
            ];
        }

        throw new Exception("CLI workflow tests currently require a Linux or Windows host.");
    }

    private static void RunCliWorkflowFixture(CompilerInvocation compilerInvocation, string projectRoot, string fixtureRoot, string tempDir, CliWorkflowCase workflowCase)
    {
        var fixtureName = workflowCase.FixtureName;
        var fixtureDir = Path.Combine(fixtureRoot, fixtureName);
        var mainPath = Path.Combine(fixtureDir, "main.zorb");
        var cliTargetName = workflowCase.TargetName;
        var runtimeExpectation = ReadRuntimeExpectationForTarget(fixtureDir, cliTargetName);
        var expectedStdOut = runtimeExpectation.ExpectedStdOut ?? "";
        var expectedStdErr = runtimeExpectation.ExpectedStdErr ?? "";
        var expectedExit = runtimeExpectation.ExpectedExit;

        var fixtureTempDir = Path.Combine(tempDir, fixtureName);
        Directory.CreateDirectory(fixtureTempDir);

        var outputFileName = OperatingSystem.IsWindows() ? $"{fixtureName}.exe" : fixtureName;
        var builtBinaryPath = Path.Combine(fixtureTempDir, outputFileName);

        var build = RunProcessWithTimeoutArgs(
            compilerInvocation.FileName,
            BuildCommandArguments(compilerInvocation, "build", mainPath, "--target", cliTargetName, "-o", builtBinaryPath),
            projectRoot,
            TimeSpan.FromSeconds(30));

        if (build.ExitCode != 0)
            throw new Exception($"CLI build failed for fixture '{fixtureName}' with exit code {build.ExitCode}.{Environment.NewLine}{build.StdErr}{build.StdOut}".Trim());

        if (!File.Exists(builtBinaryPath))
            throw new Exception($"CLI build for fixture '{fixtureName}' did not produce the requested binary.");

        var builtExecution = RunBuiltCliBinary(builtBinaryPath, fixtureTempDir, cliTargetName);
        if (builtExecution.ExitCode != expectedExit)
            throw new Exception($"Built fixture '{fixtureName}' exit code mismatch. Expected {expectedExit}, got {builtExecution.ExitCode}.");

        var actualBuiltStdOut = NormalizeNewlines(builtExecution.StdOut);
        var actualBuiltStdErr = NormalizeNewlines(builtExecution.StdErr);
        if (!string.Equals(actualBuiltStdOut, expectedStdOut, StringComparison.Ordinal))
            throw new Exception($"Built fixture '{fixtureName}' stdout mismatch.{Environment.NewLine}Expected:{Environment.NewLine}{expectedStdOut}{Environment.NewLine}Actual:{Environment.NewLine}{actualBuiltStdOut}");
        if (!string.Equals(actualBuiltStdErr, expectedStdErr, StringComparison.Ordinal))
            throw new Exception($"Built fixture '{fixtureName}' stderr mismatch.{Environment.NewLine}Expected:{Environment.NewLine}{expectedStdErr}{Environment.NewLine}Actual:{Environment.NewLine}{actualBuiltStdErr}");

        var run = RunProcessWithTimeoutArgs(
            compilerInvocation.FileName,
            BuildCommandArguments(compilerInvocation, "run", mainPath, "--target", cliTargetName),
            projectRoot,
            TimeSpan.FromSeconds(30));

        if (run.ExitCode != expectedExit)
            throw new Exception($"CLI run for fixture '{fixtureName}' exit code mismatch. Expected {expectedExit}, got {run.ExitCode}.{Environment.NewLine}{run.StdErr}{run.StdOut}".Trim());

        var actualRunStdOut = NormalizeNewlines(run.StdOut);
        var actualRunStdErr = NormalizeNewlines(run.StdErr);
        if (!string.Equals(actualRunStdOut, expectedStdOut, StringComparison.Ordinal))
            throw new Exception($"CLI run for fixture '{fixtureName}' stdout mismatch.{Environment.NewLine}Expected:{Environment.NewLine}{expectedStdOut}{Environment.NewLine}Actual:{Environment.NewLine}{actualRunStdOut}");
        if (!string.Equals(actualRunStdErr, expectedStdErr, StringComparison.Ordinal))
            throw new Exception($"CLI run for fixture '{fixtureName}' stderr mismatch.{Environment.NewLine}Expected:{Environment.NewLine}{expectedStdErr}{Environment.NewLine}Actual:{Environment.NewLine}{actualRunStdErr}");
    }

    private static ProcessResult RunBuiltCliBinary(string builtBinaryPath, string workingDirectory, string targetName)
    {
        if (OperatingSystem.IsLinux())
        {
            if (string.Equals(targetName, "host-linux-aarch64", StringComparison.Ordinal) ||
                string.Equals(targetName, "freestanding-linux-aarch64", StringComparison.Ordinal))
            {
                var args = new List<string> { "30s" };
                var qemu = FindAArch64Qemu();
                if (qemu == null)
                    throw new Exception($"AArch64 CLI workflow tests require qemu-aarch64. Install 'qemu-user' or set {AArch64QemuEnvironmentVariable}.");

                args.Add(qemu);
                var sysroot = ResolveAArch64Sysroot();
                if (sysroot != null)
                {
                    args.Add("-L");
                    args.Add(sysroot);
                }

                args.Add(builtBinaryPath);
                return RunProcessWithTimeoutArgs("timeout", args, workingDirectory, TimeSpan.FromSeconds(30));
            }

            return RunProcessWithTimeoutArgs("timeout", ["30s", builtBinaryPath], workingDirectory, TimeSpan.FromSeconds(30));
        }

        if (OperatingSystem.IsWindows())
            return RunProcessWithTimeoutArgs(builtBinaryPath, Array.Empty<string>(), workingDirectory, TimeSpan.FromSeconds(30));

        throw new Exception("CLI workflow tests currently require a Linux or Windows host.");
    }

    private static CliArgumentCase[] GetCliArgumentCases(string sampleInput, string tempDir, bool hasWindowsHostToolchain)
    {
        var windowsBuildOutputPath = Path.Combine(tempDir, OperatingSystem.IsWindows() ? "host-windows-arg.exe" : "host-windows-arg");
        return
        [
            new CliArgumentCase("help", "--help", 0, "Usage:", null),
            new CliArgumentCase("version", "--version", 0, "Zorb.Compiler ", null),
            new CliArgumentCase("missing_target_value", $"\"{sampleInput}\" --target", 1, "Usage:", "Missing value for --target."),
            new CliArgumentCase("unknown_target", $"\"{sampleInput}\" --target host-macos", 1, "Usage:", "Unknown target: host-macos"),
            new CliArgumentCase("unexpected_extra_input", $"\"{sampleInput}\" \"{sampleInput}\"", 1, "Usage:", "Unexpected extra input path:"),
            new CliArgumentCase("missing_linker_script_value", $"build \"{sampleInput}\" --target bare-metal-x86_64 --linker-script", 1, "Usage:", "Missing value for --linker-script."),
            new CliArgumentCase("missing_emit_linker_script_value", $"build \"{sampleInput}\" --target bare-metal-x86_64 --emit-linker-script", 1, "Usage:", "Missing value for --emit-linker-script."),
            new CliArgumentCase("emit_check_output_rejected", $"\"{sampleInput}\" --check -o out.c", 1, "Usage:", "Option -o/--output is not valid with --check."),
            new CliArgumentCase("run_output_rejected", $"run \"{sampleInput}\" -o out", 1, "Usage:", "Option -o/--output is not valid with run."),
            new CliArgumentCase("build_check_rejected", $"build \"{sampleInput}\" --check", 1, "Usage:", "Option --check cannot be combined with build or run."),
            new CliArgumentCase("run_emit_llvm_rejected", $"run \"{sampleInput}\" --emit-llvm", 1, "Usage:", "Option --emit-llvm cannot be combined with build or run."),
            new CliArgumentCase("bare_metal_run_rejected", $"run \"{sampleInput}\" --target bare-metal-x86_64", 1, null, "Run does not support target 'bare-metal-x86_64'."),
            new CliArgumentCase("linker_script_wrong_target", $"build \"{sampleInput}\" --target host-linux --linker-script kernel.ld", 1, null, "Option --linker-script is only valid with build --target bare-metal-x86_64."),
            new CliArgumentCase("emit_linker_script_wrong_target", $"build \"{sampleInput}\" --target host-linux --emit-linker-script kernel.ld", 1, null, "Option --emit-linker-script is only valid with build --target bare-metal-x86_64."),
            new CliArgumentCase(
                "host_windows_build_rejected_on_non_windows",
                $"build \"{sampleInput}\" --target host-windows -o \"{windowsBuildOutputPath}\"",
                OperatingSystem.IsWindows() && hasWindowsHostToolchain ? 0 : 1,
                null,
                OperatingSystem.IsWindows()
                    ? (hasWindowsHostToolchain ? null : "Required tools were not found in PATH. Install one of: clang-cl, cl.")
                    : "Target 'host-windows' currently requires a Windows host for build and run. Current host:")
        ];
    }

    private static void RunBareMetalCliBuildTests(string fixtureRoot)
    {
        if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        if (!IsBareMetalLinkerAvailable())
            return;

        var projectRoot = GetProjectRoot();
        var compilerInvocation = GetCompilerInvocation(projectRoot);

        var fixtureDir = Path.Combine(fixtureRoot, "bare_metal_debug_port");
        var mainPath = Path.Combine(fixtureDir, "main.zorb");

        WithTempDirectory("zorb-bare-metal-cli", tempDir =>
        {
            var imagePath = Path.Combine(tempDir, "kernel.elf");
            var emittedBundledLinkerScriptPath = Path.Combine(tempDir, "bundled.ld");
            var bundledBuild = RunProcessWithTimeoutArgs(
                compilerInvocation.FileName,
                BuildCommandArguments(
                    compilerInvocation,
                    "build", mainPath, "--target", "bare-metal-x86_64", "-o", imagePath, "--emit-linker-script", emittedBundledLinkerScriptPath),
                projectRoot,
                TimeSpan.FromSeconds(30));

            if (bundledBuild.ExitCode != 0)
                throw new Exception($"Bare-metal CLI build failed with exit code {bundledBuild.ExitCode}.{Environment.NewLine}{bundledBuild.StdErr}{bundledBuild.StdOut}".Trim());

            if (!File.Exists(imagePath))
                throw new Exception("Bare-metal CLI build did not produce the requested kernel image.");

            if (!File.Exists(emittedBundledLinkerScriptPath))
                throw new Exception("Bare-metal CLI build did not emit the bundled linker script.");

            if (!bundledBuild.StdOut.Contains("Linker script: bundled bare-metal-x86_64 default", StringComparison.Ordinal))
                throw new Exception("Bare-metal CLI build did not report using the bundled linker script.");

            if (!bundledBuild.StdOut.Contains($"Emitted linker script to {emittedBundledLinkerScriptPath}", StringComparison.Ordinal))
                throw new Exception("Bare-metal CLI build did not report the emitted linker script path.");

            var emittedBundledLinkerScript = NormalizeNewlines(File.ReadAllText(emittedBundledLinkerScriptPath, Encoding.UTF8));
            if (!emittedBundledLinkerScript.Contains("ENTRY(_start)", StringComparison.Ordinal) ||
                !emittedBundledLinkerScript.Contains(". = 1M;", StringComparison.Ordinal))
            {
                throw new Exception("Bare-metal CLI build emitted an unexpected bundled linker script.");
            }

            var customLinkerScriptPath = Path.Combine(tempDir, "custom.ld");
            File.WriteAllText(customLinkerScriptPath, """
OUTPUT_FORMAT(elf64-x86-64)
OUTPUT_ARCH(i386:x86-64)
ENTRY(_start)

SECTIONS
{
    . = 2M;

    .text ALIGN(4K) :
    {
        *(.text .text.*)
    }

    .rodata ALIGN(4K) :
    {
        *(.rodata .rodata.*)
    }

    .data ALIGN(4K) :
    {
        *(.data .data.*)
    }

    .bss ALIGN(4K) :
    {
        *(COMMON)
        *(.bss .bss.*)
    }

    /DISCARD/ :
    {
        *(.comment)
        *(.eh_frame)
        *(.note .note.*)
    }
}
""", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var customImagePath = Path.Combine(tempDir, "kernel-custom.elf");
            var emittedCustomLinkerScriptPath = Path.Combine(tempDir, "custom-emitted.ld");
            var customBuild = RunProcessWithTimeoutArgs(
                compilerInvocation.FileName,
                BuildCommandArguments(
                    compilerInvocation,
                    "build", mainPath, "--target", "bare-metal-x86_64", "--linker-script", customLinkerScriptPath, "--emit-linker-script", emittedCustomLinkerScriptPath, "-o", customImagePath),
                projectRoot,
                TimeSpan.FromSeconds(30));

            if (customBuild.ExitCode != 0)
                throw new Exception($"Bare-metal CLI build with custom linker script failed with exit code {customBuild.ExitCode}.{Environment.NewLine}{customBuild.StdErr}{customBuild.StdOut}".Trim());

            if (!File.Exists(customImagePath))
                throw new Exception("Bare-metal CLI build with custom linker script did not produce the requested kernel image.");

            if (!File.Exists(emittedCustomLinkerScriptPath))
                throw new Exception("Bare-metal CLI build with custom linker script did not emit the linker script.");

            if (!customBuild.StdOut.Contains($"Linker script: {customLinkerScriptPath}", StringComparison.Ordinal))
                throw new Exception("Bare-metal CLI build did not report the custom linker script path.");

            if (!customBuild.StdOut.Contains($"Emitted linker script to {emittedCustomLinkerScriptPath}", StringComparison.Ordinal))
                throw new Exception("Bare-metal CLI build did not report the emitted custom linker script path.");

            var emittedCustomLinkerScript = NormalizeNewlines(File.ReadAllText(emittedCustomLinkerScriptPath, Encoding.UTF8));
            var customLinkerScript = NormalizeNewlines(File.ReadAllText(customLinkerScriptPath, Encoding.UTF8));
            if (!string.Equals(emittedCustomLinkerScript, customLinkerScript, StringComparison.Ordinal))
                throw new Exception("Bare-metal CLI build did not emit the exact custom linker script content.");
        });
    }
}
