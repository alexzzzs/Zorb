using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Zorb.Compiler.AST;
using Zorb.Compiler.Codegen;
using Zorb.Compiler.Parsing;
using Zorb.Compiler.Semantic;
using Zorb.Compiler.Utils;

internal static partial class Program
{
    private static void RunSemanticDiagnosticOutputTests(string fixtureRoot)
    {
        var projectRoot = GetProjectRoot();
        var compilerInvocation = GetCompilerInvocation(projectRoot);
        var sampleInput = Path.Combine(fixtureRoot, "error_undeclared", "main.zorb");
        var result = RunProcessWithTimeoutArgs(
            compilerInvocation.FileName,
            CombineCommandArguments(compilerInvocation.ArgumentsPrefix, $"\"{sampleInput}\""),
            projectRoot,
            TimeSpan.FromSeconds(30));

        if (result.ExitCode != 1)
            throw new Exception($"Expected semantic failure exit code 1, got {result.ExitCode}.");

        var stdout = NormalizeNewlines(result.StdOut);
        var stderr = NormalizeNewlines(result.StdErr);

        if (!string.IsNullOrWhiteSpace(stdout))
            throw new Exception($"Semantic diagnostics should not be written to stdout.{Environment.NewLine}Actual stdout:{Environment.NewLine}{stdout}");

        if (!stderr.Contains("Semantic check failed.", StringComparison.Ordinal))
            throw new Exception($"Semantic failure stderr did not contain the expected phase banner.{Environment.NewLine}Actual stderr:{Environment.NewLine}{stderr}");

        var declarationDiagnostic = "Use of undeclared error 'error.Fail'. Declare it first with 'error Fail = ...'.";
        var diagnosticCount = CountOccurrences(stderr, declarationDiagnostic);
        if (diagnosticCount != 2)
            throw new Exception($"Expected exactly 2 semantic diagnostic messages for the two distinct source locations, got {diagnosticCount}.{Environment.NewLine}Actual stderr:{Environment.NewLine}{stderr}");

        var firstLocationCount = CountOccurrences(stderr, $"{sampleInput}:2:18:");
        var secondLocationCount = CountOccurrences(stderr, $"{sampleInput}:2:5:");
        if (firstLocationCount != 1 || secondLocationCount != 1)
        {
            throw new Exception(
                $"Expected exactly one rendered diagnostic per source location, got {firstLocationCount} for 2:18 and {secondLocationCount} for 2:5.{Environment.NewLine}Actual stderr:{Environment.NewLine}{stderr}");
        }
    }

    private static void RunExampleCompilationTest(string examplePath)
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
        RunLlvmEmissionTest(exampleDir, examplePath);
    }

    private static ParseGraphResult ParseFile(string path)
    {
        return ImportGraphParser.ParseWithImports(path);
    }

    private static List<string> ReadExpectationLines(string fixtureDir, string fileName)
    {
        var path = Path.Combine(fixtureDir, fileName);
        if (!File.Exists(path))
            return new List<string>();

        return File.ReadAllLines(path, Encoding.UTF8)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
            .ToList();
    }

    private static List<string> ReadExpectationLinesForCurrentHost(string fixtureDir, string fileName)
    {
        var path = ResolveExpectationPathForCurrentHost(fixtureDir, fileName);
        if (!File.Exists(path))
            return new List<string>();

        return File.ReadAllLines(path, Encoding.UTF8)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
            .ToList();
    }

    private static string ResolveExpectationPathForCurrentHost(string fixtureDir, string fileName)
    {
        var genericPath = Path.Combine(fixtureDir, fileName);
        var hostSuffix = OperatingSystem.IsWindows() ? "-windows" : "-linux";
        var hostSpecificFileName = Path.GetFileNameWithoutExtension(fileName) + hostSuffix + Path.GetExtension(fileName);
        var hostSpecificPath = Path.Combine(fixtureDir, hostSpecificFileName);
        return File.Exists(hostSpecificPath) ? hostSpecificPath : genericPath;
    }

    private static void AssertNoErrors(List<string> errors)
    {
        if (errors.Count > 0)
            throw new Exception(string.Join(Environment.NewLine, errors));
    }

    private static void AssertNoWarnings(List<string> warnings)
    {
        if (warnings.Count > 0)
            throw new Exception(string.Join(Environment.NewLine, warnings));
    }

    private static void AssertContains(List<string> diagnostics, string expected, string diagnosticKind)
    {
        if (!diagnostics.Any(diagnostic => diagnostic.Contains(expected, StringComparison.Ordinal)))
            throw new Exception($"Expected {diagnosticKind} containing '{expected}'. Actual:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}");
    }

    private static void AssertTextContains(string text, string expected)
    {
        if (!text.Contains(expected, StringComparison.Ordinal))
            throw new Exception($"Expected generated output containing '{expected}'.");
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static FixturePhase ReadExpectedPhase(string fixtureDir)
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
            _ => throw new Exception($"Unknown expected phase '{text}' in expect-phase.txt")
        };
    }

    private static void AssertPhase(FixturePhase actual, FixturePhase expected, string? failureMessage)
    {
        if (actual != expected)
        {
            var detail = string.IsNullOrEmpty(failureMessage) ? "" : $" {failureMessage}";
            throw new Exception($"Expected phase '{expected}', got '{actual}'.{detail}");
        }
    }

    private static CapturedCompilation CaptureConsole(Func<FixtureCompilation> action)
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

    private static string NormalizeNewlines(string contents)
    {
        if (contents.Length > 0 && contents[0] == '\uFEFF')
            contents = contents[1..];
        return contents.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
    }

    private static void WithTempDirectory(string prefix, Action<string> action)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Exception? actionException = null;

        try
        {
            action(tempDir);
        }
        catch (Exception ex)
        {
            actionException = ex;
            throw;
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch (DirectoryNotFoundException)
                {
                    // A child process may already have completed cleanup of a
                    // disposable test directory; teardown remains successful.
                }
                catch when (actionException != null)
                {
                }
            }
        }
    }

    private static string EnsureToolAvailable(params string[] toolNames)
    {
        try
        {
            return ExternalTools.EnsureToolAvailable(toolNames);
        }
        catch (ZorbCompilerException ex)
        {
            throw new Exception(ex.Message, ex);
        }
    }

    private static bool IsAnyToolAvailable(params string[] toolNames)
    {
        foreach (var toolName in toolNames)
        {
            if (ExternalTools.IsToolAvailable(toolName))
                return true;
        }

        return false;
    }

    private static bool IsBareMetalLinkerAvailable()
    {
        var configured = Environment.GetEnvironmentVariable("ZORB_LLD");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return true;

        if (ExternalTools.IsToolAvailable("ld.lld"))
            return true;

        return ExternalTools.FindAvailableToolByPrefix("ld.lld-") != null;
    }

    private static string? FindAArch64LinuxCompiler()
    {
        var configured = Environment.GetEnvironmentVariable(AArch64CrossCompilerEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
                return Path.GetFullPath(configured);

            throw new Exception($"{AArch64CrossCompilerEnvironmentVariable} points to a missing file: {Path.GetFullPath(configured)}");
        }

        return ExternalTools.FindAvailableTool("aarch64-linux-gnu-gcc");
    }

    private static string? FindAArch64Qemu()
    {
        var configured = Environment.GetEnvironmentVariable(AArch64QemuEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
                return Path.GetFullPath(configured);

            throw new Exception($"{AArch64QemuEnvironmentVariable} points to a missing file: {Path.GetFullPath(configured)}");
        }

        return ExternalTools.FindAvailableTool("qemu-aarch64")
            ?? ExternalTools.FindAvailableTool("qemu-aarch64-static");
    }

    private static string? ResolveAArch64Sysroot()
    {
        var configured = Environment.GetEnvironmentVariable(AArch64SysrootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Directory.Exists(configured))
                return Path.GetFullPath(configured);

            throw new Exception($"{AArch64SysrootEnvironmentVariable} points to a missing directory: {Path.GetFullPath(configured)}");
        }

        return Directory.Exists(DefaultAArch64LinuxSysroot)
            ? DefaultAArch64LinuxSysroot
            : null;
    }

    private static string FindAncestorContainingFile(string startPath, string fileName)
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

    private static string GetProjectRoot()
    {
        var testProjectRoot = FindAncestorContainingFile(AppContext.BaseDirectory, "Zorb.Compiler.Tests.csproj");
        return Directory.GetParent(testProjectRoot)?.Parent?.FullName
            ?? throw new Exception($"Unable to determine repository root from '{testProjectRoot}'.");
    }

    private static string GetFixtureRoot()
    {
        return Path.Combine(GetProjectRoot(), "tests/csharp", "fixtures");
    }

    private static bool CanBuildAArch64LinuxTarget()
    {
        return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 || FindAArch64LinuxCompiler() != null;
    }

    private static bool CanRunAArch64LinuxTarget()
    {
        return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 || FindAArch64Qemu() != null;
    }

    private static ProcessResult RunProcessWithTimeoutArgs(string fileName, IEnumerable<string> arguments, string workingDirectory, TimeSpan timeout)
    {
        try
        {
            var result = ExternalTools.RunProcessWithTimeout(fileName, arguments, workingDirectory, timeout);
            return new ProcessResult(result.ExitCode, result.StdOut, result.StdErr);
        }
        catch (ZorbCompilerException ex)
        {
            throw new Exception(ex.Message, ex);
        }
    }

    private static IReadOnlyList<string> CombineCommandArguments(IReadOnlyList<string> prefix, string arguments)
    {
        var result = new List<string>(prefix);
        result.AddRange(ExternalTools.SplitCommandLine(arguments));
        return result;
    }

    private static IReadOnlyList<string> BuildCommandArguments(CompilerInvocation invocation, params string[] arguments)
    {
        var result = new List<string>(invocation.ArgumentsPrefix);
        result.AddRange(arguments);
        return result;
    }

    private enum FixturePhase
    {
        Success,
        Parse,
        Semantic
    }

    private sealed record FixtureCompilation(
        List<Node> Ast,
        List<string> ParseErrors,
        TypeChecker Checker,
        string Generated,
        FixturePhase Phase,
        string? FailureMessage);

    private sealed record FixtureExpectations(
        FixturePhase ExpectedPhase,
        List<string> ExpectedErrors,
        List<string> ExpectedWarnings);

    private sealed record CapturedCompilation(FixtureCompilation Result, string StdOut, string StdErr);

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

    private sealed record RuntimeExpectation(string TargetName, string? ExpectedStdOut, string? ExpectedStdErr, int ExpectedExit);

    private sealed record CompilerInvocation(string FileName, IReadOnlyList<string> ArgumentsPrefix);

    private sealed record CliWorkflowCase(string FixtureName, string TargetName);

    private sealed record AArch64EmissionCase(string FixtureName, string TargetName, IReadOnlyList<string> ExpectedLlvmIrSubstrings);

    private sealed record AArch64RuntimeCase(string FixtureName, string TargetName);

    private sealed record CheckedFixture(string FixtureDir, string MainPath, TypeChecker Checker, IReadOnlyList<Node> BackendNodes);

    private sealed record BackendInstructionExpectation(string Op, int MinimumCount);

    private sealed record LlvmBackendRegressionCase(
        string FixtureName,
        IReadOnlyList<string> ExpectedBackendIrSubstrings,
        IReadOnlyList<BackendInstructionExpectation> ExpectedInstructionCounts,
        IReadOnlyList<string> ExpectedLlvmIrSubstrings);

    private sealed record CliArgumentCase(string Name, string Arguments, int ExpectedExitCode, string? ExpectedStdOutSubstring, string? ExpectedStdErrSubstring);
}
