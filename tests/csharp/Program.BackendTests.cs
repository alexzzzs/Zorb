using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Zorb.Compiler.AST;
using Zorb.Compiler.AST.Statements;
using Zorb.Compiler.Codegen;
using Zorb.Compiler.Semantic;
using Zorb.Compiler.Utils;

internal static partial class Program
{
    private static void RunLlvmWriterStateResetTests(string fixtureRoot)
    {
        var fixture = LoadCheckedFixture(Path.Combine(fixtureRoot, "runtime_hello_world"));
        var start = fixture.BackendNodes
            .OfType<FunctionDecl>()
            .Single(function => string.Equals(function.Name, "_start", StringComparison.Ordinal));
        var writer = new ZigBackendIrWriter(fixture.Checker);
        var target = new ZigBackendTarget("x86_64-pc-linux-gnu");

        _ = writer.Write(
            fixture.BackendNodes,
            "writer_state_first",
            target,
            ZigBackendOutputKind.Object,
            "writer-state-first.o",
            addHostedEntryShim: true);
        if (!string.Equals(start.Name, "_start", StringComparison.Ordinal))
            throw new Exception("Hosted LLVM entry lowering mutated the checked _start declaration.");

        _ = writer.Write(
            fixture.BackendNodes,
            "writer_state_second",
            target,
            ZigBackendOutputKind.Object,
            "writer-state-second.o",
            addHostedEntryShim: true);
        if (!string.Equals(start.Name, "_start", StringComparison.Ordinal))
            throw new Exception("Repeated hosted LLVM entry lowering mutated the checked _start declaration.");
    }

    private static void RunLlvmBackendOutputModeTests(string fixtureRoot)
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

    private static void RunLlvmBackendRegressionTests(string fixtureRoot)
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

    private static CheckedFixture LoadCheckedFixture(string fixtureDir)
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

    private static string WriteBackendIr(
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

    private static ProcessResult EmitBackendArtifact(string backendPath, string backendIrPath, string workingDirectory)
    {
        return RunProcessWithTimeoutArgs(
            backendPath,
            [backendIrPath],
            workingDirectory,
            TimeSpan.FromSeconds(30));
    }

    private static void AssertBackendInstructionCountAtLeast(string backendIr, string fixtureName, string op, int minimumCount)
    {
        using var document = JsonDocument.Parse(backendIr);
        var actualCount = CountBackendInstructions(document.RootElement, op);
        if (actualCount < minimumCount)
        {
            throw new Exception(
                $"Fixture '{fixtureName}' expected at least {minimumCount} backend '{op}' instructions, got {actualCount}.{Environment.NewLine}{backendIr}");
        }
    }

    private static int CountBackendInstructions(JsonElement module, string op)
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

    private static LlvmBackendRegressionCase[] GetLlvmBackendRegressionCases()
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

    private static string GetNativeLlvmTriple()
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

    private static string GetLlvmBackendPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("ZORB_LLVM_BACKEND");
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return Path.GetFullPath(configuredPath);

        var executableName = OperatingSystem.IsWindows() ? "zorb-llvm-backend.exe" : "zorb-llvm-backend";
        var candidate = Path.Combine(GetProjectRoot(), "backend/llvm", "zig-out", "bin", executableName);
        if (File.Exists(candidate))
            return candidate;

        throw new Exception("LLVM backend executable was not found. Build backend/llvm or set ZORB_LLVM_BACKEND.");
    }

    private static CompilerInvocation GetCompilerInvocation(string projectRoot)
    {
        var configurationDir = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new Exception("Unable to determine test output configuration.");
        var targetFrameworkDir = new DirectoryInfo(AppContext.BaseDirectory).Name;
        var outputDir = Path.Combine(projectRoot, "seed/csharp", "bin", configurationDir, targetFrameworkDir);
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

    private static IEnumerable<CompilerInvocation> GetCandidateCompilerInvocations(string directory)
    {
        var executableName = OperatingSystem.IsWindows() ? "Zorb.Compiler.exe" : "Zorb.Compiler";
        yield return new CompilerInvocation(Path.Combine(directory, executableName), []);
        yield return new CompilerInvocation("dotnet", [Path.Combine(directory, "Zorb.Compiler.dll")]);
    }

    private static bool CompilerInvocationExists(CompilerInvocation invocation)
    {
        if (!string.Equals(invocation.FileName, "dotnet", StringComparison.Ordinal))
            return File.Exists(invocation.FileName);

        var dllPath = invocation.ArgumentsPrefix.Count == 1 ? invocation.ArgumentsPrefix[0] : "";
        return File.Exists(dllPath);
    }
}
