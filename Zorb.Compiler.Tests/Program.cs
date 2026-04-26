using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Zorb.Compiler.AST;
using Zorb.Compiler.Codegen;
using Zorb.Compiler.Lexer;
using Zorb.Compiler.Parser;
using Zorb.Compiler.Parsing;
using Zorb.Compiler.Semantic;
using Zorb.Compiler.Utils;

var testProjectRoot = FindAncestorContainingFile(AppContext.BaseDirectory, "Zorb.Compiler.Tests.csproj");
var fixtureRoot = Path.Combine(testProjectRoot, "fixtures");
var fixtureDirs = Directory.GetDirectories(fixtureRoot).OrderBy(path => path, StringComparer.Ordinal).ToList();
var failures = new List<string>();
var updateSnapshots = args.Contains("--update-snapshots", StringComparer.Ordinal);

foreach (var fixtureDir in fixtureDirs)
{
    var fixtureName = Path.GetFileName(fixtureDir);

    try
    {
        RunFixture(fixtureDir, updateSnapshots);
        Console.WriteLine($"PASS {fixtureName}");
    }
    catch (Exception ex)
    {
        failures.Add($"{fixtureName}: {ex.Message}");
        Console.WriteLine($"FAIL {fixtureName}");
    }
}

try
{
    RunCliWorkflowTests(fixtureRoot);
    Console.WriteLine("PASS cli_workflow");
}
catch (Exception ex)
{
    failures.Add($"cli_workflow: {ex.Message}");
    Console.WriteLine("FAIL cli_workflow");
}

try
{
    RunBareMetalCliBuildTests(fixtureRoot);
    Console.WriteLine("PASS cli_bare_metal");
}
catch (Exception ex)
{
    failures.Add($"cli_bare_metal: {ex.Message}");
    Console.WriteLine("FAIL cli_bare_metal");
}

try
{
    RunCliArgumentValidationTests(fixtureRoot);
    Console.WriteLine("PASS cli_args");
}
catch (Exception ex)
{
    failures.Add($"cli_args: {ex.Message}");
    Console.WriteLine("FAIL cli_args");
}

var projectRoot = Directory.GetParent(testProjectRoot)?.FullName
    ?? throw new Exception($"Unable to determine repository root from '{testProjectRoot}'.");
var examplesRoot = Path.Combine(projectRoot, "examples");
var examplePaths = Directory.EnumerateFiles(examplesRoot, "*.zorb", SearchOption.AllDirectories)
    .Where(path =>
    {
        var fileName = Path.GetFileName(path);
        if (string.Equals(fileName, "main.zorb", StringComparison.Ordinal))
            return true;

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            return true;

        var siblingMainPath = Path.Combine(directory, "main.zorb");
        return !File.Exists(siblingMainPath);
    })
    .OrderBy(path => path, StringComparer.Ordinal)
    .ToArray();

foreach (var examplePath in examplePaths)
{
    var exampleName = Path.GetRelativePath(projectRoot, examplePath);

    try
    {
        RunExampleCompilationTest(examplePath);
        Console.WriteLine($"PASS {exampleName}");
    }
    catch (Exception ex)
    {
        failures.Add($"{exampleName}: {ex.Message}");
        Console.WriteLine($"FAIL {exampleName}");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine();
    foreach (var failure in failures)
        Console.Error.WriteLine(failure);
    return 1;
}

return 0;

static void RunCliWorkflowTests(string fixtureRoot)
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

    var testProjectRoot = FindAncestorContainingFile(AppContext.BaseDirectory, "Zorb.Compiler.Tests.csproj");
    var projectRoot = Directory.GetParent(testProjectRoot)?.FullName
        ?? throw new Exception($"Unable to determine repository root from '{testProjectRoot}'.");
    var compilerInvocation = GetCompilerInvocation(projectRoot);

    var tempDir = Path.Combine(Path.GetTempPath(), "zorb-cli-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);

    try
    {
        foreach (var workflowCase in GetCliWorkflowCases())
            RunCliWorkflowFixture(compilerInvocation, projectRoot, fixtureRoot, tempDir, workflowCase);
    }
    finally
    {
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, recursive: true);
    }
}

static void RunCliArgumentValidationTests(string fixtureRoot)
{
    var testProjectRoot = FindAncestorContainingFile(AppContext.BaseDirectory, "Zorb.Compiler.Tests.csproj");
    var projectRoot = Directory.GetParent(testProjectRoot)?.FullName
        ?? throw new Exception($"Unable to determine repository root from '{testProjectRoot}'.");
    var compilerInvocation = GetCompilerInvocation(projectRoot);
    var sampleInput = Path.Combine(fixtureRoot, "runtime_hello_world", "main.zorb");

    foreach (var testCase in GetCliArgumentCases(sampleInput))
    {
        var result = RunProcessWithTimeout(
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
}

static CliArgumentCase[] GetCliArgumentCases(string sampleInput)
{
    return
    [
        new CliArgumentCase(
            "help",
            "--help",
            0,
            "Usage:",
            null),
        new CliArgumentCase(
            "version",
            "--version",
            0,
            "Zorb.Compiler ",
            null),
        new CliArgumentCase(
            "missing_target_value",
            $"\"{sampleInput}\" --target",
            1,
            "Usage:",
            "Missing value for --target."),
        new CliArgumentCase(
            "unknown_target",
            $"\"{sampleInput}\" --target host-macos",
            1,
            "Usage:",
            "Unknown target: host-macos"),
        new CliArgumentCase(
            "unexpected_extra_input",
            $"\"{sampleInput}\" \"{sampleInput}\"",
            1,
            "Usage:",
            "Unexpected extra input path:"),
        new CliArgumentCase(
            "keep_c_emit_c_mode",
            $"\"{sampleInput}\" --keep-c out.c",
            1,
            "Usage:",
            "Option --keep-c is only valid with build or run."),
        new CliArgumentCase(
            "missing_linker_script_value",
            $"build \"{sampleInput}\" --target bare-metal-x86_64 --linker-script",
            1,
            "Usage:",
            "Missing value for --linker-script."),
        new CliArgumentCase(
            "missing_emit_linker_script_value",
            $"build \"{sampleInput}\" --target bare-metal-x86_64 --emit-linker-script",
            1,
            "Usage:",
            "Missing value for --emit-linker-script."),
        new CliArgumentCase(
            "emit_check_output_rejected",
            $"\"{sampleInput}\" --check -o out.c",
            1,
            "Usage:",
            "Option -o/--output is not valid with --check."),
        new CliArgumentCase(
            "run_output_rejected",
            $"run \"{sampleInput}\" -o out",
            1,
            "Usage:",
            "Option -o/--output is not valid with run."),
        new CliArgumentCase(
            "build_check_rejected",
            $"build \"{sampleInput}\" --check",
            1,
            "Usage:",
            "Option --check cannot be combined with build or run."),
        new CliArgumentCase(
            "bare_metal_run_rejected",
            $"run \"{sampleInput}\" --target bare-metal-x86_64",
            1,
            null,
            "Run does not support target 'bare-metal-x86_64'."),
        new CliArgumentCase(
            "linker_script_wrong_target",
            $"build \"{sampleInput}\" --target host-linux --linker-script kernel.ld",
            1,
            null,
            "Option --linker-script is only valid with build --target bare-metal-x86_64."),
        new CliArgumentCase(
            "emit_linker_script_wrong_target",
            $"build \"{sampleInput}\" --target host-linux --emit-linker-script kernel.ld",
            1,
            null,
            "Option --emit-linker-script is only valid with build --target bare-metal-x86_64."),
        new CliArgumentCase(
            "host_windows_build_rejected_on_non_windows",
            $"build \"{sampleInput}\" --target host-windows -o out.exe",
            OperatingSystem.IsWindows() ? 0 : 1,
            null,
            OperatingSystem.IsWindows() ? null : "Target 'host-windows' currently requires a Windows host for build and run. Current host:")
    ];
}

static void RunBareMetalCliBuildTests(string fixtureRoot)
{
    if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        return;

    EnsureToolAvailable("gcc");
    EnsureToolAvailable("ld");

    var testProjectRoot = FindAncestorContainingFile(AppContext.BaseDirectory, "Zorb.Compiler.Tests.csproj");
    var projectRoot = Directory.GetParent(testProjectRoot)?.FullName
        ?? throw new Exception($"Unable to determine repository root from '{testProjectRoot}'.");
    var compilerInvocation = GetCompilerInvocation(projectRoot);

    var fixtureDir = Path.Combine(fixtureRoot, "bare_metal_debug_port");
    var mainPath = Path.Combine(fixtureDir, "main.zorb");
    var tempDir = Path.Combine(Path.GetTempPath(), "zorb-bare-metal-cli", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);

    try
    {
        var imagePath = Path.Combine(tempDir, "kernel.elf");
        var keptCPath = Path.Combine(tempDir, "kernel.c");
        var emittedBundledLinkerScriptPath = Path.Combine(tempDir, "bundled.ld");
        var bundledBuild = RunProcessWithTimeout(
            compilerInvocation.FileName,
            CombineCommandArguments(compilerInvocation.ArgumentsPrefix, $"build \"{mainPath}\" --target bare-metal-x86_64 -o \"{imagePath}\" --keep-c \"{keptCPath}\" --emit-linker-script \"{emittedBundledLinkerScriptPath}\""),
            projectRoot,
            TimeSpan.FromSeconds(30));

        if (bundledBuild.ExitCode != 0)
            throw new Exception($"Bare-metal CLI build failed with exit code {bundledBuild.ExitCode}.{Environment.NewLine}{bundledBuild.StdErr}{bundledBuild.StdOut}".Trim());

        if (!File.Exists(imagePath))
            throw new Exception("Bare-metal CLI build did not produce the requested kernel image.");

        if (!File.Exists(keptCPath))
            throw new Exception("Bare-metal CLI build did not keep the requested C output.");

        if (!File.Exists(emittedBundledLinkerScriptPath))
            throw new Exception("Bare-metal CLI build did not emit the bundled linker script.");

        if (!bundledBuild.StdOut.Contains("Linker script: bundled bare-metal-x86_64 default", StringComparison.Ordinal))
            throw new Exception("Bare-metal CLI build did not report using the bundled linker script.");

        if (!bundledBuild.StdOut.Contains($"Emitted linker script to {emittedBundledLinkerScriptPath}", StringComparison.Ordinal))
            throw new Exception("Bare-metal CLI build did not report the emitted linker script path.");

        var keptC = NormalizeNewlines(File.ReadAllText(keptCPath, Encoding.UTF8));
        var emittedBundledLinkerScript = NormalizeNewlines(File.ReadAllText(emittedBundledLinkerScriptPath, Encoding.UTF8));
        var expectedSnippets = new[]
        {
            "#define __zorb_builtin_is_bare_metal 1",
            "#define __zorb_builtin_is_linux 0",
            "#define __zorb_builtin_is_x86_64 1",
            "outb %b0, $0xE9"
        };

        foreach (var snippet in expectedSnippets)
        {
            if (!keptC.Contains(snippet, StringComparison.Ordinal))
                throw new Exception($"Bare-metal CLI build output did not contain expected snippet '{snippet}'.");
        }

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
        var customBuild = RunProcessWithTimeout(
            compilerInvocation.FileName,
            CombineCommandArguments(compilerInvocation.ArgumentsPrefix, $"build \"{mainPath}\" --target bare-metal-x86_64 --linker-script \"{customLinkerScriptPath}\" --emit-linker-script \"{emittedCustomLinkerScriptPath}\" -o \"{customImagePath}\""),
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
    }
    finally
    {
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, recursive: true);
    }
}

static CliWorkflowCase[] GetCliWorkflowCases()
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

static void RunCliWorkflowFixture(CompilerInvocation compilerInvocation, string projectRoot, string fixtureRoot, string tempDir, CliWorkflowCase workflowCase)
{
    var fixtureName = workflowCase.FixtureName;
    var fixtureDir = Path.Combine(fixtureRoot, fixtureName);
    var mainPath = Path.Combine(fixtureDir, "main.zorb");
    var cliTargetName = workflowCase.TargetName;
    var cliExpectation = ReadCliWorkflowExpectation(fixtureDir, cliTargetName);
    var expectedStdOut = cliExpectation.ExpectedStdOut ?? "";
    var expectedStdErr = cliExpectation.ExpectedStdErr ?? "";
    var expectedExit = cliExpectation.ExpectedExit;

    var fixtureTempDir = Path.Combine(tempDir, fixtureName);
    Directory.CreateDirectory(fixtureTempDir);

    var outputFileName = OperatingSystem.IsWindows() ? $"{fixtureName}.exe" : fixtureName;
    var builtBinaryPath = Path.Combine(fixtureTempDir, outputFileName);
    var keptCPath = Path.Combine(fixtureTempDir, $"{fixtureName}.c");

    var build = RunProcessWithTimeout(
        compilerInvocation.FileName,
        CombineCommandArguments(compilerInvocation.ArgumentsPrefix, $"build \"{mainPath}\" --target {cliTargetName} -o \"{builtBinaryPath}\" --keep-c \"{keptCPath}\""),
        projectRoot,
        TimeSpan.FromSeconds(30));

    if (build.ExitCode != 0)
        throw new Exception($"CLI build failed for fixture '{fixtureName}' with exit code {build.ExitCode}.{Environment.NewLine}{build.StdErr}{build.StdOut}".Trim());

    if (!File.Exists(builtBinaryPath))
        throw new Exception($"CLI build for fixture '{fixtureName}' did not produce the requested binary.");

    if (!File.Exists(keptCPath))
        throw new Exception($"CLI build for fixture '{fixtureName}' did not keep the requested C output.");

    var builtExecution = RunBuiltCliBinary(builtBinaryPath, fixtureTempDir);
    if (builtExecution.ExitCode != expectedExit)
        throw new Exception($"Built fixture '{fixtureName}' exit code mismatch. Expected {expectedExit}, got {builtExecution.ExitCode}.");

    var actualBuiltStdOut = NormalizeNewlines(builtExecution.StdOut);
    var actualBuiltStdErr = NormalizeNewlines(builtExecution.StdErr);
    if (!string.Equals(actualBuiltStdOut, expectedStdOut, StringComparison.Ordinal))
        throw new Exception($"Built fixture '{fixtureName}' stdout mismatch.{Environment.NewLine}Expected:{Environment.NewLine}{expectedStdOut}{Environment.NewLine}Actual:{Environment.NewLine}{actualBuiltStdOut}");
    if (!string.Equals(actualBuiltStdErr, expectedStdErr, StringComparison.Ordinal))
        throw new Exception($"Built fixture '{fixtureName}' stderr mismatch.{Environment.NewLine}Expected:{Environment.NewLine}{expectedStdErr}{Environment.NewLine}Actual:{Environment.NewLine}{actualBuiltStdErr}");

    var run = RunProcessWithTimeout(
        compilerInvocation.FileName,
        CombineCommandArguments(compilerInvocation.ArgumentsPrefix, $"run \"{mainPath}\" --target {cliTargetName}"),
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

static ProcessResult RunBuiltCliBinary(string builtBinaryPath, string workingDirectory)
{
    if (OperatingSystem.IsLinux())
        return RunProcessWithTimeout("timeout", $"30s \"{builtBinaryPath}\"", workingDirectory, TimeSpan.FromSeconds(30));

    if (OperatingSystem.IsWindows())
        return RunProcessWithTimeout(builtBinaryPath, "", workingDirectory, TimeSpan.FromSeconds(30));

    throw new Exception("CLI workflow tests currently require a Linux or Windows host.");
}

static CompilerInvocation GetCompilerInvocation(string projectRoot)
{
    var configurationDir = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
        ?? throw new Exception("Unable to determine test output configuration.");
    var targetFrameworkDir = new DirectoryInfo(AppContext.BaseDirectory).Name;
    var outputDir = Path.Combine(projectRoot, "Zorb.Compiler", "bin", configurationDir, targetFrameworkDir);
    foreach (var invocation in GetCandidateCompilerInvocations(AppContext.BaseDirectory))
    {
        if (CompilerInvocationExists(invocation))
            return invocation;
    }

    foreach (var invocation in GetCandidateCompilerInvocations(outputDir))
    {
        if (CompilerInvocationExists(invocation))
            return invocation;
    }

    throw new Exception(
        $"Compiler executable was not found in either '{AppContext.BaseDirectory}' or '{outputDir}'.");
}

static IEnumerable<CompilerInvocation> GetCandidateCompilerInvocations(string directory)
{
    var executableName = OperatingSystem.IsWindows() ? "Zorb.Compiler.exe" : "Zorb.Compiler";
    yield return new CompilerInvocation(Path.Combine(directory, executableName), "");
    yield return new CompilerInvocation("dotnet", $"\"{Path.Combine(directory, "Zorb.Compiler.dll")}\"");
}

static bool CompilerInvocationExists(CompilerInvocation invocation)
{
    if (!string.Equals(invocation.FileName, "dotnet", StringComparison.Ordinal))
        return File.Exists(invocation.FileName);

    var dllPath = invocation.ArgumentsPrefix.Trim().Trim('"');
    return File.Exists(dllPath);
}

static void RunExampleCompilationTest(string examplePath)
{
    if (!File.Exists(examplePath))
        throw new Exception($"Example source was not found at '{examplePath}'.");

    var exampleDir = Path.GetDirectoryName(examplePath)
        ?? throw new Exception($"Unable to determine example directory for '{examplePath}'.");

    var compilation = CompileFixture(examplePath, exampleDir);

    var allErrors = new List<string>();
    allErrors.AddRange(compilation.ParseErrors);
    allErrors.AddRange(compilation.Checker.Errors.Errors);
    if (!string.IsNullOrEmpty(compilation.FailureMessage))
        allErrors.Add(compilation.FailureMessage);

    var diagnosticsText = string.Join(Environment.NewLine, allErrors);
    AssertPhase(compilation.Phase, FixturePhase.Success, diagnosticsText);
    AssertNoErrors(allErrors);
    AssertNoWarnings(compilation.Checker.Errors.Warnings);
}

static void RunFixture(string fixtureDir, bool updateSnapshots)
{
    var mainPath = Path.Combine(fixtureDir, "main.zorb");
    if (!File.Exists(mainPath))
        throw new Exception("Fixture is missing main.zorb");

    var expectedPhase = ReadExpectedPhase(fixtureDir);
    var expectedErrors = ReadExpectationLines(fixtureDir, "expect-errors.txt");
    var expectedWarnings = ReadExpectationLines(fixtureDir, "expect-warnings.txt");
    var shouldCaptureOutput = expectedPhase != FixturePhase.Success || expectedErrors.Count > 0 || expectedWarnings.Count > 0;
    var compilation = shouldCaptureOutput
        ? CaptureConsole(() => CompileFixture(mainPath, fixtureDir))
        : new CapturedCompilation(CompileFixture(mainPath, fixtureDir), "", "");

    AssertPhase(compilation.Result.Phase, expectedPhase, compilation.Result.FailureMessage);

    var allErrors = new List<string>();
    allErrors.AddRange(compilation.Result.ParseErrors);
    allErrors.AddRange(compilation.Result.Checker.Errors.Errors);
    if (!string.IsNullOrEmpty(compilation.Result.FailureMessage))
        allErrors.Add(compilation.Result.FailureMessage);
    var allWarnings = compilation.Result.Checker.Errors.Warnings;

    foreach (var expected in expectedWarnings)
        AssertContains(allWarnings, expected, "warning");

    if (expectedPhase != FixturePhase.Success || expectedErrors.Count > 0)
    {
        foreach (var expected in expectedErrors)
            AssertContains(allErrors, expected, "error");
        return;
    }

    AssertNoErrors(allErrors);
    if (expectedWarnings.Count == 0)
        AssertNoWarnings(allWarnings);

    var generated = compilation.Result.Generated;

    foreach (var expected in ReadExpectationLinesForCurrentHost(fixtureDir, "expect-generated.txt"))
        AssertTextContains(generated, expected);

    var snapshotPath = ResolveExpectationPathForCurrentHost(fixtureDir, "expect-generated-full.c");
    if (File.Exists(snapshotPath))
    {
        if (updateSnapshots)
        {
            File.WriteAllText(snapshotPath, generated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        else
        {
            var expectedSnapshot = File.ReadAllText(snapshotPath, Encoding.UTF8);
            if (!string.Equals(NormalizeNewlines(generated), NormalizeNewlines(expectedSnapshot), StringComparison.Ordinal))
                throw new Exception($"Generated output did not match snapshot '{Path.GetFileName(snapshotPath)}'.");
        }
    }

    foreach (var line in ReadExpectationLinesForCurrentHost(fixtureDir, "expect-generated-counts.txt"))
    {
        var separatorIndex = line.LastIndexOf("=>", StringComparison.Ordinal);
        if (separatorIndex < 0)
            throw new Exception($"Invalid count expectation '{line}'");

        var needle = line[..separatorIndex].Trim();
        var countText = line[(separatorIndex + 2)..].Trim();
        if (!int.TryParse(countText, out var expectedCount))
            throw new Exception($"Invalid count in expectation '{line}'");

        var actualCount = CountOccurrences(generated, needle);
        if (actualCount != expectedCount)
            throw new Exception($"Expected '{needle}' {expectedCount} time(s), got {actualCount}.");
    }

    RunRuntimeExpectationsIfPresent(fixtureDir, mainPath);
}

static FixtureCompilation CompileFixture(string mainPath, string fixtureDir)
{
    var parseResult = ParseFile(mainPath);
    var ast = parseResult.EntryNodes;
    var parseErrors = parseResult.Errors;
    if (parseErrors.Count > 0)
        return new FixtureCompilation(ast, parseErrors, new TypeChecker(), "", FixturePhase.Parse, null);

    var checker = new TypeChecker();
    checker.Check(ast, fixtureDir, parseResult.Files);
    if (checker.Errors.Errors.Count > 0)
        return new FixtureCompilation(ast, parseErrors, checker, "", FixturePhase.Semantic, null);

    try
    {
        var generator = new CGenerator(fixtureDir, checker.SymbolTable);
        var generated = generator.Generate(ast, parseResult.Files);
        return new FixtureCompilation(ast, parseErrors, checker, generated, FixturePhase.Success, null);
    }
    catch (Exception ex)
    {
        return new FixtureCompilation(ast, parseErrors, checker, "", FixturePhase.Codegen, ex.Message);
    }
}

static void RunRuntimeExpectationsIfPresent(string fixtureDir, string mainPath)
{
    var runtimeExpectations = ReadRuntimeExpectations(fixtureDir);
    if (runtimeExpectations.Count == 0)
        return;

    var runnableExpectations = runtimeExpectations
        .Where(IsRuntimeExpectationRunnableOnCurrentHost)
        .ToList();
    if (runnableExpectations.Count == 0)
        return;

    var runtimeCompilations = new Dictionary<(bool PreserveStart, bool NoStdLib), FixtureCompilation>();

    foreach (var runtimeExpectation in runnableExpectations)
    {
        var compilationKey = GetRuntimeCompilationOptions(runtimeExpectation.TargetName);
        if (!runtimeCompilations.TryGetValue(compilationKey, out var runtimeCompilation))
        {
            runtimeCompilation = CompileRuntimeFixture(mainPath, fixtureDir, compilationKey.PreserveStart, compilationKey.NoStdLib);
            AssertNoErrors(runtimeCompilation.ParseErrors);
            AssertNoErrors(runtimeCompilation.Checker.Errors.Errors);
            AssertNoWarnings(runtimeCompilation.Checker.Errors.Warnings);
            runtimeCompilations[compilationKey] = runtimeCompilation;
        }

        RunRuntimeExpectation(fixtureDir, runtimeCompilation.Generated, runtimeExpectation);
    }
}

static FixtureCompilation CompileRuntimeFixture(string mainPath, string fixtureDir, bool preserveStart, bool noStdLib)
{
    var parseResult = ParseFile(mainPath);
    var ast = parseResult.EntryNodes;
    var parseErrors = parseResult.Errors;
    if (parseErrors.Count > 0)
        return new FixtureCompilation(ast, parseErrors, new TypeChecker(), "", FixturePhase.Parse, null);

    var checker = new TypeChecker();
    checker.Check(ast, fixtureDir, parseResult.Files);
    if (checker.Errors.Errors.Count > 0)
        return new FixtureCompilation(ast, parseErrors, checker, "", FixturePhase.Semantic, null);

    try
    {
        var generator = new CGenerator(fixtureDir, checker.SymbolTable)
        {
            PreserveStart = preserveStart,
            NoStdLib = noStdLib
        };
        var generated = generator.Generate(ast, parseResult.Files);
        return new FixtureCompilation(ast, parseErrors, checker, generated, FixturePhase.Success, null);
    }
    catch (Exception ex)
    {
        return new FixtureCompilation(ast, parseErrors, checker, "", FixturePhase.Codegen, ex.Message);
    }
}

static ParseGraphResult ParseFile(string path)
{
    return ImportGraphParser.ParseWithImports(path);
}

static List<string> ReadExpectationLines(string fixtureDir, string fileName)
{
    var path = Path.Combine(fixtureDir, fileName);
    if (!File.Exists(path))
        return new List<string>();

    return File.ReadAllLines(path, Encoding.UTF8)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
        .ToList();
}

static List<string> ReadExpectationLinesForCurrentHost(string fixtureDir, string fileName)
{
    var path = ResolveExpectationPathForCurrentHost(fixtureDir, fileName);
    if (!File.Exists(path))
        return new List<string>();

    return File.ReadAllLines(path, Encoding.UTF8)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
        .ToList();
}

static string ResolveExpectationPathForCurrentHost(string fixtureDir, string fileName)
{
    var genericPath = Path.Combine(fixtureDir, fileName);
    var hostSuffix = OperatingSystem.IsWindows() ? "-windows" : "-linux";
    var hostSpecificFileName = Path.GetFileNameWithoutExtension(fileName) + hostSuffix + Path.GetExtension(fileName);
    var hostSpecificPath = Path.Combine(fixtureDir, hostSpecificFileName);
    return File.Exists(hostSpecificPath) ? hostSpecificPath : genericPath;
}

static void AssertNoErrors(List<string> errors)
{
    if (errors.Count > 0)
        throw new Exception(string.Join(Environment.NewLine, errors));
}

static void AssertNoWarnings(List<string> warnings)
{
    if (warnings.Count > 0)
        throw new Exception(string.Join(Environment.NewLine, warnings));
}

static void AssertContains(List<string> diagnostics, string expected, string diagnosticKind)
{
    if (!diagnostics.Any(diagnostic => diagnostic.Contains(expected, StringComparison.Ordinal)))
        throw new Exception($"Expected {diagnosticKind} containing '{expected}'. Actual:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}");
}

static void AssertTextContains(string text, string expected)
{
    if (!text.Contains(expected, StringComparison.Ordinal))
        throw new Exception($"Expected generated output containing '{expected}'.");
}

static int CountOccurrences(string text, string needle)
{
    int count = 0;
    int index = 0;
    while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
    {
        count++;
        index += needle.Length;
    }
    return count;
}

static FixturePhase ReadExpectedPhase(string fixtureDir)
{
    var path = Path.Combine(fixtureDir, "expect-phase.txt");
    if (!File.Exists(path))
        return FixturePhase.Success;

    var text = File.ReadAllText(path, Encoding.UTF8).Trim();
    return text switch
    {
        "success" => FixturePhase.Success,
        "parse" => FixturePhase.Parse,
        "semantic" => FixturePhase.Semantic,
        "codegen" => FixturePhase.Codegen,
        _ => throw new Exception($"Unknown expected phase '{text}' in expect-phase.txt")
    };
}

static void AssertPhase(FixturePhase actual, FixturePhase expected, string? failureMessage)
{
    if (actual != expected)
    {
        var detail = string.IsNullOrEmpty(failureMessage) ? "" : $" {failureMessage}";
        throw new Exception($"Expected phase '{expected}', got '{actual}'.{detail}");
    }
}

static CapturedCompilation CaptureConsole(Func<FixtureCompilation> action)
{
    var originalOut = Console.Out;
    var originalError = Console.Error;
    using var stdout = new StringWriter();
    using var stderr = new StringWriter();

    Console.SetOut(stdout);
    Console.SetError(stderr);

    try
    {
        return new CapturedCompilation(action(), stdout.ToString(), stderr.ToString());
    }
    finally
    {
        Console.SetOut(originalOut);
        Console.SetError(originalError);
    }
}

static string NormalizeNewlines(string contents)
{
    if (contents.Length > 0 && contents[0] == '\uFEFF')
        contents = contents[1..];
    return contents.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
}

static List<RuntimeExpectation> ReadRuntimeExpectations(string fixtureDir)
{
    var expectations = new List<RuntimeExpectation>();
    var hostStdOutPath = Path.Combine(fixtureDir, "expect-stdout.txt");
    var hostStdErrPath = Path.Combine(fixtureDir, "expect-stderr.txt");
    var hostExitPath = Path.Combine(fixtureDir, "expect-exit.txt");
    if (File.Exists(hostStdOutPath) || File.Exists(hostStdErrPath) || File.Exists(hostExitPath))
    {
        expectations.Add(new RuntimeExpectation(
            "freestanding-linux",
            File.Exists(hostStdOutPath) ? NormalizeNewlines(File.ReadAllText(hostStdOutPath, Encoding.UTF8)) : null,
            File.Exists(hostStdErrPath) ? NormalizeNewlines(File.ReadAllText(hostStdErrPath, Encoding.UTF8)) : null,
            File.Exists(hostExitPath) ? int.Parse(File.ReadAllText(hostExitPath, Encoding.UTF8).Trim()) : 0));
    }

    var windowsStdOutPath = Path.Combine(fixtureDir, "expect-stdout-windows.txt");
    var windowsStdErrPath = Path.Combine(fixtureDir, "expect-stderr-windows.txt");
    var windowsExitPath = Path.Combine(fixtureDir, "expect-exit-windows.txt");
    if (File.Exists(windowsStdOutPath) || File.Exists(windowsStdErrPath) || File.Exists(windowsExitPath))
    {
        expectations.Add(new RuntimeExpectation(
            "host-windows",
            File.Exists(windowsStdOutPath)
                ? NormalizeNewlines(File.ReadAllText(windowsStdOutPath, Encoding.UTF8))
                : (File.Exists(hostStdOutPath) ? NormalizeNewlines(File.ReadAllText(hostStdOutPath, Encoding.UTF8)) : null),
            File.Exists(windowsStdErrPath)
                ? NormalizeNewlines(File.ReadAllText(windowsStdErrPath, Encoding.UTF8))
                : (File.Exists(hostStdErrPath) ? NormalizeNewlines(File.ReadAllText(hostStdErrPath, Encoding.UTF8)) : null),
            File.Exists(windowsExitPath)
                ? int.Parse(File.ReadAllText(windowsExitPath, Encoding.UTF8).Trim())
                : (File.Exists(hostExitPath) ? int.Parse(File.ReadAllText(hostExitPath, Encoding.UTF8).Trim()) : 0)));
    }

    var aarch64StdOutPath = Path.Combine(fixtureDir, "expect-stdout-aarch64.txt");
    var aarch64StdErrPath = Path.Combine(fixtureDir, "expect-stderr-aarch64.txt");
    var aarch64ExitPath = Path.Combine(fixtureDir, "expect-exit-aarch64.txt");
    if (File.Exists(aarch64StdOutPath) || File.Exists(aarch64StdErrPath) || File.Exists(aarch64ExitPath))
    {
        expectations.Add(new RuntimeExpectation(
            "linux-aarch64",
            File.Exists(aarch64StdOutPath) ? NormalizeNewlines(File.ReadAllText(aarch64StdOutPath, Encoding.UTF8)) : null,
            File.Exists(aarch64StdErrPath) ? NormalizeNewlines(File.ReadAllText(aarch64StdErrPath, Encoding.UTF8)) : null,
            File.Exists(aarch64ExitPath) ? int.Parse(File.ReadAllText(aarch64ExitPath, Encoding.UTF8).Trim()) : 0));
    }

    return expectations;
}

static RuntimeExpectation ReadCliWorkflowExpectation(string fixtureDir, string targetName)
{
    var expectations = ReadRuntimeExpectations(fixtureDir);
    var expectation = expectations.FirstOrDefault(item => string.Equals(item.TargetName, targetName, StringComparison.Ordinal));
    if (expectation != null)
        return expectation;

    if (string.Equals(targetName, "freestanding-linux", StringComparison.Ordinal))
    {
        var hostLinuxExpectation = expectations.FirstOrDefault(item => string.Equals(item.TargetName, "freestanding-linux", StringComparison.Ordinal));
        if (hostLinuxExpectation != null)
            return hostLinuxExpectation;
    }

    if (string.Equals(targetName, "host-linux", StringComparison.Ordinal))
    {
        var freestandingExpectation = expectations.FirstOrDefault(item => string.Equals(item.TargetName, "freestanding-linux", StringComparison.Ordinal));
        if (freestandingExpectation != null)
            return freestandingExpectation;
    }

    if (string.Equals(targetName, "host-windows", StringComparison.Ordinal))
    {
        var hostLinuxExpectation = expectations.FirstOrDefault(item => string.Equals(item.TargetName, "freestanding-linux", StringComparison.Ordinal));
        if (hostLinuxExpectation != null)
            return hostLinuxExpectation;
    }

    throw new Exception($"Fixture '{Path.GetFileName(fixtureDir)}' is missing runtime expectations for CLI target '{targetName}'.");
}

static bool IsRuntimeExpectationRunnableOnCurrentHost(RuntimeExpectation runtimeExpectation)
{
    return runtimeExpectation.TargetName switch
    {
        "freestanding-linux" or "host-linux" or "linux-aarch64" => OperatingSystem.IsLinux(),
        "host-windows" => OperatingSystem.IsWindows(),
        _ => false
    };
}

static (bool PreserveStart, bool NoStdLib) GetRuntimeCompilationOptions(string targetName)
{
    return targetName switch
    {
        "freestanding-linux" => (PreserveStart: true, NoStdLib: true),
        "host-linux" => (PreserveStart: false, NoStdLib: false),
        "linux-aarch64" => (PreserveStart: true, NoStdLib: true),
        "host-windows" => (PreserveStart: false, NoStdLib: false),
        _ => throw new Exception($"Unknown runtime target '{targetName}'.")
    };
}

static void RunRuntimeExpectation(string fixtureDir, string generated, RuntimeExpectation runtimeExpectation)
{
    var tempDir = Path.Combine(
        Path.GetTempPath(),
        "zorb-runtime-fixtures",
        Path.GetFileName(fixtureDir) + "-" + runtimeExpectation.TargetName + "-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);

    try
    {
        var sourcePath = Path.Combine(tempDir, "out.c");
        File.WriteAllText(sourcePath, generated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        ProcessResult compile;
        ProcessResult execution;

        if (runtimeExpectation.TargetName == "freestanding-linux")
        {
            EnsureToolAvailable("timeout");
            var binaryPath = Path.Combine(tempDir, "out");
            compile = RunProcess(
                "gcc",
                $"-O2 -nostdlib -fno-pie -no-pie -z execstack -fno-builtin \"{sourcePath}\" -o \"{binaryPath}\"",
                tempDir);

            if (compile.ExitCode != 0)
                throw new Exception($"Runtime gcc compile failed with exit code {compile.ExitCode}.{Environment.NewLine}{compile.StdErr}{compile.StdOut}".Trim());

            execution = RunProcess("timeout", $"30s \"{binaryPath}\"", tempDir);
        }
        else if (runtimeExpectation.TargetName == "host-linux")
        {
            EnsureToolAvailable("timeout");
            var binaryPath = Path.Combine(tempDir, "out");
            compile = RunProcess(
                "gcc",
                $"-O2 \"{sourcePath}\" -o \"{binaryPath}\"",
                tempDir);

            if (compile.ExitCode != 0)
                throw new Exception($"Hosted Linux gcc compile failed with exit code {compile.ExitCode}.{Environment.NewLine}{compile.StdErr}{compile.StdOut}".Trim());

            execution = RunProcess("timeout", $"30s \"{binaryPath}\"", tempDir);
        }
        else if (runtimeExpectation.TargetName == "linux-aarch64")
        {
            EnsureToolAvailable("aarch64-linux-gnu-gcc");
            EnsureToolAvailable("qemu-aarch64");
            EnsureToolAvailable("timeout");

            var binaryPath = Path.Combine(tempDir, "out-aarch64");
            compile = RunProcess(
                "aarch64-linux-gnu-gcc",
                $"-O2 -nostdlib -static -fno-pie -no-pie -z execstack -fno-builtin \"{sourcePath}\" -o \"{binaryPath}\"",
                tempDir);

            if (compile.ExitCode != 0)
                throw new Exception($"Runtime aarch64 gcc compile failed with exit code {compile.ExitCode}.{Environment.NewLine}{compile.StdErr}{compile.StdOut}".Trim());

            execution = RunProcess("timeout", $"30s qemu-aarch64 \"{binaryPath}\"", tempDir);
        }
        else if (runtimeExpectation.TargetName == "host-windows")
        {
            var compiler = EnsureToolAvailable("clang-cl", "cl");

            var binaryPath = Path.Combine(tempDir, "out.exe");
            compile = RunProcess(
                compiler,
                GetWindowsCompileArguments(compiler, sourcePath, binaryPath),
                tempDir);

            if (compile.ExitCode != 0)
                throw new Exception($"Runtime Windows compile failed with exit code {compile.ExitCode}.{Environment.NewLine}{compile.StdErr}{compile.StdOut}".Trim());

            execution = RunProcessWithTimeout(binaryPath, "", tempDir, TimeSpan.FromSeconds(30));
        }
        else
        {
            throw new Exception($"Unknown runtime target '{runtimeExpectation.TargetName}'.");
        }

        var actualStdOut = NormalizeNewlines(execution.StdOut);
        var actualStdErr = NormalizeNewlines(execution.StdErr);

        if (execution.ExitCode != runtimeExpectation.ExpectedExit)
        {
            throw new Exception(
                $"Target '{runtimeExpectation.TargetName}' expected runtime exit code {runtimeExpectation.ExpectedExit}, got {execution.ExitCode}.{Environment.NewLine}{execution.StdErr}".Trim());
        }

        if (runtimeExpectation.ExpectedStdOut != null &&
            !string.Equals(actualStdOut, runtimeExpectation.ExpectedStdOut, StringComparison.Ordinal))
        {
            throw new Exception(
                $"Target '{runtimeExpectation.TargetName}' runtime stdout did not match expectation.{Environment.NewLine}Expected:{Environment.NewLine}{runtimeExpectation.ExpectedStdOut}{Environment.NewLine}Actual:{Environment.NewLine}{actualStdOut}");
        }

        if (runtimeExpectation.ExpectedStdErr != null &&
            !string.Equals(actualStdErr, runtimeExpectation.ExpectedStdErr, StringComparison.Ordinal))
        {
            throw new Exception(
                $"Target '{runtimeExpectation.TargetName}' runtime stderr did not match expectation.{Environment.NewLine}Expected:{Environment.NewLine}{runtimeExpectation.ExpectedStdErr}{Environment.NewLine}Actual:{Environment.NewLine}{actualStdErr}");
        }
    }
    finally
    {
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, recursive: true);
    }
}

static string EnsureToolAvailable(params string[] toolNames)
{
    if (toolNames.Length == 0)
        throw new ArgumentException("At least one tool name must be provided.", nameof(toolNames));

    var locator = OperatingSystem.IsWindows() ? "where" : "which";
    foreach (var toolName in toolNames)
    {
        var check = RunProcess(locator, toolName, Directory.GetCurrentDirectory());
        if (check.ExitCode == 0 && !string.IsNullOrWhiteSpace(check.StdOut))
            return toolName;
    }

    if (toolNames.Length == 1)
        throw new Exception($"Required runtime tool '{toolNames[0]}' was not found in PATH.");

    throw new Exception($"Required runtime tools were not found in PATH. Install one of: {string.Join(", ", toolNames)}.");
}

static string FindAncestorContainingFile(string startPath, string fileName)
{
    var current = new DirectoryInfo(Path.GetFullPath(startPath));
    while (current != null)
    {
        var candidate = Path.Combine(current.FullName, fileName);
        if (File.Exists(candidate))
            return current.FullName;

        current = current.Parent;
    }

    throw new Exception($"Unable to locate '{fileName}' from '{startPath}'.");
}

static string GetWindowsCompileArguments(string compiler, string cSourcePath, string outputPath)
{
    return compiler switch
    {
        "clang-cl" => $"/nologo /TC /O2 \"{cSourcePath}\" /Fe:\"{outputPath}\" /link kernel32.lib",
        "cl" => $"/nologo /TC /O2 \"{cSourcePath}\" /Fe:\"{outputPath}\" /link kernel32.lib",
        _ => throw new Exception($"Unsupported Windows compiler '{compiler}'.")
    };
}

static ProcessResult RunProcess(string fileName, string arguments, string workingDirectory)
{
    return RunProcessCore(fileName, arguments, workingDirectory, timeout: null);
}

static ProcessResult RunProcessWithTimeout(string fileName, string arguments, string workingDirectory, TimeSpan timeout)
{
    return RunProcessCore(fileName, arguments, workingDirectory, timeout);
}

static ProcessResult RunProcessCore(string fileName, string arguments, string workingDirectory, TimeSpan? timeout)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };

    using var process = Process.Start(startInfo) ?? throw new Exception($"Failed to start process '{fileName}'.");
    var stdOutTask = process.StandardOutput.ReadToEndAsync();
    var stdErrTask = process.StandardError.ReadToEndAsync();

    if (timeout.HasValue && !process.WaitForExit((int)timeout.Value.TotalMilliseconds))
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }

        process.WaitForExit();
        Task.WaitAll(stdOutTask, stdErrTask);
        throw new Exception($"Process '{fileName}' timed out after {timeout.Value.TotalSeconds:0} seconds.");
    }

    process.WaitForExit();
    Task.WaitAll(stdOutTask, stdErrTask);
    return new ProcessResult(process.ExitCode, stdOutTask.Result, stdErrTask.Result);
}

static string CombineCommandArguments(string prefix, string arguments)
{
    if (string.IsNullOrEmpty(prefix))
        return arguments;

    if (string.IsNullOrEmpty(arguments))
        return prefix;

    return prefix + " " + arguments;
}

enum FixturePhase
{
    Success,
    Parse,
    Semantic,
    Codegen
}

sealed record FixtureCompilation(
    List<Node> Ast,
    List<string> ParseErrors,
    TypeChecker Checker,
    string Generated,
    FixturePhase Phase,
    string? FailureMessage);

sealed record CapturedCompilation(FixtureCompilation Result, string StdOut, string StdErr);

sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

sealed record RuntimeExpectation(string TargetName, string? ExpectedStdOut, string? ExpectedStdErr, int ExpectedExit);

sealed record CompilerInvocation(string FileName, string ArgumentsPrefix);

sealed record CliWorkflowCase(string FixtureName, string TargetName);

sealed record CliArgumentCase(string Name, string Arguments, int ExpectedExitCode, string? ExpectedStdOutSubstring, string? ExpectedStdErrSubstring);
