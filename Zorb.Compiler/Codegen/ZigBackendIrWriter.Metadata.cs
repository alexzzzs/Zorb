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

public sealed partial class ZigBackendIrWriter
{
    private static void AddEntryShim(
        List<FunctionDecl> functions,
        ZigBackendTarget target,
        bool addFreestandingEntryShim,
        bool addHostedEntryShim)
    {
        var main = functions.FirstOrDefault(function =>
            function.NamespacePath.Count == 0 && function.Name == "main");
        var start = functions.FirstOrDefault(function =>
            function.NamespacePath.Count == 0 && function.Name == "_start");

        if (addFreestandingEntryShim && start == null && main != null)
        {
            var exitSyscallNumber = GetFreestandingExitSyscallNumber(target);
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
            var exitNumber = new NumberExpr { Value = exitSyscallNumber };
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
        var definitions = CollectGenericFunctionDefinitions(nodes);
        var functions = CollectConcreteFunctions(nodes);
        var pending = new Queue<CallExpr>(CollectCalls(functions));
        var generated = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var call))
        {
            if (call.TypeArguments.Count == 0)
                continue;

            var resolvedName = ResolveCallTargetName(call);
            if (!definitions.TryGetValue(resolvedName, out var definition))
                continue;

            var instanceName = GenericFunctionName(resolvedName, call.TypeArguments);
            if (!generated.Add(instanceName))
                continue;

            var instance = InstantiateGenericFunction(definition, call.TypeArguments, instanceName);
            functions.Add(instance);
            EnqueueNestedCalls(pending, instance);
        }
        return functions;
    }
    private static Dictionary<string, FunctionDecl> CollectGenericFunctionDefinitions(IReadOnlyList<Node> nodes)
    {
        return nodes.OfType<FunctionDecl>()
            .Where(function => function.TypeParameters.Count > 0)
            .ToDictionary(
                function => QualifiedNames.GetFullName(function.NamespacePath, function.Name),
                StringComparer.Ordinal);
    }
    private static List<FunctionDecl> CollectConcreteFunctions(IReadOnlyList<Node> nodes)
    {
        return nodes.OfType<FunctionDecl>()
            .Where(function => function.TypeParameters.Count == 0)
            .ToList();
    }
    private static string ResolveCallTargetName(CallExpr call)
    {
        return call.ResolvedTargetQualifiedName
            ?? call.ResolvedQualifiedName
            ?? QualifiedNames.GetFullName(call.NamespacePath, call.Name);
    }
    private static FunctionDecl InstantiateGenericFunction(
        FunctionDecl definition,
        IReadOnlyList<TypeNode> typeArguments,
        string instanceName)
    {
        var substitutions = AstSpecialization.BuildTypeSubstitutions(
            definition.TypeParameters,
            typeArguments);
        var instance = AstSpecialization.InstantiateFunction(definition, substitutions);
        var (_, shortName) = QualifiedNames.SplitQualifiedName(instanceName);
        instance.Name = shortName;
        instance.TypeParameters.Clear();
        instance.TypeParameterSpecs.Clear();
        return instance;
    }
    private static void EnqueueNestedCalls(Queue<CallExpr> pending, FunctionDecl instance)
    {
        foreach (var nestedCall in CollectCalls(new Node[] { instance }))
            pending.Enqueue(nestedCall);
    }
    private static string GenericFunctionName(
        string resolvedName,
        IReadOnlyList<TypeNode> typeArguments)
    {
        return resolvedName + "$g$" + string.Join("$", typeArguments.Select(FormatTypeKey));
    }
    private static string FormatTypeKey(TypeNode type)
    {
        if (type.IsFunction)
        {
            var parameters = string.Join("_", type.ParamTypes.Select(FormatTypeKey));
            var returnType = FormatTypeKey(type.ReturnType ?? new TypeNode { Name = "void" });
            return $"fn_{parameters}_ret_{returnType}";
        }

        if (type.IsErrorUnion)
        {
            var innerType = FormatTypeKey(type.ErrorInnerType ?? new TypeNode { Name = type.Name });
            return $"err_{innerType}";
        }

        var key = QualifiedNames.GetFullName(type.NamespacePath, type.Name)
            .Replace(".", "_", StringComparison.Ordinal);
        if (type.TypeArguments.Count > 0)
            key += "_" + string.Join("_", type.TypeArguments.Select(FormatTypeKey));
        if (type.IsSlice)
            key = "slice_" + key;
        if (type.ArraySize is int length)
            key = $"array{length}_{key}";
        if (type.IsPointer)
            key = $"{new string('p', Math.Max(type.PointerLevel, 1))}_{key}";
        return type.IsVolatile ? $"volatile_{key}" : key;
    }

    private static long GetFreestandingExitSyscallNumber(ZigBackendTarget target)
    {
        return target.Triple switch
        {
            "x86_64-pc-linux-gnu" => 60,
            "aarch64-unknown-linux-gnu" => 93,
            _ => throw new ZorbCompilerException(
                $"Freestanding entry shim does not support target triple '{target.Triple}'.")
        };
    }
    private static IEnumerable<CallExpr> CollectCalls(IEnumerable<Node> roots)
    {
        var calls = new List<CallExpr>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

        static bool IsSkippableValue(object value)
        {
            return value is string || value.GetType().IsPrimitive;
        }

        static bool CanTraverseProperties(object value)
        {
            return value is Node or AsmOperand or MatchCase or SwitchCase or StructLiteralField or Parameter;
        }

        static IEnumerable<PropertyInfo> GetTraversableProperties(object value)
        {
            return value.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.GetIndexParameters().Length == 0);
        }

        static void AddCallIfPresent(object value, List<CallExpr> calls)
        {
            if (value is CallExpr call)
                calls.Add(call);
        }

        static bool TryVisitSequence(object value, Action<object?> visit)
        {
            if (value is not IEnumerable sequence)
                return false;

            foreach (var item in sequence)
                visit(item);
            return true;
        }

        static void VisitProperties(object value, Action<object?> visit)
        {
            foreach (var property in GetTraversableProperties(value))
                visit(property.GetValue(value));
        }

        void Visit(object? value)
        {
            if (value == null || IsSkippableValue(value))
                return;
            if (!visited.Add(value))
                return;
            AddCallIfPresent(value, calls);
            if (TryVisitSequence(value, Visit))
                return;
            if (!CanTraverseProperties(value))
                return;
            VisitProperties(value, Visit);
        }

        foreach (var root in roots)
            Visit(root);
        return calls;
    }
    private static string FormatType(TypeNode type)
    {
        var prefix = type.IsPointer ? new string('*', Math.Max(type.PointerLevel, 1)) : "";
        return prefix + QualifiedNames.GetFullName(type.NamespacePath, type.Name);
    }
}
