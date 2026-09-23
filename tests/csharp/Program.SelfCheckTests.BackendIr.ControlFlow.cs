internal static partial class Program
{
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
}
