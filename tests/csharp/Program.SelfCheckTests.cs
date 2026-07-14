internal static partial class Program
{
    private const int SelfCheckTimeoutSeconds = 30;

    // Linux is the first supported self-check lane.  The hosted entry-point
    // adapter deliberately uses the platform C argc/argv ABI, so Windows
    // coverage follows once path canonicalization is shared there as well.
    private static void RunSelfCheckBootstrapTests(string fixtureRoot)
    {
        if (!OperatingSystem.IsLinux())
            return;

        var projectRoot = GetProjectRoot();
        var compilerInvocation = GetCompilerInvocation(projectRoot);
        var selfCheckSource = Path.Combine(projectRoot, "compiler", "self-check", "main.zorb");
        var selfGraphInput = selfCheckSource;
        var validInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "simple.zorb");
        var builtinSizeofInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "builtin_sizeof.zorb");
        var qualifiedHeapAllocationInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "qualified_heap_alloc.zorb");
        var importedInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "import_graph", "main.zorb");
        var aliasedImportInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "import_alias", "main.zorb");
        var canonicalImportInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "import_canonical", "main.zorb");
        var aliasedEnumImportInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "import_alias_enum", "main.zorb");
        var privateImportInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "import_private", "main.zorb");
        var transitiveImportInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "import_transitive", "main.zorb");
        var canonicalCycleInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "import_canonical_cycle", "main.zorb");
        var cycleInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "import_cycle", "main.zorb");
        var missingImportInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "import_missing", "main.zorb");
        var invalidInput = Path.Combine(fixtureRoot, "parse_parameter_missing_colon", "main.zorb");
        var backendIrInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_scalar.zorb");
        var backendIrAddInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_add.zorb");
        var backendIrSignedDivInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_signed_div.zorb");
        var backendIrMultipleFunctionsInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_multiple_functions.zorb");

        WithTempDirectory("zorb-self-check-tests", tempDir =>
        {
            var binaryPath = Path.Combine(tempDir, "zorb-self-check");
            var build = RunProcessWithTimeoutArgs(
                compilerInvocation.FileName,
                BuildCommandArguments(
                    compilerInvocation,
                    "build", selfCheckSource, "--target", "host-linux", "-o", binaryPath),
                projectRoot,
                TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));

            if (build.ExitCode != 0 || !File.Exists(binaryPath))
            {
                throw new Exception(
                    $"Unable to build zorb-self-check.{Environment.NewLine}{build.StdErr}{build.StdOut}".Trim());
            }

            AssertSelfCheckResult(binaryPath, projectRoot, [validInput], 0, "self-check succeeded:", null);
            AssertSelfCheckResult(binaryPath, projectRoot, [builtinSizeofInput], 0, "self-check succeeded:", null);
            AssertSelfCheckResult(binaryPath, projectRoot, [qualifiedHeapAllocationInput], 0, "self-check succeeded:", null);
            // This is the milestone's bootstrap proof: the native executable
            // checks every frontend and standard-library file in its own
            // import graph without invoking the production frontend at run time.
            AssertSelfCheckResult(binaryPath, projectRoot, [selfGraphInput], 0, "self-check succeeded:", null);
            AssertSelfCheckResult(binaryPath, projectRoot, [importedInput], 0, "self-check succeeded:", null);
            AssertSelfCheckResult(binaryPath, projectRoot, [aliasedImportInput], 0, "self-check succeeded:", null);
            AssertSelfCheckResult(binaryPath, projectRoot, [aliasedEnumImportInput], 0, "self-check succeeded:", null);
            AssertSelfCheckResult(binaryPath, projectRoot, [canonicalImportInput], 0, "self-check succeeded:", null);
            AssertSelfCheckResult(binaryPath, projectRoot, [privateImportInput], 1, null, "error[name.unknown]");
            AssertSelfCheckResult(binaryPath, projectRoot, [transitiveImportInput], 0, "self-check succeeded:", null);
            AssertSelfCheckResult(binaryPath, projectRoot, [cycleInput], 0, "self-check succeeded:", null);
            AssertSelfCheckResult(binaryPath, projectRoot, [canonicalCycleInput], 0, "self-check succeeded:", null);
            AssertSelfCheckResult(binaryPath, projectRoot, [missingImportInput], 1, null, "error[import.not-found]");
            AssertSelfCheckResult(binaryPath, projectRoot, [invalidInput], 1, null, "error[parse.invalid-syntax]");
            AssertSelfCheckJsonResult(binaryPath, projectRoot, validInput, 0, "result", "ok", null);
            AssertSelfCheckJsonResult(binaryPath, projectRoot, invalidInput, 1, "diagnostic", null, "parse.invalid-syntax");
            AssertSelfCheckJsonStream(binaryPath, projectRoot, "--dump-tokens", validInput, "token");
            AssertSelfCheckJsonStream(binaryPath, projectRoot, "--dump-ast", validInput, "ast-module");
            AssertNativeBackendIr(binaryPath, projectRoot, tempDir, backendIrInput, backendIrAddInput, backendIrSignedDivInput);
            AssertNativeMultipleFunctionsBackendIr(binaryPath, projectRoot, tempDir, backendIrMultipleFunctionsInput);
            AssertSelfCheckBatchIsolation(binaryPath, projectRoot, validInput, importedInput, invalidInput);
            AssertSelfCheckResult(binaryPath, projectRoot, [], 64, null, "usage: zorb-self-check [--json|--dump-tokens|--dump-ast] <entry.zorb>");
        });
    }

    private static void AssertNativeBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath,
        string addInputPath,
        string signedDivInputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-scalar.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());

        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var root = document.RootElement;
            if (root.GetProperty("schema_version").GetInt32() != 2)
                throw new Exception("native backend IR emitted the wrong schema version.");
            var function = root.GetProperty("functions")[0];
            if (function.GetProperty("name").GetString() != "answer")
                throw new Exception("native backend IR did not retain the source function name.");
            var instruction = function.GetProperty("blocks")[0].GetProperty("instructions")[0];
            if (instruction.GetProperty("op").GetString() != "integer_constant" ||
                instruction.GetProperty("integer").GetInt64() != 42)
                throw new Exception("native backend IR did not lower the integer return expression.");
        }

        var irPath = Path.Combine(tempDirectory, "native-scalar.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native frontend IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (!llvm.Contains("define i32 @answer()", StringComparison.Ordinal) ||
            !llvm.Contains("ret i32 42", StringComparison.Ordinal))
            throw new Exception($"native frontend IR produced unexpected LLVM.\n{llvm}".Trim());

        AssertNativeBinaryBackendIr(
            binaryPath,
            workingDirectory,
            tempDirectory,
            addInputPath,
            "add",
            "add",
            "define i32 @add(i32 %lhs, i32 %rhs)",
            "add i32 %lhs, %rhs");
        AssertNativeBinaryBackendIr(
            binaryPath,
            workingDirectory,
            tempDirectory,
            signedDivInputPath,
            "divide",
            "signed_div",
            "define i64 @divide(i64 %lhs, i64 %rhs)",
            "sdiv i64 %lhs, %rhs");
    }

    private static void AssertNativeBinaryBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath,
        string functionName,
        string backendOperation,
        string expectedDefinition,
        string expectedInstruction)
    {
        var llvmPath = Path.Combine(tempDirectory, $"native-{functionName}.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native {functionName} backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var function = document.RootElement.GetProperty("functions")[0];
            if (function.GetProperty("parameters").GetArrayLength() != 2)
                throw new Exception($"native {functionName} backend IR did not emit both parameters.");
            var instruction = function.GetProperty("blocks")[0].GetProperty("instructions")[0];
            if (instruction.GetProperty("op").GetString() != "binary" ||
                instruction.GetProperty("binary_op").GetString() != backendOperation ||
                instruction.GetProperty("lhs").GetInt64() != 1 ||
                instruction.GetProperty("rhs").GetInt64() != 2)
                throw new Exception($"native {functionName} backend IR emitted the wrong binary instruction.");
        }
        var irPath = Path.Combine(tempDirectory, $"native-{functionName}.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native {functionName} IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (!llvm.Contains(expectedDefinition, StringComparison.Ordinal) ||
            !llvm.Contains(expectedInstruction, StringComparison.Ordinal))
            throw new Exception($"native {functionName} IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativeMultipleFunctionsBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-multiple.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native multiple-function backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var functions = document.RootElement.GetProperty("functions");
            if (functions.GetArrayLength() != 2 ||
                functions[0].GetProperty("id").GetInt64() != 1 ||
                functions[0].GetProperty("name").GetString() != "add" ||
                functions[1].GetProperty("id").GetInt64() != 2 ||
                functions[1].GetProperty("name").GetString() != "multiply")
                throw new Exception("native backend IR did not preserve both source functions and their IDs.");
        }
        var irPath = Path.Combine(tempDirectory, "native-multiple.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native multiple-function IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (!llvm.Contains("define i32 @add(i32 %lhs, i32 %rhs)", StringComparison.Ordinal) ||
            !llvm.Contains("define i32 @multiply(i32 %lhs, i32 %rhs)", StringComparison.Ordinal) ||
            !llvm.Contains("mul i32 %lhs, %rhs", StringComparison.Ordinal))
            throw new Exception($"native multiple-function IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertSelfCheckJsonResult(
        string binaryPath,
        string workingDirectory,
        string inputPath,
        int expectedExitCode,
        string expectedKind,
        string? expectedStatus,
        string? expectedCode)
    {
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--json", inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));

        if (execution.ExitCode != expectedExitCode)
            throw new Exception($"zorb-self-check JSON mode expected exit code {expectedExitCode}, got {execution.ExitCode}.\n{execution.StdErr}{execution.StdOut}".Trim());
        if (!string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"zorb-self-check JSON mode wrote to stderr.\n{execution.StdErr}".Trim());

        var output = execution.StdOut.Trim();
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.GetProperty("kind").GetString() != expectedKind)
                throw new Exception($"expected kind '{expectedKind}'");
            if (expectedStatus != null && root.GetProperty("status").GetString() != expectedStatus)
                throw new Exception($"expected status '{expectedStatus}'");
            if (expectedCode != null && root.GetProperty("code").GetString() != expectedCode)
                throw new Exception($"expected code '{expectedCode}'");
            if (root.GetProperty("file").GetString() != inputPath)
                throw new Exception($"expected file '{inputPath}'");
            if (expectedCode != null && (root.GetProperty("line").GetInt64() < 1 || root.GetProperty("column").GetInt64() < 1))
                throw new Exception("expected one-based diagnostic location");
        }
        catch (Exception ex) when (ex is not System.Text.Json.JsonException)
        {
            throw new Exception($"zorb-self-check JSON assertion failed: {ex.Message}\nActual stdout:\n{execution.StdOut}".Trim());
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new Exception($"zorb-self-check did not emit one valid JSON object: {ex.Message}\nActual stdout:\n{execution.StdOut}".Trim());
        }
    }

    private static void AssertSelfCheckJsonStream(
        string binaryPath,
        string workingDirectory,
        string mode,
        string inputPath,
        string expectedRecordKind)
    {
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            [mode, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"zorb-self-check {mode} failed.\n{execution.StdErr}{execution.StdOut}".Trim());

        var kinds = execution.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => System.Text.Json.JsonDocument.Parse(line).RootElement.GetProperty("kind").GetString())
            .ToArray();
        if (!kinds.Contains(expectedRecordKind, StringComparer.Ordinal) || !kinds.Contains("result", StringComparer.Ordinal))
            throw new Exception($"zorb-self-check {mode} did not emit '{expectedRecordKind}' and final result records.\n{execution.StdOut}".Trim());
    }

    private static void AssertSelfCheckBatchIsolation(
        string binaryPath,
        string workingDirectory,
        string validInput,
        string importedInput,
        string invalidInput)
    {
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--batch-json", validInput, importedInput, invalidInput, validInput],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 1 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"zorb-self-check batch mode did not isolate source-graph sessions.\n{execution.StdErr}{execution.StdOut}".Trim());

        var records = execution.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => System.Text.Json.JsonDocument.Parse(line).RootElement)
            .ToArray();
        var successfulEntries = records.Count(record =>
            record.GetProperty("kind").GetString() == "result" &&
            record.GetProperty("status").GetString() == "ok");
        var parseDiagnostics = records.Count(record =>
            record.GetProperty("kind").GetString() == "diagnostic" &&
            record.GetProperty("code").GetString() == "parse.invalid-syntax");
        if (successfulEntries != 3 || parseDiagnostics != 1)
            throw new Exception($"zorb-self-check batch mode emitted unexpected records.\n{execution.StdOut}".Trim());

        var lastRecord = records[^1];
        if (lastRecord.GetProperty("kind").GetString() != "result" ||
            lastRecord.GetProperty("file").GetString() != validInput)
            throw new Exception($"zorb-self-check batch mode did not continue with a fresh session after failure.\n{execution.StdOut}".Trim());
    }

    private static void AssertSelfCheckResult(
        string binaryPath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        int expectedExitCode,
        string? expectedStdOutSubstring,
        string? expectedStdErrSubstring)
    {
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            arguments,
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));

        if (execution.ExitCode != expectedExitCode)
        {
            throw new Exception(
                $"zorb-self-check expected exit code {expectedExitCode}, got {execution.ExitCode}.{Environment.NewLine}{execution.StdErr}{execution.StdOut}".Trim());
        }

        if (expectedStdOutSubstring != null && !execution.StdOut.Contains(expectedStdOutSubstring, StringComparison.Ordinal))
        {
            throw new Exception(
                $"zorb-self-check stdout did not contain '{expectedStdOutSubstring}'.{Environment.NewLine}{execution.StdOut}".Trim());
        }

        if (expectedStdErrSubstring != null && !execution.StdErr.Contains(expectedStdErrSubstring, StringComparison.Ordinal))
        {
            throw new Exception(
                $"zorb-self-check stderr did not contain '{expectedStdErrSubstring}'.{Environment.NewLine}{execution.StdErr}".Trim());
        }
    }
}
