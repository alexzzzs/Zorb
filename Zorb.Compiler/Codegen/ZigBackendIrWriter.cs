using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections;
using System.Reflection;
using Zorb.Compiler.AST;
using Zorb.Compiler.AST.Expressions;
using Zorb.Compiler.AST.Statements;
using Zorb.Compiler.Layouts;
using Zorb.Compiler.Semantic;
using Zorb.Compiler.Utils;

namespace Zorb.Compiler.Codegen;

public enum ZigBackendOutputKind
{
    LlvmIr,
    Bitcode,
    Object,
    Assembly
}

public sealed record ZigBackendTarget(
    string Triple,
    string Cpu = "generic",
    string Features = "",
    string Optimize = "O0");

public sealed partial class ZigBackendIrWriter
{
    public const uint SchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly TypeChecker _typeChecker;

    public ZigBackendIrWriter(TypeChecker typeChecker)
    {
        _typeChecker = typeChecker;
    }

    public string Write(
        IReadOnlyList<Node> nodes,
        string moduleName,
        ZigBackendTarget target,
        ZigBackendOutputKind outputKind,
        string outputPath,
        bool addFreestandingEntryShim = false,
        bool addHostedEntryShim = false)
    {
        var typeInterner = new TypeInterner(nodes);
        var functions = InstantiateGenericFunctions(nodes);
        AddEntryShim(functions, addFreestandingEntryShim, addHostedEntryShim);
        var globals = nodes.OfType<VariableDeclarationNode>().ToList();
        var globalIds = globals
            .Select((global, index) => (global.Name, Id: checked((uint)index + 1)))
            .ToDictionary(item => item.Name, item => item.Id, StringComparer.Ordinal);
        var functionIds = functions
            .Select((function, index) => (
                Name: QualifiedNames.GetFullName(function.NamespacePath, function.Name),
                Id: checked((uint)index + 1)))
            .ToDictionary(item => item.Name, item => item.Id, StringComparer.Ordinal);
        var functionTypes = functions.ToDictionary(
            function => QualifiedNames.GetFullName(function.NamespacePath, function.Name),
            FunctionType,
            StringComparer.Ordinal);

        var module = new BackendModule
        {
            SchemaVersion = SchemaVersion,
            ModuleName = moduleName,
            Target = new BackendTarget
            {
                Triple = target.Triple,
                Cpu = target.Cpu,
                Features = target.Features,
                Optimize = target.Optimize
            },
            OutputKind = outputKind switch
            {
                ZigBackendOutputKind.LlvmIr => "llvm_ir",
                ZigBackendOutputKind.Bitcode => "bitcode",
                ZigBackendOutputKind.Object => "object",
                ZigBackendOutputKind.Assembly => "assembly",
                _ => throw new ArgumentOutOfRangeException(nameof(outputKind), outputKind, null)
            },
            OutputPath = outputPath,
            Functions = functions
                .Select(function => LowerFunction(function, functionIds, functionTypes, globalIds, globals, target, typeInterner))
                .ToList(),
            Globals = globals
                .Select(global => LowerGlobal(global, globalIds[global.Name], functionIds, typeInterner))
                .ToList(),
            Types = typeInterner.Types
        };

        return JsonSerializer.Serialize(module, JsonOptions);
    }

    private sealed partial class ScalarFunctionLowerer
    {
        private readonly TypeChecker _typeChecker;
        private readonly IReadOnlyDictionary<string, uint> _functionIds;
        private readonly IReadOnlyDictionary<string, TypeNode> _functionTypes;
        private readonly IReadOnlyDictionary<string, uint> _globalIds;
        private readonly IReadOnlyDictionary<string, VariableDeclarationNode> _globals;
        private readonly IReadOnlyDictionary<string, uint> _parameterIds;
        private readonly FunctionDecl _function;
        private readonly ZigBackendTarget _target;
        private readonly TypeInterner _typeInterner;
        private readonly List<BackendBlock> _blocks = new();
        private readonly Stack<Dictionary<string, LocalBinding>> _localScopes = new();
        private readonly Stack<LoopTargets> _loopTargets = new();
        private BackendBlock? _currentBlock;
        private uint _nextValueId;
        private uint _nextBlockId = 1;

        private sealed record LocalBinding(uint Address, TypeNode Type, bool IsCatchError = false);
        private sealed record LoopTargets(uint BreakBlock, uint ContinueBlock);
    }

    private sealed class BackendModule
    {
        public uint SchemaVersion { get; init; }
        public string ModuleName { get; init; } = "";
        public BackendTarget Target { get; init; } = new();
        public string OutputKind { get; init; } = "llvm_ir";
        public string OutputPath { get; init; } = "";
        public List<BackendType> Types { get; init; } = new();
        public List<BackendGlobal> Globals { get; init; } = new();
        public List<BackendFunction> Functions { get; init; } = new();
    }

    private sealed class BackendGlobal
    {
        public uint Id { get; init; }
        public string Name { get; init; } = "";
        public uint Type { get; init; }
        public string Linkage { get; init; } = "internal";
        public bool Constant { get; init; }
        public BackendConstant Initializer { get; init; } = new();
    }

    private sealed class BackendConstant
    {
        public string Kind { get; init; } = "zero";
        public long? Integer { get; init; }
        public string? Text { get; init; }
        public uint? Function { get; init; }
        public List<BackendConstant> Elements { get; init; } = new();
    }

    private sealed class BackendType
    {
        public uint Id { get; init; }
        public string Kind { get; set; } = "";
        public string? Name { get; set; }
        public string? Scalar { get; set; }
        public uint? ElementType { get; set; }
        public ulong? Length { get; set; }
        public List<BackendTypeField> Fields { get; set; } = new();
        public bool Packed { get; set; }
    }

    private sealed class BackendTypeField
    {
        public string Name { get; init; } = "";
        public uint Type { get; init; }
    }

    private sealed class BackendTarget
    {
        public string Triple { get; init; } = "";
        public string Cpu { get; init; } = "generic";
        public string Features { get; init; } = "";
        public string Optimize { get; init; } = "O0";
    }

    private sealed class BackendFunction
    {
        public uint Id { get; init; }
        public string Name { get; init; } = "";
        public string Linkage { get; init; } = "external";
        public uint ReturnType { get; init; }
        public List<BackendParameter> Parameters { get; init; } = new();
        public List<BackendBlock> Blocks { get; init; } = new();
    }

    private sealed class BackendParameter
    {
        public uint Id { get; init; }
        public string Name { get; init; } = "";
        public uint Type { get; init; }
    }

    private sealed class BackendBlock
    {
        public uint Id { get; init; }
        public string Name { get; init; } = "";
        public List<BackendInstruction> Instructions { get; init; } = new();
        public BackendTerminator? Terminator { get; set; }
    }

    private sealed class BackendInstruction
    {
        public uint Id { get; init; }
        public string Op { get; init; } = "";
        public uint Type { get; init; }
        public long? Integer { get; init; }
        public string? Text { get; init; }
        public string? BinaryOp { get; init; }
        public string? CompareOp { get; init; }
        public string? CastOp { get; init; }
        public uint? Lhs { get; init; }
        public uint? Rhs { get; init; }
        public uint? Callee { get; init; }
        public List<uint> Arguments { get; init; } = new();
        public List<uint> IncomingValues { get; init; } = new();
        public List<uint> IncomingBlocks { get; init; } = new();
        public uint? SourceType { get; init; }
        public uint? FieldIndex { get; init; }
        public uint? Global { get; init; }
        public string? AsmTemplate { get; init; }
        public string? Constraints { get; init; }
        public List<uint> OutputTypes { get; init; } = new();
        public List<uint> OutputAddresses { get; init; } = new();
    }

    private sealed class BackendTerminator
    {
        public string Op { get; init; } = "";
        public uint? Value { get; init; }
        public uint? Target { get; init; }
        public uint? Condition { get; init; }
        public uint? TrueTarget { get; init; }
        public uint? FalseTarget { get; init; }
    }
}
