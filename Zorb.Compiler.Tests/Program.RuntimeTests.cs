using System.Text;
using System.Runtime.InteropServices;
using Zorb.Compiler.AST;
using Zorb.Compiler.Parsing;
using Zorb.Compiler.Semantic;

internal static partial class Program
{
    private static void RunFixture(string fixtureDir)
    {
        var mainPath = Path.Combine(fixtureDir, "main.zorb");
        if (!File.Exists(mainPath))
            throw new Exception("Fixture is missing main.zorb");

        var expectations = LoadFixtureExpectations(fixtureDir);
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

        var aarch64StdOutPath = Path.Combine(fixtureDir, "expect-stdout-linux-aarch64.txt");
        var aarch64StdErrPath = Path.Combine(fixtureDir, "expect-stderr-linux-aarch64.txt");
        var aarch64ExitPath = Path.Combine(fixtureDir, "expect-exit-linux-aarch64.txt");
        if (File.Exists(aarch64StdOutPath) || File.Exists(aarch64StdErrPath) || File.Exists(aarch64ExitPath))
        {
            expectations.Add(new RuntimeExpectation(
                "freestanding-linux-aarch64",
                File.Exists(aarch64StdOutPath)
                    ? NormalizeNewlines(File.ReadAllText(aarch64StdOutPath, Encoding.UTF8))
                    : (File.Exists(hostStdOutPath) ? NormalizeNewlines(File.ReadAllText(hostStdOutPath, Encoding.UTF8)) : null),
                File.Exists(aarch64StdErrPath)
                    ? NormalizeNewlines(File.ReadAllText(aarch64StdErrPath, Encoding.UTF8))
                    : (File.Exists(hostStdErrPath) ? NormalizeNewlines(File.ReadAllText(hostStdErrPath, Encoding.UTF8)) : null),
                File.Exists(aarch64ExitPath)
                    ? int.Parse(File.ReadAllText(aarch64ExitPath, Encoding.UTF8).Trim())
                    : (File.Exists(hostExitPath) ? int.Parse(File.ReadAllText(hostExitPath, Encoding.UTF8).Trim()) : 0)));
        }

        var hostAarch64StdOutPath = Path.Combine(fixtureDir, "expect-stdout-host-linux-aarch64.txt");
        var hostAarch64StdErrPath = Path.Combine(fixtureDir, "expect-stderr-host-linux-aarch64.txt");
        var hostAarch64ExitPath = Path.Combine(fixtureDir, "expect-exit-host-linux-aarch64.txt");
        if (File.Exists(hostAarch64StdOutPath) || File.Exists(hostAarch64StdErrPath) || File.Exists(hostAarch64ExitPath))
        {
            expectations.Add(new RuntimeExpectation(
                "host-linux-aarch64",
                File.Exists(hostAarch64StdOutPath)
                    ? NormalizeNewlines(File.ReadAllText(hostAarch64StdOutPath, Encoding.UTF8))
                    : (File.Exists(hostStdOutPath) ? NormalizeNewlines(File.ReadAllText(hostStdOutPath, Encoding.UTF8)) : null),
                File.Exists(hostAarch64StdErrPath)
                    ? NormalizeNewlines(File.ReadAllText(hostAarch64StdErrPath, Encoding.UTF8))
                    : (File.Exists(hostStdErrPath) ? NormalizeNewlines(File.ReadAllText(hostStdErrPath, Encoding.UTF8)) : null),
                File.Exists(hostAarch64ExitPath)
                    ? int.Parse(File.ReadAllText(hostAarch64ExitPath, Encoding.UTF8).Trim())
                    : (File.Exists(hostExitPath) ? int.Parse(File.ReadAllText(hostExitPath, Encoding.UTF8).Trim()) : 0)));
        }

        return expectations;
    }

    private static RuntimeExpectation ReadRuntimeExpectationForTarget(string fixtureDir, string targetName)
    {
        var expectations = ReadRuntimeExpectations(fixtureDir);
        var expectation = expectations.FirstOrDefault(item => string.Equals(item.TargetName, targetName, StringComparison.Ordinal));
        if (expectation != null)
            return expectation;

        if (string.Equals(targetName, "freestanding-linux", StringComparison.Ordinal))
        {
            var linuxExpectation = expectations.FirstOrDefault(item => string.Equals(item.TargetName, "freestanding-linux", StringComparison.Ordinal));
            if (linuxExpectation != null)
                return linuxExpectation;
        }

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
            "freestanding-linux-aarch64" or "host-linux-aarch64" => OperatingSystem.IsLinux() && (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 || FindAArch64Qemu() != null),
            "host-windows" => OperatingSystem.IsWindows(),
            _ => false
        };
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
