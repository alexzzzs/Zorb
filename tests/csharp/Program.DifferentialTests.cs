using System.Text.Json;
using System.Text.RegularExpressions;

internal static partial class Program
{
    private const int DifferentialTimeoutSeconds = 30;
    private const string FrontendParityCaseEnvironmentVariable = "ZORB_FRONTEND_PARITY_CASE";

    private sealed record DifferentialCase(string Name, string InputPath);

    private sealed record NormalizedDiagnostic(
        string Phase,
        string Code,
        string File,
        int Line,
        int Column,
        int Length);

    private static void RunFrontendDifferentialTests(string fixtureRoot)
    {
        if (!OperatingSystem.IsLinux())
            return;

        var projectRoot = GetProjectRoot();
        var compilerInvocation = GetCompilerInvocation(projectRoot);
        var selfCheckSource = Path.Combine(projectRoot, "compiler", "self-check", "main.zorb");
        var cases = SelectFrontendParityCases(LoadFrontendParityCases(projectRoot))
            .Select(item => new DifferentialCase(item.Name, item.InputPath))
            .ToArray();

        WithTempDirectory("zorb-differential", tempDir =>
        {
            var nativePath = Path.Combine(tempDir, "zorb-self-check");
            var build = RunProcessWithTimeoutArgs(
                compilerInvocation.FileName,
                BuildCommandArguments(compilerInvocation, "build", selfCheckSource, "--target", "host-linux", "-o", nativePath),
                projectRoot,
                TimeSpan.FromSeconds(DifferentialTimeoutSeconds));
            if (build.ExitCode != 0 || !File.Exists(nativePath))
                throw new Exception($"Unable to build native differential frontend.\n{build.StdErr}{build.StdOut}".Trim());

            foreach (var parityCase in cases)
                AssertFrontendParity(compilerInvocation, nativePath, projectRoot, parityCase);
        });
    }

    // Keeping this filter in the harness (rather than changing the manifest)
    // makes one-fixture debugging reproducible without accidentally reducing
    // the normal CI gate. For example: ZORB_FRONTEND_PARITY_CASE=simple.
    private static IReadOnlyList<FixtureParityCase> SelectFrontendParityCases(IReadOnlyList<FixtureParityCase> cases)
    {
        var requestedName = Environment.GetEnvironmentVariable(FrontendParityCaseEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(requestedName))
            return cases;

        var selected = cases
            .Where(parityCase => string.Equals(parityCase.Name, requestedName, StringComparison.Ordinal))
            .ToArray();
        if (selected.Length != 1)
            throw new Exception($"No enabled frontend parity case named '{requestedName}'.");
        return selected;
    }

    private static void AssertFrontendParity(
        CompilerInvocation compilerInvocation,
        string nativePath,
        string projectRoot,
        DifferentialCase parityCase)
    {
        var stageZero = RunProcessWithTimeoutArgs(
            compilerInvocation.FileName,
            BuildCommandArguments(compilerInvocation, parityCase.InputPath, "--check"),
            projectRoot,
            TimeSpan.FromSeconds(DifferentialTimeoutSeconds));
        var native = RunProcessWithTimeoutArgs(
            nativePath,
            ["--json", parityCase.InputPath],
            projectRoot,
            TimeSpan.FromSeconds(DifferentialTimeoutSeconds));

        var stageZeroDiagnostic = NormalizeStageZeroDiagnostic(stageZero);
        var nativeDiagnostic = NormalizeNativeDiagnostic(native);
        var stageZeroSucceeded = stageZero.ExitCode == 0;
        var nativeSucceeded = native.ExitCode == 0;
        if (stageZeroSucceeded != nativeSucceeded)
        {
            throw new Exception(
                $"Differential case '{parityCase.Name}' disagreed on success. Stage 0 exit {stageZero.ExitCode}; native exit {native.ExitCode}.\n" +
                $"Stage 0 stderr:\n{stageZero.StdErr}\nNative stdout:\n{native.StdOut}\nNative stderr:\n{native.StdErr}");
        }

        if (stageZeroSucceeded)
            return;
        if (stageZeroDiagnostic == null || nativeDiagnostic == null)
        {
            throw new Exception(
                $"Differential case '{parityCase.Name}' did not provide comparable diagnostics.\n" +
                $"Stage 0 stderr:\n{stageZero.StdErr}\nNative stdout:\n{native.StdOut}\nNative stderr:\n{native.StdErr}");
        }

        AssertEqual(parityCase.Name, "phase", stageZeroDiagnostic.Phase, nativeDiagnostic.Phase);
        AssertEqual(parityCase.Name, "code", stageZeroDiagnostic.Code, nativeDiagnostic.Code);
        AssertEqual(parityCase.Name, "file", NormalizeDiagnosticPath(stageZeroDiagnostic.File), NormalizeDiagnosticPath(nativeDiagnostic.File));
        if (!SpansOverlap(stageZeroDiagnostic, nativeDiagnostic))
        {
            throw new Exception(
                $"Differential case '{parityCase.Name}' reported non-overlapping spans: " +
                $"stage 0 {stageZeroDiagnostic.Line}:{stageZeroDiagnostic.Column}+{stageZeroDiagnostic.Length}, " +
                $"native {nativeDiagnostic.Line}:{nativeDiagnostic.Column}+{nativeDiagnostic.Length}.");
        }
    }

    private static NormalizedDiagnostic? NormalizeStageZeroDiagnostic(ProcessResult result)
    {
        var output = StripAnsi(NormalizeNewlines(result.StdErr));
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var phase = output.Contains("Parse failed.", StringComparison.Ordinal) ? "parse" : "semantic";
        // The native frontend uses `type.invalid` as its stable fallback for
        // semantic errors that do not yet have a narrower category.
        var code = phase == "parse" ? "parse.invalid-syntax" : "type.invalid";
        if (output.Contains("Unsupported escape sequence", StringComparison.Ordinal) || output.Contains("Unterminated string literal", StringComparison.Ordinal))
        {
            phase = "lexical";
            code = "lex.invalid-token";
        }
        if (output.Contains("Array literal for type", StringComparison.Ordinal))
            code = "type.array-element-count";
        if (output.Contains("Match over bool must cover both values", StringComparison.Ordinal))
            code = "flow.match-not-exhaustive";
        if (output.Contains("Match over enum", StringComparison.Ordinal))
            code = "flow.match-not-exhaustive";
        if (output.Contains("Match over union", StringComparison.Ordinal))
            code = "flow.match-not-exhaustive";
        if (output.Contains("Condition must have type 'bool'", StringComparison.Ordinal))
            code = "type.condition-not-bool";
        if (IsStageZeroUnknownNameDiagnostic(output))
            code = "name.unknown";
        // `sizeof` resolves a named type operand, unlike a generic argument
        // validation failure, so retain the native frontend's unknown-name
        // category for that narrower Stage 0 wording.
        if (output.Contains("Unknown type '", StringComparison.Ordinal) && output.Contains("Builtin.sizeof", StringComparison.Ordinal))
            code = "name.unknown";
        // Invalid error declarations can produce downstream undeclared-error
        // messages; Stage 0's primary diagnostic remains the declaration.
        if (output.Contains("Error declaration '", StringComparison.Ordinal))
            code = "type.invalid";
        if (output.Contains("Import file not found:", StringComparison.Ordinal))
        {
            phase = "import";
            code = "import.not-found";
        }

        var match = Regex.Match(output, @"^(?<file>.+?):(?<line>\d+):(?<column>\d+):", RegexOptions.Multiline);
        if (!match.Success)
            return null;
        return new NormalizedDiagnostic(
            phase,
            code,
            match.Groups["file"].Value,
            int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(match.Groups["column"].Value, System.Globalization.CultureInfo.InvariantCulture),
            1);
    }

    private static bool IsStageZeroUnknownNameDiagnostic(string output) =>
        output.Contains("Use of undeclared identifier", StringComparison.Ordinal) ||
        output.Contains("Use of undeclared error", StringComparison.Ordinal) ||
        output.Contains("is not visible from this file", StringComparison.Ordinal);

    private static NormalizedDiagnostic? NormalizeNativeDiagnostic(ProcessResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.StdErr))
            return null;

        foreach (var line in NormalizeNewlines(result.StdOut).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.GetProperty("kind").GetString() != "diagnostic")
                continue;
            return new NormalizedDiagnostic(
                root.GetProperty("phase").GetString() ?? "",
                root.GetProperty("code").GetString() ?? "",
                root.GetProperty("file").GetString() ?? "",
                root.GetProperty("line").GetInt32(),
                root.GetProperty("column").GetInt32(),
                root.GetProperty("length").GetInt32());
        }
        return null;
    }

    private static bool SpansOverlap(NormalizedDiagnostic left, NormalizedDiagnostic right)
    {
        if (left.Line != right.Line)
            return false;
        var leftEnd = left.Column + Math.Max(1, left.Length);
        var rightEnd = right.Column + Math.Max(1, right.Length);
        return left.Column < rightEnd && right.Column < leftEnd;
    }

    private static string NormalizeDiagnosticPath(string path) => Path.GetFullPath(path);

    private static string StripAnsi(string text) => Regex.Replace(text, "\\u001B\\[[0-?]*[ -/]*[@-~]", "");

    private static void AssertEqual(string caseName, string field, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Exception($"Differential case '{caseName}' disagreed on {field}: stage 0 '{expected}', native '{actual}'.");
    }
}
