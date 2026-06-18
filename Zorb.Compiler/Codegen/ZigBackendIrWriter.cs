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

public sealed class ZigBackendIrWriter
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

    private static void AddEntryShim(
        List<FunctionDecl> functions,
        bool addFreestandingEntryShim,
        bool addHostedEntryShim)
    {
        var main = functions.FirstOrDefault(function =>
            function.NamespacePath.Count == 0 && function.Name == "main");
        var start = functions.FirstOrDefault(function =>
            function.NamespacePath.Count == 0 && function.Name == "_start");

        if (addFreestandingEntryShim && start == null && main != null)
        {
            var mainType = FunctionType(main);
            var callMain = new CallExpr
            {
                Name = "main",
                ResolvedTargetQualifiedName = "main",
                ResolvedFunctionType = mainType
            };
            Expr exitCode = main.ReturnType.Name == "void"
                ? new NumberExpr { Value = 0 }
                : callMain;
            var exitNumber = new NumberExpr { Value = 60 };
            functions.Add(new FunctionDecl
            {
                Name = "_start",
                ReturnType = new TypeNode { Name = "void" },
                Body = new List<Statement>
                {
                    main.ReturnType.Name == "void"
                        ? new ExpressionStatement { Expression = callMain }
                        : new ExpressionStatement
                        {
                            Expression = new CallExpr
                            {
                                Name = "syscall",
                                Args = new List<Expr> { exitNumber, exitCode },
                                ResolvedTargetQualifiedName = "syscall",
                                ResolvedFunctionType = SyscallType()
                            }
                        },
                    main.ReturnType.Name == "void"
                        ? new ExpressionStatement
                        {
                            Expression = new CallExpr
                            {
                                Name = "syscall",
                                Args = new List<Expr> { exitNumber, exitCode },
                                ResolvedTargetQualifiedName = "syscall",
                                ResolvedFunctionType = SyscallType()
                            }
                        }
                        : new ReturnNode()
                }
            });
            return;
        }

        if (addHostedEntryShim && main == null && start != null)
        {
            var renamedStart = new FunctionDecl
            {
                File = start.File,
                Line = start.Line,
                Column = start.Column,
                Length = start.Length,
                IsExported = start.IsExported,
                NamespacePath = new List<string>(start.NamespacePath),
                Name = "__zorb_user_start",
                Parameters = start.Parameters,
                ReturnType = start.ReturnType,
                Body = start.Body,
                IsExtern = start.IsExtern,
                Attributes = start.Attributes,
                AlignExpr = start.AlignExpr
            };
            functions[functions.IndexOf(start)] = renamedStart;
            functions.Add(new FunctionDecl
            {
                Name = "main",
                ReturnType = new TypeNode { Name = "i32" },
                Body = new List<Statement>
                {
                    new ExpressionStatement
                    {
                        Expression = new CallExpr
                        {
                            Name = "__zorb_user_start",
                            ResolvedTargetQualifiedName = "__zorb_user_start",
                            ResolvedFunctionType = FunctionType(renamedStart)
                        }
                    },
                    new ReturnNode { Value = new NumberExpr { Value = 0 } }
                }
            });
        }
    }

    private static TypeNode FunctionType(FunctionDecl function)
    {
        return new TypeNode
        {
            Name = "fn",
            IsFunction = true,
            ParamTypes = function.Parameters.Select(parameter => parameter.TypeName.Clone()).ToList(),
            ReturnType = function.ReturnType.Clone()
        };
    }

    private static TypeNode SyscallType()
    {
        return new TypeNode
        {
            Name = "fn",
            IsFunction = true,
            ParamTypes = Enumerable.Repeat(new TypeNode { Name = "i64" }, 7).ToList(),
            ReturnType = new TypeNode { Name = "i64" }
        };
    }

    private static List<FunctionDecl> InstantiateGenericFunctions(IReadOnlyList<Node> nodes)
    {
        var definitions = nodes.OfType<FunctionDecl>()
            .Where(function => function.TypeParameters.Count > 0)
            .ToDictionary(
                function => QualifiedNames.GetFullName(function.NamespacePath, function.Name),
                StringComparer.Ordinal);
        var functions = nodes.OfType<FunctionDecl>()
            .Where(function => function.TypeParameters.Count == 0)
            .ToList();
        var pending = new Queue<CallExpr>(CollectCalls(nodes));
        var generated = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var call))
        {
            if (call.TypeArguments.Count == 0)
                continue;
            var resolvedName = call.ResolvedTargetQualifiedName
                ?? call.ResolvedQualifiedName
                ?? QualifiedNames.GetFullName(call.NamespacePath, call.Name);
            if (!definitions.TryGetValue(resolvedName, out var definition))
                continue;
            var instanceName = GenericFunctionName(resolvedName, call.TypeArguments);
            if (!generated.Add(instanceName))
                continue;

            var substitutions = AstSpecialization.BuildTypeSubstitutions(
                definition.TypeParameters,
                call.TypeArguments);
            var instance = AstSpecialization.InstantiateFunction(definition, substitutions);
            var (_, shortName) = QualifiedNames.SplitQualifiedName(instanceName);
            instance.Name = shortName;
            instance.TypeParameters.Clear();
            functions.Add(instance);
            foreach (var nestedCall in CollectCalls(new Node[] { instance }))
                pending.Enqueue(nestedCall);
        }
        return functions;
    }

    private static string GenericFunctionName(
        string resolvedName,
        IReadOnlyList<TypeNode> typeArguments)
    {
        return resolvedName + "$g$" + string.Join("$", typeArguments.Select(FormatTypeKey));
    }

    private static string FormatTypeKey(TypeNode type)
    {
        var key = FormatType(type)
            .Replace(".", "_", StringComparison.Ordinal)
            .Replace("*", "ptr", StringComparison.Ordinal);
        if (type.TypeArguments.Count > 0)
            key += "_" + string.Join("_", type.TypeArguments.Select(FormatTypeKey));
        if (type.IsSlice)
            key = "slice_" + key;
        if (type.ArraySize is int length)
            key = $"array{length}_{key}";
        return key;
    }

    private static IEnumerable<CallExpr> CollectCalls(IEnumerable<Node> roots)
    {
        var calls = new List<CallExpr>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

        void Visit(object? value)
        {
            if (value == null || value is string || value.GetType().IsPrimitive)
                return;
            if (!visited.Add(value))
                return;
            if (value is CallExpr call)
                calls.Add(call);
            if (value is IEnumerable sequence)
            {
                foreach (var item in sequence)
                    Visit(item);
                return;
            }
            if (value is not Node && value is not AsmOperand && value is not MatchCase &&
                value is not SwitchCase && value is not StructLiteralField &&
                value is not Parameter)
                return;
            foreach (var property in value.GetType().GetProperties(
                         BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length == 0)
                    Visit(property.GetValue(value));
            }
        }

        foreach (var root in roots)
            Visit(root);
        return calls;
    }

    private BackendFunction LowerFunction(
        FunctionDecl function,
        IReadOnlyDictionary<string, uint> functionIds,
        IReadOnlyDictionary<string, TypeNode> functionTypes,
        IReadOnlyDictionary<string, uint> globalIds,
        IReadOnlyList<VariableDeclarationNode> globals,
        ZigBackendTarget target,
        TypeInterner typeInterner)
    {
        var fullName = QualifiedNames.GetFullName(function.NamespacePath, function.Name);
        var functionId = functionIds[fullName];
        var nextValueId = 1u;
        var parameterIds = new Dictionary<string, uint>(StringComparer.Ordinal);
        var parameters = new List<BackendParameter>(function.Parameters.Count);
        foreach (var parameter in function.Parameters)
        {
            var parameterId = nextValueId++;
            parameterIds.Add(parameter.Name, parameterId);
            parameters.Add(new BackendParameter
            {
                Id = parameterId,
                Name = parameter.Name,
                Type = typeInterner.Intern(parameter.TypeName)
            });
        }

        if (function.IsExtern)
        {
            return new BackendFunction
            {
                Id = functionId,
                Name = fullName,
                Linkage = "external",
                ReturnType = typeInterner.Intern(function.ReturnType),
                Parameters = parameters
            };
        }

        var lowerer = new ScalarFunctionLowerer(
            _typeChecker,
            functionIds,
            functionTypes,
            globalIds,
            globals.ToDictionary(global => global.Name, StringComparer.Ordinal),
            parameterIds,
            nextValueId,
            function,
            target,
            typeInterner);
        var blocks = lowerer.Lower();

        return new BackendFunction
        {
            Id = functionId,
            Name = fullName,
            Linkage = "external",
            ReturnType = typeInterner.Intern(function.ReturnType),
            Parameters = parameters,
            Blocks = blocks
        };
    }

    private static BackendGlobal LowerGlobal(
        VariableDeclarationNode global,
        uint id,
        IReadOnlyDictionary<string, uint> functionIds,
        TypeInterner typeInterner)
    {
        return new BackendGlobal
        {
            Id = id,
            Name = global.Name,
            Type = typeInterner.Intern(global.TypeName),
            Linkage = global.IsExported ? "external" : "internal",
            Constant = global.IsConst,
            Initializer = LowerGlobalInitializer(global.Value, functionIds)
        };
    }

    private static BackendConstant LowerGlobalInitializer(
        Expr? expression,
        IReadOnlyDictionary<string, uint> functionIds)
    {
        return expression switch
        {
            null => new BackendConstant { Kind = "zero" },
            NumberExpr number => new BackendConstant { Kind = "integer", Integer = number.Value },
            StringExpr text => new BackendConstant { Kind = "string", Text = text.Value },
            BuiltinExpr builtin when builtin.Name == "true"
                => new BackendConstant { Kind = "integer", Integer = 1 },
            BuiltinExpr builtin when builtin.Name == "false"
                => new BackendConstant { Kind = "integer", Integer = 0 },
            UnaryExpr { Operator: "-", Operand: NumberExpr number }
                => new BackendConstant { Kind = "integer", Integer = -number.Value },
            CastExpr { Expr: NumberExpr { Value: 0 } }
                => new BackendConstant { Kind = "zero" },
            CastExpr { Expr: StringExpr text }
                => new BackendConstant { Kind = "string", Text = text.Value },
            CastExpr { Expr: NumberExpr number }
                => new BackendConstant { Kind = "pointer_integer", Integer = number.Value },
            IdentifierExpr identifier when functionIds.TryGetValue(identifier.Name, out var functionId)
                => new BackendConstant { Kind = "function", Function = functionId },
            ArrayLiteralExpr array
                => new BackendConstant
                {
                    Kind = "aggregate",
                    Elements = array.Elements
                        .Select(element => LowerGlobalInitializer(element, functionIds))
                        .ToList()
                },
            _ => throw new ZorbCompilerException(
                $"Zig backend global initializer lowering does not support {expression.GetType().Name}.")
        };
    }

    private static string FormatType(TypeNode type)
    {
        var prefix = type.IsPointer ? new string('*', Math.Max(type.PointerLevel, 1)) : "";
        return prefix + QualifiedNames.GetFullName(type.NamespacePath, type.Name);
    }

    private sealed class ScalarFunctionLowerer
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

        public ScalarFunctionLowerer(
            TypeChecker typeChecker,
            IReadOnlyDictionary<string, uint> functionIds,
            IReadOnlyDictionary<string, TypeNode> functionTypes,
            IReadOnlyDictionary<string, uint> globalIds,
            IReadOnlyDictionary<string, VariableDeclarationNode> globals,
            IReadOnlyDictionary<string, uint> parameterIds,
            uint nextValueId,
            FunctionDecl function,
            ZigBackendTarget target,
            TypeInterner typeInterner)
        {
            _typeChecker = typeChecker;
            _functionIds = functionIds;
            _functionTypes = functionTypes;
            _globalIds = globalIds;
            _globals = globals;
            _parameterIds = parameterIds;
            _nextValueId = nextValueId;
            _function = function;
            _target = target;
            _typeInterner = typeInterner;
        }

        public List<BackendBlock> Lower()
        {
            _currentBlock = CreateBlock("entry");
            PushScope();
            foreach (var parameter in _function.Parameters)
            {
                var parameterValue = _parameterIds[parameter.Name];
                var address = EmitInstruction("alloca", parameter.TypeName);
                _ = EmitInstruction("store", parameter.TypeName, lhs: address, rhs: parameterValue);
                DeclareLocal(parameter.Name, parameter.TypeName, address);
            }
            LowerStatements(_function.Body);
            if (_currentBlock != null && _currentBlock.Terminator == null)
            {
                if (_function.ReturnType.Name == "void")
                    _currentBlock.Terminator = new BackendTerminator { Op = "return_void" };
                else
                    throw Unsupported(_function, $"function '{_function.Name}' has an unterminated value-returning path");
            }

            foreach (var block in _blocks)
            {
                if (block.Terminator == null)
                    throw Unsupported(_function, $"block '{block.Name}' is not terminated");
            }
            PopScope();
            return _blocks;
        }

        private void LowerStatements(IReadOnlyList<Statement> statements)
        {
            foreach (var statement in statements)
            {
                if (_currentBlock == null)
                    break;
                LowerStatement(statement);
            }
        }

        private void LowerStatement(Statement statement)
        {
            switch (statement)
            {
                case ReturnNode returnNode:
                    if (returnNode.Value == null)
                    {
                        Terminate(new BackendTerminator { Op = "return_void" });
                    }
                    else if (_function.ReturnType.IsErrorUnion)
                    {
                        var innerType = _function.ReturnType.ErrorInnerType
                            ?? throw Unsupported(returnNode, "error union has no success type");
                        uint successValue;
                        uint errorValue;
                        if (returnNode.Value is ErrorExpr error)
                        {
                            successValue = EmitZeroConstant(innerType);
                            errorValue = LowerErrorValue(error);
                        }
                        else if (returnNode.Value is IdentifierExpr identifier &&
                                 LookupLocal(identifier.Name) is { IsCatchError: true } catchError)
                        {
                            successValue = EmitZeroConstant(innerType);
                            errorValue = EmitInstruction("load", catchError.Type, lhs: catchError.Address);
                        }
                        else
                        {
                            successValue = LowerExpression(returnNode.Value, innerType);
                            errorValue = EmitIntegerConstant(0, new TypeNode { Name = "i32" });
                        }
                        var result = NextValueId();
                        AddInstruction(new BackendInstruction
                        {
                            Id = result,
                            Op = "aggregate",
                            Type = _typeInterner.Intern(_function.ReturnType),
                            Arguments = new List<uint> { successValue, errorValue }
                        });
                        Terminate(new BackendTerminator { Op = "return_value", Value = result });
                    }
                    else
                    {
                        var valueType = _typeChecker.GetCheckedExpressionType(returnNode.Value)
                            ?? _function.ReturnType;
                        var value = LowerExpression(returnNode.Value, _function.ReturnType);
                        if (IsScalarInteger(valueType) && IsScalarInteger(_function.ReturnType))
                            value = CoerceInteger(value, valueType, _function.ReturnType);
                        Terminate(new BackendTerminator { Op = "return_value", Value = value });
                    }
                    return;

                case ExpressionStatement expressionStatement:
                    if (expressionStatement.Expression is CatchExpr catchExpression)
                        _ = LowerCatch(catchExpression, discardResult: true);
                    else
                        _ = LowerExpression(expressionStatement.Expression);
                    return;

                case VariableDeclarationNode variable:
                {
                    var address = EmitInstruction("alloca", variable.TypeName);
                    DeclareLocal(variable.Name, variable.TypeName, address);
                    if (variable.Value != null)
                    {
                        var value = LowerExpression(variable.Value, variable.TypeName);
                        _ = EmitInstruction("store", variable.TypeName, lhs: address, rhs: value);
                    }
                    return;
                }

                case AssignStmt assignment:
                {
                    var targetType = GetCheckedType(assignment.Target);
                    var address = LowerAddress(assignment.Target);
                    var value = LowerExpression(assignment.Value, targetType);
                    _ = EmitInstruction("store", targetType, lhs: address, rhs: value);
                    return;
                }

                case IfStmt ifStatement:
                    LowerIf(ifStatement);
                    return;

                case WhileStmt whileStatement:
                    LowerWhile(whileStatement);
                    return;

                case ForStmt forStatement:
                    LowerFor(forStatement);
                    return;

                case SwitchStmt switchStatement:
                    LowerSwitch(switchStatement);
                    return;

                case MatchStmt matchStatement:
                    LowerMatch(matchStatement);
                    return;

                case AsmStatementNode asmStatement:
                    LowerAsm(asmStatement);
                    return;

                case BreakStmt:
                    if (_loopTargets.Count == 0)
                        throw Unsupported(statement, "break outside a loop");
                    Terminate(new BackendTerminator
                    {
                        Op = "branch",
                        Target = _loopTargets.Peek().BreakBlock
                    });
                    return;

                case ContinueStmt:
                    if (_loopTargets.Count == 0)
                        throw Unsupported(statement, "continue outside a loop");
                    Terminate(new BackendTerminator
                    {
                        Op = "branch",
                        Target = _loopTargets.Peek().ContinueBlock
                    });
                    return;

                default:
                    throw Unsupported(statement, statement.GetType().Name);
            }
        }

        private void LowerIf(IfStmt statement)
        {
            if (TryEvaluateTargetCondition(statement.Condition, out var fixedCondition))
            {
                PushScope();
                LowerStatements(fixedCondition ? statement.Body : statement.ElseBody);
                PopScope();
                return;
            }

            var condition = LowerExpression(statement.Condition, new TypeNode { Name = "bool" });
            var thenBlock = CreateBlock("if.then");
            var elseBlock = CreateBlock("if.else");
            Terminate(new BackendTerminator
            {
                Op = "conditional_branch",
                Condition = condition,
                TrueTarget = thenBlock.Id,
                FalseTarget = elseBlock.Id
            });

            _currentBlock = thenBlock;
            PushScope();
            LowerStatements(statement.Body);
            PopScope();
            var thenFallsThrough = _currentBlock != null;
            var thenEnd = _currentBlock;

            _currentBlock = elseBlock;
            PushScope();
            LowerStatements(statement.ElseBody);
            PopScope();
            var elseFallsThrough = _currentBlock != null;
            var elseEnd = _currentBlock;

            if (!thenFallsThrough && !elseFallsThrough)
            {
                _currentBlock = null;
                return;
            }

            var mergeBlock = CreateBlock("if.end");
            if (thenEnd != null)
            {
                _currentBlock = thenEnd;
                Terminate(new BackendTerminator { Op = "branch", Target = mergeBlock.Id });
            }
            if (elseEnd != null)
            {
                _currentBlock = elseEnd;
                Terminate(new BackendTerminator { Op = "branch", Target = mergeBlock.Id });
            }
            _currentBlock = mergeBlock;
        }

        private void LowerAsm(AsmStatementNode statement)
        {
            var outputTypes = new List<uint>(statement.Outputs.Count);
            var outputAddresses = new List<uint>(statement.Outputs.Count);
            foreach (var output in statement.Outputs)
            {
                outputTypes.Add(_typeInterner.Intern(GetCheckedType(output.Expression)));
                outputAddresses.Add(LowerAddress(output.Expression));
            }

            var arguments = statement.Inputs
                .Select(input =>
                {
                    var inputType = GetCheckedType(input.Expression);
                    var value = LowerExpression(input.Expression, inputType);
                    if (IsScalarInteger(inputType) && GetScalarBitWidth(inputType) < 32)
                    {
                        value = CoerceInteger(
                            value,
                            inputType,
                            new TypeNode { Name = inputType.Name.StartsWith('i') ? "i32" : "u32" });
                    }
                    return value;
                })
                .ToList();
            var constraints = statement.Outputs.Select(output => ConvertAsmConstraint(output.Constraint))
                .Concat(statement.Inputs.Select(input => ConvertAsmConstraint(input.Constraint)))
                .Concat(statement.Clobbers.Select(clobber => $"~{{{clobber}}}"));

            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "inline_asm",
                Type = _typeInterner.Intern(new TypeNode { Name = "void" }),
                AsmTemplate = ConvertAsmTemplate(string.Join("\n\t", statement.Code)),
                Constraints = string.Join(",", constraints),
                Arguments = arguments,
                OutputTypes = outputTypes,
                OutputAddresses = outputAddresses
            });
        }

        private static string ConvertAsmTemplate(string template)
        {
            const string percentSentinel = "\u0001";
            var converted = template
                .Replace("%%", percentSentinel, StringComparison.Ordinal)
                .Replace("$", "$$", StringComparison.Ordinal);
            converted = System.Text.RegularExpressions.Regex.Replace(
                converted,
                @"%b([0-9]+)",
                match => $"${{{match.Groups[1].Value}:b}}");
            converted = System.Text.RegularExpressions.Regex.Replace(
                converted,
                @"%([0-9]+)",
                match => $"${match.Groups[1].Value}");
            return converted.Replace(percentSentinel, "%", StringComparison.Ordinal);
        }

        private static string ConvertAsmConstraint(string constraint)
        {
            var prefixLength = 0;
            while (prefixLength < constraint.Length &&
                   constraint[prefixLength] is '=' or '+' or '&')
            {
                prefixLength++;
            }
            var prefix = constraint[..prefixLength];
            var body = constraint[prefixLength..];
            var register = body switch
            {
                "a" => "ax",
                "b" => "bx",
                "c" => "cx",
                "d" => "dx",
                "S" => "si",
                "D" => "di",
                _ => null
            };
            return register == null ? constraint : $"{prefix}{{{register}}}";
        }

        private bool TryEvaluateTargetCondition(Expr expression, out bool value)
        {
            switch (expression)
            {
                case BuiltinExpr builtin when builtin.Name is
                    "true" or
                    "false" or
                    "Builtin.IsLinux" or
                    "Builtin.IsWindows" or
                    "Builtin.IsBareMetal" or
                    "Builtin.IsX86_64" or
                    "Builtin.IsAArch64":
                    value = GetBuiltinValue(builtin.Name);
                    return true;

                case UnaryExpr { Operator: "!" } unary
                    when TryEvaluateTargetCondition(unary.Operand, out var operand):
                    value = !operand;
                    return true;

                case BinaryExpr { Operator: "&&" } binary
                    when TryEvaluateTargetCondition(binary.Left, out var left)
                         && TryEvaluateTargetCondition(binary.Right, out var right):
                    value = left && right;
                    return true;

                case BinaryExpr { Operator: "||" } binary
                    when TryEvaluateTargetCondition(binary.Left, out var left)
                         && TryEvaluateTargetCondition(binary.Right, out var right):
                    value = left || right;
                    return true;

                default:
                    value = false;
                    return false;
            }
        }

        private void LowerWhile(WhileStmt statement)
        {
            var conditionBlock = CreateBlock("while.cond");
            var bodyBlock = CreateBlock("while.body");
            var exitBlock = CreateBlock("while.end");
            Terminate(new BackendTerminator { Op = "branch", Target = conditionBlock.Id });

            _currentBlock = conditionBlock;
            var condition = LowerExpression(statement.Condition, new TypeNode { Name = "bool" });
            Terminate(new BackendTerminator
            {
                Op = "conditional_branch",
                Condition = condition,
                TrueTarget = bodyBlock.Id,
                FalseTarget = exitBlock.Id
            });

            _loopTargets.Push(new LoopTargets(exitBlock.Id, conditionBlock.Id));
            _currentBlock = bodyBlock;
            PushScope();
            LowerStatements(statement.Body);
            PopScope();
            _loopTargets.Pop();
            if (_currentBlock != null)
                Terminate(new BackendTerminator { Op = "branch", Target = conditionBlock.Id });

            _currentBlock = exitBlock;
        }

        private void LowerFor(ForStmt statement)
        {
            PushScope();
            if (statement.Initializer != null)
                LowerStatement(statement.Initializer);
            if (_currentBlock == null)
            {
                PopScope();
                return;
            }

            var conditionBlock = CreateBlock("for.cond");
            var bodyBlock = CreateBlock("for.body");
            var updateBlock = CreateBlock("for.update");
            var exitBlock = CreateBlock("for.end");
            Terminate(new BackendTerminator { Op = "branch", Target = conditionBlock.Id });

            _currentBlock = conditionBlock;
            var condition = statement.Condition != null
                ? LowerExpression(statement.Condition, new TypeNode { Name = "bool" })
                : EmitIntegerConstant(1, new TypeNode { Name = "bool" });
            Terminate(new BackendTerminator
            {
                Op = "conditional_branch",
                Condition = condition,
                TrueTarget = bodyBlock.Id,
                FalseTarget = exitBlock.Id
            });

            _loopTargets.Push(new LoopTargets(exitBlock.Id, updateBlock.Id));
            _currentBlock = bodyBlock;
            PushScope();
            LowerStatements(statement.Body);
            PopScope();
            if (_currentBlock != null)
                Terminate(new BackendTerminator { Op = "branch", Target = updateBlock.Id });

            _currentBlock = updateBlock;
            if (statement.Update != null)
                LowerStatement(statement.Update);
            if (_currentBlock != null)
                Terminate(new BackendTerminator { Op = "branch", Target = conditionBlock.Id });
            _loopTargets.Pop();

            _currentBlock = exitBlock;
            PopScope();
        }

        private void LowerSwitch(SwitchStmt statement)
        {
            var switchType = GetCheckedType(statement.Expression);
            var switchValue = LowerExpression(statement.Expression, switchType);
            var exitBlock = CreateBlock("switch.end");
            var hasExitPredecessor = false;

            foreach (var switchCase in statement.Cases)
            {
                var caseBody = CreateBlock("switch.case");
                var nextCase = CreateBlock("switch.next");
                var caseValue = LowerExpression(switchCase.Value, switchType);
                var comparison = EmitComparison("equal", switchValue, caseValue);
                Terminate(new BackendTerminator
                {
                    Op = "conditional_branch",
                    Condition = comparison,
                    TrueTarget = caseBody.Id,
                    FalseTarget = nextCase.Id
                });

                _currentBlock = caseBody;
                PushScope();
                LowerStatements(switchCase.Body);
                PopScope();
                if (_currentBlock != null)
                {
                    Terminate(new BackendTerminator { Op = "branch", Target = exitBlock.Id });
                    hasExitPredecessor = true;
                }
                _currentBlock = nextCase;
            }

            PushScope();
            LowerStatements(statement.ElseBody);
            PopScope();
            if (_currentBlock != null)
            {
                if (statement.ElseBody.Count == 0 && _typeInterner.IsEnumType(switchType))
                {
                    Terminate(new BackendTerminator { Op = "unreachable" });
                }
                else
                {
                    Terminate(new BackendTerminator { Op = "branch", Target = exitBlock.Id });
                    hasExitPredecessor = true;
                }
            }

            if (hasExitPredecessor)
            {
                _currentBlock = exitBlock;
            }
            else
            {
                _blocks.Remove(exitBlock);
                _currentBlock = null;
            }
        }

        private void LowerMatch(MatchStmt statement)
        {
            var matchType = GetCheckedType(statement.Expression);
            var matchValue = LowerExpression(statement.Expression, matchType);
            if (_typeInterner.IsUnionType(matchType))
            {
                LowerUnionMatch(statement, matchType, matchValue);
                return;
            }

            LowerScalarMatch(statement, matchType, matchValue);
        }

        private void LowerScalarMatch(MatchStmt statement, TypeNode matchType, uint matchValue)
        {
            var cases = statement.Cases.Select(matchCase =>
            {
                if (matchCase.Pattern is not QualifiedMatchPattern pattern)
                    throw Unsupported(matchCase.Pattern, "non-scalar match pattern");
                return (Value: pattern.Value, matchCase.Body);
            }).ToList();
            LowerOrderedCases(
                cases.Select(item => (
                    CaseValue: LowerExpression(item.Value, matchType),
                    item.Body,
                    Binding: (Action?)null)).ToList(),
                matchValue,
                statement.ElseBody,
                exhaustiveWithoutElse: statement.ElseBody.Count == 0 &&
                    (_typeInterner.IsEnumType(matchType) || IsBoolType(matchType)));
        }

        private void LowerUnionMatch(MatchStmt statement, TypeNode matchType, uint matchValue)
        {
            var tagType = new TypeNode { Name = "i32" };
            var tag = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = tag,
                Op = "extract_value",
                Type = _typeInterner.Intern(tagType),
                Lhs = matchValue,
                FieldIndex = 0
            });

            var cases = new List<(uint CaseValue, List<Statement> Body, Action? Binding)>();
            foreach (var matchCase in statement.Cases)
            {
                string variantName;
                string? bindingName = null;
                TypeNode? bindingType = null;
                if (matchCase.Pattern is UnionMatchPattern unionPattern &&
                    unionPattern.VariantName != null)
                {
                    variantName = unionPattern.VariantName;
                    bindingName = unionPattern.BindingName;
                    bindingType = unionPattern.BindingType;
                }
                else if (matchCase.Pattern is QualifiedMatchPattern qualified &&
                         QualifiedNames.TryGetQualifiedName(qualified.Value) is string name)
                {
                    variantName = name[(name.LastIndexOf('.') + 1)..];
                }
                else
                {
                    throw Unsupported(matchCase.Pattern, "invalid union match pattern");
                }

                var variantIndex = _typeInterner.GetUnionVariantIndex(matchType, variantName);
                Action? binding = null;
                if (bindingName != null)
                {
                    var capturedName = bindingName;
                    var capturedType = bindingType
                        ?? _typeInterner.GetUnionVariantType(matchType, variantName);
                    binding = () =>
                    {
                        var payload = NextValueId();
                        AddInstruction(new BackendInstruction
                        {
                            Id = payload,
                            Op = "extract_value",
                            Type = _typeInterner.Intern(capturedType),
                            Lhs = matchValue,
                            FieldIndex = variantIndex + 1
                        });
                        var address = EmitInstruction("alloca", capturedType);
                        _ = EmitInstruction("store", capturedType, lhs: address, rhs: payload);
                        DeclareLocal(capturedName, capturedType, address);
                    };
                }
                cases.Add((
                    EmitIntegerConstant(variantIndex, tagType),
                    matchCase.Body,
                    binding));
            }
            LowerOrderedCases(cases, tag, statement.ElseBody, statement.ElseBody.Count == 0);
        }

        private void LowerOrderedCases(
            IReadOnlyList<(uint CaseValue, List<Statement> Body, Action? Binding)> cases,
            uint comparedValue,
            IReadOnlyList<Statement> elseBody,
            bool exhaustiveWithoutElse)
        {
            var exitBlock = CreateBlock("match.end");
            var hasExitPredecessor = false;
            foreach (var matchCase in cases)
            {
                var caseBody = CreateBlock("match.case");
                var nextCase = CreateBlock("match.next");
                var comparison = EmitComparison("equal", comparedValue, matchCase.CaseValue);
                Terminate(new BackendTerminator
                {
                    Op = "conditional_branch",
                    Condition = comparison,
                    TrueTarget = caseBody.Id,
                    FalseTarget = nextCase.Id
                });

                _currentBlock = caseBody;
                PushScope();
                matchCase.Binding?.Invoke();
                LowerStatements(matchCase.Body);
                PopScope();
                if (_currentBlock != null)
                {
                    Terminate(new BackendTerminator { Op = "branch", Target = exitBlock.Id });
                    hasExitPredecessor = true;
                }
                _currentBlock = nextCase;
            }

            PushScope();
            LowerStatements(elseBody);
            PopScope();
            if (_currentBlock != null)
            {
                if (exhaustiveWithoutElse)
                    Terminate(new BackendTerminator { Op = "unreachable" });
                else
                {
                    Terminate(new BackendTerminator { Op = "branch", Target = exitBlock.Id });
                    hasExitPredecessor = true;
                }
            }

            if (hasExitPredecessor)
                _currentBlock = exitBlock;
            else
            {
                _blocks.Remove(exitBlock);
                _currentBlock = null;
            }
        }

        private BackendBlock CreateBlock(string name)
        {
            var id = _nextBlockId++;
            var block = new BackendBlock
            {
                Id = id,
                Name = $"{name}.{id}"
            };
            _blocks.Add(block);
            return block;
        }

        private void Terminate(BackendTerminator terminator)
        {
            var block = _currentBlock
                ?? throw new ZorbCompilerException("Attempted to terminate an unavailable LLVM block.");
            if (block.Terminator != null)
                throw new ZorbCompilerException($"LLVM block '{block.Name}' already has a terminator.");
            block.Terminator = terminator;
            _currentBlock = null;
        }

        private uint LowerExpression(Expr expression, TypeNode? expectedType = null)
        {
            var actualType = expectedType != null ? GetCheckedType(expression) : null;
            if (expectedType?.IsSlice == true && actualType?.ArraySize != null)
            {
                return LowerArrayToSlice(expression, actualType, expectedType);
            }

            if (expectedType?.IsPointer == true &&
                actualType?.ArraySize != null &&
                !expectedType.IsSlice &&
                Math.Max(expectedType.PointerLevel, 1) == 1)
            {
                var zero = EmitIntegerConstant(0, new TypeNode { Name = "i64" });
                return EmitIndexAddress(
                    LowerAddress(expression),
                    zero,
                    actualType,
                    GetArrayElementType(actualType));
            }

            switch (expression)
            {
                case IdentifierExpr identifier:
                {
                    var type = GetCheckedType(identifier);
                    if (type.IsFunction &&
                        _functionIds.TryGetValue(identifier.Name, out var functionId))
                    {
                        var functionValue = NextValueId();
                        AddInstruction(new BackendInstruction
                        {
                            Id = functionValue,
                            Op = "function_address",
                            Type = _typeInterner.Intern(type),
                            Callee = functionId
                        });
                        return functionValue;
                    }
                    return EmitInstruction("load", type, lhs: LowerAddress(identifier));
                }

                case NumberExpr number:
                {
                    var type = expectedType ?? GetCheckedType(expression);
                    return EmitIntegerConstant(number.Value, type);
                }

                case StringExpr text:
                {
                    var id = NextValueId();
                    AddInstruction(new BackendInstruction
                    {
                        Id = id,
                        Op = "string_constant",
                        Type = _typeInterner.Intern(new TypeNode { Name = "string" }),
                        Text = text.Value
                    });
                    return id;
                }

                case ArrayLiteralExpr arrayLiteral:
                {
                    var arrayType = arrayLiteral.TypeName;
                    var elementType = GetArrayElementType(arrayType);
                    var elements = arrayLiteral.Elements
                        .Select(element => LowerExpression(element, elementType))
                        .ToList();
                    var id = NextValueId();
                    AddInstruction(new BackendInstruction
                    {
                        Id = id,
                        Op = "aggregate",
                        Type = _typeInterner.Intern(arrayType),
                        Arguments = elements
                    });
                    return id;
                }

                case StructLiteralExpr structLiteral:
                {
                    if (_typeInterner.IsUnionType(structLiteral.TypeName))
                    {
                        if (structLiteral.Fields.Count != 1)
                            throw Unsupported(structLiteral, "union literal must initialize one variant");
                        var field = structLiteral.Fields[0];
                        var variantIndex = _typeInterner.GetUnionVariantIndex(
                            structLiteral.TypeName,
                            field.Name);
                        var variants = _typeInterner.GetUnionVariants(structLiteral.TypeName);
                        var unionValues = new List<uint>(variants.Count + 1)
                        {
                            EmitIntegerConstant(variantIndex, new TypeNode { Name = "i32" })
                        };
                        for (var index = 0; index < variants.Count; index++)
                        {
                            unionValues.Add(index == variantIndex
                                ? LowerExpression(field.Value, variants[index].TypeName)
                                : EmitZeroConstant(variants[index].TypeName));
                        }
                        var unionId = NextValueId();
                        AddInstruction(new BackendInstruction
                        {
                            Id = unionId,
                            Op = "aggregate",
                            Type = _typeInterner.Intern(structLiteral.TypeName),
                            Arguments = unionValues
                        });
                        return unionId;
                    }

                    var fields = _typeInterner.GetStructFields(structLiteral.TypeName);
                    var valuesByName = structLiteral.Fields.ToDictionary(
                        field => field.Name,
                        StringComparer.Ordinal);
                    var values = new List<uint>(fields.Count);
                    foreach (var field in fields)
                    {
                        if (!valuesByName.TryGetValue(field.Name, out var initializer))
                            throw Unsupported(structLiteral, $"missing struct field '{field.Name}'");
                        values.Add(LowerExpression(initializer.Value, field.TypeName));
                    }
                    var id = NextValueId();
                    AddInstruction(new BackendInstruction
                    {
                        Id = id,
                        Op = "aggregate",
                        Type = _typeInterner.Intern(structLiteral.TypeName),
                        Arguments = values
                    });
                    return id;
                }

                case IndexExpr index:
                {
                    var elementType = GetCheckedType(index);
                    var address = LowerAddress(index);
                    return EmitInstruction("load", elementType, lhs: address);
                }

                case FieldExpr field:
                {
                    if (_typeInterner.TryGetEnumMember(field, out var enumType, out var enumValue))
                        return EmitIntegerConstant(enumValue, enumType);
                    var fieldType = GetCheckedType(field);
                    if (fieldType.IsFunction &&
                        field.ResolvedQualifiedName is string resolvedFunction &&
                        _functionIds.TryGetValue(resolvedFunction, out var resolvedFunctionId))
                    {
                        var functionValue = NextValueId();
                        AddInstruction(new BackendInstruction
                        {
                            Id = functionValue,
                            Op = "function_address",
                            Type = _typeInterner.Intern(fieldType),
                            Callee = resolvedFunctionId
                        });
                        return functionValue;
                    }
                    return EmitInstruction("load", fieldType, lhs: LowerAddress(field));
                }

                case BuiltinExpr builtin when builtin.Name is
                    "true" or
                    "false" or
                    "Builtin.IsLinux" or
                    "Builtin.IsWindows" or
                    "Builtin.IsBareMetal" or
                    "Builtin.IsX86_64" or
                    "Builtin.IsAArch64":
                {
                    return EmitIntegerConstant(
                        GetBuiltinValue(builtin.Name) ? 1 : 0,
                        new TypeNode { Name = "bool" });
                }

                case ErrorExpr error:
                    return LowerErrorValue(error);

                case CatchExpr catchExpression:
                    return LowerCatch(catchExpression);

                case SizeofExpr sizeofExpression:
                {
                    var id = NextValueId();
                    AddInstruction(new BackendInstruction
                    {
                        Id = id,
                        Op = "size_of",
                        Type = _typeInterner.Intern(new TypeNode { Name = "i64" }),
                        SourceType = _typeInterner.Intern(sizeofExpression.TargetType)
                    });
                    return id;
                }

                case UnaryExpr unary when unary.Operator == "-":
                {
                    var type = GetCheckedType(expression);
                    var operand = LowerExpression(unary.Operand, type);
                    var zero = EmitIntegerConstant(0, type);
                    var id = NextValueId();
                    AddInstruction(new BackendInstruction
                    {
                        Id = id,
                        Op = "binary",
                        Type = _typeInterner.Intern(type),
                        BinaryOp = "sub",
                        Lhs = zero,
                        Rhs = operand
                    });
                    return id;
                }

                case UnaryExpr unary when unary.Operator == "!":
                {
                    var operand = LowerExpression(unary.Operand, new TypeNode { Name = "bool" });
                    var zero = EmitIntegerConstant(0, new TypeNode { Name = "bool" });
                    var id = NextValueId();
                    AddInstruction(new BackendInstruction
                    {
                        Id = id,
                        Op = "compare",
                        Type = _typeInterner.Intern(new TypeNode { Name = "bool" }),
                        CompareOp = "equal",
                        Lhs = operand,
                        Rhs = zero
                    });
                    return id;
                }

                case UnaryExpr unary when unary.Operator == "&":
                {
                    var operandType = GetCheckedType(unary.Operand);
                    if (operandType.ArraySize != null)
                    {
                        var baseAddress = LowerAddress(unary.Operand);
                        var zero = EmitIntegerConstant(0, new TypeNode { Name = "i64" });
                        return EmitIndexAddress(
                            baseAddress,
                            zero,
                            operandType,
                            GetArrayElementType(operandType));
                    }
                    return LowerAddress(unary.Operand);
                }

                case CastExpr cast:
                {
                    var sourceType = GetCheckedType(cast.Expr);
                    var targetType = cast.TargetType;
                    var source = LowerExpression(cast.Expr, sourceType);
                    var sourceIsPointer = IsPointerLike(sourceType);
                    var targetIsPointer = IsPointerLike(targetType);
                    if (sourceIsPointer && targetIsPointer)
                        return source;
                    if (sourceIsPointer || targetIsPointer)
                    {
                        var pointerCastId = NextValueId();
                        AddInstruction(new BackendInstruction
                        {
                            Id = pointerCastId,
                            Op = "cast",
                            Type = _typeInterner.Intern(targetType),
                            CastOp = sourceIsPointer ? "pointer_to_integer" : "integer_to_pointer",
                            Lhs = source
                        });
                        return pointerCastId;
                    }
                    var sourceWidth = GetScalarBitWidth(sourceType);
                    var targetWidth = GetScalarBitWidth(targetType);
                    if (sourceWidth == targetWidth)
                        return source;

                    var id = NextValueId();
                    AddInstruction(new BackendInstruction
                    {
                        Id = id,
                        Op = "cast",
                        Type = _typeInterner.Intern(targetType),
                        CastOp = targetWidth < sourceWidth
                            ? "truncate"
                            : IsSignedScalar(sourceType) ? "sign_extend" : "zero_extend",
                        Lhs = source
                    });
                    return id;
                }

                case BinaryExpr binary:
                {
                    if (binary.Operator is "&&" or "||")
                        return LowerLogical(binary);

                    var leftType = GetCheckedType(binary.Left);
                    var rightType = GetCheckedType(binary.Right);
                    if (binary.Operator is "+" or "-" &&
                        (leftType.IsPointer || rightType.IsPointer))
                    {
                        var pointerExpression = leftType.IsPointer ? binary.Left : binary.Right;
                        var pointerType = leftType.IsPointer ? leftType : rightType;
                        var indexExpression = leftType.IsPointer ? binary.Right : binary.Left;
                        var pointer = LowerExpression(pointerExpression, pointerType);
                        var indexType = GetCheckedType(indexExpression);
                        var index = LowerExpression(indexExpression, indexType);
                        index = CoerceInteger(index, indexType, new TypeNode { Name = "i64" });
                        if (binary.Operator == "-")
                        {
                            var zero = EmitIntegerConstant(0, new TypeNode { Name = "i64" });
                            index = EmitBinary("sub", zero, index, new TypeNode { Name = "i64" });
                        }
                        var elementType = GetPointerElementType(pointerType);
                        return EmitIndexAddress(pointer, index, elementType, elementType);
                    }

                    var resultType = GetCheckedType(expression);
                    var leftOperandType = GetCheckedType(binary.Left);
                    var rightOperandType = GetCheckedType(binary.Right);
                    var operandType = IsComparison(binary.Operator)
                        ? GetComparisonOperandType(leftOperandType, rightOperandType)
                        : resultType;
                    uint lhs;
                    uint rhs;
                    if (IsScalarInteger(leftOperandType) &&
                        IsScalarInteger(rightOperandType) &&
                        IsScalarInteger(operandType))
                    {
                        lhs = LowerIntegerOperand(binary.Left, leftOperandType, operandType);
                        rhs = LowerIntegerOperand(binary.Right, rightOperandType, operandType);
                    }
                    else
                    {
                        lhs = LowerExpression(binary.Left, leftOperandType);
                        rhs = LowerExpression(binary.Right, rightOperandType);
                    }
                    var id = NextValueId();
                    AddInstruction(new BackendInstruction
                    {
                        Id = id,
                        Op = IsComparison(binary.Operator) ? "compare" : "binary",
                        Type = _typeInterner.Intern(resultType),
                        BinaryOp = MapBinaryOp(binary.Operator, operandType),
                        CompareOp = MapCompareOp(binary.Operator, operandType),
                        Lhs = lhs,
                        Rhs = rhs
                    });
                    return id;
                }

                case CallExpr call:
                {
                    var initiallyResolvedName = call.ResolvedTargetQualifiedName
                        ?? call.ResolvedQualifiedName
                        ?? QualifiedNames.GetFullName(call.NamespacePath, call.Name);
                    if (call.TypeArguments.Count > 0)
                        initiallyResolvedName = GenericFunctionName(
                            initiallyResolvedName,
                            call.TypeArguments);
                    Expr? indirectTarget = call.TargetExpr;
                    if (indirectTarget == null &&
                        LookupLocal(call.Name) is { Type.IsFunction: true })
                    {
                        indirectTarget = new IdentifierExpr { Name = call.Name };
                    }
                    if (indirectTarget != null &&
                        !_functionIds.ContainsKey(initiallyResolvedName))
                    {
                        var indirectFunctionType = call.ResolvedFunctionType
                            ?? GetCheckedType(indirectTarget)
                            ?? throw Unsupported(call, "indirect call has no checked function type");
                        var callee = LowerExpression(indirectTarget, indirectFunctionType);
                        var indirectArguments = LowerCallArguments(call, indirectFunctionType);
                        var indirectId = NextValueId();
                        AddInstruction(new BackendInstruction
                        {
                            Id = indirectId,
                            Op = "indirect_call",
                            Type = _typeInterner.Intern(
                                indirectFunctionType.ReturnType ?? new TypeNode { Name = "void" }),
                            SourceType = _typeInterner.Intern(indirectFunctionType),
                            Lhs = callee,
                            Arguments = indirectArguments
                        });
                        return indirectId;
                    }

                    var resolvedName = call.ResolvedTargetQualifiedName
                        ?? call.ResolvedQualifiedName
                        ?? QualifiedNames.GetFullName(call.NamespacePath, call.Name);
                    if (call.TypeArguments.Count > 0)
                        resolvedName = GenericFunctionName(resolvedName, call.TypeArguments);
                    if (resolvedName == "syscall")
                    {
                        var syscallType = new TypeNode { Name = "i64" };
                        var syscallArguments = call.Args
                            .Select(argument =>
                            {
                                var argumentType = GetCheckedType(argument);
                                var value = LowerExpression(argument, argumentType);
                                return CoerceInteger(value, argumentType, syscallType);
                            })
                            .ToList();
                        if (syscallArguments.Count is < 1 or > 7)
                            throw Unsupported(expression, "syscall requires between one and seven arguments");

                        var syscallId = NextValueId();
                        AddInstruction(new BackendInstruction
                        {
                            Id = syscallId,
                            Op = "syscall",
                            Type = _typeInterner.Intern(new TypeNode { Name = "i64" }),
                            Arguments = syscallArguments
                        });
                        return syscallId;
                    }
                    if (!_functionIds.TryGetValue(resolvedName, out var calleeId))
                        throw Unsupported(expression, $"call target '{resolvedName}' is not in the backend module");

                    var functionType = call.ResolvedFunctionType
                        ?? ResolveFunctionType(call)
                        ?? throw Unsupported(expression, $"call target '{resolvedName}' has no checked function type");
                    var arguments = LowerCallArguments(call, functionType);

                    var id = NextValueId();
                    AddInstruction(new BackendInstruction
                    {
                        Id = id,
                        Op = "call",
                        Type = _typeInterner.Intern(functionType.ReturnType ?? new TypeNode { Name = "void" }),
                        Callee = calleeId,
                        Arguments = arguments
                    });
                    return id;
                }

                default:
                    throw Unsupported(expression, expression.GetType().Name);
            }
        }

        private List<uint> LowerCallArguments(CallExpr call, TypeNode functionType)
        {
            var arguments = new List<uint>(call.Args.Count);
            for (var index = 0; index < call.Args.Count; index++)
            {
                var parameterType = index < functionType.ParamTypes.Count
                    ? functionType.ParamTypes[index]
                    : null;
                var argument = call.Args[index];
                var argumentType = GetCheckedType(argument);
                if (parameterType?.IsPointer == true &&
                    argumentType.ArraySize != null &&
                    !parameterType.IsSlice &&
                    Math.Max(parameterType.PointerLevel, 1) == 1)
                {
                    var zero = EmitIntegerConstant(0, new TypeNode { Name = "i64" });
                    arguments.Add(EmitIndexAddress(
                        LowerAddress(argument),
                        zero,
                        argumentType,
                        GetArrayElementType(argumentType)));
                    continue;
                }

                var value = LowerExpression(argument, parameterType);
                if (parameterType != null &&
                    IsScalarInteger(argumentType) &&
                    IsScalarInteger(parameterType))
                {
                    value = CoerceInteger(value, argumentType, parameterType);
                }
                arguments.Add(value);
            }
            return arguments;
        }

        private uint LowerLogical(BinaryExpr binary)
        {
            var lhs = LowerExpression(binary.Left, new TypeNode { Name = "bool" });
            var lhsBlock = _currentBlock
                ?? throw new ZorbCompilerException("Logical expression lost its left-hand block.");
            var shortCircuitValue = EmitIntegerConstant(
                binary.Operator == "||" ? 1 : 0,
                new TypeNode { Name = "bool" });
            var rhsBlock = CreateBlock("logical.rhs");
            var mergeBlock = CreateBlock("logical.end");
            Terminate(new BackendTerminator
            {
                Op = "conditional_branch",
                Condition = lhs,
                TrueTarget = binary.Operator == "&&" ? rhsBlock.Id : mergeBlock.Id,
                FalseTarget = binary.Operator == "&&" ? mergeBlock.Id : rhsBlock.Id
            });

            _currentBlock = rhsBlock;
            var rhs = LowerExpression(binary.Right, new TypeNode { Name = "bool" });
            var rhsEnd = _currentBlock
                ?? throw new ZorbCompilerException("Logical expression right-hand side terminated unexpectedly.");
            Terminate(new BackendTerminator { Op = "branch", Target = mergeBlock.Id });

            _currentBlock = mergeBlock;
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "phi",
                Type = _typeInterner.Intern(new TypeNode { Name = "bool" }),
                IncomingValues = [shortCircuitValue, rhs],
                IncomingBlocks = [lhsBlock.Id, rhsEnd.Id]
            });
            return id;
        }

        private uint LowerArrayToSlice(Expr expression, TypeNode arrayType, TypeNode sliceType)
        {
            var elementType = GetArrayElementType(arrayType);
            var zero = EmitIntegerConstant(0, new TypeNode { Name = "i64" });
            var pointer = EmitIndexAddress(
                LowerAddress(expression),
                zero,
                arrayType,
                elementType);
            var length = EmitIntegerConstant(
                arrayType.ArraySize ?? throw Unsupported(expression, "array without a resolved length"),
                new TypeNode { Name = "i64" });
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "aggregate",
                Type = _typeInterner.Intern(sliceType),
                Arguments = [pointer, length]
            });
            return id;
        }

        private uint LowerSliceIndexAddress(
            IndexExpr expression,
            TypeNode sliceType,
            TypeNode elementType,
            uint index)
        {
            var slice = LowerExpression(expression.Target, sliceType);
            var pointer = EmitExtractValue(slice, AddressOf(elementType), 0);
            var length = EmitExtractValue(slice, new TypeNode { Name = "i64" }, 1);
            var indexType = GetCheckedType(expression.Index);
            var zero = EmitIntegerConstant(0, indexType);
            uint failed;
            if (IsSignedScalar(indexType))
            {
                var negative = EmitComparison("signed_less", index, zero);
                var pastEnd = EmitComparison("unsigned_greater_equal", index, length);
                failed = EmitBinary("bit_or", negative, pastEnd, new TypeNode { Name = "bool" });
            }
            else
            {
                failed = EmitComparison("unsigned_greater_equal", index, length);
            }

            var failureBlock = CreateBlock("slice.oob");
            var successBlock = CreateBlock("slice.index");
            Terminate(new BackendTerminator
            {
                Op = "conditional_branch",
                Condition = failed,
                TrueTarget = failureBlock.Id,
                FalseTarget = successBlock.Id
            });

            _currentBlock = failureBlock;
            var exitCode = EmitIntegerConstant(1, new TypeNode { Name = "i32" });
            _ = EmitInstruction("process_exit", new TypeNode { Name = "void" }, lhs: exitCode);
            Terminate(new BackendTerminator { Op = "unreachable" });

            _currentBlock = successBlock;
            return EmitIndexAddress(pointer, index, elementType, elementType);
        }

        private uint LowerAddress(Expr expression)
        {
            switch (expression)
            {
                case IdentifierExpr identifier:
                    if (LookupLocal(identifier.Name) is LocalBinding local)
                        return local.Address;
                    if (_globalIds.TryGetValue(identifier.Name, out var globalId))
                    {
                        var id = NextValueId();
                        AddInstruction(new BackendInstruction
                        {
                            Id = id,
                            Op = "global_address",
                            Type = _typeInterner.Intern(_globals[identifier.Name].TypeName),
                            Global = globalId
                        });
                        return id;
                    }
                    throw Unsupported(expression, $"identifier '{identifier.Name}' is not addressable");

                case IndexExpr index:
                {
                    var targetType = GetCheckedType(index.Target);
                    var elementType = GetCheckedType(index);
                    var indexValue = LowerExpression(index.Index, new TypeNode { Name = "i64" });
                    if (targetType.ArraySize != null)
                    {
                        return EmitIndexAddress(
                            LowerAddress(index.Target),
                            indexValue,
                            targetType,
                            elementType);
                    }
                    if (targetType.IsPointer && !targetType.IsErrorUnion)
                    {
                        return EmitIndexAddress(
                            LowerExpression(index.Target, targetType),
                            indexValue,
                            GetPointerElementType(targetType),
                            elementType);
                    }
                    if (targetType.IsSlice)
                        return LowerSliceIndexAddress(index, targetType, elementType, indexValue);
                    throw Unsupported(index, $"index address for type '{FormatType(targetType)}'");
                }

                case FieldExpr field:
                {
                    if (field.ResolvedQualifiedName is string resolvedGlobal &&
                        _globalIds.TryGetValue(resolvedGlobal, out var resolvedGlobalId) &&
                        _globals.TryGetValue(resolvedGlobal, out var resolvedGlobalDeclaration))
                    {
                        var globalAddress = NextValueId();
                        AddInstruction(new BackendInstruction
                        {
                            Id = globalAddress,
                            Op = "global_address",
                            Type = _typeInterner.Intern(resolvedGlobalDeclaration.TypeName),
                            Global = resolvedGlobalId
                        });
                        return globalAddress;
                    }
                    var targetType = GetCheckedType(field.Target);
                    TypeNode containerType;
                    uint baseAddress;
                    if (targetType.IsPointer && !targetType.IsErrorUnion)
                    {
                        containerType = GetPointerElementType(targetType);
                        baseAddress = LowerExpression(field.Target, targetType);
                    }
                    else
                    {
                        containerType = targetType;
                        baseAddress = LowerAddress(field.Target);
                    }

                    var id = NextValueId();
                    AddInstruction(new BackendInstruction
                    {
                        Id = id,
                        Op = "field_address",
                        Type = _typeInterner.Intern(GetCheckedType(field)),
                        SourceType = _typeInterner.Intern(containerType),
                        FieldIndex = _typeInterner.GetStructFieldIndex(containerType, field.Field),
                        Lhs = baseAddress
                    });
                    return id;
                }

                default:
                {
                    var type = GetCheckedType(expression);
                    var value = LowerExpression(expression, type);
                    var address = EmitInstruction("alloca", type);
                    _ = EmitInstruction("store", type, lhs: address, rhs: value);
                    return address;
                }
            }
        }

        private void AddInstruction(BackendInstruction instruction)
        {
            var block = _currentBlock
                ?? throw new ZorbCompilerException("Attempted to emit an instruction without an active LLVM block.");
            block.Instructions.Add(instruction);
        }

        private uint EmitInstruction(
            string op,
            TypeNode type,
            uint? lhs = null,
            uint? rhs = null)
        {
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = op,
                Type = _typeInterner.Intern(type),
                Lhs = lhs,
                Rhs = rhs
            });
            return id;
        }

        private uint EmitIntegerConstant(long value, TypeNode type)
        {
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "integer_constant",
                Type = _typeInterner.Intern(type),
                Integer = value
            });
            return id;
        }

        private uint EmitZeroConstant(TypeNode type)
        {
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "zero_constant",
                Type = _typeInterner.Intern(type)
            });
            return id;
        }

        private uint LowerErrorValue(ErrorExpr error)
        {
            var symbolName = $"Error_{error.ErrorCode}";
            if (!_globalIds.TryGetValue(symbolName, out var globalId) ||
                !_globals.TryGetValue(symbolName, out var global))
            {
                throw Unsupported(error, $"declared error '{error.ErrorCode}' is missing from the backend module");
            }

            var address = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = address,
                Op = "global_address",
                Type = _typeInterner.Intern(global.TypeName),
                Global = globalId
            });
            return EmitInstruction("load", global.TypeName, lhs: address);
        }

        private uint LowerCatch(CatchExpr expression, bool discardResult = false)
        {
            var errorUnionType = GetCheckedType(expression.Left);
            if (!errorUnionType.IsErrorUnion)
                throw Unsupported(expression, "catch operand is not an error union");

            var result = LowerExpression(expression.Left, errorUnionType);
            var successType = errorUnionType.ErrorInnerType
                ?? throw Unsupported(expression, "error union has no success type");
            var successValue = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = successValue,
                Op = "extract_value",
                Type = _typeInterner.Intern(successType),
                Lhs = result,
                FieldIndex = 0
            });
            var errorType = new TypeNode { Name = "i32" };
            var errorValue = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = errorValue,
                Op = "extract_value",
                Type = _typeInterner.Intern(errorType),
                Lhs = result,
                FieldIndex = 1
            });
            var zero = EmitIntegerConstant(0, errorType);
            var hasError = EmitComparison("not_equal", errorValue, zero);
            var catchBlock = CreateBlock("catch.error");
            var successBlock = CreateBlock("catch.success");
            var mergeBlock = CreateBlock("catch.end");
            Terminate(new BackendTerminator
            {
                Op = "conditional_branch",
                Condition = hasError,
                TrueTarget = catchBlock.Id,
                FalseTarget = successBlock.Id
            });

            _currentBlock = successBlock;
            Terminate(new BackendTerminator { Op = "branch", Target = mergeBlock.Id });

            _currentBlock = catchBlock;
            PushScope();
            var errorAddress = EmitInstruction("alloca", errorType);
            _ = EmitInstruction("store", errorType, lhs: errorAddress, rhs: errorValue);
            DeclareLocal(expression.ErrorVar, errorType, errorAddress, isCatchError: true);
            var fallbackExpression = !discardResult && expression.CatchBody.LastOrDefault() is ExpressionStatement fallback
                ? fallback.Expression
                : null;
            var statements = fallbackExpression == null
                ? expression.CatchBody
                : expression.CatchBody.Take(expression.CatchBody.Count - 1).ToList();
            LowerStatements(statements);
            uint? fallbackValue = null;
            uint? fallbackBlock = null;
            if (_currentBlock != null && fallbackExpression != null)
            {
                var fallbackType = GetCheckedType(fallbackExpression);
                fallbackValue = IsScalarInteger(fallbackType) && IsScalarInteger(successType)
                    ? LowerIntegerOperand(fallbackExpression, fallbackType, successType)
                    : LowerExpression(fallbackExpression, successType);
                fallbackBlock = _currentBlock.Id;
                Terminate(new BackendTerminator { Op = "branch", Target = mergeBlock.Id });
            }
            else if (_currentBlock != null && discardResult)
            {
                Terminate(new BackendTerminator { Op = "branch", Target = mergeBlock.Id });
            }
            PopScope();
            if (_currentBlock != null)
                throw Unsupported(expression, "catch body can fall through without producing a value");

            _currentBlock = mergeBlock;
            if (fallbackValue.HasValue && fallbackBlock.HasValue)
            {
                var mergedValue = NextValueId();
                AddInstruction(new BackendInstruction
                {
                    Id = mergedValue,
                    Op = "phi",
                    Type = _typeInterner.Intern(successType),
                    IncomingValues = [successValue, fallbackValue.Value],
                    IncomingBlocks = [successBlock.Id, fallbackBlock.Value]
                });
                return mergedValue;
            }

            return successValue;
        }

        private uint CoerceInteger(uint value, TypeNode sourceType, TypeNode targetType)
        {
            var sourceWidth = GetScalarBitWidth(sourceType);
            var targetWidth = GetScalarBitWidth(targetType);
            if (sourceWidth == targetWidth)
                return value;

            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "cast",
                Type = _typeInterner.Intern(targetType),
                CastOp = targetWidth < sourceWidth
                    ? "truncate"
                    : IsSignedScalar(sourceType) ? "sign_extend" : "zero_extend",
                Lhs = value
            });
            return id;
        }

        private uint LowerIntegerOperand(Expr expression, TypeNode sourceType, TypeNode targetType)
        {
            if (expression is NumberExpr)
                return LowerExpression(expression, targetType);

            var value = LowerExpression(expression, sourceType);
            return CoerceInteger(value, sourceType, targetType);
        }

        private uint EmitComparison(string comparison, uint lhs, uint rhs)
        {
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "compare",
                Type = _typeInterner.Intern(new TypeNode { Name = "bool" }),
                CompareOp = comparison,
                Lhs = lhs,
                Rhs = rhs
            });
            return id;
        }

        private uint EmitBinary(string operation, uint lhs, uint rhs, TypeNode type)
        {
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "binary",
                Type = _typeInterner.Intern(type),
                BinaryOp = operation,
                Lhs = lhs,
                Rhs = rhs
            });
            return id;
        }

        private uint EmitExtractValue(uint aggregate, TypeNode type, uint fieldIndex)
        {
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "extract_value",
                Type = _typeInterner.Intern(type),
                Lhs = aggregate,
                FieldIndex = fieldIndex
            });
            return id;
        }

        private uint EmitIndexAddress(
            uint baseAddress,
            uint index,
            TypeNode sourceType,
            TypeNode elementType)
        {
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "index_address",
                Type = _typeInterner.Intern(elementType),
                SourceType = _typeInterner.Intern(sourceType),
                Lhs = baseAddress,
                Rhs = index
            });
            return id;
        }

        private static TypeNode GetArrayElementType(TypeNode arrayType)
        {
            var elementType = arrayType.Clone();
            elementType.ArraySize = null;
            elementType.ArraySizeExpr = null;
            return elementType;
        }

        private static TypeNode GetIndexedElementType(TypeNode type)
        {
            if (type.ArraySize != null)
                return GetArrayElementType(type);
            if (type.IsSlice)
            {
                var element = type.Clone();
                element.IsSlice = false;
                return element;
            }
            if (type.IsPointer)
                return GetPointerElementType(type);
            throw new ZorbCompilerException($"Type '{FormatType(type)}' is not indexable.");
        }

        private static TypeNode GetPointerElementType(TypeNode pointerType)
        {
            var elementType = pointerType.Clone();
            var pointerLevel = Math.Max(elementType.PointerLevel, 1);
            elementType.PointerLevel = pointerLevel - 1;
            elementType.IsPointer = elementType.PointerLevel > 0;
            return elementType;
        }

        private static TypeNode AddressOf(TypeNode type)
        {
            var pointer = type.Clone();
            pointer.IsPointer = true;
            pointer.PointerLevel = Math.Max(pointer.PointerLevel, 0) + 1;
            return pointer;
        }

        private bool GetBuiltinValue(string name)
        {
            var triple = _target.Triple;
            return name switch
            {
                "true" => true,
                "false" => false,
                "Builtin.IsLinux" => triple.Contains("linux", StringComparison.Ordinal),
                "Builtin.IsWindows" => triple.Contains("windows", StringComparison.Ordinal),
                "Builtin.IsBareMetal" => triple.Contains("-none-", StringComparison.Ordinal),
                "Builtin.IsX86_64" => triple.StartsWith("x86_64-", StringComparison.Ordinal),
                "Builtin.IsAArch64" => triple.StartsWith("aarch64-", StringComparison.Ordinal),
                _ => throw new ZorbCompilerException($"Unknown target builtin '{name}'.")
            };
        }

        private void PushScope()
        {
            _localScopes.Push(new Dictionary<string, LocalBinding>(StringComparer.Ordinal));
        }

        private void PopScope()
        {
            _localScopes.Pop();
        }

        private void DeclareLocal(
            string name,
            TypeNode type,
            uint address,
            bool isCatchError = false)
        {
            _localScopes.Peek().Add(
                name,
                new LocalBinding(address, type.Clone(), isCatchError));
        }

        private LocalBinding? LookupLocal(string name)
        {
            foreach (var scope in _localScopes)
            {
                if (scope.TryGetValue(name, out var local))
                    return local;
            }
            return null;
        }

        private TypeNode GetCheckedType(Expr expression)
        {
            if (_typeChecker.GetCheckedExpressionType(expression) is TypeNode checkedType)
                return checkedType;

            return expression switch
            {
                IdentifierExpr identifier when LookupLocal(identifier.Name) is LocalBinding local
                    => local.Type.Clone(),
                IdentifierExpr identifier when _globals.TryGetValue(identifier.Name, out var global)
                    => global.TypeName.Clone(),
                IndexExpr index
                    => GetIndexedElementType(GetCheckedType(index.Target)),
                FieldExpr field when field.ResolvedQualifiedName is string resolvedGlobal &&
                                     _globals.TryGetValue(resolvedGlobal, out var global)
                    => global.TypeName.Clone(),
                FieldExpr field when field.ResolvedQualifiedName is string resolvedFunction &&
                                     _functionTypes.TryGetValue(resolvedFunction, out var functionType)
                    => functionType.Clone(),
                FieldExpr field when GetCheckedType(field.Target).IsSlice && field.Field == "len"
                    => new TypeNode { Name = "i64" },
                FieldExpr field when GetCheckedType(field.Target).IsSlice && field.Field == "ptr"
                    => AddressOf(GetIndexedElementType(GetCheckedType(field.Target))),
                FieldExpr field
                    => _typeInterner.GetStructFieldType(GetCheckedType(field.Target), field.Field),
                NumberExpr
                    => new TypeNode { Name = "i64" },
                StringExpr
                    => new TypeNode { Name = "string" },
                BuiltinExpr builtin when builtin.Name is
                    "true" or
                    "false" or
                    "Builtin.IsLinux" or
                    "Builtin.IsWindows" or
                    "Builtin.IsBareMetal" or
                    "Builtin.IsX86_64" or
                    "Builtin.IsAArch64"
                    => new TypeNode { Name = "bool" },
                UnaryExpr { Operator: "!" }
                    => new TypeNode { Name = "bool" },
                UnaryExpr { Operator: "-" } unary
                    => GetCheckedType(unary.Operand),
                UnaryExpr { Operator: "&" } unary
                    => AddressOf(GetCheckedType(unary.Operand)),
                CastExpr cast
                    => cast.TargetType.Clone(),
                BinaryExpr binary when IsComparison(binary.Operator)
                    => new TypeNode { Name = "bool" },
                BinaryExpr binary when binary.Operator is "&&" or "||"
                    => new TypeNode { Name = "bool" },
                BinaryExpr binary
                    => GetCheckedType(binary.Left),
                CallExpr { ResolvedFunctionType: not null } call
                    => call.ResolvedFunctionType!.ReturnType?.Clone()
                       ?? throw Unsupported(call, "call has no return type"),
                CallExpr call when ResolveFunctionType(call) is TypeNode functionType
                    => functionType.ReturnType?.Clone()
                       ?? throw Unsupported(call, "call has no return type"),
                CallExpr { TargetExpr: not null } call
                    when GetCheckedType(call.TargetExpr).IsFunction
                    => GetCheckedType(call.TargetExpr).ReturnType?.Clone()
                       ?? throw Unsupported(call, "call has no return type"),
                ErrorExpr
                    => new TypeNode { Name = "i32" },
                SizeofExpr
                    => new TypeNode { Name = "i64" },
                CatchExpr catchExpression
                    => GetCheckedType(catchExpression.Left).ErrorInnerType?.Clone()
                       ?? throw Unsupported(catchExpression, "catch operand has no success type"),
                _ => throw Unsupported(expression, "missing checked expression type")
            };
        }

        private TypeNode? ResolveFunctionType(CallExpr call)
        {
            var resolvedName = call.ResolvedTargetQualifiedName
                ?? call.ResolvedQualifiedName
                ?? QualifiedNames.GetFullName(call.NamespacePath, call.Name);
            if (call.TypeArguments.Count > 0)
                resolvedName = GenericFunctionName(resolvedName, call.TypeArguments);
            return _functionTypes.TryGetValue(resolvedName, out var type)
                ? type
                : null;
        }

        private uint NextValueId()
        {
            return _nextValueId++;
        }

        private static bool IsComparison(string op)
        {
            return op is "==" or "!=" or "<" or "<=" or ">" or ">=";
        }

        private static bool IsBoolType(TypeNode type)
        {
            return !type.IsPointer &&
                   !type.IsSlice &&
                   !type.IsErrorUnion &&
                   !type.IsFunction &&
                   type.ArraySize == null &&
                   type.Name == "bool";
        }

        private static bool IsScalarInteger(TypeNode type)
        {
            return !type.IsPointer &&
                   !type.IsSlice &&
                   !type.IsErrorUnion &&
                   type.ArraySize == null &&
                   type.Name is "bool" or "i8" or "u8" or "i16" or "u16" or
                       "i32" or "u32" or "i64" or "u64";
        }

        private static int GetScalarBitWidth(TypeNode type)
        {
            return type.Name switch
            {
                "i8" or "u8" => 8,
                "i16" or "u16" => 16,
                "bool" or "i32" or "u32" => 32,
                "i64" or "u64" or "size_t" => 64,
                _ => throw new ZorbCompilerException(
                    $"Integer cast lowering does not support type '{FormatType(type)}'.")
            };
        }

        private static bool IsSignedScalar(TypeNode type)
        {
            return type.Name is "i8" or "i16" or "i32" or "i64";
        }

        private static bool IsPointerLike(TypeNode type)
        {
            return type.IsPointer || type.IsFunction || type.Name == "string";
        }

        private static TypeNode GetComparisonOperandType(TypeNode leftType, TypeNode rightType)
        {
            if (!IsScalarInteger(leftType) || !IsScalarInteger(rightType))
                return leftType;

            var promotedLeft = PromoteIntegerForComparison(leftType);
            var promotedRight = PromoteIntegerForComparison(rightType);
            var leftWidth = GetScalarBitWidth(promotedLeft);
            var rightWidth = GetScalarBitWidth(promotedRight);
            var leftSigned = IsSignedScalar(promotedLeft);
            var rightSigned = IsSignedScalar(promotedRight);

            if (leftSigned == rightSigned)
                return leftWidth >= rightWidth ? promotedLeft : promotedRight;

            var signedType = leftSigned ? promotedLeft : promotedRight;
            var unsignedType = leftSigned ? promotedRight : promotedLeft;
            var signedWidth = GetScalarBitWidth(signedType);
            var unsignedWidth = GetScalarBitWidth(unsignedType);

            if (signedWidth > unsignedWidth)
                return signedType;

            return new TypeNode { Name = UnsignedTypeName(Math.Max(signedWidth, unsignedWidth)) };
        }

        private static TypeNode PromoteIntegerForComparison(TypeNode type)
        {
            return GetScalarBitWidth(type) < 32
                ? new TypeNode { Name = "i32" }
                : type;
        }

        private static string UnsignedTypeName(int bitWidth)
        {
            return bitWidth switch
            {
                8 => "u8",
                16 => "u16",
                32 => "u32",
                64 => "u64",
                _ => throw new ZorbCompilerException(
                    $"Unsupported integer width '{bitWidth}' in comparison lowering.")
            };
        }

        private static string? MapBinaryOp(string op, TypeNode operandType)
        {
            if (IsComparison(op))
                return null;

            var signed = operandType.Name.StartsWith('i');
            return op switch
            {
                "+" => "add",
                "-" => "sub",
                "*" => "mul",
                "/" => signed ? "signed_div" : "unsigned_div",
                "%" => signed ? "signed_rem" : "unsigned_rem",
                "&" => "bit_and",
                "|" => "bit_or",
                "^" => "bit_xor",
                "<<" => "shift_left",
                ">>" => signed ? "arithmetic_shift_right" : "logical_shift_right",
                _ => throw new ZorbCompilerException($"Unsupported scalar binary operator '{op}'.")
            };
        }

        private static string? MapCompareOp(string op, TypeNode operandType)
        {
            if (!IsComparison(op))
                return null;

            var signed = operandType.Name.StartsWith('i');
            return op switch
            {
                "==" => "equal",
                "!=" => "not_equal",
                "<" => signed ? "signed_less" : "unsigned_less",
                "<=" => signed ? "signed_less_equal" : "unsigned_less_equal",
                ">" => signed ? "signed_greater" : "unsigned_greater",
                ">=" => signed ? "signed_greater_equal" : "unsigned_greater_equal",
                _ => throw new ZorbCompilerException($"Unsupported scalar comparison operator '{op}'.")
            };
        }

        private static ZorbCompilerException Unsupported(Node node, string detail)
        {
            return new ZorbCompilerException(
                $"Zig backend scalar expression lowering does not support {detail} at {node.File}:{node.Line}:{node.Column}.");
        }

        private sealed record LocalBinding(uint Address, TypeNode Type, bool IsCatchError = false);
        private sealed record LoopTargets(uint BreakBlock, uint ContinueBlock);
    }

    private sealed class TypeInterner
    {
        private readonly Dictionary<string, uint> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StructNode> _structs;
        private readonly Dictionary<string, EnumNode> _enums;
        private readonly Dictionary<string, UnionNode> _unions;
        private readonly HashSet<string> _externTypes;
        private readonly List<BackendType> _types = new();

        public TypeInterner(IReadOnlyList<Node> nodes)
        {
            _structs = nodes.OfType<StructNode>().ToDictionary(
                node => QualifiedNames.GetFullName(node.NamespacePath, node.Name),
                StringComparer.Ordinal);
            _enums = nodes.OfType<EnumNode>().ToDictionary(
                node => QualifiedNames.GetFullName(node.NamespacePath, node.Name),
                StringComparer.Ordinal);
            _unions = nodes.OfType<UnionNode>().ToDictionary(
                node => QualifiedNames.GetFullName(node.NamespacePath, node.Name),
                StringComparer.Ordinal);
            _externTypes = nodes.OfType<ExternTypeDecl>()
                .Select(node => QualifiedNames.GetFullName(node.NamespacePath, node.Name))
                .ToHashSet(StringComparer.Ordinal);
        }

        public List<BackendType> Types => _types;

        public IReadOnlyList<StructField> GetStructFields(TypeNode type)
        {
            var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
            if (!_structs.TryGetValue(fullName, out var structNode))
                throw Unsupported(type, $"unknown struct '{fullName}'");
            if (structNode.TypeParameters.Count == 0)
                return structNode.Fields;

            var substitutions = AstSpecialization.BuildTypeSubstitutions(
                structNode.TypeParameters,
                type.TypeArguments);
            return structNode.Fields.Select(field => new StructField
            {
                File = field.File,
                Line = field.Line,
                Column = field.Column,
                Length = field.Length,
                Name = field.Name,
                TypeName = AstSpecialization.SubstituteTypeParameters(field.TypeName, substitutions),
                Attributes = new List<string>(field.Attributes),
                OffsetExpr = field.OffsetExpr
            }).ToList();
        }

        public bool IsEnumType(TypeNode type)
        {
            var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
            return _enums.ContainsKey(fullName) || TryGetTagUnion(fullName, out _);
        }

        public bool IsUnionType(TypeNode type)
        {
            var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
            return _unions.ContainsKey(fullName);
        }

        public IReadOnlyList<UnionVariant> GetUnionVariants(TypeNode type)
        {
            var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
            if (!_unions.TryGetValue(fullName, out var unionNode))
                throw Unsupported(type, $"unknown union '{fullName}'");

            if (unionNode.TypeParameters.Count == 0)
                return unionNode.Variants;

            var substitutions = AstSpecialization.BuildTypeSubstitutions(
                unionNode.TypeParameters,
                type.TypeArguments);
            return unionNode.Variants.Select(variant => new UnionVariant
            {
                File = variant.File,
                Line = variant.Line,
                Column = variant.Column,
                Length = variant.Length,
                Name = variant.Name,
                TypeName = AstSpecialization.SubstituteTypeParameters(variant.TypeName, substitutions)
            }).ToList();
        }

        public uint GetUnionVariantIndex(TypeNode type, string variantName)
        {
            var variants = GetUnionVariants(type);
            for (var index = 0; index < variants.Count; index++)
            {
                if (string.Equals(variants[index].Name, variantName, StringComparison.Ordinal))
                    return checked((uint)index);
            }
            throw Unsupported(type, $"unknown union variant '{variantName}'");
        }

        public TypeNode GetUnionVariantType(TypeNode type, string variantName)
        {
            return GetUnionVariants(type)
                       .FirstOrDefault(variant => string.Equals(
                           variant.Name,
                           variantName,
                           StringComparison.Ordinal))
                       ?.TypeName.Clone()
                   ?? throw Unsupported(type, $"unknown union variant '{variantName}'");
        }

        public uint GetStructFieldIndex(TypeNode type, string fieldName)
        {
            type = Dereference(type);
            if (type.IsErrorUnion)
            {
                return fieldName switch
                {
                    "value" => 0,
                    "error" => 1,
                    _ => throw Unsupported(type, $"unknown error-union field '{fieldName}'")
                };
            }
            if (type.IsSlice)
            {
                return fieldName switch
                {
                    "ptr" => 0,
                    "len" => 1,
                    _ => throw Unsupported(type, $"unknown slice field '{fieldName}'")
                };
            }
            if (IsUnionType(type))
            {
                if (fieldName == "tag")
                    return 0;
                return GetUnionVariantIndex(type, fieldName) + 1;
            }
            var fields = GetStructFields(type);
            for (var index = 0; index < fields.Count; index++)
            {
                if (string.Equals(fields[index].Name, fieldName, StringComparison.Ordinal))
                    return checked((uint)index);
            }
            throw Unsupported(type, $"unknown field '{fieldName}'");
        }

        public TypeNode GetStructFieldType(TypeNode type, string fieldName)
        {
            type = Dereference(type);
            if (type.IsErrorUnion)
            {
                return fieldName switch
                {
                    "value" => type.ErrorInnerType?.Clone()
                               ?? throw Unsupported(type, "error union has no success type"),
                    "error" => new TypeNode { Name = "i32" },
                    _ => throw Unsupported(type, $"unknown error-union field '{fieldName}'")
                };
            }
            if (type.IsSlice)
            {
                return fieldName switch
                {
                    "ptr" => SlicePointerType(type),
                    "len" => new TypeNode { Name = "i64" },
                    _ => throw Unsupported(type, $"unknown slice field '{fieldName}'")
                };
            }
            if (IsUnionType(type))
            {
                return fieldName == "tag"
                    ? new TypeNode
                    {
                        Name = QualifiedNames.GetFullName(type.NamespacePath, type.Name) + ".Tag"
                    }
                    : GetUnionVariantType(type, fieldName);
            }

            return GetStructFields(type)
                       .FirstOrDefault(field => string.Equals(field.Name, fieldName, StringComparison.Ordinal))
                       ?.TypeName.Clone()
                   ?? throw Unsupported(type, $"unknown field '{fieldName}'");
        }

        public bool TryGetEnumMember(FieldExpr field, out TypeNode enumType, out long value)
        {
            enumType = null!;
            value = 0;
            if (TryResolveStaticEnumMember(field, out enumType, out value))
                return true;

            var resolvedFieldName = field.ResolvedQualifiedName
                ?? QualifiedNames.TryGetQualifiedName(field);
            if (resolvedFieldName is string qualifiedName)
            {
                var tagMarker = qualifiedName.LastIndexOf(".Tag.", StringComparison.Ordinal);
                if (tagMarker >= 0)
                {
                    var unionName = qualifiedName[..tagMarker];
                    var variantName = qualifiedName[(tagMarker + ".Tag.".Length)..];
                    if (_unions.TryGetValue(unionName, out var unionNode))
                    {
                        for (var index = 0; index < unionNode.Variants.Count; index++)
                        {
                            if (!string.Equals(
                                    unionNode.Variants[index].Name,
                                    variantName,
                                    StringComparison.Ordinal))
                                continue;
                            enumType = new TypeNode { Name = unionName + ".Tag" };
                            value = index;
                            return true;
                        }
                    }
                }

                var memberSeparator = qualifiedName.LastIndexOf('.');
                if (memberSeparator > 0)
                {
                    var resolvedEnumName = qualifiedName[..memberSeparator];
                    var resolvedMemberName = qualifiedName[(memberSeparator + 1)..];
                    if (_enums.TryGetValue(resolvedEnumName, out var resolvedEnum))
                    {
                        var resolvedMember = resolvedEnum.Members.FirstOrDefault(candidate =>
                            string.Equals(candidate.Name, resolvedMemberName, StringComparison.Ordinal));
                        if (resolvedMember?.ResolvedValue is long resolvedMemberValue)
                        {
                            enumType = new TypeNode
                            {
                                Name = resolvedEnum.Name,
                                NamespacePath = new List<string>(resolvedEnum.NamespacePath)
                            };
                            value = resolvedMemberValue;
                            return true;
                        }
                    }
                }
            }
            if (field.Target is not IdentifierExpr identifier)
                return false;

            var enumName = identifier.Name;
            if (!_enums.TryGetValue(enumName, out var enumNode))
                return false;
            var member = enumNode.Members.FirstOrDefault(
                candidate => string.Equals(candidate.Name, field.Field, StringComparison.Ordinal));
            if (member?.ResolvedValue is not long resolvedValue)
                return false;

            enumType = new TypeNode
            {
                Name = enumNode.Name,
                NamespacePath = new List<string>(enumNode.NamespacePath)
            };
            value = resolvedValue;
            return true;
        }

        private bool TryResolveStaticEnumMember(FieldExpr field, out TypeNode enumType, out long value)
        {
            enumType = null!;
            value = 0;
            if (!TryResolveStaticTypeReference(field.Target, out var ownerType))
                return false;

            if (IsEnumType(ownerType))
            {
                var enumDefinition = GetEnumDefinition(ownerType);
                var member = enumDefinition?.Members.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, field.Field, StringComparison.Ordinal));
                if (member?.ResolvedValue is not long resolvedValue)
                    return false;

                enumType = ownerType.Clone();
                value = resolvedValue;
                return true;
            }

            return false;
        }

        private bool TryResolveStaticTypeReference(Expr expr, out TypeNode type)
        {
            switch (expr)
            {
                case TypeReferenceExpr typeReference:
                    type = typeReference.TypeName.Clone();
                    return true;

                case FieldExpr { Field: "Tag" } field when TryResolveStaticTypeReference(field.Target, out var unionType) && IsUnionType(unionType):
                    type = new TypeNode
                    {
                        Name = "Tag",
                        NamespacePath = unionType.NamespacePath.Concat(new[] { unionType.Name }).ToList(),
                        TypeArguments = unionType.TypeArguments.Select(argument => argument.Clone()).ToList()
                    };
                    return true;

                default:
                    type = null!;
                    return false;
            }
        }

        private EnumNode? GetEnumDefinition(TypeNode type)
        {
            var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
            if (TryGetTagUnion(fullName, out var unionNode))
                return BuildConcreteTagEnum(unionNode);

            if (!_enums.TryGetValue(fullName, out var enumNode))
                return null;

            return enumNode.TypeParameters.Count == 0
                ? enumNode
                : new EnumNode
                {
                    File = enumNode.File,
                    Line = enumNode.Line,
                    Column = enumNode.Column,
                    Length = enumNode.Length,
                    IsExported = enumNode.IsExported,
                    NamespacePath = new List<string>(enumNode.NamespacePath),
                    Name = enumNode.Name,
                    UnderlyingType = enumNode.UnderlyingType.Clone(),
                    Members = enumNode.Members.Select(member => new EnumMember
                    {
                        File = member.File,
                        Line = member.Line,
                        Column = member.Column,
                        Length = member.Length,
                        Name = member.Name,
                        Value = member.Value,
                        ResolvedValue = member.ResolvedValue
                    }).ToList()
                };
        }

        private static EnumNode BuildConcreteTagEnum(UnionNode unionNode)
        {
            return new EnumNode
            {
                Name = "Tag",
                NamespacePath = unionNode.NamespacePath.Concat(new[] { unionNode.Name }).ToList(),
                UnderlyingType = new TypeNode { Name = "i32" },
                Members = unionNode.Variants.Select((variant, index) => new EnumMember
                {
                    Name = variant.Name,
                    ResolvedValue = index
                }).ToList()
            };
        }

        private static TypeNode Dereference(TypeNode type)
        {
            if (!type.IsPointer)
                return type;
            var result = type.Clone();
            result.PointerLevel = Math.Max(0, result.PointerLevel - 1);
            result.IsPointer = result.PointerLevel > 0;
            return result;
        }

        private static TypeNode SlicePointerType(TypeNode sliceType)
        {
            var result = sliceType.Clone();
            result.IsSlice = false;
            result.ArraySize = null;
            result.ArraySizeExpr = null;
            result.IsPointer = true;
            result.PointerLevel = Math.Max(1, result.PointerLevel + 1);
            return result;
        }

        public uint Intern(TypeNode type)
        {
            var key = GetKey(type);
            if (_ids.TryGetValue(key, out var existing))
                return existing;

            var id = checked((uint)_types.Count + 1);
            _ids.Add(key, id);
            var backendType = new BackendType { Id = id };
            _types.Add(backendType);

            if (type.IsFunction)
            {
                backendType.Kind = "function";
                backendType.Name = $"zorb.fn.{id}";
                backendType.ElementType = Intern(
                    type.ReturnType ?? new TypeNode { Name = "void" });
                backendType.Fields = type.ParamTypes.Select((parameter, index) =>
                    new BackendTypeField
                    {
                        Name = $"arg{index}",
                        Type = Intern(parameter)
                    }).ToList();
                return id;
            }

            if (type.IsErrorUnion)
            {
                backendType.Kind = "error_union";
                backendType.Name = $"zorb.result.{id}";
                backendType.ElementType = Intern(type.ErrorInnerType ?? new TypeNode { Name = type.Name });
                return id;
            }

            if (type.IsSlice)
            {
                var elementType = type.Clone();
                elementType.IsSlice = false;
                elementType.ArraySize = null;
                backendType.Kind = "slice";
                backendType.Name = $"zorb.slice.{id}";
                backendType.ElementType = Intern(elementType);
                return id;
            }

            if (type.ArraySize is int arraySize)
            {
                var elementType = type.Clone();
                elementType.ArraySize = null;
                elementType.ArraySizeExpr = null;
                backendType.Kind = "array";
                backendType.ElementType = Intern(elementType);
                backendType.Length = checked((ulong)arraySize);
                return id;
            }

            if (type.IsPointer)
            {
                var pointee = type.Clone();
                var pointerLevel = Math.Max(type.PointerLevel, 1);
                pointee.PointerLevel = pointerLevel - 1;
                pointee.IsPointer = pointee.PointerLevel > 0;
                backendType.Kind = "pointer";
                backendType.ElementType = Intern(pointee);
                return id;
            }

            if (type.Name == "string")
            {
                backendType.Kind = "string";
                backendType.ElementType = Intern(new TypeNode { Name = "u8" });
                return id;
            }

            var scalar = NormalizeScalar(type.Name);
            if (scalar != null)
            {
                backendType.Kind = "scalar";
                backendType.Scalar = scalar;
                return id;
            }

            var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
            if (TryGetTagUnion(fullName, out _))
            {
                backendType.Kind = "enum";
                backendType.Name = type.TypeArguments.Count == 0
                    ? fullName
                    : fullName + "$" + string.Join("$", type.TypeArguments.Select(FormatTypeKey));
                backendType.ElementType = Intern(new TypeNode { Name = "i32" });
                return id;
            }
            if (_structs.TryGetValue(fullName, out var structNode))
            {
                backendType.Kind = "struct";
                backendType.Name = type.TypeArguments.Count == 0
                    ? fullName
                    : fullName + "$" + string.Join("$", type.TypeArguments.Select(FormatTypeKey));
                backendType.Packed = StructLayout.HasPackedAttribute(structNode);
                backendType.Fields = GetStructFields(type).Select(field => new BackendTypeField
                {
                    Name = field.Name,
                    Type = Intern(field.TypeName)
                }).ToList();
                return id;
            }

            if (_enums.TryGetValue(fullName, out var enumNode))
            {
                backendType.Kind = "enum";
                backendType.Name = type.TypeArguments.Count == 0
                    ? fullName
                    : fullName + "$" + string.Join("$", type.TypeArguments.Select(FormatTypeKey));
                backendType.ElementType = Intern(enumNode.UnderlyingType);
                return id;
            }

            if (_unions.TryGetValue(fullName, out var unionNode))
            {
                backendType.Kind = "union";
                backendType.Name = type.TypeArguments.Count == 0
                    ? fullName
                    : fullName + "$" + string.Join("$", type.TypeArguments.Select(FormatTypeKey));
                backendType.Fields = GetUnionVariants(type).Select(variant => new BackendTypeField
                {
                    Name = variant.Name,
                    Type = Intern(variant.TypeName)
                }).ToList();
                return id;
            }

            if (_externTypes.Contains(fullName))
            {
                backendType.Kind = "scalar";
                backendType.Scalar = fullName == "size_t" ? "u64" : "u64";
                return id;
            }

            throw Unsupported(type, $"type '{fullName}'");
        }

        private bool TryGetTagUnion(string typeName, out UnionNode unionNode)
        {
            unionNode = null!;
            if (!typeName.EndsWith(".Tag", StringComparison.Ordinal))
                return false;
            return _unions.TryGetValue(typeName[..^".Tag".Length], out unionNode!);
        }

        private static string? NormalizeScalar(string name)
        {
            return name switch
            {
                "void" or "bool" or
                "i8" or "u8" or
                "i16" or "u16" or
                "i32" or "u32" or
                "i64" or "u64" => name,
                "char" => "u8",
                _ => null
            };
        }

        private static string GetKey(TypeNode type)
        {
            if (type.IsFunction)
            {
                return $"fn({string.Join(",", type.ParamTypes.Select(GetKey))})->{GetKey(type.ReturnType ?? new TypeNode { Name = "void" })}";
            }
            if (type.IsErrorUnion)
                return $"!{GetKey(type.ErrorInnerType ?? new TypeNode { Name = type.Name })}";

            var baseName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
            if (type.TypeArguments.Count > 0)
                baseName += $"<{string.Join(",", type.TypeArguments.Select(GetKey))}>";
            if (type.IsSlice)
                baseName = $"[]{baseName}";
            if (type.ArraySize is int size)
                baseName = $"[{size}]{baseName}";
            if (type.IsPointer)
                baseName = $"{new string('*', Math.Max(type.PointerLevel, 1))}{baseName}";
            return baseName;
        }

        private static ZorbCompilerException Unsupported(TypeNode type, string detail)
        {
            return new ZorbCompilerException(
                $"Zig backend type interning does not support {detail} ({FormatType(type)}).");
        }
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
