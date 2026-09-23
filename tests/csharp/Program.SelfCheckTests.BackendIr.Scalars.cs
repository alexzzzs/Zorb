internal static partial class Program
{
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
}
