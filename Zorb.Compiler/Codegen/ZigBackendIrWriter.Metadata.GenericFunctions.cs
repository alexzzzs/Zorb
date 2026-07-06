using Zorb.Compiler.AST;
using Zorb.Compiler.AST.Expressions;
using Zorb.Compiler.AST.Statements;
using Zorb.Compiler.Utils;

namespace Zorb.Compiler.Codegen;

public sealed partial class ZigBackendIrWriter
{
    private static List<FunctionDecl> InstantiateGenericFunctions(IReadOnlyList<Node> nodes)
    {
        var definitions = CollectGenericFunctionDefinitions(nodes);
        var functions = CollectConcreteFunctions(nodes);
        var pending = new Queue<GenericFunctionReference>(CollectGenericFunctionReferences(functions.Cast<Node>().Concat(nodes.OfType<VariableDeclarationNode>())));
        var generated = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var call))
        {
            if (!definitions.TryGetValue(call.ResolvedName, out var definition))
                continue;

            var instanceName = GenericFunctionName(call.ResolvedName, call.TypeArguments);
            if (!generated.Add(instanceName))
                continue;

            var instance = InstantiateGenericFunction(definition, call.TypeArguments, instanceName);
            functions.Add(instance);
            EnqueueNestedGenericFunctionReferences(pending, instance);
        }
        return functions;
    }

    private sealed record GenericFunctionReference(string ResolvedName, IReadOnlyList<TypeNode> TypeArguments);

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

    private static IEnumerable<GenericFunctionReference> CollectGenericFunctionReferences(IEnumerable<Node> nodes)
    {
        foreach (var expression in AstTraversal.EnumerateExpressions(nodes))
        {
            if (TryCreateGenericFunctionReference(expression, out var reference))
                yield return reference;
        }
    }

    private static bool TryCreateGenericFunctionReference(Expr expression, out GenericFunctionReference reference)
    {
        switch (expression)
        {
            case IdentifierExpr identifier when identifier.TypeArguments.Count > 0:
                reference = new GenericFunctionReference(identifier.Name, identifier.TypeArguments);
                return true;

            case FieldExpr field when field.TypeArguments.Count > 0 && field.ResolvedQualifiedName != null:
                reference = new GenericFunctionReference(field.ResolvedQualifiedName, field.TypeArguments);
                return true;

            case CallExpr call when call.TypeArguments.Count > 0:
                reference = new GenericFunctionReference(ResolveCallTargetName(call), call.TypeArguments);
                return true;
        }

        reference = null!;
        return false;
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

    private static void EnqueueNestedGenericFunctionReferences(Queue<GenericFunctionReference> pending, FunctionDecl instance)
    {
        foreach (var reference in CollectGenericFunctionReferences([instance]))
            pending.Enqueue(reference);
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
}
