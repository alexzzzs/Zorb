using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Zorb.Compiler.AST;
using Zorb.Compiler.AST.Expressions;
using Zorb.Compiler.AST.Statements;
using Zorb.Compiler.Codegen;
using Zorb.Compiler.Lexer;
using Zorb.Compiler.Parser;
using Zorb.Compiler.Parsing;
using Zorb.Compiler.Semantic;
using Zorb.Compiler.Utils;

const string RunAArch64TestsEnvironmentVariable = "ZORB_RUN_AARCH64_TESTS";
const string AArch64CrossCompilerEnvironmentVariable = "ZORB_AARCH64_LINUX_GCC";
const string AArch64QemuEnvironmentVariable = "ZORB_QEMU_AARCH64";
const string AArch64SysrootEnvironmentVariable = "ZORB_AARCH64_LINUX_SYSROOT";
const string DefaultAArch64LinuxSysroot = "/usr/aarch64-linux-gnu";

var testProjectRoot = FindAncestorContainingFile(AppContext.BaseDirectory, "Zorb.Compiler.Tests.csproj");
var fixtureRoot = Path.Combine(testProjectRoot, "fixtures");
var fixtureDirs = Directory.GetDirectories(fixtureRoot).OrderBy(path => path, StringComparer.Ordinal).ToList();
var failures = new List<string>();

foreach (var fixtureDir in fixtureDirs)
    RunNamedTest(Path.GetFileName(fixtureDir), () => RunFixture(fixtureDir));

RunNamedTest("cli_workflow", () => RunCliWorkflowTests(fixtureRoot));
RunNamedTest("aarch64_linux_targets", () => RunAArch64LinuxCrossTargetTests(fixtureRoot));
RunNamedTest("cli_bare_metal", () => RunBareMetalCliBuildTests(fixtureRoot));
RunNamedTest("cli_args", () => RunCliArgumentValidationTests(fixtureRoot));
RunNamedTest("semantic_output", () => RunSemanticDiagnosticOutputTests(fixtureRoot));
RunNamedTest("type_checker_state_reset", RunTypeCheckerStateResetTests);
RunNamedTest("llvm_writer_state_reset", () => RunLlvmWriterStateResetTests(fixtureRoot));
RunNamedTest("llvm_backend_output_modes", () => RunLlvmBackendOutputModeTests(fixtureRoot));
RunNamedTest("llvm_backend_regressions", () => RunLlvmBackendRegressionTests(fixtureRoot));
RunNamedTest("unknown_type_cascade", RunUnknownTypeCascadeTests);
RunNamedTest("invalid_postfix_cascade", RunInvalidPostfixCascadeTests);
RunNamedTest("builtin_parser_reserved_declarations", RunBuiltinParserReservedDeclarationTests);
RunNamedTest("resolved_call_metadata", RunResolvedCallMetadataTests);

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
    RunNamedTest(Path.GetRelativePath(projectRoot, examplePath), () => RunExampleCompilationTest(examplePath));

if (failures.Count > 0)
{
    Console.Error.WriteLine();
    foreach (var failure in failures)
        Console.Error.WriteLine(failure);
    return 1;
}

return 0;

void RunNamedTest(string testName, Action action)
{
    try
    {
        action();
        Console.WriteLine($"PASS {testName}");
    }
    catch (Exception ex)
    {
        failures.Add($"{testName}: {ex.Message}");
        Console.WriteLine($"FAIL {testName}");
    }
}

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

    WithTempDirectory("zorb-cli-tests", tempDir =>
    {
        foreach (var workflowCase in GetCliWorkflowCases())
            RunCliWorkflowFixture(compilerInvocation, projectRoot, fixtureRoot, tempDir, workflowCase);
    });
}

static void RunAArch64LinuxCrossTargetTests(string fixtureRoot)
{
    if (!OperatingSystem.IsLinux())
        return;

    var shouldRun = string.Equals(
        Environment.GetEnvironmentVariable(RunAArch64TestsEnvironmentVariable),
        "1",
        StringComparison.Ordinal);

    var linuxCompiler = FindAArch64LinuxCompiler();
    var qemu = FindAArch64Qemu();
    var isNativeAArch64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    var canBuild = isNativeAArch64 || linuxCompiler != null;
    var canRun = isNativeAArch64 || qemu != null;

    if (!shouldRun && (!canBuild || !canRun))
        return;

    if (!canBuild)
    {
        throw new Exception(
            $"AArch64 Linux target tests require either an aarch64 host or an AArch64 cross-compiler. Install 'aarch64-linux-gnu-gcc' or set {AArch64CrossCompilerEnvironmentVariable}.");
    }

    if (!canRun)
    {
        throw new Exception(
            $"AArch64 Linux target tests require either an aarch64 host or qemu-aarch64. Install 'qemu-user' or set {AArch64QemuEnvironmentVariable}.");
    }

    EnsureToolAvailable("timeout");

    var testProjectRoot = FindAncestorContainingFile(AppContext.BaseDirectory, "Zorb.Compiler.Tests.csproj");
    var projectRoot = Directory.GetParent(testProjectRoot)?.FullName
        ?? throw new Exception($"Unable to determine repository root from '{testProjectRoot}'.");
    var compilerInvocation = GetCompilerInvocation(projectRoot);

    RunAArch64EmissionVerification(compilerInvocation, projectRoot, fixtureRoot);

    WithTempDirectory("zorb-aarch64-cli-tests", tempDir =>
    {
        foreach (var workflowCase in GetAArch64CliWorkflowCases())
            RunCliWorkflowFixture(compilerInvocation, projectRoot, fixtureRoot, tempDir, workflowCase);
    });
}

static void RunCliArgumentValidationTests(string fixtureRoot)
{
    var testProjectRoot = FindAncestorContainingFile(AppContext.BaseDirectory, "Zorb.Compiler.Tests.csproj");
    var projectRoot = Directory.GetParent(testProjectRoot)?.FullName
        ?? throw new Exception($"Unable to determine repository root from '{testProjectRoot}'.");
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

static void RunSemanticDiagnosticOutputTests(string fixtureRoot)
{
    var testProjectRoot = FindAncestorContainingFile(AppContext.BaseDirectory, "Zorb.Compiler.Tests.csproj");
    var projectRoot = Directory.GetParent(testProjectRoot)?.FullName
        ?? throw new Exception($"Unable to determine repository root from '{testProjectRoot}'.");
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

static void RunLlvmWriterStateResetTests(string fixtureRoot)
{
    var fixtureDir = Path.Combine(fixtureRoot, "runtime_hello_world");
    var mainPath = Path.Combine(fixtureDir, "main.zorb");
    var parseResult = ParseFile(mainPath);
    AssertNoErrors(parseResult.Errors);

    var checker = new TypeChecker();
    checker.Check(parseResult.EntryNodes, fixtureDir, parseResult.Files);
    AssertNoErrors(checker.Errors.Errors);

    var start = parseResult.EntryNodes
        .OfType<FunctionDecl>()
        .Single(function => string.Equals(function.Name, "_start", StringComparison.Ordinal));
    var backendNodes = parseResult.Files.Values
        .SelectMany(nodes => nodes)
        .Distinct<Node>(ReferenceEqualityComparer.Instance)
        .ToList();
    var writer = new ZigBackendIrWriter(checker);
    var target = new ZigBackendTarget("x86_64-pc-linux-gnu");

    _ = writer.Write(
        backendNodes,
        "writer_state_first",
        target,
        ZigBackendOutputKind.Object,
        "writer-state-first.o",
        addHostedEntryShim: true);
    if (!string.Equals(start.Name, "_start", StringComparison.Ordinal))
        throw new Exception("Hosted LLVM entry lowering mutated the checked _start declaration.");

    _ = writer.Write(
        backendNodes,
        "writer_state_second",
        target,
        ZigBackendOutputKind.Object,
        "writer-state-second.o",
        addHostedEntryShim: true);
    if (!string.Equals(start.Name, "_start", StringComparison.Ordinal))
        throw new Exception("Repeated hosted LLVM entry lowering mutated the checked _start declaration.");
}

static void RunLlvmBackendOutputModeTests(string fixtureRoot)
{
    var fixture = LoadCheckedFixture(Path.Combine(fixtureRoot, "snapshot_minimal_program"));
    var backendPath = GetLlvmBackendPath();

    WithTempDirectory("zorb-llvm-output-modes", tempDir =>
    {
        var outputModes = new[]
        {
            (Kind: ZigBackendOutputKind.LlvmIr, Extension: ".ll"),
            (Kind: ZigBackendOutputKind.Bitcode, Extension: ".bc"),
            (Kind: ZigBackendOutputKind.Object, Extension: OperatingSystem.IsWindows() ? ".obj" : ".o"),
            (Kind: ZigBackendOutputKind.Assembly, Extension: ".s")
        };

        foreach (var outputMode in outputModes)
        {
            var outputPath = Path.Combine(tempDir, "out" + outputMode.Extension);
            var backendIrPath = Path.Combine(tempDir, outputMode.Kind + ".json");
            var backendIr = WriteBackendIr(
                fixture,
                "output_mode_" + outputMode.Kind,
                GetNativeLlvmTriple(),
                outputMode.Kind,
                outputPath);
            File.WriteAllText(backendIrPath, backendIr, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var emission = EmitBackendArtifact(backendPath, backendIrPath, tempDir);
            if (emission.ExitCode != 0)
            {
                throw new Exception(
                    $"Backend {outputMode.Kind} emission failed with exit code {emission.ExitCode}.{Environment.NewLine}{emission.StdErr}{emission.StdOut}".Trim());
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new Exception($"Backend {outputMode.Kind} emission did not produce non-empty output.");

            if (outputMode.Kind == ZigBackendOutputKind.LlvmIr)
            {
                var llvmIr = File.ReadAllText(outputPath, Encoding.UTF8);
                AssertTextContains(llvmIr, "define");
                AssertTextContains(llvmIr, "target triple");
            }
        }
    });
}

static void RunLlvmBackendRegressionTests(string fixtureRoot)
{
    var backendPath = GetLlvmBackendPath();

    WithTempDirectory("zorb-llvm-regressions", tempDir =>
    {
        foreach (var regressionCase in GetLlvmBackendRegressionCases())
        {
            var fixture = LoadCheckedFixture(Path.Combine(fixtureRoot, regressionCase.FixtureName));
            var outputPath = Path.Combine(tempDir, regressionCase.FixtureName + ".ll");
            var backendIrPath = Path.Combine(tempDir, regressionCase.FixtureName + ".json");
            var backendIr = WriteBackendIr(
                fixture,
                "regression_" + regressionCase.FixtureName,
                GetNativeLlvmTriple(),
                ZigBackendOutputKind.LlvmIr,
                outputPath);
            File.WriteAllText(backendIrPath, backendIr, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            foreach (var expectedBackendText in regressionCase.ExpectedBackendIrSubstrings)
                AssertTextContains(backendIr, expectedBackendText);
            foreach (var expectedInstruction in regressionCase.ExpectedInstructionCounts)
            {
                AssertBackendInstructionCountAtLeast(
                    backendIr,
                    regressionCase.FixtureName,
                    expectedInstruction.Op,
                    expectedInstruction.MinimumCount);
            }

            var emission = EmitBackendArtifact(backendPath, backendIrPath, tempDir);
            if (emission.ExitCode != 0)
            {
                throw new Exception(
                    $"Backend regression emission failed for fixture '{regressionCase.FixtureName}' with exit code {emission.ExitCode}.{Environment.NewLine}{emission.StdErr}{emission.StdOut}".Trim());
            }

            var llvmIr = File.ReadAllText(outputPath, Encoding.UTF8);
            foreach (var expectedLlvmText in regressionCase.ExpectedLlvmIrSubstrings)
                AssertTextContains(llvmIr, expectedLlvmText);
        }
    });
}

static CheckedFixture LoadCheckedFixture(string fixtureDir)
{
    var mainPath = Path.Combine(fixtureDir, "main.zorb");
    var parseResult = ParseFile(mainPath);
    AssertNoErrors(parseResult.Errors);

    var checker = new TypeChecker();
    checker.Check(parseResult.EntryNodes, fixtureDir, parseResult.Files);
    AssertNoErrors(checker.Errors.Errors);

    var backendNodes = parseResult.Files.Values
        .SelectMany(nodes => nodes)
        .Distinct<Node>(ReferenceEqualityComparer.Instance)
        .ToList();
    return new CheckedFixture(fixtureDir, mainPath, checker, backendNodes);
}

static string WriteBackendIr(
    CheckedFixture fixture,
    string moduleName,
    string targetTriple,
    ZigBackendOutputKind outputKind,
    string outputPath)
{
    var writer = new ZigBackendIrWriter(fixture.Checker);
    return writer.Write(
        fixture.BackendNodes,
        moduleName,
        new ZigBackendTarget(targetTriple),
        outputKind,
        outputPath);
}

static ProcessResult EmitBackendArtifact(string backendPath, string backendIrPath, string workingDirectory)
{
    return RunProcessWithTimeoutArgs(
        backendPath,
        [backendIrPath],
        workingDirectory,
        TimeSpan.FromSeconds(30));
}

static void AssertBackendInstructionCountAtLeast(string backendIr, string fixtureName, string op, int minimumCount)
{
    using var document = JsonDocument.Parse(backendIr);
    var actualCount = CountBackendInstructions(document.RootElement, op);
    if (actualCount < minimumCount)
    {
        throw new Exception(
            $"Fixture '{fixtureName}' expected at least {minimumCount} backend '{op}' instructions, got {actualCount}.{Environment.NewLine}{backendIr}");
    }
}

static int CountBackendInstructions(JsonElement module, string op)
{
    var count = 0;
    if (!module.TryGetProperty("functions", out var functions))
        return count;

    foreach (var function in functions.EnumerateArray())
    {
        if (!function.TryGetProperty("blocks", out var blocks))
            continue;

        foreach (var block in blocks.EnumerateArray())
        {
            if (!block.TryGetProperty("instructions", out var instructions))
                continue;

            foreach (var instruction in instructions.EnumerateArray())
            {
                if (instruction.TryGetProperty("op", out var opValue) &&
                    string.Equals(opValue.GetString(), op, StringComparison.Ordinal))
                {
                    count++;
                }
            }
        }
    }

    return count;
}

static LlvmBackendRegressionCase[] GetLlvmBackendRegressionCases()
{
    return
    [
        new LlvmBackendRegressionCase(
            "runtime_generic_union",
            ["Result$i64$bool"],
            [new BackendInstructionExpectation("extract_value", 2), new BackendInstructionExpectation("aggregate", 1)],
            ["extractvalue"]),
        new LlvmBackendRegressionCase(
            "runtime_generic_enum",
            ["Token$i64"],
            [new BackendInstructionExpectation("compare", 2)],
            ["icmp eq"]),
        new LlvmBackendRegressionCase(
            "runtime_numeric_match",
            [],
            [new BackendInstructionExpectation("compare", 2)],
            ["icmp eq"]),
        new LlvmBackendRegressionCase(
            "runtime_bool_match",
            [],
            [new BackendInstructionExpectation("compare", 2)],
            ["icmp eq"]),
        new LlvmBackendRegressionCase(
            "runtime_generic_inference_and_coercions",
            [],
            [new BackendInstructionExpectation("index_address", 4)],
            ["getelementptr"])
    ];
}

static string GetNativeLlvmTriple()
{
    var architecture = RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "aarch64",
        _ => throw new Exception($"LLVM backend output-mode tests do not support architecture '{RuntimeInformation.ProcessArchitecture}'.")
    };

    if (OperatingSystem.IsLinux())
        return $"{architecture}-pc-linux-gnu";
    if (OperatingSystem.IsWindows())
        return $"{architecture}-pc-windows-msvc";

    throw new Exception("LLVM backend output-mode tests require Linux or Windows.");
}

static string GetLlvmBackendPath()
{
    var configuredPath = Environment.GetEnvironmentVariable("ZORB_LLVM_BACKEND");
    if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        return Path.GetFullPath(configuredPath);

    var executableName = OperatingSystem.IsWindows() ? "zorb-llvm-backend.exe" : "zorb-llvm-backend";
    var candidate = Path.Combine(GetProjectRoot(), "Zorb.LlvmBackend", "zig-out", "bin", executableName);
    if (File.Exists(candidate))
        return candidate;

    throw new Exception("LLVM backend executable was not found. Build Zorb.LlvmBackend or set ZORB_LLVM_BACKEND.");
}

static void RunTypeCheckerStateResetTests()
{
    WithTempDirectory("zorb-typechecker-state", tempDir =>
    {
        var firstPath = Path.Combine(tempDir, "first.zorb");
        var secondPath = Path.Combine(tempDir, "second.zorb");

        File.WriteAllText(firstPath, """
const Error_First: i32 = 1

fn first() -> i32 {
    return Error_First
}
""");

        File.WriteAllText(secondPath, """
fn second() -> i32 {
    return missing_symbol
}
""");

        var checker = new TypeChecker();
        var firstParse = ParseFile(firstPath);
        checker.Check(firstParse.EntryNodes, tempDir, firstParse.Files);
        AssertNoErrors(firstParse.Errors);
        AssertNoErrors(checker.Errors.Errors);

        var secondParse = ParseFile(secondPath);
        checker.Check(secondParse.EntryNodes, tempDir, secondParse.Files);
        AssertNoErrors(secondParse.Errors);

        if (!checker.Errors.Errors.Any(error => error.Contains("Use of undeclared identifier 'missing_symbol'.", StringComparison.Ordinal)))
            throw new Exception($"Expected missing symbol diagnostic after reused checker reset.{Environment.NewLine}{string.Join(Environment.NewLine, checker.Errors.Errors)}");

        if (checker.Errors.Errors.Any(error => error.Contains("Error_First", StringComparison.Ordinal)))
            throw new Exception($"TypeChecker leaked diagnostics or symbols from the first check.{Environment.NewLine}{string.Join(Environment.NewLine, checker.Errors.Errors)}");
    });
}

static void RunUnknownTypeCascadeTests()
{
    WithTempDirectory("zorb-unknown-type-cascade", tempDir =>
    {
        var mainPath = Path.Combine(tempDir, "main.zorb");
        File.WriteAllText(mainPath, """
fn main() -> i64 {
    if missing.field {
        return 1
    }

    return 0
}
""");

        var compilation = CompileFixture(mainPath, tempDir);
        AssertPhase(compilation.Phase, FixturePhase.Semantic, compilation.FailureMessage);

        var errors = compilation.Checker.Errors.Errors;
        if (!errors.Any(error => error.Contains("Use of undeclared identifier 'missing'.", StringComparison.Ordinal)))
            throw new Exception($"Expected undeclared identifier diagnostic.{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");

        if (errors.Any(error => error.Contains("Condition must have type 'bool'", StringComparison.Ordinal)))
            throw new Exception($"Unexpected bool-condition follow-on diagnostic for unknown field target.{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
    });
}

static void RunInvalidPostfixCascadeTests()
{
    WithTempDirectory("zorb-invalid-postfix-cascade", tempDir =>
    {
        var mainPath = Path.Combine(tempDir, "main.zorb");
        var source = """
fn main() -> i64 {
    return , .field(1)[0]
}
""";

        var lexer = new Lexer(source, mainPath);
        var parser = new Parser(lexer.Tokenize(), mainPath);
        var ast = parser.ParseProgram();

        if (parser.ErrorReporter.Errors.Count == 0)
            throw new Exception("Expected parser to report the invalid expression.");

        var checker = new TypeChecker();
        checker.Check(ast, tempDir);

        if (checker.Errors.Errors.Count != 0)
        {
            throw new Exception(
                $"Expected no semantic follow-on diagnostics for invalid postfix target.{Environment.NewLine}{string.Join(Environment.NewLine, checker.Errors.Errors)}");
        }
    });
}

static void RunBuiltinParserReservedDeclarationTests()
{
    var compileErrorDecl = new FunctionDecl
    {
        NamespacePath = ["Builtin"],
        Name = "CompileError",
        ReturnType = new TypeNode { Name = "void" }
    };
    var sizeofDecl = new FunctionDecl
    {
        NamespacePath = ["Builtin"],
        Name = "sizeof",
        ReturnType = new TypeNode { Name = "i64" }
    };

    var checker = new TypeChecker();
    checker.Check([compileErrorDecl, sizeofDecl]);

    AssertContains(
        checker.Errors.Errors,
        "Top-level declaration 'Builtin.CompileError' conflicts with a built-in symbol.",
        "error");
    AssertContains(
        checker.Errors.Errors,
        "Top-level declaration 'Builtin.sizeof' conflicts with a built-in symbol.",
        "error");
}

static void RunResolvedCallMetadataTests()
{
    var testProjectRoot = FindAncestorContainingFile(AppContext.BaseDirectory, "Zorb.Compiler.Tests.csproj");
    var fixtureRoot = Path.Combine(testProjectRoot, "fixtures");

    AssertResolvedCallMetadata(
        Path.Combine(fixtureRoot, "import_alias_function_value"),
        expectedQualifiedName: "use",
        expectedTargetQualifiedName: null,
        expectedParamCount: 1);

    AssertResolvedCallMetadata(
        Path.Combine(fixtureRoot, "import_alias_callable_variable_call"),
        expectedQualifiedName: null,
        expectedTargetQualifiedName: "cb",
        expectedParamCount: 1);

    AssertResolvedCallMetadata(
        Path.Combine(fixtureRoot, "import_alias_qualified_call"),
        expectedQualifiedName: null,
        expectedTargetQualifiedName: "module.answer",
        expectedParamCount: 0);
}

static void AssertResolvedCallMetadata(string fixtureDir, string? expectedQualifiedName, string? expectedTargetQualifiedName, int expectedParamCount)
{
    var mainPath = Path.Combine(fixtureDir, "main.zorb");
    var compilation = CompileFixture(mainPath, fixtureDir);
    AssertPhase(compilation.Phase, FixturePhase.Success, compilation.FailureMessage);
    AssertNoErrors(compilation.ParseErrors);
    AssertNoErrors(compilation.Checker.Errors.Errors);

    var mainFunction = compilation.Ast.OfType<FunctionDecl>().FirstOrDefault(fn => string.Equals(fn.Name, "main", StringComparison.Ordinal))
        ?? throw new Exception($"Expected fixture '{fixtureDir}' to define a main function.");
    var call = FindFirstCallInStatements(mainFunction.Body)
        ?? throw new Exception($"Expected to find a call expression in main() for fixture '{fixtureDir}'.");

    if (!string.Equals(call.ResolvedQualifiedName, expectedQualifiedName, StringComparison.Ordinal))
        throw new Exception($"ResolvedQualifiedName mismatch. Expected '{expectedQualifiedName ?? "<null>"}', got '{call.ResolvedQualifiedName ?? "<null>"}'.");

    if (!string.Equals(call.ResolvedTargetQualifiedName, expectedTargetQualifiedName, StringComparison.Ordinal))
        throw new Exception($"ResolvedTargetQualifiedName mismatch. Expected '{expectedTargetQualifiedName ?? "<null>"}', got '{call.ResolvedTargetQualifiedName ?? "<null>"}'.");

    if (call.ResolvedFunctionType == null || !call.ResolvedFunctionType.IsFunction)
        throw new Exception("ResolvedFunctionType was not cached as a function type.");

    if (call.ResolvedFunctionType.ParamTypes.Count != expectedParamCount)
        throw new Exception($"ResolvedFunctionType cached unexpected parameter count. Expected {expectedParamCount}, got {call.ResolvedFunctionType.ParamTypes.Count}.");

    if (call.ResolvedFunctionType.ReturnType?.Name != "i64")
        throw new Exception("ResolvedFunctionType cached unexpected return type.");
}

static CallExpr? FindFirstCallInNode(Node node)
{
    switch (node)
    {
        case FunctionDecl fn:
            return FindFirstCallInStatements(fn.Body);
        case VariableDeclarationNode varDecl:
            return varDecl.Value != null ? FindFirstCallInExpr(varDecl.Value) : null;
        case ExpressionStatement exprStmt:
            return FindFirstCallInExpr(exprStmt.Expression);
        case AssignStmt assign:
            return FindFirstCallInExpr(assign.Target) ?? FindFirstCallInExpr(assign.Value);
        case ReturnNode returnNode:
            return returnNode.Value != null ? FindFirstCallInExpr(returnNode.Value) : null;
        case IfStmt ifStmt:
            return FindFirstCallInExpr(ifStmt.Condition) ?? FindFirstCallInStatements(ifStmt.Body) ?? FindFirstCallInStatements(ifStmt.ElseBody);
        case WhileStmt whileStmt:
            return FindFirstCallInExpr(whileStmt.Condition) ?? FindFirstCallInStatements(whileStmt.Body);
        case ForStmt forStmt:
            return (forStmt.Initializer != null ? FindFirstCallInStatement(forStmt.Initializer) : null)
                ?? (forStmt.Condition != null ? FindFirstCallInExpr(forStmt.Condition) : null)
                ?? (forStmt.Update != null ? FindFirstCallInStatement(forStmt.Update) : null)
                ?? FindFirstCallInStatements(forStmt.Body);
        default:
            return null;
    }
}

static CallExpr? FindFirstCallInStatement(Statement statement)
{
    return FindFirstCallInStatements([statement]);
}

static CallExpr? FindFirstCallInStatements(IEnumerable<Statement> statements)
{
    foreach (var statement in statements)
    {
        if (FindFirstCallInNode(statement) is CallExpr call)
            return call;
    }

    return null;
}

static CallExpr? FindFirstCallInExpr(Expr expr)
{
    switch (expr)
    {
        case CallExpr call:
            return call;
        case BinaryExpr binary:
            return FindFirstCallInExpr(binary.Left) ?? FindFirstCallInExpr(binary.Right);
        case UnaryExpr unary:
            return FindFirstCallInExpr(unary.Operand);
        case CastExpr cast:
            return FindFirstCallInExpr(cast.Expr);
        case IndexExpr index:
            return FindFirstCallInExpr(index.Target) ?? FindFirstCallInExpr(index.Index);
        case FieldExpr field:
            return FindFirstCallInExpr(field.Target);
        case StructLiteralExpr structLiteral:
            return FindFirstCallInExprs(structLiteral.Fields.Select(field => field.Value));
        case ArrayLiteralExpr arrayLiteral:
            return FindFirstCallInExprs(arrayLiteral.Elements);
        case CatchExpr catchExpr:
            return FindFirstCallInExpr(catchExpr.Left) ?? FindFirstCallInStatements(catchExpr.CatchBody);
        default:
            return null;
    }
}

static CallExpr? FindFirstCallInExprs(IEnumerable<Expr> expressions)
{
    foreach (var expr in expressions)
    {
        if (FindFirstCallInExpr(expr) is CallExpr call)
            return call;
    }

    return null;
}

static void WithTempDirectory(string prefix, Action<string> action)
{
    var tempDir = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);

    try
    {
        action(tempDir);
    }
    finally
    {
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, recursive: true);
    }
}

static CliArgumentCase[] GetCliArgumentCases(string sampleInput, string tempDir, bool hasWindowsHostToolchain)
{
    var windowsBuildOutputPath = Path.Combine(tempDir, OperatingSystem.IsWindows() ? "host-windows-arg.exe" : "host-windows-arg");
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
            "run_emit_llvm_rejected",
            $"run \"{sampleInput}\" --emit-llvm",
            1,
            "Usage:",
            "Option --emit-llvm cannot be combined with build or run."),
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
            $"build \"{sampleInput}\" --target host-windows -o \"{windowsBuildOutputPath}\"",
            OperatingSystem.IsWindows() && hasWindowsHostToolchain ? 0 : 1,
            null,
            OperatingSystem.IsWindows()
                ? (hasWindowsHostToolchain ? null : "Required tools were not found in PATH. Install one of: clang-cl, cl.")
                : "Target 'host-windows' currently requires a Windows host for build and run. Current host:")
    ];
}

static void RunBareMetalCliBuildTests(string fixtureRoot)
{
    if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) ||
        RuntimeInformation.ProcessArchitecture != Architecture.X64)
        return;

    if (!IsBareMetalLinkerAvailable())
        return;

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

static CliWorkflowCase[] GetAArch64CliWorkflowCases()
{
    return
    [
        new CliWorkflowCase("runtime_hello_world", "freestanding-linux-aarch64"),
        new CliWorkflowCase("runtime_string_escapes", "freestanding-linux-aarch64"),
        new CliWorkflowCase("runtime_condition_catch", "freestanding-linux-aarch64"),
        new CliWorkflowCase("runtime_task_yield_without_fiber", "freestanding-linux-aarch64"),
        new CliWorkflowCase("runtime_host_platform_branch", "host-linux-aarch64"),
        new CliWorkflowCase("runtime_stdlib_support_checks", "host-linux-aarch64")
    ];
}

static void RunAArch64EmissionVerification(CompilerInvocation compilerInvocation, string projectRoot, string fixtureRoot)
{
    var emissionCases = new[]
    {
        new AArch64EmissionCase("runtime_hello_world", "freestanding-linux-aarch64", ["svc #0"]),
        new AArch64EmissionCase("stdlib_linux_arch_syscall_codegen", "freestanding-linux-aarch64", ["svc #0"]),
        new AArch64EmissionCase("stdlib_task_aarch64_codegen", "freestanding-linux-aarch64", ["svc #0"]),
        new AArch64EmissionCase("runtime_host_platform_branch", "host-linux-aarch64", [])
    };

    WithTempDirectory("zorb-aarch64-emission", tempDir =>
    {
        foreach (var emissionCase in emissionCases)
        {
            var fixtureDir = Path.Combine(fixtureRoot, emissionCase.FixtureName);
            var mainPath = Path.Combine(fixtureDir, "main.zorb");
            var llvmPath = Path.Combine(tempDir, emissionCase.FixtureName + ".ll");
            var binaryPath = Path.Combine(tempDir, emissionCase.FixtureName);

            var emission = RunProcessWithTimeoutArgs(
                compilerInvocation.FileName,
                BuildCommandArguments(compilerInvocation, "--emit-llvm", mainPath, "--target", emissionCase.TargetName, "-o", llvmPath),
                projectRoot,
                TimeSpan.FromSeconds(30));
            if (emission.ExitCode != 0)
            {
                throw new Exception(
                    $"AArch64 LLVM emission failed for fixture '{emissionCase.FixtureName}' with exit code {emission.ExitCode}.{Environment.NewLine}{emission.StdErr}{emission.StdOut}".Trim());
            }

            if (!File.Exists(llvmPath) || new FileInfo(llvmPath).Length == 0)
                throw new Exception($"AArch64 LLVM emission did not produce output for fixture '{emissionCase.FixtureName}'.");

            var llvmIr = File.ReadAllText(llvmPath, Encoding.UTF8);
            AssertTextContains(llvmIr, "target triple = \"aarch64-unknown-linux-gnu\"");
            foreach (var expectedLlvmText in emissionCase.ExpectedLlvmIrSubstrings)
                AssertTextContains(llvmIr, expectedLlvmText);

            var build = RunProcessWithTimeoutArgs(
                compilerInvocation.FileName,
                BuildCommandArguments(compilerInvocation, "build", mainPath, "--target", emissionCase.TargetName, "-o", binaryPath),
                projectRoot,
                TimeSpan.FromSeconds(30));
            if (build.ExitCode != 0)
            {
                throw new Exception(
                    $"AArch64 build failed for fixture '{emissionCase.FixtureName}' with exit code {build.ExitCode}.{Environment.NewLine}{build.StdErr}{build.StdOut}".Trim());
            }

            if (!File.Exists(binaryPath) || new FileInfo(binaryPath).Length == 0)
                throw new Exception($"AArch64 build did not produce output for fixture '{emissionCase.FixtureName}'.");
        }
    });
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

static ProcessResult RunBuiltCliBinary(string builtBinaryPath, string workingDirectory, string targetName)
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
    yield return new CompilerInvocation(Path.Combine(directory, executableName), []);
    yield return new CompilerInvocation("dotnet", [Path.Combine(directory, "Zorb.Compiler.dll")]);
}

static bool CompilerInvocationExists(CompilerInvocation invocation)
{
    if (!string.Equals(invocation.FileName, "dotnet", StringComparison.Ordinal))
        return File.Exists(invocation.FileName);

    var dllPath = invocation.ArgumentsPrefix.Count == 1 ? invocation.ArgumentsPrefix[0] : "";
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
    RunLlvmEmissionTest(exampleDir, examplePath);
}

static void RunFixture(string fixtureDir)
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

static FixtureExpectations LoadFixtureExpectations(string fixtureDir)
{
    return new FixtureExpectations(
        ReadExpectedPhase(fixtureDir),
        ReadExpectationLines(fixtureDir, "expect-errors.txt"),
        ReadExpectationLines(fixtureDir, "expect-warnings.txt"));
}

static CapturedCompilation CompileFixtureForExpectations(string mainPath, string fixtureDir, FixtureExpectations expectations)
{
    var shouldCaptureOutput = expectations.ExpectedPhase != FixturePhase.Success
        || expectations.ExpectedErrors.Count > 0
        || expectations.ExpectedWarnings.Count > 0;
    return shouldCaptureOutput
        ? CaptureConsole(() => CompileFixture(mainPath, fixtureDir))
        : new CapturedCompilation(CompileFixture(mainPath, fixtureDir), "", "");
}

static void AssertFixtureDiagnostics(CapturedCompilation compilation, FixtureExpectations expectations)
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

static List<string> CollectFixtureErrors(FixtureCompilation compilation)
{
    var allErrors = new List<string>();
    allErrors.AddRange(compilation.ParseErrors);
    allErrors.AddRange(compilation.Checker.Errors.Errors);
    if (!string.IsNullOrEmpty(compilation.FailureMessage))
        allErrors.Add(compilation.FailureMessage);
    return allErrors;
}

static void RunFixtureRuntimeChecks(string fixtureDir, string mainPath)
{
    RunLlvmEmissionTest(fixtureDir, mainPath);
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

    return new FixtureCompilation(ast, parseErrors, checker, "", FixturePhase.Success, null);
}

static void RunLlvmEmissionTest(string fixtureDir, string mainPath)
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

static void RunRuntimeExpectationsIfPresent(string fixtureDir, string mainPath)
{
    foreach (var runtimeExpectation in ReadRuntimeExpectations(fixtureDir)
        .Where(IsRuntimeExpectationRunnableOnCurrentHost))
    {
        RunLlvmRuntimeExpectation(fixtureDir, mainPath, runtimeExpectation);
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

    if (string.Equals(targetName, "freestanding-linux-aarch64", StringComparison.Ordinal))
    {
        var genericAarch64Expectation = expectations.FirstOrDefault(item => string.Equals(item.TargetName, "freestanding-linux-aarch64", StringComparison.Ordinal));
        if (genericAarch64Expectation != null)
            return genericAarch64Expectation;

        var freestandingExpectation = expectations.FirstOrDefault(item => string.Equals(item.TargetName, "freestanding-linux", StringComparison.Ordinal));
        if (freestandingExpectation != null)
            return freestandingExpectation;
    }

    if (string.Equals(targetName, "host-linux-aarch64", StringComparison.Ordinal))
    {
        var hostAarch64Expectation = expectations.FirstOrDefault(item => string.Equals(item.TargetName, "host-linux-aarch64", StringComparison.Ordinal));
        if (hostAarch64Expectation != null)
            return hostAarch64Expectation;

        var freestandingAarch64Expectation = expectations.FirstOrDefault(item => string.Equals(item.TargetName, "freestanding-linux-aarch64", StringComparison.Ordinal));
        if (freestandingAarch64Expectation != null)
            return freestandingAarch64Expectation;

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
        "freestanding-linux" or "host-linux" => OperatingSystem.IsLinux(),
        "freestanding-linux-aarch64" or "host-linux-aarch64" => OperatingSystem.IsLinux() && (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 || FindAArch64Qemu() != null),
        "host-windows" => OperatingSystem.IsWindows(),
        _ => false
    };
}

static void RunLlvmRuntimeExpectation(string fixtureDir, string mainPath, RuntimeExpectation runtimeExpectation)
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

static void CopyRuntimeDataFiles(string fixtureDir, string tempDir)
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

static string EnsureToolAvailable(params string[] toolNames)
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

static bool IsAnyToolAvailable(params string[] toolNames)
{
    foreach (var toolName in toolNames)
    {
        if (ExternalTools.IsToolAvailable(toolName))
            return true;
    }

    return false;
}

static bool IsBareMetalLinkerAvailable()
{
    var configured = Environment.GetEnvironmentVariable("ZORB_LLD");
    if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        return true;

    if (ExternalTools.IsToolAvailable("ld.lld"))
        return true;

    return ExternalTools.FindAvailableToolByPrefix("ld.lld-") != null;
}

static string? FindAArch64LinuxCompiler()
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

static string? FindAArch64Qemu()
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

static string? ResolveAArch64Sysroot()
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

static string GetProjectRoot()
{
    var testProjectRoot = FindAncestorContainingFile(AppContext.BaseDirectory, "Zorb.Compiler.Tests.csproj");
    return Directory.GetParent(testProjectRoot)?.FullName
        ?? throw new Exception($"Unable to determine repository root from '{testProjectRoot}'.");
}

static ProcessResult RunProcessWithTimeoutArgs(string fileName, IEnumerable<string> arguments, string workingDirectory, TimeSpan timeout)
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

static IReadOnlyList<string> CombineCommandArguments(IReadOnlyList<string> prefix, string arguments)
{
    var result = new List<string>(prefix);
    result.AddRange(ExternalTools.SplitCommandLine(arguments));
    return result;
}

static IReadOnlyList<string> BuildCommandArguments(CompilerInvocation invocation, params string[] arguments)
{
    var result = new List<string>(invocation.ArgumentsPrefix);
    result.AddRange(arguments);
    return result;
}

enum FixturePhase
{
    Success,
    Parse,
    Semantic
}

sealed record FixtureCompilation(
    List<Node> Ast,
    List<string> ParseErrors,
    TypeChecker Checker,
    string Generated,
    FixturePhase Phase,
    string? FailureMessage);

sealed record FixtureExpectations(
    FixturePhase ExpectedPhase,
    List<string> ExpectedErrors,
    List<string> ExpectedWarnings);

sealed record CapturedCompilation(FixtureCompilation Result, string StdOut, string StdErr);

sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

sealed record RuntimeExpectation(string TargetName, string? ExpectedStdOut, string? ExpectedStdErr, int ExpectedExit);

sealed record CompilerInvocation(string FileName, IReadOnlyList<string> ArgumentsPrefix);

sealed record CliWorkflowCase(string FixtureName, string TargetName);

sealed record AArch64EmissionCase(string FixtureName, string TargetName, IReadOnlyList<string> ExpectedLlvmIrSubstrings);

sealed record CheckedFixture(string FixtureDir, string MainPath, TypeChecker Checker, IReadOnlyList<Node> BackendNodes);

sealed record BackendInstructionExpectation(string Op, int MinimumCount);

sealed record LlvmBackendRegressionCase(
    string FixtureName,
    IReadOnlyList<string> ExpectedBackendIrSubstrings,
    IReadOnlyList<BackendInstructionExpectation> ExpectedInstructionCounts,
    IReadOnlyList<string> ExpectedLlvmIrSubstrings);

sealed record CliArgumentCase(string Name, string Arguments, int ExpectedExitCode, string? ExpectedStdOutSubstring, string? ExpectedStdErrSubstring);
