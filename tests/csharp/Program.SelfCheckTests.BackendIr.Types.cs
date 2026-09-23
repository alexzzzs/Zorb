internal static partial class Program
{
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
}
