using System.Text;
using System.Runtime.InteropServices;
using Zorb.Compiler.AST;
using Zorb.Compiler.Parsing;
using Zorb.Compiler.Semantic;

internal static partial class Program
{
    private static void RunFixture(string fixtureDir, NativeFixtureHarness nativeFixtureHarness)
    {
        var mainPath = Path.Combine(fixtureDir, "main.zorb");
        if (!File.Exists(mainPath))
            throw new Exception("Fixture is missing main.zorb");

        var expectations = LoadFixtureExpectations(fixtureDir);
        nativeFixtureHarness.Validate(mainPath, expectations);
        var compilation = CompileFixtureForExpectations(mainPath, fixtureDir, expectations);
        AssertFixtureDiagnostics(compilation, expectations);

        if (expectations.ExpectedPhase != FixturePhase.Success || expectations.ExpectedErrors.Count > 0)
            return;

        RunFixtureRuntimeChecks(fixtureDir, mainPath);
    }

    private static FixtureExpectations LoadFixtureExpectations(string fixtureDir)
    {
        return new FixtureExpectations(
            ReadExpectedPhase(fixtureDir),
            ReadExpectationLines(fixtureDir, "expect-errors.txt"),
            ReadExpectationLines(fixtureDir, "expect-warnings.txt"));
    }

    private static CapturedCompilation CompileFixtureForExpectations(string mainPath, string fixtureDir, FixtureExpectations expectations)
    {
        var shouldCaptureOutput = expectations.ExpectedPhase != FixturePhase.Success
            || expectations.ExpectedErrors.Count > 0
            || expectations.ExpectedWarnings.Count > 0;
        return shouldCaptureOutput
            ? CaptureConsole(() => CompileFixture(mainPath, fixtureDir))
            : new CapturedCompilation(CompileFixture(mainPath, fixtureDir), "", "");
    }

    private static void AssertFixtureDiagnostics(CapturedCompilation compilation, FixtureExpectations expectations)
    {
        AssertPhase(compilation.Result.Phase, expectations.ExpectedPhase, compilation.Result.FailureMessage);

        var allErrors = CollectFixtureErrors(compilation.Result);
        var allWarnings = compilation.Result.Checker.Errors.Warnings;

        foreach (var expected in expectations.ExpectedWarnings)
            AssertContains(allWarnings, expected, "warning");

        if (expectations.ExpectedPhase != FixturePhase.Success || expectations.ExpectedErrors.Count > 0)
        {
            foreach (var expected in expectations.ExpectedErrors)
                AssertContains(allErrors, expected, "error");
            return;
        }

        AssertNoErrors(allErrors);
        if (expectations.ExpectedWarnings.Count == 0)
            AssertNoWarnings(allWarnings);
    }

    private static List<string> CollectFixtureErrors(FixtureCompilation compilation)
    {
        var allErrors = new List<string>();
        allErrors.AddRange(compilation.ParseErrors);
        allErrors.AddRange(compilation.Checker.Errors.Errors);
        if (!string.IsNullOrEmpty(compilation.FailureMessage))
            allErrors.Add(compilation.FailureMessage);
        return allErrors;
    }

    private static void RunFixtureRuntimeChecks(string fixtureDir, string mainPath)
    {
        RunLlvmEmissionTest(fixtureDir, mainPath);
        RunRuntimeExpectationsIfPresent(fixtureDir, mainPath);
    }

    private static FixtureCompilation CompileFixture(string mainPath, string fixtureDir)
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

        return new FixtureCompilation(ast, parseErrors, checker, "", FixturePhase.Success, null);
    }

    private static void RunLlvmEmissionTest(string fixtureDir, string mainPath)
    {
        var projectRoot = GetProjectRoot();
        var compilerInvocation = GetCompilerInvocation(projectRoot);
        WithTempDirectory("zorb-llvm-emission", tempDir =>
        {
            var outputPath = Path.Combine(tempDir, "out.ll");
            var emission = RunProcessWithTimeoutArgs(
                compilerInvocation.FileName,
                BuildCommandArguments(compilerInvocation, "--emit-llvm", mainPath, "-o", outputPath),
                projectRoot,
                TimeSpan.FromSeconds(30));

            if (emission.ExitCode != 0)
            {
                throw new Exception(
                    $"LLVM emission failed with exit code {emission.ExitCode}.{Environment.NewLine}{emission.StdErr}{emission.StdOut}".Trim());
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new Exception("LLVM emission did not produce a non-empty output file.");

            var llvmIr = File.ReadAllText(outputPath, Encoding.UTF8);
            if (!llvmIr.Contains("target triple =", StringComparison.Ordinal))
                throw new Exception("LLVM output did not contain a target triple.");

            foreach (var expected in ReadExpectationLinesForCurrentHost(fixtureDir, "expect-llvm.txt"))
                AssertTextContains(llvmIr, expected);
        });
    }

    private static void RunRuntimeExpectationsIfPresent(string fixtureDir, string mainPath)
    {
        foreach (var runtimeExpectation in ReadRuntimeExpectations(fixtureDir)
            .Where(IsRuntimeExpectationRunnableOnCurrentHost))
        {
            RunLlvmRuntimeExpectation(fixtureDir, mainPath, runtimeExpectation);
        }
    }

    private static List<RuntimeExpectation> ReadRuntimeExpectations(string fixtureDir)
    {
        var expectations = new List<RuntimeExpectation>();
        var hostStdOutPath = Path.Combine(fixtureDir, "expect-stdout.txt");
        var hostStdErrPath = Path.Combine(fixtureDir, "expect-stderr.txt");
        var hostExitPath = Path.Combine(fixtureDir, "expect-exit.txt");

        AddRuntimeExpectationIfPresent(
            expectations,
            fixtureDir,
            "freestanding-linux",
            runtimeSuffix: null,
            fallbackStdOutPath: null,
            fallbackStdErrPath: null,
            fallbackExitPath: null);
        AddRuntimeExpectationIfPresent(
            expectations,
            fixtureDir,
            "host-windows",
            "windows",
            hostStdOutPath,
            hostStdErrPath,
            hostExitPath);
        AddRuntimeExpectationIfPresent(
            expectations,
            fixtureDir,
            "freestanding-linux-aarch64",
            "linux-aarch64",
            hostStdOutPath,
            hostStdErrPath,
            hostExitPath);
        AddRuntimeExpectationIfPresent(
            expectations,
            fixtureDir,
            "host-linux-aarch64",
            "host-linux-aarch64",
            hostStdOutPath,
            hostStdErrPath,
            hostExitPath);

        return expectations;
    }

    private static RuntimeExpectation ReadRuntimeExpectationForTarget(string fixtureDir, string targetName)
    {
        var expectations = ReadRuntimeExpectations(fixtureDir);
        var expectation = expectations.FirstOrDefault(item => string.Equals(item.TargetName, targetName, StringComparison.Ordinal));
        if (expectation != null)
            return expectation;

        if (string.Equals(targetName, "host-linux", StringComparison.Ordinal))
        {
            var freestandingExpectation = expectations.FirstOrDefault(item => string.Equals(item.TargetName, "freestanding-linux", StringComparison.Ordinal));
            if (freestandingExpectation != null)
                return freestandingExpectation;
        }

        if (string.Equals(targetName, "host-windows", StringComparison.Ordinal))
        {
            var linuxExpectation = expectations.FirstOrDefault(item => string.Equals(item.TargetName, "freestanding-linux", StringComparison.Ordinal));
            if (linuxExpectation != null)
                return linuxExpectation;
        }

        if (string.Equals(targetName, "freestanding-linux-aarch64", StringComparison.Ordinal))
        {
            var freestandingAarch64Expectation = expectations.FirstOrDefault(item => string.Equals(item.TargetName, "freestanding-linux-aarch64", StringComparison.Ordinal));
            if (freestandingAarch64Expectation != null)
                return freestandingAarch64Expectation;

            var freestandingLinuxExpectation = expectations.FirstOrDefault(item => string.Equals(item.TargetName, "freestanding-linux", StringComparison.Ordinal));
            if (freestandingLinuxExpectation != null)
                return new RuntimeExpectation(
                    targetName,
                    freestandingLinuxExpectation.ExpectedStdOut,
                    freestandingLinuxExpectation.ExpectedStdErr,
                    freestandingLinuxExpectation.ExpectedExit);
        }

        if (string.Equals(targetName, "host-linux-aarch64", StringComparison.Ordinal))
        {
            var hostAarch64Expectation = expectations.FirstOrDefault(item => string.Equals(item.TargetName, "host-linux-aarch64", StringComparison.Ordinal));
            if (hostAarch64Expectation != null)
                return hostAarch64Expectation;

            var freestandingAarch64Expectation = expectations.FirstOrDefault(item => string.Equals(item.TargetName, "freestanding-linux-aarch64", StringComparison.Ordinal));
            if (freestandingAarch64Expectation != null)
            {
                return new RuntimeExpectation(
                    targetName,
                    freestandingAarch64Expectation.ExpectedStdOut,
                    freestandingAarch64Expectation.ExpectedStdErr,
                    freestandingAarch64Expectation.ExpectedExit);
            }

            var freestandingLinuxExpectation = expectations.FirstOrDefault(item => string.Equals(item.TargetName, "freestanding-linux", StringComparison.Ordinal));
            if (freestandingLinuxExpectation != null)
            {
                return new RuntimeExpectation(
                    targetName,
                    freestandingLinuxExpectation.ExpectedStdOut,
                    freestandingLinuxExpectation.ExpectedStdErr,
                    freestandingLinuxExpectation.ExpectedExit);
            }
        }

        throw new Exception($"Fixture '{Path.GetFileName(fixtureDir)}' is missing runtime expectations for target '{targetName}'.");
    }

    private static bool IsRuntimeExpectationRunnableOnCurrentHost(RuntimeExpectation runtimeExpectation)
    {
        return runtimeExpectation.TargetName switch
        {
            "freestanding-linux" or "host-linux" => OperatingSystem.IsLinux(),
            "freestanding-linux-aarch64" or "host-linux-aarch64" => OperatingSystem.IsLinux() && CanBuildAArch64LinuxTarget() && CanRunAArch64LinuxTarget(),
            "host-windows" => OperatingSystem.IsWindows(),
            _ => false
        };
    }

    private static void AddRuntimeExpectationIfPresent(
        List<RuntimeExpectation> expectations,
        string fixtureDir,
        string targetName,
        string? runtimeSuffix,
        string? fallbackStdOutPath,
        string? fallbackStdErrPath,
        string? fallbackExitPath)
    {
        var suffix = string.IsNullOrEmpty(runtimeSuffix) ? string.Empty : "-" + runtimeSuffix;
        var stdOutPath = Path.Combine(fixtureDir, "expect-stdout" + suffix + ".txt");
        var stdErrPath = Path.Combine(fixtureDir, "expect-stderr" + suffix + ".txt");
        var exitPath = Path.Combine(fixtureDir, "expect-exit" + suffix + ".txt");
        if (!File.Exists(stdOutPath) && !File.Exists(stdErrPath) && !File.Exists(exitPath))
            return;

        expectations.Add(new RuntimeExpectation(
            targetName,
            ReadOptionalRuntimeText(stdOutPath, fallbackStdOutPath),
            ReadOptionalRuntimeText(stdErrPath, fallbackStdErrPath),
            ReadOptionalRuntimeExit(exitPath, fallbackExitPath)));
    }

    private static string? ReadOptionalRuntimeText(string path, string? fallbackPath)
    {
        if (File.Exists(path))
            return NormalizeNewlines(File.ReadAllText(path, Encoding.UTF8));

        return fallbackPath != null && File.Exists(fallbackPath)
            ? NormalizeNewlines(File.ReadAllText(fallbackPath, Encoding.UTF8))
            : null;
    }

    private static int ReadOptionalRuntimeExit(string path, string? fallbackPath)
    {
        if (File.Exists(path))
            return int.Parse(File.ReadAllText(path, Encoding.UTF8).Trim());

        return fallbackPath != null && File.Exists(fallbackPath)
            ? int.Parse(File.ReadAllText(fallbackPath, Encoding.UTF8).Trim())
            : 0;
    }

    private static void RunLlvmRuntimeExpectation(string fixtureDir, string mainPath, RuntimeExpectation runtimeExpectation)
    {
        var projectRoot = GetProjectRoot();
        var compilerInvocation = GetCompilerInvocation(projectRoot);
        WithTempDirectory($"zorb-runtime-{Path.GetFileName(fixtureDir)}", tempDir =>
        {
            CopyRuntimeDataFiles(fixtureDir, tempDir);
            var binaryName = OperatingSystem.IsWindows() ? "out.exe" : "out";
            var binaryPath = Path.Combine(tempDir, binaryName);
            var build = RunProcessWithTimeoutArgs(
                compilerInvocation.FileName,
                BuildCommandArguments(
                    compilerInvocation,
                    "build", mainPath, "--target", runtimeExpectation.TargetName, "-o", binaryPath),
                projectRoot,
                TimeSpan.FromSeconds(30));

            if (build.ExitCode != 0)
            {
                throw new Exception(
                    $"LLVM build for target '{runtimeExpectation.TargetName}' failed with exit code {build.ExitCode}.{Environment.NewLine}{build.StdErr}{build.StdOut}".Trim());
            }

            if (!File.Exists(binaryPath))
                throw new Exception($"LLVM build for target '{runtimeExpectation.TargetName}' did not produce a binary.");

            var execution = RunBuiltCliBinary(binaryPath, tempDir, runtimeExpectation.TargetName);

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
        });
    }

    private static void CopyRuntimeDataFiles(string fixtureDir, string tempDir)
    {
        foreach (var path in Directory.EnumerateFiles(fixtureDir))
        {
            var fileName = Path.GetFileName(path);
            if (string.Equals(fileName, "main.zorb", StringComparison.Ordinal) ||
                fileName.StartsWith("expect-", StringComparison.Ordinal))
            {
                continue;
            }

            File.Copy(path, Path.Combine(tempDir, fileName), overwrite: true);
        }
    }
}
