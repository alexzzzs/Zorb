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
        var windowsStyleImportedInput = importedInput.Replace('/', '\\');
        var aliasedImportInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "import_alias", "main.zorb");
        var canonicalImportInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "import_canonical", "main.zorb");
        var aliasedEnumImportInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "import_alias_enum", "main.zorb");
        var privateImportInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "import_private", "main.zorb");
        var transitiveImportInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "import_transitive", "main.zorb");
        var canonicalCycleInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "import_canonical_cycle", "main.zorb");
        var cycleInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "import_cycle", "main.zorb");
        var missingImportInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "import_missing", "main.zorb");
        var invalidInput = Path.Combine(fixtureRoot, "parse_parameter_missing_colon", "main.zorb");
        var catchExpressionInput = Path.Combine(fixtureRoot, "catch_expression_codegen", "main.zorb");
        var backendIrInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_scalar.zorb");
        var backendIrAddInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_add.zorb");
        var backendIrSignedDivInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_signed_div.zorb");
        var backendIrMultipleFunctionsInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_multiple_functions.zorb");
        var backendIrDirectCallInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_direct_call.zorb");
        var backendIrNestedExpressionInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_nested_expression.zorb");
        var backendIrLocalInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_local.zorb");
        var backendIrMultipleLocalsInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_multiple_locals.zorb");
        var backendIrAssignmentInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_assignment.zorb");
        var backendIrParametersInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_parameters.zorb");
        var backendIrNegationInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_negation.zorb");
        var backendIrComparisonInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_comparison.zorb");
        var backendIrIfElseInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_if_else.zorb");
        var backendIrIfFallthroughInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_if_fallthrough.zorb");
        var backendIrWhileInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_while.zorb");
        var backendIrWhileSequenceInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_while_sequence.zorb");
        var backendIrWhileContinueInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_while_continue.zorb");
        var backendIrWhileBreakInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_while_break.zorb");
        var backendIrNestedControlInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_nested_control.zorb");
        var backendIrForInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_for.zorb");
        var backendIrSwitchMatchInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_switch_match.zorb");
        var backendIrErrorUnionCatchInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_error_union_catch.zorb");
        var backendIrErrorUnionI32Input = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_error_union_i32.zorb");
        var backendIrUnsignedInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_unsigned.zorb");
        var backendIrMixedScalarsInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_mixed_scalars.zorb");
        var backendIrPointersArraysInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_pointers_arrays.zorb");
        var backendIrSlicesStringsInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_slices_strings.zorb");
        var backendIrStructsInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_structs.zorb");
        var backendIrEnumsInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_enums.zorb");
        var backendIrUnionsInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_unions.zorb");
        var backendIrGlobalsConstantsInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_globals_constants.zorb");
        var backendIrCastsInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_casts.zorb");
        var backendIrFunctionValuesInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_function_values.zorb");
        var backendIrBuiltinsPlatformInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_builtins_platform.zorb");
        var backendIrGenericsInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "backend_ir_generics.zorb");
        var stressPipelineInput = Path.Combine(projectRoot, "examples", "advanced", "stress_pipeline.zorb");
        var threadsInput = Path.Combine(projectRoot, "examples", "advanced", "threads.zorb");
        var invalidForUpdateInput = Path.Combine(projectRoot, "compiler", "self-check", "fixtures", "for_update_declaration_invalid.zorb");

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
            AssertSelfCheckResult(binaryPath, projectRoot, [windowsStyleImportedInput], 0, "self-check succeeded:", null);
            AssertSelfCheckResult(binaryPath, projectRoot, [aliasedImportInput], 0, "self-check succeeded:", null);
            AssertSelfCheckResult(binaryPath, projectRoot, [aliasedEnumImportInput], 0, "self-check succeeded:", null);
            AssertSelfCheckResult(binaryPath, projectRoot, [canonicalImportInput], 0, "self-check succeeded:", null);
            AssertNativeLoweringSucceeded(binaryPath, projectRoot, tempDir, stressPipelineInput);
            AssertNativeLoweringSucceeded(binaryPath, projectRoot, tempDir, threadsInput);
            AssertSelfCheckResult(binaryPath, projectRoot, [privateImportInput], 1, null, "error[name.unknown]");
            AssertSelfCheckResult(binaryPath, projectRoot, [transitiveImportInput], 0, "self-check succeeded:", null);
            AssertSelfCheckResult(binaryPath, projectRoot, [cycleInput], 0, "self-check succeeded:", null);
            AssertSelfCheckResult(binaryPath, projectRoot, [canonicalCycleInput], 0, "self-check succeeded:", null);
            AssertSelfCheckResult(binaryPath, projectRoot, [missingImportInput], 1, null, "error[import.not-found]");
            AssertSelfCheckResult(binaryPath, projectRoot, [invalidInput], 1, null, "error[parse.invalid-syntax]");
            AssertSelfCheckResult(binaryPath, projectRoot, [invalidForUpdateInput], 1, null, "error[parse.invalid-syntax]");
            AssertSelfCheckJsonResult(binaryPath, projectRoot, validInput, 0, "result", "ok", null);
            AssertSelfCheckJsonResult(binaryPath, projectRoot, invalidInput, 1, "diagnostic", null, "parse.invalid-syntax");
            AssertSelfCheckJsonStream(binaryPath, projectRoot, "--dump-tokens", validInput, "token");
            AssertSelfCheckJsonStream(binaryPath, projectRoot, "--dump-ast", validInput, "ast-module");
            AssertSelfCheckAstHasNoUnknownKinds(binaryPath, projectRoot, catchExpressionInput);
            AssertNativeBackendIr(binaryPath, projectRoot, tempDir, backendIrInput, backendIrAddInput, backendIrSignedDivInput);
            AssertNativeMultipleFunctionsBackendIr(binaryPath, projectRoot, tempDir, backendIrMultipleFunctionsInput);
            AssertNativeDirectCallBackendIr(binaryPath, projectRoot, tempDir, backendIrDirectCallInput);
            AssertNativeNestedExpressionBackendIr(binaryPath, projectRoot, tempDir, backendIrNestedExpressionInput);
            AssertNativeLocalBackendIr(binaryPath, projectRoot, tempDir, backendIrLocalInput);
            AssertNativeMultipleLocalsBackendIr(binaryPath, projectRoot, tempDir, backendIrMultipleLocalsInput);
            AssertNativeAssignmentBackendIr(binaryPath, projectRoot, tempDir, backendIrAssignmentInput);
            AssertNativeParametersBackendIr(binaryPath, projectRoot, tempDir, backendIrParametersInput);
            AssertNativeNegationBackendIr(binaryPath, projectRoot, tempDir, backendIrNegationInput);
            AssertNativeComparisonBackendIr(binaryPath, projectRoot, tempDir, backendIrComparisonInput);
            AssertNativeIfElseBackendIr(binaryPath, projectRoot, tempDir, backendIrIfElseInput);
            AssertNativeIfFallthroughBackendIr(binaryPath, projectRoot, tempDir, backendIrIfFallthroughInput);
            AssertNativeWhileBackendIr(binaryPath, projectRoot, tempDir, backendIrWhileInput, "while", 1, 2);
            AssertNativeWhileBackendIr(binaryPath, projectRoot, tempDir, backendIrWhileSequenceInput, "while-sequence", 2, 2);
            AssertNativeWhileBackendIr(binaryPath, projectRoot, tempDir, backendIrWhileContinueInput, "while-continue", 1, 2);
            AssertNativeWhileBackendIr(binaryPath, projectRoot, tempDir, backendIrWhileBreakInput, "while-break", 1, 4);
            AssertNativeNestedControlBackendIr(binaryPath, projectRoot, tempDir, backendIrNestedControlInput);
            AssertNativeForBackendIr(binaryPath, projectRoot, tempDir, backendIrForInput);
            AssertNativeSwitchMatchBackendIr(binaryPath, projectRoot, tempDir, backendIrSwitchMatchInput);
            AssertNativeErrorUnionCatchBackendIr(
                binaryPath, projectRoot, tempDir, backendIrErrorUnionCatchInput, backendIrErrorUnionI32Input);
            AssertNativeUnsignedBackendIr(binaryPath, projectRoot, tempDir, backendIrUnsignedInput);
            AssertNativeMixedScalarsBackendIr(binaryPath, projectRoot, tempDir, backendIrMixedScalarsInput);
            AssertNativePointersArraysBackendIr(binaryPath, projectRoot, tempDir, backendIrPointersArraysInput);
            AssertNativeSlicesStringsBackendIr(binaryPath, projectRoot, tempDir, backendIrSlicesStringsInput);
            AssertNativeNominalAggregatesBackendIr(
                binaryPath, projectRoot, tempDir,
                backendIrStructsInput, backendIrEnumsInput, backendIrUnionsInput);
            AssertNativeGlobalsConstantsBackendIr(
                binaryPath, projectRoot, tempDir, backendIrGlobalsConstantsInput);
            AssertNativeCastsFunctionValuesBackendIr(
                binaryPath, projectRoot, tempDir, backendIrCastsInput, backendIrFunctionValuesInput);
            AssertNativeBuiltinsPlatformBackendIr(
                binaryPath, projectRoot, tempDir, backendIrBuiltinsPlatformInput);
            AssertNativeGenericsBackendIr(
                binaryPath, projectRoot, tempDir, backendIrGenericsInput);
            AssertSelfCheckBatchIsolation(binaryPath, projectRoot, validInput, importedInput, invalidInput);
            AssertSelfCheckResult(binaryPath, projectRoot, [], 64, null, "usage: zorb-self-check [--json|--dump-tokens|--dump-ast] <entry.zorb>");
            foreach (var option in new[]
            {
                "--json",
                "--dump-tokens",
                "--dump-ast",
                "--batch",
                "--batch-json",
                "--emit-backend-ir"
            })
            {
                AssertSelfCheckResult(
                    binaryPath,
                    projectRoot,
                    [option],
                    64,
                    null,
                    "usage: zorb-self-check [--json|--dump-tokens|--dump-ast] <entry.zorb>");
            }
        });
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

    private static void AssertNativeLoweringSucceeded(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var outputPath = Path.Combine(tempDirectory, $"{Path.GetFileNameWithoutExtension(inputPath)}-native.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), outputPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));

        if (execution.ExitCode != 0)
            throw new Exception($"native lowering expected exit code 0, got {execution.ExitCode}.\n{execution.StdErr}{execution.StdOut}".Trim());
        if (!string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native lowering wrote to stderr.\n{execution.StdErr}".Trim());

        using var document = System.Text.Json.JsonDocument.Parse(execution.StdOut.Trim());
        if (document.RootElement.GetProperty("schema_version").GetInt32() != 2)
            throw new Exception("native lowering emitted the wrong backend IR schema version.");

        var irPath = Path.Combine(tempDirectory, $"{Path.GetFileNameWithoutExtension(inputPath)}-native.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new Exception($"native lowering did not produce LLVM IR at '{outputPath}'.\n{backend.StdErr}{backend.StdOut}".Trim());
    }

    private static void AssertSelfCheckAstHasNoUnknownKinds(
        string binaryPath,
        string workingDirectory,
        string inputPath)
    {
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--dump-ast", inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"zorb-self-check AST dump failed.\n{execution.StdErr}{execution.StdOut}".Trim());

        foreach (var line in execution.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = System.Text.Json.JsonDocument.Parse(line);
            var record = document.RootElement;
            if (record.TryGetProperty("expressionKind", out var expressionKind) &&
                expressionKind.GetString() == "unknown")
                throw new Exception($"zorb-self-check serialized an unknown expression kind.\n{line}");
            if (record.TryGetProperty("statementKind", out var statementKind) &&
                statementKind.GetString() == "unknown")
                throw new Exception($"zorb-self-check serialized an unknown statement kind.\n{line}");
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
