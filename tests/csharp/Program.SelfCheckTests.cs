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
            AssertSelfCheckResult(binaryPath, projectRoot, [aliasedImportInput], 0, "self-check succeeded:", null);
            AssertSelfCheckResult(binaryPath, projectRoot, [aliasedEnumImportInput], 0, "self-check succeeded:", null);
            AssertSelfCheckResult(binaryPath, projectRoot, [canonicalImportInput], 0, "self-check succeeded:", null);
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
            if (root.GetProperty("types").GetArrayLength() != 2 ||
                root.GetProperty("types")[1].GetProperty("scalar").GetString() != "bool")
                throw new Exception("native integer backend IR did not intern its bool condition type.");
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
            "add i32 %");
        AssertNativeBinaryBackendIr(
            binaryPath,
            workingDirectory,
            tempDirectory,
            signedDivInputPath,
            "divide",
            "signed_div",
            "define i64 @divide(i64 %lhs, i64 %rhs)",
            "sdiv i64 %");
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
            System.Text.Json.JsonElement? binaryInstruction = null;
            foreach (var instruction in function.GetProperty("blocks")[0].GetProperty("instructions").EnumerateArray())
            {
                if (instruction.GetProperty("op").GetString() == "binary")
                {
                    binaryInstruction = instruction;
                    break;
                }
            }
            if (binaryInstruction is null ||
                binaryInstruction.Value.GetProperty("binary_op").GetString() != backendOperation)
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
            !llvm.Contains("mul i32 %", StringComparison.Ordinal))
            throw new Exception($"native multiple-function IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativeDirectCallBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-direct-call.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native direct-call backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var call = document.RootElement.GetProperty("functions")[1]
                .GetProperty("blocks")[0].GetProperty("instructions").EnumerateArray()
                .Single(instruction => instruction.GetProperty("op").GetString() == "call");
            if (
                call.GetProperty("callee").GetInt64() != 1 ||
                call.GetProperty("arguments").GetArrayLength() != 2)
                throw new Exception("native backend IR did not lower the direct call and argument IDs.");
        }
        var irPath = Path.Combine(tempDirectory, "native-direct-call.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native direct-call IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (!llvm.Contains("define i32 @add_again(i32 %lhs, i32 %rhs)", StringComparison.Ordinal) ||
            !llvm.Contains("call i32 @add(i32 %", StringComparison.Ordinal))
            throw new Exception($"native direct-call IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativeNestedExpressionBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-nested-expression.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native nested-expression backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var instructions = document.RootElement.GetProperty("functions")[1]
                .GetProperty("blocks")[0].GetProperty("instructions");
            var operations = instructions.EnumerateArray()
                .Select(instruction => instruction.GetProperty("op").GetString() == "binary"
                    ? instruction.GetProperty("binary_op").GetString()
                    : instruction.GetProperty("op").GetString())
                .ToArray();
            var meaningfulOperations = operations.Where(operation =>
                operation is not "alloca" and not "store" and not "load").ToArray();
            if (!meaningfulOperations.SequenceEqual(["call", "integer_constant", "add", "mul"], StringComparer.Ordinal))
                throw new Exception("native backend IR did not emit the nested expression in value dependency order.");
        }
        var irPath = Path.Combine(tempDirectory, "native-nested-expression.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native nested-expression IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (!llvm.Contains("call i32 @add(i32 %", StringComparison.Ordinal) ||
            !llvm.Contains("add i32 %", StringComparison.Ordinal) ||
            !llvm.Contains("mul i32", StringComparison.Ordinal))
            throw new Exception($"native nested-expression IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativeLocalBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-local.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native local backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var instructions = document.RootElement.GetProperty("functions")[0]
                .GetProperty("blocks")[0].GetProperty("instructions");
            var operations = instructions.EnumerateArray()
                .Select(instruction => instruction.GetProperty("op").GetString())
                .ToArray();
            if (operations.Count(operation => operation == "alloca") != 3 ||
                operations.Count(operation => operation == "store") != 3 ||
                operations.Count(operation => operation == "load") != 3 ||
                operations.Count(operation => operation == "binary") != 1)
                throw new Exception("native backend IR did not lower the scalar local's address, store, and load.");
        }
        var irPath = Path.Combine(tempDirectory, "native-local.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native local IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (!llvm.Contains("alloca i32", StringComparison.Ordinal) ||
            !llvm.Contains("store i32", StringComparison.Ordinal) ||
            !llvm.Contains("load i32", StringComparison.Ordinal))
            throw new Exception($"native local IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativeAssignmentBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-assignment.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native assignment backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var instructions = document.RootElement.GetProperty("functions")[0]
                .GetProperty("blocks")[0].GetProperty("instructions");
            var operations = instructions.EnumerateArray()
                .Select(instruction => instruction.GetProperty("op").GetString())
                .ToArray();
            if (operations.Count(operation => operation == "alloca") != 3 ||
                operations.Count(operation => operation == "store") != 4 ||
                operations.Count(operation => operation == "load") < 4 ||
                operations.Count(operation => operation == "binary") != 2 ||
                operations.Count(operation => operation == "integer_constant") != 1)
                throw new Exception("native backend IR did not lower the mutable local assignment in dependency order.");
        }
        var irPath = Path.Combine(tempDirectory, "native-assignment.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native assignment IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (llvm.Split("store i32", StringSplitOptions.None).Length - 1 != 4 ||
            !llvm.Contains("mul i32", StringComparison.Ordinal))
            throw new Exception($"native assignment IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativeMultipleLocalsBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-multiple-locals.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native multiple-local backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var block = document.RootElement.GetProperty("functions")[0].GetProperty("blocks")[0];
            var instructions = block.GetProperty("instructions");
            var terminator = block.GetProperty("terminator");
            var operations = instructions.EnumerateArray()
                .Select(instruction => instruction.GetProperty("op").GetString())
                .ToArray();
            if (operations.Count(operation => operation == "alloca") != 4 ||
                operations.Count(operation => operation == "store") != 5 ||
                operations.Count(operation => operation == "load") < 6 ||
                operations.Count(operation => operation == "binary") != 4 ||
                operations.Count(operation => operation == "integer_constant") != 2 ||
                terminator.GetProperty("op").GetString() != "return_value")
                throw new Exception("native backend IR did not preserve multiple local addresses across initialization, assignment, and return.");
        }
        var irPath = Path.Combine(tempDirectory, "native-multiple-locals.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native multiple-local IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (llvm.Split("alloca i32", StringSplitOptions.None).Length - 1 != 4 ||
            llvm.Split("store i32", StringSplitOptions.None).Length - 1 != 5 ||
            llvm.Split("load i32", StringSplitOptions.None).Length - 1 < 6)
            throw new Exception($"native multiple-local IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativeParametersBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-parameters.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native parameter backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var function = document.RootElement.GetProperty("functions")[0];
            var instructions = function.GetProperty("blocks")[0].GetProperty("instructions");
            var operations = instructions.EnumerateArray()
                .Select(instruction => instruction.GetProperty("op").GetString()).ToArray();
            if (function.GetProperty("parameters").GetArrayLength() != 3 ||
                operations.Count(operation => operation == "alloca") != 3 ||
                operations.Count(operation => operation == "store") != 3 ||
                operations.Count(operation => operation == "load") != 3 ||
                operations.Count(operation => operation == "binary") != 2)
                throw new Exception("native backend IR did not allocate values after all three parameter IDs.");
        }
        var irPath = Path.Combine(tempDirectory, "native-parameters.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native parameter IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (!llvm.Contains("define i64 @sum_three(i64 %first, i64 %second, i64 %third)", StringComparison.Ordinal) ||
            llvm.Split("add i64", StringSplitOptions.None).Length - 1 != 2)
            throw new Exception($"native parameter IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativeNegationBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-negation.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native negation backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var instructions = document.RootElement.GetProperty("functions")[0]
                .GetProperty("blocks")[0].GetProperty("instructions");
            if (!instructions.EnumerateArray().Any(instruction =>
                    instruction.GetProperty("op").GetString() == "integer_constant" &&
                    instruction.GetProperty("integer").GetInt64() == 0) ||
                !instructions.EnumerateArray().Any(instruction =>
                    instruction.GetProperty("op").GetString() == "binary" &&
                    instruction.GetProperty("binary_op").GetString() == "sub"))
                throw new Exception("native backend IR did not lower unary negation as zero minus the nested operand.");
        }
        var irPath = Path.Combine(tempDirectory, "native-negation.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native negation IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (!llvm.Contains("add i32 %", StringComparison.Ordinal) ||
            !llvm.Contains("sub i32 0", StringComparison.Ordinal))
            throw new Exception($"native negation IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativeComparisonBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-comparison.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native comparison backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var root = document.RootElement;
            var instruction = root.GetProperty("functions")[0].GetProperty("blocks")[0]
                .GetProperty("instructions").EnumerateArray()
                .Single(item => item.GetProperty("op").GetString() == "compare");
            if (root.GetProperty("types")[0].GetProperty("scalar").GetString() != "bool" ||
                instruction.GetProperty("compare_op").GetString() != "equal")
                throw new Exception("native backend IR did not lower the boolean comparison.");
        }
        var irPath = Path.Combine(tempDirectory, "native-comparison.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native comparison IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (!llvm.Contains("define i32 @same(i32 %lhs, i32 %rhs)", StringComparison.Ordinal) ||
            !llvm.Contains("icmp eq i32 %", StringComparison.Ordinal) ||
            !llvm.Contains("zext i1", StringComparison.Ordinal))
            throw new Exception($"native comparison IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativeIfElseBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-if-else.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native if/else backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var function = document.RootElement.GetProperty("functions")[0];
            var blocks = function.GetProperty("blocks");
            var entryTerminator = blocks[0].GetProperty("terminator");
            var comparison = blocks[0].GetProperty("instructions").EnumerateArray()
                .Single(instruction => instruction.GetProperty("op").GetString() == "compare");
            if (blocks.GetArrayLength() != 3 ||
                comparison.GetProperty("type").GetInt64() != 2 ||
                comparison.GetProperty("compare_op").GetString() != "signed_less" ||
                entryTerminator.GetProperty("op").GetString() != "conditional_branch" ||
                entryTerminator.GetProperty("condition").GetInt64() != comparison.GetProperty("id").GetInt64() ||
                entryTerminator.GetProperty("true_target").GetInt64() != 2 ||
                entryTerminator.GetProperty("false_target").GetInt64() != 3 ||
                blocks[1].GetProperty("terminator").GetProperty("op").GetString() != "return_value" ||
                blocks[2].GetProperty("terminator").GetProperty("op").GetString() != "return_value")
                throw new Exception("native backend IR did not lower the if/else block graph and value references.");
        }
        var irPath = Path.Combine(tempDirectory, "native-if-else.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native if/else IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (!llvm.Contains("icmp slt i32 %", StringComparison.Ordinal) ||
            !llvm.Contains("br i1", StringComparison.Ordinal) ||
            !llvm.Contains("then:", StringComparison.Ordinal) ||
            !llvm.Contains("else:", StringComparison.Ordinal))
            throw new Exception($"native if/else IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativeIfFallthroughBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-if-fallthrough.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native if fallthrough backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var blocks = document.RootElement.GetProperty("functions")[0].GetProperty("blocks");
            var entryTerminator = blocks[0].GetProperty("terminator");
            var comparison = blocks[0].GetProperty("instructions").EnumerateArray()
                .Single(instruction => instruction.GetProperty("op").GetString() == "compare");
            if (blocks.GetArrayLength() != 3 ||
                comparison.GetProperty("compare_op").GetString() != "signed_greater" ||
                entryTerminator.GetProperty("op").GetString() != "conditional_branch" ||
                entryTerminator.GetProperty("condition").GetInt64() != comparison.GetProperty("id").GetInt64() ||
                entryTerminator.GetProperty("true_target").GetInt64() != 2 ||
                entryTerminator.GetProperty("false_target").GetInt64() != 3 ||
                blocks[1].GetProperty("name").GetString() != "then" ||
                blocks[1].GetProperty("terminator").GetProperty("op").GetString() != "return_value" ||
                blocks[2].GetProperty("name").GetString() != "continuation" ||
                blocks[2].GetProperty("terminator").GetProperty("op").GetString() != "return_value")
                throw new Exception("native backend IR did not lower the if fallthrough graph and value references.");
        }
        var irPath = Path.Combine(tempDirectory, "native-if-fallthrough.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native if fallthrough IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (!llvm.Contains("icmp sgt i32 %", StringComparison.Ordinal) ||
            !llvm.Contains("br i1", StringComparison.Ordinal) ||
            !llvm.Contains("then:", StringComparison.Ordinal) ||
            !llvm.Contains("continuation:", StringComparison.Ordinal))
            throw new Exception($"native if fallthrough IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativeWhileBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath,
        string artifactStem,
        int expectedBodyStoreCount,
        int expectedBodyTarget)
    {
        var llvmPath = Path.Combine(tempDirectory, $"native-{artifactStem}.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native while backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var blocks = document.RootElement.GetProperty("functions")[0].GetProperty("blocks");
            var conditionTerminator = blocks[1].GetProperty("terminator");
            var comparison = blocks[1].GetProperty("instructions").EnumerateArray()
                .Single(instruction => instruction.GetProperty("op").GetString() == "compare");
            var bodyStoreCount = blocks[2].GetProperty("instructions").EnumerateArray()
                .Count(instruction => instruction.GetProperty("op").GetString() == "store");
            if (blocks.GetArrayLength() != 4 ||
                blocks[0].GetProperty("terminator").GetProperty("op").GetString() != "branch" ||
                blocks[0].GetProperty("terminator").GetProperty("target").GetInt64() != 2 ||
                blocks[1].GetProperty("name").GetString() != "condition" ||
                comparison.GetProperty("compare_op").GetString() != "signed_less" ||
                conditionTerminator.GetProperty("op").GetString() != "conditional_branch" ||
                conditionTerminator.GetProperty("condition").GetInt64() != comparison.GetProperty("id").GetInt64() ||
                conditionTerminator.GetProperty("true_target").GetInt64() != 3 ||
                conditionTerminator.GetProperty("false_target").GetInt64() != 4 ||
                blocks[2].GetProperty("name").GetString() != "body" ||
                bodyStoreCount != expectedBodyStoreCount ||
                blocks[2].GetProperty("terminator").GetProperty("op").GetString() != "branch" ||
                blocks[2].GetProperty("terminator").GetProperty("target").GetInt64() != expectedBodyTarget ||
                blocks[3].GetProperty("name").GetString() != "exit" ||
                blocks[3].GetProperty("terminator").GetProperty("op").GetString() != "return_value")
                throw new Exception("native backend IR did not lower the while loop block graph and terminators.");
        }
        var irPath = Path.Combine(tempDirectory, $"native-{artifactStem}.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native while IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        var expectedBodyBranch = expectedBodyTarget == 4 ? "br label %exit" : "br label %condition";
        if (!llvm.Contains("br label %condition", StringComparison.Ordinal) ||
            !llvm.Contains(expectedBodyBranch, StringComparison.Ordinal) ||
            !llvm.Contains("icmp slt i64", StringComparison.Ordinal) ||
            !llvm.Contains("body:", StringComparison.Ordinal) ||
            !llvm.Contains("exit:", StringComparison.Ordinal) ||
            !llvm.Contains("store i64", StringComparison.Ordinal) ||
            !llvm.Contains("load i64", StringComparison.Ordinal))
            throw new Exception($"native while IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativeNestedControlBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-nested-control.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native nested-control backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var functions = document.RootElement.GetProperty("functions");
            var blocks = functions[0].GetProperty("blocks");
            var nestedLoopBlocks = functions[1].GetProperty("blocks");
            var elseIfBlocks = functions[2].GetProperty("blocks");
            if (blocks.GetArrayLength() != 8 ||
                blocks[2].GetProperty("terminator").GetProperty("op").GetString() != "conditional_branch" ||
                blocks[2].GetProperty("terminator").GetProperty("true_target").GetInt64() != 5 ||
                blocks[2].GetProperty("terminator").GetProperty("false_target").GetInt64() != 6 ||
                blocks[4].GetProperty("terminator").GetProperty("op").GetString() != "branch" ||
                blocks[4].GetProperty("terminator").GetProperty("target").GetInt64() != 2 ||
                blocks[5].GetProperty("terminator").GetProperty("op").GetString() != "conditional_branch" ||
                blocks[5].GetProperty("terminator").GetProperty("true_target").GetInt64() != 7 ||
                blocks[5].GetProperty("terminator").GetProperty("false_target").GetInt64() != 8 ||
                blocks[6].GetProperty("terminator").GetProperty("op").GetString() != "branch" ||
                blocks[6].GetProperty("terminator").GetProperty("target").GetInt64() != 4 ||
                blocks[7].GetProperty("instructions")[0].GetProperty("op").GetString() != "alloca" ||
                blocks[7].GetProperty("terminator").GetProperty("op").GetString() != "branch" ||
                blocks[7].GetProperty("terminator").GetProperty("target").GetInt64() != 2 ||
                blocks[3].GetProperty("terminator").GetProperty("op").GetString() != "return_value" ||
                nestedLoopBlocks.GetArrayLength() != 11 ||
                nestedLoopBlocks[7].GetProperty("terminator").GetProperty("op").GetString() != "branch" ||
                nestedLoopBlocks[7].GetProperty("terminator").GetProperty("target").GetInt64() != 7 ||
                nestedLoopBlocks[9].GetProperty("terminator").GetProperty("op").GetString() != "branch" ||
                nestedLoopBlocks[9].GetProperty("terminator").GetProperty("target").GetInt64() != 2 ||
                nestedLoopBlocks[10].GetProperty("terminator").GetProperty("op").GetString() != "branch" ||
                nestedLoopBlocks[10].GetProperty("terminator").GetProperty("target").GetInt64() != 2 ||
                elseIfBlocks.GetArrayLength() != 7 ||
                elseIfBlocks[2].GetProperty("terminator").GetProperty("op").GetString() != "conditional_branch" ||
                elseIfBlocks[2].GetProperty("terminator").GetProperty("true_target").GetInt64() != 4 ||
                elseIfBlocks[2].GetProperty("terminator").GetProperty("false_target").GetInt64() != 5 ||
                elseIfBlocks[4].GetProperty("terminator").GetProperty("op").GetString() != "branch" ||
                elseIfBlocks[4].GetProperty("terminator").GetProperty("target").GetInt64() != 6 ||
                elseIfBlocks[6].GetProperty("terminator").GetProperty("op").GetString() != "return_value")
                throw new Exception("native backend IR did not lower nested conditionals, loop controls, and shadowed block locals.");
        }
        var irPath = Path.Combine(tempDirectory, "native-nested-control.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native nested-control IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (llvm.Split("alloca i64", StringSplitOptions.None).Length - 1 != 11 ||
            !llvm.Contains("br label %condition", StringComparison.Ordinal) ||
            !llvm.Contains("br label %exit", StringComparison.Ordinal) ||
            llvm.Split("icmp eq i64", StringSplitOptions.None).Length - 1 != 4)
            throw new Exception($"native nested-control IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativeForBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-for.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native for backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var functions = document.RootElement.GetProperty("functions");
            var simpleBlocks = functions[0].GetProperty("blocks");
            var nestedBlocks = functions[1].GetProperty("blocks");
            var emptyBlocks = functions[2].GetProperty("blocks");
            if (simpleBlocks.GetArrayLength() != 9 ||
                simpleBlocks[3].GetProperty("name").GetString() != "for.update" ||
                simpleBlocks[3].GetProperty("terminator").GetProperty("target").GetInt64() != 2 ||
                simpleBlocks[5].GetProperty("terminator").GetProperty("target").GetInt64() != 4 ||
                simpleBlocks[7].GetProperty("terminator").GetProperty("target").GetInt64() != 5 ||
                simpleBlocks[8].GetProperty("terminator").GetProperty("target").GetInt64() != 4 ||
                nestedBlocks.GetArrayLength() != 15 ||
                nestedBlocks[9].GetProperty("terminator").GetProperty("target").GetInt64() != 8 ||
                nestedBlocks[11].GetProperty("terminator").GetProperty("target").GetInt64() != 9 ||
                nestedBlocks[13].GetProperty("terminator").GetProperty("target").GetInt64() != 5 ||
                nestedBlocks[14].GetProperty("terminator").GetProperty("target").GetInt64() != 4 ||
                emptyBlocks.GetArrayLength() != 7 ||
                emptyBlocks[1].GetProperty("instructions")[0].GetProperty("type").GetInt64() != 2 ||
                emptyBlocks[1].GetProperty("instructions")[0].GetProperty("integer").GetInt64() != 1 ||
                emptyBlocks[3].GetProperty("terminator").GetProperty("target").GetInt64() != 2 ||
                emptyBlocks[5].GetProperty("terminator").GetProperty("target").GetInt64() != 5)
                throw new Exception("native backend IR did not lower for clauses and nested loop-control targets.");
        }
        var irPath = Path.Combine(tempDirectory, "native-for.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native for IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (llvm.Split("alloca i64", StringSplitOptions.None).Length - 1 != 9 ||
            !llvm.Contains("for.condition:", StringComparison.Ordinal) ||
            !llvm.Contains("for.update:", StringComparison.Ordinal) ||
            !llvm.Contains("br label %for.update", StringComparison.Ordinal))
            throw new Exception($"native for IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativeSwitchMatchBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-switch-match.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native switch/match backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var functions = document.RootElement.GetProperty("functions");
            var switchBlocks = functions[0].GetProperty("blocks");
            var matchBlocks = functions[1].GetProperty("blocks");
            var terminatingBlocks = functions[2].GetProperty("blocks");
            var switchComparison = switchBlocks[0].GetProperty("instructions").EnumerateArray()
                .Single(instruction => instruction.GetProperty("op").GetString() == "compare");
            var matchComparison = matchBlocks[0].GetProperty("instructions").EnumerateArray()
                .Single(instruction => instruction.GetProperty("op").GetString() == "compare");
            if (switchBlocks.GetArrayLength() != 6 ||
                matchBlocks.GetArrayLength() != 6 ||
                switchComparison.GetProperty("compare_op").GetString() != "equal" ||
                switchBlocks[0].GetProperty("terminator").GetProperty("true_target").GetInt64() != 2 ||
                switchBlocks[0].GetProperty("terminator").GetProperty("false_target").GetInt64() != 3 ||
                switchBlocks[2].GetProperty("terminator").GetProperty("true_target").GetInt64() != 4 ||
                switchBlocks[2].GetProperty("terminator").GetProperty("false_target").GetInt64() != 5 ||
                switchBlocks[1].GetProperty("terminator").GetProperty("target").GetInt64() != 6 ||
                switchBlocks[3].GetProperty("terminator").GetProperty("target").GetInt64() != 6 ||
                switchBlocks[4].GetProperty("terminator").GetProperty("target").GetInt64() != 6 ||
                matchComparison.GetProperty("compare_op").GetString() != "equal" ||
                matchBlocks[0].GetProperty("terminator").GetProperty("true_target").GetInt64() != 2 ||
                matchBlocks[0].GetProperty("terminator").GetProperty("false_target").GetInt64() != 3 ||
                matchBlocks[2].GetProperty("terminator").GetProperty("true_target").GetInt64() != 4 ||
                matchBlocks[2].GetProperty("terminator").GetProperty("false_target").GetInt64() != 5 ||
                matchBlocks[1].GetProperty("terminator").GetProperty("target").GetInt64() != 6 ||
                matchBlocks[3].GetProperty("terminator").GetProperty("target").GetInt64() != 6 ||
                matchBlocks[4].GetProperty("terminator").GetProperty("target").GetInt64() != 6 ||
                matchBlocks[5].GetProperty("terminator").GetProperty("op").GetString() != "return_value" ||
                terminatingBlocks.GetArrayLength() != 3 ||
                terminatingBlocks[1].GetProperty("terminator").GetProperty("op").GetString() != "return_value" ||
                terminatingBlocks[2].GetProperty("terminator").GetProperty("op").GetString() != "return_value")
                throw new Exception("native backend IR did not lower ordered switch/match cases and terminating branches.");
        }
        var irPath = Path.Combine(tempDirectory, "native-switch-match.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native switch/match IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (llvm.Split("icmp eq i64", StringSplitOptions.None).Length - 1 != 5 ||
            llvm.Split("alloca i64", StringSplitOptions.None).Length - 1 != 5 ||
            !llvm.Contains("case.body:", StringComparison.Ordinal) ||
            !llvm.Contains("case.continuation:", StringComparison.Ordinal))
            throw new Exception($"native switch/match IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativeErrorUnionCatchBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath,
        string i32InputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-error-union-catch.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native error-union/catch backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var root = document.RootElement;
            var types = root.GetProperty("types");
            var functions = root.GetProperty("functions");
            var mayFailBlocks = functions[0].GetProperty("blocks");
            var recoverBlocks = functions[1].GetProperty("blocks");
            var propagateBlocks = functions[2].GetProperty("blocks");
            var discardBlocks = functions[3].GetProperty("blocks");
            var voidBlocks = functions[4].GetProperty("blocks");
            var forwardBlocks = functions[5].GetProperty("blocks");
            var mayFailInstructions = mayFailBlocks.EnumerateArray()
                .SelectMany(block => block.GetProperty("instructions").EnumerateArray()).ToArray();
            var recoverInstructions = recoverBlocks.EnumerateArray()
                .SelectMany(block => block.GetProperty("instructions").EnumerateArray()).ToArray();
            var propagateInstructions = propagateBlocks.EnumerateArray()
                .SelectMany(block => block.GetProperty("instructions").EnumerateArray()).ToArray();
            var forwardInstructions = forwardBlocks.EnumerateArray()
                .SelectMany(block => block.GetProperty("instructions").EnumerateArray()).ToArray();
            if (types.GetArrayLength() != 5 ||
                types[4].GetProperty("kind").GetString() != "error_union" ||
                types[4].GetProperty("element_type").GetInt64() != 1 ||
                functions[0].GetProperty("return_type").GetInt64() != 5 ||
                !mayFailInstructions.Any(instruction => instruction.GetProperty("op").GetString() == "aggregate") ||
                recoverInstructions.Count(instruction => instruction.GetProperty("op").GetString() == "extract_value") < 2 ||
                !recoverInstructions.Any(instruction => instruction.GetProperty("op").GetString() == "phi") ||
                propagateBlocks[1].GetProperty("terminator").GetProperty("op").GetString() != "return_value" ||
                !propagateInstructions.Any(instruction => instruction.GetProperty("op").GetString() == "aggregate") ||
                discardBlocks[1].GetProperty("terminator").GetProperty("target").GetInt64() != 4 ||
                discardBlocks[3].GetProperty("terminator").GetProperty("op").GetString() != "return_value" ||
                functions[4].GetProperty("return_type").GetInt64() != 4 ||
                voidBlocks[0].GetProperty("terminator").GetProperty("op").GetString() != "return_void" ||
                !forwardInstructions.Any(instruction => instruction.GetProperty("op").GetString() == "call") ||
                forwardBlocks[0].GetProperty("terminator").GetProperty("op").GetString() != "return_value")
                throw new Exception("native backend IR did not preserve error-union, catch, propagation, discard, and void contracts.");
        }
        var irPath = Path.Combine(tempDirectory, "native-error-union-catch.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native error-union/catch IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (!llvm.Contains("%zorb.result.i64 = type { i64, i32 }", StringComparison.Ordinal) ||
            !llvm.Contains("define %zorb.result.i64 @may_fail(i64 %flag)", StringComparison.Ordinal) ||
            !llvm.Contains("phi i64", StringComparison.Ordinal) ||
            llvm.Split("extractvalue %zorb.result.i64", StringSplitOptions.None).Length - 1 != 6 ||
            !llvm.Contains("ret void", StringComparison.Ordinal))
            throw new Exception($"native error-union/catch IR produced unexpected LLVM.\n{llvm}".Trim());

        var i32LlvmPath = Path.Combine(tempDirectory, "native-error-union-i32.ll");
        var i32Execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), i32LlvmPath, i32InputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (i32Execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(i32Execution.StdErr))
            throw new Exception($"native i32 error-union emission failed.\n{i32Execution.StdErr}{i32Execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(i32Execution.StdOut))
        {
            var functions = document.RootElement.GetProperty("functions");
            var successInstructions = functions[0].GetProperty("blocks")[0].GetProperty("instructions");
            var successAggregate = successInstructions.EnumerateArray()
                .Single(instruction => instruction.GetProperty("op").GetString() == "aggregate");
            var errorInstructions = functions[1].GetProperty("blocks")[0].GetProperty("instructions");
            var errorAggregate = errorInstructions.EnumerateArray()
                .Single(instruction => instruction.GetProperty("op").GetString() == "aggregate");
            var errorZero = errorInstructions.EnumerateArray()
                .Single(instruction => instruction.GetProperty("op").GetString() == "zero_constant");
            if (successAggregate.GetProperty("op").GetString() != "aggregate" ||
                successAggregate.GetProperty("arguments").GetArrayLength() != 2 ||
                errorAggregate.GetProperty("arguments")[0].GetInt64() != errorZero.GetProperty("id").GetInt64() ||
                errorAggregate.GetProperty("arguments").GetArrayLength() != 2)
                throw new Exception("native !i32 lowering confused an ordinary i32 local with a catch error binding.");
        }
        var i32IrPath = Path.Combine(tempDirectory, "native-error-union-i32.json");
        File.WriteAllText(i32IrPath, i32Execution.StdOut);
        var i32Backend = EmitBackendArtifact(GetLlvmBackendPath(), i32IrPath, tempDirectory);
        if (i32Backend.ExitCode != 0 || !File.Exists(i32LlvmPath))
            throw new Exception($"Zig backend rejected native i32 error-union IR.\n{i32Backend.StdErr}{i32Backend.StdOut}".Trim());
    }

    private static void AssertNativeUnsignedBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-unsigned.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native unsigned backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var root = document.RootElement;
            var functions = root.GetProperty("functions");
            if (root.GetProperty("types")[0].GetProperty("scalar").GetString() != "u8" ||
                !functions[0].GetProperty("blocks")[0].GetProperty("instructions").EnumerateArray().Any(
                    instruction => instruction.GetProperty("op").GetString() == "binary" && instruction.GetProperty("binary_op").GetString() == "unsigned_div") ||
                !functions[1].GetProperty("blocks")[0].GetProperty("instructions").EnumerateArray().Any(
                    instruction => instruction.GetProperty("op").GetString() == "binary" && instruction.GetProperty("binary_op").GetString() == "unsigned_rem") ||
                !functions[2].GetProperty("blocks")[0].GetProperty("instructions").EnumerateArray().Any(
                    instruction => instruction.GetProperty("op").GetString() == "binary" && instruction.GetProperty("binary_op").GetString() == "logical_shift_right") ||
                !functions[3].GetProperty("blocks")[0].GetProperty("instructions").EnumerateArray().Any(
                    instruction => instruction.GetProperty("op").GetString() == "compare" && instruction.GetProperty("compare_op").GetString() == "unsigned_less"))
                throw new Exception("native backend IR did not select unsigned arithmetic, shift, and comparison operations.");
        }
        var irPath = Path.Combine(tempDirectory, "native-unsigned.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native unsigned IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (!llvm.Contains("udiv i8", StringComparison.Ordinal) ||
            !llvm.Contains("urem i8", StringComparison.Ordinal) ||
            !llvm.Contains("lshr i8", StringComparison.Ordinal) ||
            !llvm.Contains("icmp ult i8", StringComparison.Ordinal))
            throw new Exception($"native unsigned IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativeMixedScalarsBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-mixed-scalars.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native mixed-scalar backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var root = document.RootElement;
            var types = root.GetProperty("types");
            var functions = root.GetProperty("functions");
            if (types.GetArrayLength() != 11 ||
                types[0].GetProperty("scalar").GetString() != "i64" ||
                types[1].GetProperty("scalar").GetString() != "bool" ||
                types[2].GetProperty("scalar").GetString() != "u8" ||
                types[3].GetProperty("scalar").GetString() != "u16" ||
                types[4].GetProperty("scalar").GetString() != "u64" ||
                types[5].GetProperty("scalar").GetString() != "i32" ||
                types[6].GetProperty("scalar").GetString() != "i16" ||
                types[7].GetProperty("scalar").GetString() != "void" ||
                types[8].GetProperty("element_type").GetInt64() != 7 ||
                types[9].GetProperty("element_type").GetInt64() != 5 ||
                types[10].GetProperty("scalar").GetString() != "u32" ||
                functions[1].GetProperty("return_type").GetInt64() != 2 ||
                functions[1].GetProperty("parameters")[0].GetProperty("type").GetInt64() != 3 ||
                !functions[1].GetProperty("blocks")[0].GetProperty("instructions").EnumerateArray().Any(
                    instruction => instruction.GetProperty("op").GetString() == "compare" && instruction.GetProperty("compare_op").GetString() == "unsigned_less") ||
                !functions[2].GetProperty("blocks")[0].GetProperty("instructions").EnumerateArray().Any(instruction => instruction.GetProperty("type").GetInt64() == 3) ||
                !functions[4].GetProperty("blocks")[0].GetProperty("instructions").EnumerateArray().Any(
                    instruction => instruction.GetProperty("op").GetString() == "compare" && instruction.GetProperty("compare_op").GetString() == "unsigned_less") ||
                !functions[4].GetProperty("blocks")[0].GetProperty("instructions").EnumerateArray().Any(instruction => instruction.GetProperty("type").GetInt64() == 2) ||
                functions[5].GetProperty("return_type").GetInt64() != 4 ||
                functions[5].GetProperty("parameters")[0].GetProperty("type").GetInt64() != 2 ||
                functions[5].GetProperty("parameters")[1].GetProperty("type").GetInt64() != 4 ||
                !functions[6].GetProperty("blocks")[0].GetProperty("instructions").EnumerateArray().Any(
                    instruction => instruction.GetProperty("op").GetString() == "binary" && instruction.GetProperty("binary_op").GetString() == "unsigned_div") ||
                functions[7].GetProperty("return_type").GetInt64() != 8 ||
                functions[7].GetProperty("parameters")[0].GetProperty("type").GetInt64() != 6 ||
                functions[8].GetProperty("return_type").GetInt64() != 9 ||
                !functions[9].GetProperty("blocks")[3].GetProperty("instructions").EnumerateArray().Any(instruction => instruction.GetProperty("type").GetInt64() == 7) ||
                functions[10].GetProperty("return_type").GetInt64() != 10 ||
                !functions[11].GetProperty("blocks")[3].GetProperty("instructions").EnumerateArray().Any(instruction => instruction.GetProperty("type").GetInt64() == 5) ||
                functions[12].GetProperty("blocks")[0].GetProperty("instructions").EnumerateArray().Count(instruction => instruction.GetProperty("type").GetInt64() == 11) < 2)
                throw new Exception("native mixed-scalar IR did not preserve signature, operand, call, and error-union type identities.");
        }
        var irPath = Path.Combine(tempDirectory, "native-mixed-scalars.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native mixed-scalar IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (!llvm.Contains("%zorb.result.i16 = type { i16, i32 }", StringComparison.Ordinal) ||
            !llvm.Contains("%zorb.result.u64 = type { i64, i32 }", StringComparison.Ordinal) ||
            llvm.Split("icmp ult i8", StringSplitOptions.None).Length - 1 != 2 ||
            !llvm.Contains("call i32 @identity_bool", StringComparison.Ordinal) ||
            !llvm.Contains("udiv i64", StringComparison.Ordinal) ||
            !llvm.Contains("phi i16", StringComparison.Ordinal) ||
            !llvm.Contains("phi i64", StringComparison.Ordinal) ||
            !llvm.Contains("define void @consume_i32(i32 %value)", StringComparison.Ordinal))
            throw new Exception($"native mixed-scalar IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativePointersArraysBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-pointers-arrays.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native pointer/array backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var root = document.RootElement;
            var types = root.GetProperty("types");
            var pointerType = types.EnumerateArray().Single(type => type.GetProperty("kind").GetString() == "pointer");
            var arrayTypes = types.EnumerateArray().Where(type => type.GetProperty("kind").GetString() == "array").ToArray();
            var arrayTwoType = arrayTypes.Single(type => type.GetProperty("length").GetInt64() == 2);
            var arrayThreeType = arrayTypes.Single(type => type.GetProperty("length").GetInt64() == 3);
            var functions = root.GetProperty("functions");
            var pointerParameter = functions.EnumerateArray().Single(function => function.GetProperty("name").GetString() == "pointer_parameter");
            var arrayCopy = functions.EnumerateArray().Single(function => function.GetProperty("name").GetString() == "array_copy");
            var pointerIndex = pointerParameter.GetProperty("blocks")[0].GetProperty("instructions").EnumerateArray()
                .Single(instruction => instruction.GetProperty("op").GetString() == "index_address");
            var copiedInstructions = arrayCopy.GetProperty("blocks")[0].GetProperty("instructions");
            var indexedStoreAddress = copiedInstructions.EnumerateArray().First(instruction =>
                instruction.GetProperty("op").GetString() == "index_address");
            if (pointerType.GetProperty("element_type").GetInt64() != 1 ||
                arrayTypes.Length != 2 ||
                arrayTwoType.GetProperty("element_type").GetInt64() != 1 ||
                arrayThreeType.GetProperty("element_type").GetInt64() != 1 ||
                pointerIndex.GetProperty("source_type").GetInt64() != 1 ||
                indexedStoreAddress.GetProperty("source_type").GetInt64() != arrayThreeType.GetProperty("id").GetInt64())
                throw new Exception("native pointer/array IR did not preserve pointee, array length, or index source types.");
        }
        var irPath = Path.Combine(tempDirectory, "native-pointers-arrays.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native pointer/array IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (!llvm.Contains("store [3 x i64] [i64 10, i64 20, i64 30]", StringComparison.Ordinal) ||
            !llvm.Contains("load [3 x i64]", StringComparison.Ordinal) ||
            !llvm.Contains("getelementptr [3 x i64]", StringComparison.Ordinal) ||
            !llvm.Contains("getelementptr i64, ptr %", StringComparison.Ordinal) ||
            !llvm.Contains("getelementptr [2 x i64]", StringComparison.Ordinal) ||
            !llvm.Contains("define ptr @identity_pointer(ptr %pointer)", StringComparison.Ordinal) ||
            !llvm.Contains("call ptr @identity_pointer(ptr %", StringComparison.Ordinal) ||
            !llvm.Contains("define [2 x i64] @make_array()", StringComparison.Ordinal) ||
            !llvm.Contains("call [2 x i64] @make_array()", StringComparison.Ordinal) ||
            !llvm.Contains("call i64 @first_array([2 x i64]", StringComparison.Ordinal))
            throw new Exception($"native pointer/array IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativeSlicesStringsBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-slices-strings.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native slice/string backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var root = document.RootElement;
            var types = root.GetProperty("types");
            var stringType = types.EnumerateArray().Single(type => type.GetProperty("kind").GetString() == "string");
            var slices = types.EnumerateArray().Where(type => type.GetProperty("kind").GetString() == "slice").ToArray();
            var functions = root.GetProperty("functions");
            var literal = functions.EnumerateArray().Single(function => function.GetProperty("name").GetString() == "literal_string");
            var conversion = functions.EnumerateArray().Single(function => function.GetProperty("name").GetString() == "array_to_slice");
            var indexing = functions.EnumerateArray().Single(function => function.GetProperty("name").GetString() == "slice_index");
            var conversionInstructions = conversion.GetProperty("blocks")[0].GetProperty("instructions");
            var sliceAggregate = conversionInstructions.EnumerateArray().Single(instruction =>
                instruction.GetProperty("op").GetString() == "aggregate" &&
                instruction.GetProperty("arguments").GetArrayLength() == 2);
            if (stringType.GetProperty("element_type").GetInt64() <= 0 ||
                slices.Length != 2 ||
                slices.Any(type => string.IsNullOrWhiteSpace(type.GetProperty("name").GetString())) ||
                literal.GetProperty("blocks")[0].GetProperty("instructions")[0].GetProperty("text").GetString() != "zorb" ||
                sliceAggregate.GetProperty("type").GetInt64() <= 0 ||
                indexing.GetProperty("blocks").GetArrayLength() != 3 ||
                indexing.GetProperty("blocks")[1].GetProperty("instructions")[1].GetProperty("op").GetString() != "process_exit" ||
                indexing.GetProperty("blocks")[1].GetProperty("terminator").GetProperty("op").GetString() != "unreachable" ||
                indexing.GetProperty("blocks")[2].GetProperty("instructions")[0].GetProperty("source_type").GetInt64() <= 0)
                throw new Exception("native slice/string IR did not preserve representation, coercion, or checked-index control flow.");
        }
        var irPath = Path.Combine(tempDirectory, "native-slices-strings.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native slice/string IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (!llvm.Contains("private unnamed_addr constant [5 x i8] c\"zorb\\00\"", StringComparison.Ordinal) ||
            !llvm.Contains("type { ptr, i64 }", StringComparison.Ordinal) ||
            !llvm.Contains("insertvalue", StringComparison.Ordinal) ||
            !llvm.Contains("extractvalue", StringComparison.Ordinal) ||
            !llvm.Contains("icmp uge i64 %", StringComparison.Ordinal) ||
            !llvm.Contains("icmp slt i64 %", StringComparison.Ordinal) ||
            !llvm.Contains("getelementptr i64", StringComparison.Ordinal) ||
            !llvm.Contains("unreachable", StringComparison.Ordinal))
            throw new Exception($"native slice/string IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativeNominalAggregatesBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string structsInputPath,
        string enumsInputPath,
        string unionsInputPath)
    {
        var inputs = new[]
        {
            (Name: "structs", Input: structsInputPath),
            (Name: "enums", Input: enumsInputPath),
            (Name: "unions", Input: unionsInputPath)
        };
        var jsonByName = new Dictionary<string, string>(StringComparer.Ordinal);
        var llvmByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in inputs)
        {
            var llvmPath = Path.Combine(tempDirectory, $"native-{item.Name}.ll");
            var execution = RunProcessWithTimeoutArgs(
                binaryPath,
                ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, item.Input],
                workingDirectory,
                TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
            if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
                throw new Exception($"native {item.Name} backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
            var irPath = Path.Combine(tempDirectory, $"native-{item.Name}.json");
            File.WriteAllText(irPath, execution.StdOut);
            var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
            if (backend.ExitCode != 0 || !File.Exists(llvmPath))
                throw new Exception($"Zig backend rejected native {item.Name} IR.\n{backend.StdErr}{backend.StdOut}".Trim());
            jsonByName.Add(item.Name, execution.StdOut);
            llvmByName.Add(item.Name, File.ReadAllText(llvmPath));
        }

        using (var document = System.Text.Json.JsonDocument.Parse(jsonByName["structs"]))
        {
            var root = document.RootElement;
            var types = root.GetProperty("types");
            var pair = types.EnumerateArray().Single(type =>
                type.GetProperty("kind").GetString() == "struct" &&
                type.GetProperty("name").GetString() == "Pair");
            var node = types.EnumerateArray().Single(type =>
                type.GetProperty("kind").GetString() == "struct" &&
                type.GetProperty("name").GetString() == "Node");
            var nodePointer = types.EnumerateArray().Single(type =>
                type.GetProperty("kind").GetString() == "pointer" &&
                type.GetProperty("element_type").GetInt64() == node.GetProperty("id").GetInt64());
            var copy = root.GetProperty("functions").EnumerateArray().Single(function =>
                function.GetProperty("name").GetString() == "copy_and_update");
            if (pair.GetProperty("fields")[0].GetProperty("name").GetString() != "lhs" ||
                pair.GetProperty("fields")[1].GetProperty("name").GetString() != "rhs" ||
                node.GetProperty("fields")[1].GetProperty("type").GetInt64() != nodePointer.GetProperty("id").GetInt64() ||
                !copy.GetProperty("blocks")[0].GetProperty("instructions").EnumerateArray().Any(instruction =>
                    instruction.GetProperty("op").GetString() == "field_address" &&
                    instruction.GetProperty("field_index").GetInt64() == 1))
                throw new Exception("native struct IR did not preserve declaration order, recursion, or field addressing.");
        }

        using (var document = System.Text.Json.JsonDocument.Parse(jsonByName["enums"]))
        {
            var root = document.RootElement;
            var mode = root.GetProperty("types").EnumerateArray().Single(type =>
                type.GetProperty("kind").GetString() == "enum" &&
                type.GetProperty("name").GetString() == "Mode");
            var current = root.GetProperty("functions").EnumerateArray().Single(function =>
                function.GetProperty("name").GetString() == "current_mode");
            if (mode.GetProperty("element_type").GetInt64() <= 0 ||
                current.GetProperty("blocks")[0].GetProperty("instructions")[0].GetProperty("integer").GetInt64() != 4)
                throw new Exception("native enum IR did not preserve its underlying type or explicit discriminant.");
        }

        using (var document = System.Text.Json.JsonDocument.Parse(jsonByName["unions"]))
        {
            var root = document.RootElement;
            var value = root.GetProperty("types").EnumerateArray().Single(type =>
                type.GetProperty("kind").GetString() == "union" &&
                type.GetProperty("name").GetString() == "Value");
            var score = root.GetProperty("functions").EnumerateArray().Single(function =>
                function.GetProperty("name").GetString() == "score");
            var scoreBlocks = score.GetProperty("blocks");
            var extracts = scoreBlocks.EnumerateArray()
                .SelectMany(block => block.GetProperty("instructions").EnumerateArray())
                .Where(instruction => instruction.GetProperty("op").GetString() == "extract_value")
                .Select(instruction => instruction.GetProperty("field_index").GetInt64())
                .ToArray();
            if (value.GetProperty("fields").GetArrayLength() != 2 ||
                !extracts.Contains(0L) || !extracts.Contains(1L) || !extracts.Contains(2L) ||
                !scoreBlocks.EnumerateArray().Any(block =>
                    block.GetProperty("terminator").GetProperty("op").GetString() == "unreachable"))
                throw new Exception("native union IR did not preserve tag layout, variant extraction, or exhaustive match flow.");
        }

        var structsLlvm = llvmByName["structs"];
        var enumsLlvm = llvmByName["enums"];
        var unionsLlvm = llvmByName["unions"];
        if (!structsLlvm.Contains("%Pair = type { i64, i64 }", StringComparison.Ordinal) ||
            !structsLlvm.Contains("%Node = type { i64, ptr }", StringComparison.Ordinal) ||
            !structsLlvm.Contains("getelementptr", StringComparison.Ordinal) ||
            !enumsLlvm.Contains("define i32 @current_mode()", StringComparison.Ordinal) ||
            !enumsLlvm.Contains("ret i32 4", StringComparison.Ordinal) ||
            !unionsLlvm.Contains("%Value = type { i32, i64, i32 }", StringComparison.Ordinal) ||
            !unionsLlvm.Contains("extractvalue %Value %", StringComparison.Ordinal) ||
            !unionsLlvm.Contains("unreachable", StringComparison.Ordinal))
            throw new Exception("native nominal aggregate IR produced unexpected LLVM.");
    }

    private static void AssertNativeGlobalsConstantsBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-globals-constants.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native globals/constants backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var root = document.RootElement;
            var globals = root.GetProperty("globals");
            var answer = globals.EnumerateArray().Single(global => global.GetProperty("name").GetString() == "answer");
            var counter = globals.EnumerateArray().Single(global => global.GetProperty("name").GetString() == "counter");
            var minimum = globals.EnumerateArray().Single(global => global.GetProperty("name").GetString() == "minimum");
            var label = globals.EnumerateArray().Single(global => global.GetProperty("name").GetString() == "label");
            var values = globals.EnumerateArray().Single(global => global.GetProperty("name").GetString() == "values");
            var pair = globals.EnumerateArray().Single(global => global.GetProperty("name").GetString() == "pair");
            var value = globals.EnumerateArray().Single(global => global.GetProperty("name").GetString() == "value");
            var bump = root.GetProperty("functions").EnumerateArray().Single(function =>
                function.GetProperty("name").GetString() == "bump_counter");
            var bumpInstructions = bump.GetProperty("blocks")[0].GetProperty("instructions");
            if (globals.GetArrayLength() != 8 ||
                !answer.GetProperty("constant").GetBoolean() ||
                answer.GetProperty("linkage").GetString() != "external" ||
                answer.GetProperty("initializer").GetProperty("integer").GetInt64() != 42 ||
                counter.GetProperty("constant").GetBoolean() ||
                minimum.GetProperty("initializer").GetProperty("integer").GetInt64() != long.MinValue ||
                label.GetProperty("initializer").GetProperty("text").GetString() != "global" ||
                values.GetProperty("initializer").GetProperty("elements").GetArrayLength() != 3 ||
                pair.GetProperty("initializer").GetProperty("elements").GetArrayLength() != 2 ||
                value.GetProperty("initializer").GetProperty("elements").GetArrayLength() != 3 ||
                !bumpInstructions.EnumerateArray().Any(instruction =>
                    instruction.GetProperty("op").GetString() == "global_address" &&
                    instruction.GetProperty("global").GetInt64() == counter.GetProperty("id").GetInt64()) ||
                !bumpInstructions.EnumerateArray().Any(instruction => instruction.GetProperty("op").GetString() == "store"))
                throw new Exception("native global IR did not preserve linkage, mutability, initializers, or address-based access.");
        }
        var irPath = Path.Combine(tempDirectory, "native-globals-constants.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native globals/constants IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (!llvm.Contains("@answer = constant i64 42", StringComparison.Ordinal) ||
            !llvm.Contains("@counter = internal global i64 3", StringComparison.Ordinal) ||
            !llvm.Contains("@negative = internal constant i32 -7", StringComparison.Ordinal) ||
            !llvm.Contains("@minimum = internal constant i64 -9223372036854775808", StringComparison.Ordinal) ||
            !llvm.Contains("@label = internal constant ptr @.str.global", StringComparison.Ordinal) ||
            !llvm.Contains("@values = internal global [3 x i64] [i64 10, i64 20, i64 30]", StringComparison.Ordinal) ||
            !llvm.Contains("@pair = internal global %Pair { i64 4, i64 5 }", StringComparison.Ordinal) ||
            !llvm.Contains("@value = internal global %Value { i32 0, i64 7, i32 0 }", StringComparison.Ordinal) ||
            !llvm.Contains("store i64 %1, ptr @counter", StringComparison.Ordinal) ||
            !llvm.Contains("getelementptr ([3 x i64], ptr @values", StringComparison.Ordinal))
            throw new Exception($"native globals/constants IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativeCastsFunctionValuesBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string castsInputPath,
        string functionValuesInputPath)
    {
        var inputs = new[]
        {
            (Name: "casts", Input: castsInputPath),
            (Name: "function-values", Input: functionValuesInputPath)
        };
        var jsonByName = new Dictionary<string, string>(StringComparer.Ordinal);
        var llvmByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in inputs)
        {
            var llvmPath = Path.Combine(tempDirectory, $"native-{item.Name}.ll");
            var execution = RunProcessWithTimeoutArgs(
                binaryPath,
                ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, item.Input],
                workingDirectory,
                TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
            if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
                throw new Exception($"native {item.Name} backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
            var irPath = Path.Combine(tempDirectory, $"native-{item.Name}.json");
            File.WriteAllText(irPath, execution.StdOut);
            var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
            if (backend.ExitCode != 0 || !File.Exists(llvmPath))
                throw new Exception($"Zig backend rejected native {item.Name} IR.\n{backend.StdErr}{backend.StdOut}".Trim());
            jsonByName.Add(item.Name, execution.StdOut);
            llvmByName.Add(item.Name, File.ReadAllText(llvmPath));
        }

        using (var document = System.Text.Json.JsonDocument.Parse(jsonByName["casts"]))
        {
            var castOps = document.RootElement.GetProperty("functions").EnumerateArray()
                .SelectMany(function => function.GetProperty("blocks").EnumerateArray())
                .SelectMany(block => block.GetProperty("instructions").EnumerateArray())
                .Where(instruction => instruction.GetProperty("op").GetString() == "cast")
                .Select(instruction => instruction.GetProperty("cast_op").GetString())
                .ToArray();
            if (!castOps.Contains("truncate", StringComparer.Ordinal) ||
                !castOps.Contains("sign_extend", StringComparer.Ordinal) ||
                !castOps.Contains("zero_extend", StringComparer.Ordinal) ||
                !castOps.Contains("pointer_to_integer", StringComparer.Ordinal) ||
                !castOps.Contains("integer_to_pointer", StringComparer.Ordinal))
                throw new Exception("native cast IR did not cover numeric-width and pointer/integer conversions.");
        }

        using (var document = System.Text.Json.JsonDocument.Parse(jsonByName["function-values"]))
        {
            var root = document.RootElement;
            var functionType = root.GetProperty("types").EnumerateArray().Single(type =>
                type.GetProperty("kind").GetString() == "function");
            var global = root.GetProperty("globals").EnumerateArray().Single(item =>
                item.GetProperty("name").GetString() == "global_increment");
            var instructions = root.GetProperty("functions").EnumerateArray()
                .SelectMany(function => function.GetProperty("blocks").EnumerateArray())
                .SelectMany(block => block.GetProperty("instructions").EnumerateArray())
                .ToArray();
            if (functionType.GetProperty("element_type").GetInt64() <= 0 ||
                functionType.GetProperty("fields").GetArrayLength() != 1 ||
                global.GetProperty("initializer").GetProperty("kind").GetString() != "function" ||
                !instructions.Any(instruction => instruction.GetProperty("op").GetString() == "function_address") ||
                !instructions.Any(instruction =>
                    instruction.GetProperty("op").GetString() == "indirect_call" &&
                    instruction.GetProperty("source_type").GetInt64() == functionType.GetProperty("id").GetInt64()))
                throw new Exception("native function-value IR did not preserve structural signatures, addresses, or indirect calls.");
        }

        var castsLlvm = llvmByName["casts"];
        var functionValuesLlvm = llvmByName["function-values"];
        if (!castsLlvm.Contains("trunc i64 %", StringComparison.Ordinal) ||
            !castsLlvm.Contains("sext i8 %", StringComparison.Ordinal) ||
            !castsLlvm.Contains("zext i8 %", StringComparison.Ordinal) ||
            !castsLlvm.Contains("ptrtoint ptr %", StringComparison.Ordinal) ||
            !castsLlvm.Contains("inttoptr i64 %", StringComparison.Ordinal) ||
            !castsLlvm.Contains("ret ptr null", StringComparison.Ordinal) ||
            !functionValuesLlvm.Contains("@global_increment = internal constant ptr @increment", StringComparison.Ordinal) ||
            !functionValuesLlvm.Contains("call i64 %", StringComparison.Ordinal) ||
            !functionValuesLlvm.Contains("ret ptr @increment", StringComparison.Ordinal) ||
            !functionValuesLlvm.Contains("call i64 %8(i64 2)", StringComparison.Ordinal))
            throw new Exception("native casts/function-values IR produced unexpected LLVM.");
    }

    private static void AssertNativeBuiltinsPlatformBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-builtins-platform.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native builtins/platform backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var instructions = document.RootElement.GetProperty("functions").EnumerateArray()
                .SelectMany(function => function.GetProperty("blocks").EnumerateArray())
                .SelectMany(block => block.GetProperty("instructions").EnumerateArray())
                .ToArray();
            var inlineAsm = instructions.Single(instruction => instruction.GetProperty("op").GetString() == "inline_asm");
            if (!instructions.Any(instruction => instruction.GetProperty("op").GetString() == "size_of") ||
                !instructions.Any(instruction => instruction.GetProperty("op").GetString() == "syscall") ||
                inlineAsm.GetProperty("asm_template").GetString() != "addq $$1, $0" ||
                inlineAsm.GetProperty("constraints").GetString() != "=r,0" ||
                inlineAsm.GetProperty("output_types").GetArrayLength() != 1 ||
                inlineAsm.GetProperty("output_addresses").GetArrayLength() != 1)
                throw new Exception("native builtin/platform IR did not preserve sizeof, syscall, or inline-asm contracts.");
        }
        var irPath = Path.Combine(tempDirectory, "native-builtins-platform.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native builtins/platform IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (!llvm.Contains("ret i64 8", StringComparison.Ordinal) ||
            !llvm.Contains("ret i32 64", StringComparison.Ordinal) ||
            !llvm.Contains("asm sideeffect \"syscall\"", StringComparison.Ordinal) ||
            !llvm.Contains("asm sideeffect \"addq $$1, $0\", \"=r,0\"", StringComparison.Ordinal))
            throw new Exception($"native builtins/platform IR produced unexpected LLVM.\n{llvm}".Trim());
    }

    private static void AssertNativeGenericsBackendIr(
        string binaryPath,
        string workingDirectory,
        string tempDirectory,
        string inputPath)
    {
        var llvmPath = Path.Combine(tempDirectory, "native-generics.ll");
        var execution = RunProcessWithTimeoutArgs(
            binaryPath,
            ["--emit-backend-ir", GetNativeLlvmTriple(), llvmPath, inputPath],
            workingDirectory,
            TimeSpan.FromSeconds(SelfCheckTimeoutSeconds));
        if (execution.ExitCode != 0 || !string.IsNullOrWhiteSpace(execution.StdErr))
            throw new Exception($"native generic backend IR emission failed.\n{execution.StdErr}{execution.StdOut}".Trim());
        using (var document = System.Text.Json.JsonDocument.Parse(execution.StdOut))
        {
            var functions = document.RootElement.GetProperty("functions");
            var instances = functions.EnumerateArray()
                .Where(function => function.GetProperty("name").GetString()!.StartsWith("identity$g$", StringComparison.Ordinal))
                .ToArray();
            var calls = functions.EnumerateArray()
                .SelectMany(function => function.GetProperty("blocks").EnumerateArray())
                .SelectMany(block => block.GetProperty("instructions").EnumerateArray())
                .Where(instruction => instruction.GetProperty("op").GetString() == "call")
                .ToArray();
            if (instances.Length != 2 ||
                instances.Select(instance => instance.GetProperty("return_type").GetInt64()).Distinct().Count() != 2 ||
                !calls.Any(call => call.GetProperty("callee").GetInt64() == instances[0].GetProperty("id").GetInt64()) ||
                !calls.Any(call => call.GetProperty("callee").GetInt64() == instances[1].GetProperty("id").GetInt64()))
                throw new Exception("native generic IR did not deduplicate and specialize concrete function instances.");
        }
        var irPath = Path.Combine(tempDirectory, "native-generics.json");
        File.WriteAllText(irPath, execution.StdOut);
        var backend = EmitBackendArtifact(GetLlvmBackendPath(), irPath, tempDirectory);
        if (backend.ExitCode != 0 || !File.Exists(llvmPath))
            throw new Exception($"Zig backend rejected native generic IR.\n{backend.StdErr}{backend.StdOut}".Trim());
        var llvm = File.ReadAllText(llvmPath);
        if (llvm.Split("define i64 @\"identity$g$", StringSplitOptions.None).Length - 1 != 1 ||
            llvm.Split("define i32 @\"identity$g$", StringSplitOptions.None).Length - 1 != 1 ||
            !llvm.Contains("store ptr @\"identity$g$", StringComparison.Ordinal) ||
            !llvm.Contains("call i64 @apply_i64", StringComparison.Ordinal))
            throw new Exception($"native generic IR produced unexpected LLVM.\n{llvm}".Trim());
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
