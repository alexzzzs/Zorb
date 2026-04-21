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

var fixtureRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures"));
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

var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
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

    var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    var compilerExecutable = GetCompilerExecutablePath(projectRoot);
    if (!File.Exists(compilerExecutable))
        throw new Exception($"Compiler executable was not found at '{compilerExecutable}'.");

    var tempDir = Path.Combine(Path.GetTempPath(), "zorb-cli-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);

    try
    {
        foreach (var fixtureName in GetCliWorkflowFixtureNames())
            RunCliWorkflowFixture(compilerExecutable, projectRoot, fixtureRoot, tempDir, fixtureName);
    }
    finally
    {
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, recursive: true);
    }
}

static string[] GetCliWorkflowFixtureNames()
{
    if (OperatingSystem.IsWindows())
    {
        return
        [
            "runtime_hello_world",
            "runtime_string_escapes",
            "runtime_condition_catch"
        ];
    }

    if (OperatingSystem.IsLinux())
        return ["runtime_hello_world"];

    throw new Exception("CLI workflow tests currently require a Linux or Windows host.");
}

static void RunCliWorkflowFixture(string compilerExecutable, string projectRoot, string fixtureRoot, string tempDir, string fixtureName)
{
    var fixtureDir = Path.Combine(fixtureRoot, fixtureName);
    var mainPath = Path.Combine(fixtureDir, "main.zorb");
    var expectedStdOut = NormalizeNewlines(File.ReadAllText(Path.Combine(fixtureDir, "expect-stdout.txt"), Encoding.UTF8));
    var expectedExit = int.Parse(File.ReadAllText(Path.Combine(fixtureDir, "expect-exit.txt"), Encoding.UTF8).Trim());

    var fixtureTempDir = Path.Combine(tempDir, fixtureName);
    Directory.CreateDirectory(fixtureTempDir);

    var outputFileName = OperatingSystem.IsWindows() ? $"{fixtureName}.exe" : fixtureName;
    var builtBinaryPath = Path.Combine(fixtureTempDir, outputFileName);
    var keptCPath = Path.Combine(fixtureTempDir, $"{fixtureName}.c");

    var build = RunProcess(
        compilerExecutable,
        $"build \"{mainPath}\" -o \"{builtBinaryPath}\" --keep-c \"{keptCPath}\"",
        projectRoot);

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
    if (!string.Equals(actualBuiltStdOut, expectedStdOut, StringComparison.Ordinal))
        throw new Exception($"Built fixture '{fixtureName}' stdout mismatch.{Environment.NewLine}Expected:{Environment.NewLine}{expectedStdOut}{Environment.NewLine}Actual:{Environment.NewLine}{actualBuiltStdOut}");

    var run = RunProcess(
        compilerExecutable,
        $"run \"{mainPath}\"",
        projectRoot);

    if (run.ExitCode != expectedExit)
        throw new Exception($"CLI run for fixture '{fixtureName}' exit code mismatch. Expected {expectedExit}, got {run.ExitCode}.{Environment.NewLine}{run.StdErr}{run.StdOut}".Trim());

    var actualRunStdOut = NormalizeNewlines(run.StdOut);
    if (!string.Equals(actualRunStdOut, expectedStdOut, StringComparison.Ordinal))
        throw new Exception($"CLI run for fixture '{fixtureName}' stdout mismatch.{Environment.NewLine}Expected:{Environment.NewLine}{expectedStdOut}{Environment.NewLine}Actual:{Environment.NewLine}{actualRunStdOut}");
}

static ProcessResult RunBuiltCliBinary(string builtBinaryPath, string workingDirectory)
{
    if (OperatingSystem.IsLinux())
        return RunProcess("timeout", $"30s \"{builtBinaryPath}\"", workingDirectory);

    if (OperatingSystem.IsWindows())
        return RunProcess(builtBinaryPath, "", workingDirectory);

    throw new Exception("CLI workflow tests currently require a Linux or Windows host.");
}

static string GetCompilerExecutablePath(string projectRoot)
{
    var configurationDir = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
        ?? throw new Exception("Unable to determine test output configuration.");
    var targetFrameworkDir = new DirectoryInfo(AppContext.BaseDirectory).Name;
    var executableName = OperatingSystem.IsWindows() ? "Zorb.Compiler.exe" : "Zorb.Compiler";
    return Path.Combine(projectRoot, "Zorb.Compiler", "bin", configurationDir, targetFrameworkDir, executableName);
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
}

static void RunFixture(string fixtureDir, bool updateSnapshots)
{
    var mainPath = Path.Combine(fixtureDir, "main.zorb");
    if (!File.Exists(mainPath))
        throw new Exception("Fixture is missing main.zorb");

    var expectedPhase = ReadExpectedPhase(fixtureDir);
    var expectedErrors = ReadExpectationLines(fixtureDir, "expect-errors.txt");
    var shouldCaptureOutput = expectedPhase != FixturePhase.Success || expectedErrors.Count > 0;
    var compilation = shouldCaptureOutput
        ? CaptureConsole(() => CompileFixture(mainPath, fixtureDir))
        : new CapturedCompilation(CompileFixture(mainPath, fixtureDir), "", "");

    AssertPhase(compilation.Result.Phase, expectedPhase, compilation.Result.FailureMessage);

    var allErrors = new List<string>();
    allErrors.AddRange(compilation.Result.ParseErrors);
    allErrors.AddRange(compilation.Result.Checker.Errors.Errors);
    if (!string.IsNullOrEmpty(compilation.Result.FailureMessage))
        allErrors.Add(compilation.Result.FailureMessage);

    if (expectedPhase != FixturePhase.Success || expectedErrors.Count > 0)
    {
        foreach (var expected in expectedErrors)
            AssertContains(allErrors, expected);
        return;
    }

    AssertNoErrors(allErrors);

    var generated = compilation.Result.Generated;

    foreach (var expected in ReadExpectationLines(fixtureDir, "expect-generated.txt"))
        AssertTextContains(generated, expected);

    var snapshotPath = Path.Combine(fixtureDir, "expect-generated-full.c");
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

    foreach (var line in ReadExpectationLines(fixtureDir, "expect-generated-counts.txt"))
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
    var ast = ParseFile(mainPath, out var parseErrors);
    if (parseErrors.Count > 0)
        return new FixtureCompilation(ast, parseErrors, new TypeChecker(), "", FixturePhase.Parse, null);

    var checker = new TypeChecker();
    checker.Check(ast, fixtureDir);
    if (checker.Errors.Errors.Count > 0)
        return new FixtureCompilation(ast, parseErrors, checker, "", FixturePhase.Semantic, null);

    try
    {
        var generator = new CGenerator(fixtureDir, checker.SymbolTable);
        var generated = generator.Generate(ast);
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

    var runtimeCompilation = CompileRuntimeFixture(mainPath, fixtureDir);
    AssertNoErrors(runtimeCompilation.ParseErrors);
    AssertNoErrors(runtimeCompilation.Checker.Errors.Errors);

    foreach (var runtimeExpectation in runtimeExpectations)
    {
        RunRuntimeExpectation(fixtureDir, runtimeCompilation.Generated, runtimeExpectation);
    }
}

static FixtureCompilation CompileRuntimeFixture(string mainPath, string fixtureDir)
{
    var ast = ParseFile(mainPath, out var parseErrors);
    if (parseErrors.Count > 0)
        return new FixtureCompilation(ast, parseErrors, new TypeChecker(), "", FixturePhase.Parse, null);

    var checker = new TypeChecker();
    checker.Check(ast, fixtureDir);
    if (checker.Errors.Errors.Count > 0)
        return new FixtureCompilation(ast, parseErrors, checker, "", FixturePhase.Semantic, null);

    try
    {
        var generator = new CGenerator(fixtureDir, checker.SymbolTable)
        {
            PreserveStart = true,
            NoStdLib = true
        };
        var generated = generator.Generate(ast);
        return new FixtureCompilation(ast, parseErrors, checker, generated, FixturePhase.Success, null);
    }
    catch (Exception ex)
    {
        return new FixtureCompilation(ast, parseErrors, checker, "", FixturePhase.Codegen, ex.Message);
    }
}

static List<Node> ParseFile(string path, out List<string> errors)
{
    var parseResult = ImportGraphParser.ParseWithImports(path);
    errors = parseResult.Errors;
    return parseResult.EntryNodes;
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

static void AssertNoErrors(List<string> errors)
{
    if (errors.Count > 0)
        throw new Exception(string.Join(Environment.NewLine, errors));
}

static void AssertContains(List<string> errors, string expected)
{
    if (!errors.Any(error => error.Contains(expected, StringComparison.Ordinal)))
        throw new Exception($"Expected error containing '{expected}'. Actual:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
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
    var hostExitPath = Path.Combine(fixtureDir, "expect-exit.txt");
    if (File.Exists(hostStdOutPath) || File.Exists(hostExitPath))
    {
        expectations.Add(new RuntimeExpectation(
            "host-linux",
            File.Exists(hostStdOutPath) ? NormalizeNewlines(File.ReadAllText(hostStdOutPath, Encoding.UTF8)) : null,
            File.Exists(hostExitPath) ? int.Parse(File.ReadAllText(hostExitPath, Encoding.UTF8).Trim()) : 0));
    }

    var aarch64StdOutPath = Path.Combine(fixtureDir, "expect-stdout-aarch64.txt");
    var aarch64ExitPath = Path.Combine(fixtureDir, "expect-exit-aarch64.txt");
    if (File.Exists(aarch64StdOutPath) || File.Exists(aarch64ExitPath))
    {
        expectations.Add(new RuntimeExpectation(
            "linux-aarch64",
            File.Exists(aarch64StdOutPath) ? NormalizeNewlines(File.ReadAllText(aarch64StdOutPath, Encoding.UTF8)) : null,
            File.Exists(aarch64ExitPath) ? int.Parse(File.ReadAllText(aarch64ExitPath, Encoding.UTF8).Trim()) : 0));
    }

    return expectations;
}

static void RunRuntimeExpectation(string fixtureDir, string generated, RuntimeExpectation runtimeExpectation)
{
    if (!OperatingSystem.IsLinux())
        throw new Exception($"Runtime fixture execution for target '{runtimeExpectation.TargetName}' is currently supported only on Linux hosts.");

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

        if (runtimeExpectation.TargetName == "host-linux")
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
        else
        {
            throw new Exception($"Unknown runtime target '{runtimeExpectation.TargetName}'.");
        }

        var actualStdOut = NormalizeNewlines(execution.StdOut);

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

static ProcessResult RunProcess(string fileName, string arguments, string workingDirectory)
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
    var stdOut = process.StandardOutput.ReadToEnd();
    var stdErr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return new ProcessResult(process.ExitCode, stdOut, stdErr);
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

sealed record RuntimeExpectation(string TargetName, string? ExpectedStdOut, int ExpectedExit);
