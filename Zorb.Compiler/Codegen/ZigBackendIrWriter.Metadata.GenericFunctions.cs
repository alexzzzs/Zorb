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
        foreach (var node in nodes)
        {
            foreach (var reference in CollectGenericFunctionReferences(node))
                yield return reference;
        }
    }

    private static IEnumerable<GenericFunctionReference> CollectGenericFunctionReferences(Node node)
    {
        switch (node)
        {
            case FunctionDecl function:
                foreach (var statement in function.Body)
                {
                    foreach (var reference in CollectGenericFunctionReferences(statement))
                        yield return reference;
                }
                yield break;

            case VariableDeclarationNode variableDeclaration:
                if (variableDeclaration.Value != null)
                {
                    foreach (var reference in CollectGenericFunctionReferences(variableDeclaration.Value))
                        yield return reference;
                }
                yield break;

            case ExpressionStatement expressionStatement:
                foreach (var reference in CollectGenericFunctionReferences(expressionStatement.Expression))
                    yield return reference;
                yield break;

            case AssignStmt assign:
                foreach (var reference in CollectGenericFunctionReferences(assign.Target))
                    yield return reference;
                foreach (var reference in CollectGenericFunctionReferences(assign.Value))
                    yield return reference;
                yield break;

            case ReturnNode returnNode when returnNode.Value != null:
                foreach (var reference in CollectGenericFunctionReferences(returnNode.Value))
                    yield return reference;
                yield break;

            case IfStmt ifStmt:
                foreach (var reference in CollectGenericFunctionReferences(ifStmt.Condition))
                    yield return reference;
                foreach (var statement in ifStmt.Body)
                {
                    foreach (var reference in CollectGenericFunctionReferences(statement))
                        yield return reference;
                }
                foreach (var statement in ifStmt.ElseBody)
                {
                    foreach (var reference in CollectGenericFunctionReferences(statement))
                        yield return reference;
                }
                yield break;

            case WhileStmt whileStmt:
                foreach (var reference in CollectGenericFunctionReferences(whileStmt.Condition))
                    yield return reference;
                foreach (var statement in whileStmt.Body)
                {
                    foreach (var reference in CollectGenericFunctionReferences(statement))
                        yield return reference;
                }
                yield break;

            case ForStmt forStmt:
                if (forStmt.Initializer != null)
                {
                    foreach (var reference in CollectGenericFunctionReferences(forStmt.Initializer))
                        yield return reference;
                }
                if (forStmt.Condition != null)
                {
                    foreach (var reference in CollectGenericFunctionReferences(forStmt.Condition))
                        yield return reference;
                }
                if (forStmt.Update != null)
                {
                    foreach (var reference in CollectGenericFunctionReferences(forStmt.Update))
                        yield return reference;
                }
                foreach (var statement in forStmt.Body)
                {
                    foreach (var reference in CollectGenericFunctionReferences(statement))
                        yield return reference;
                }
                yield break;

            case SwitchStmt switchStmt:
                foreach (var reference in CollectGenericFunctionReferences(switchStmt.Expression))
                    yield return reference;
                foreach (var switchCase in switchStmt.Cases)
                {
                    foreach (var reference in CollectGenericFunctionReferences(switchCase.Value))
                        yield return reference;
                    foreach (var statement in switchCase.Body)
                    {
                        foreach (var reference in CollectGenericFunctionReferences(statement))
                            yield return reference;
                    }
                }
                foreach (var statement in switchStmt.ElseBody)
                {
                    foreach (var reference in CollectGenericFunctionReferences(statement))
                        yield return reference;
                }
                yield break;

            case MatchStmt matchStmt:
                foreach (var reference in CollectGenericFunctionReferences(matchStmt.Expression))
                    yield return reference;
                foreach (var matchCase in matchStmt.Cases)
                {
                    foreach (var statement in matchCase.Body)
                    {
                        foreach (var reference in CollectGenericFunctionReferences(statement))
                            yield return reference;
                    }
                }
                foreach (var statement in matchStmt.ElseBody)
                {
                    foreach (var reference in CollectGenericFunctionReferences(statement))
                        yield return reference;
                }
                yield break;

            case AsmStatementNode asmStatement:
                foreach (var operand in asmStatement.Outputs.Concat(asmStatement.Inputs))
                {
                    foreach (var reference in CollectGenericFunctionReferences(operand.Expression))
                        yield return reference;
                }
                yield break;
        }
    }

    private static IEnumerable<GenericFunctionReference> CollectGenericFunctionReferences(Expr expr)
    {
        switch (expr)
        {
            case IdentifierExpr identifier when identifier.TypeArguments.Count > 0:
                yield return new GenericFunctionReference(identifier.Name, identifier.TypeArguments);
                yield break;

            case FieldExpr field when field.TypeArguments.Count > 0 && field.ResolvedQualifiedName != null:
                yield return new GenericFunctionReference(field.ResolvedQualifiedName, field.TypeArguments);
                yield break;

            case BinaryExpr binary:
                foreach (var reference in CollectGenericFunctionReferences(binary.Left))
                    yield return reference;
                foreach (var reference in CollectGenericFunctionReferences(binary.Right))
                    yield return reference;
                yield break;

            case CallExpr call:
                if (call.TypeArguments.Count > 0)
                    yield return new GenericFunctionReference(ResolveCallTargetName(call), call.TypeArguments);
                foreach (var argument in call.Args)
                {
                    foreach (var reference in CollectGenericFunctionReferences(argument))
                        yield return reference;
                }
                if (call.TargetExpr != null)
                {
                    foreach (var reference in CollectGenericFunctionReferences(call.TargetExpr))
                        yield return reference;
                }
                yield break;

            case UnaryExpr unary:
                foreach (var reference in CollectGenericFunctionReferences(unary.Operand))
                    yield return reference;
                yield break;

            case CastExpr cast:
                foreach (var reference in CollectGenericFunctionReferences(cast.Expr))
                    yield return reference;
                yield break;

            case IndexExpr index:
                foreach (var reference in CollectGenericFunctionReferences(index.Target))
                    yield return reference;
                foreach (var reference in CollectGenericFunctionReferences(index.Index))
                    yield return reference;
                yield break;

            case FieldExpr field:
                foreach (var reference in CollectGenericFunctionReferences(field.Target))
                    yield return reference;
                yield break;

            case StructLiteralExpr structLiteral:
                foreach (var field in structLiteral.Fields)
                {
                    foreach (var reference in CollectGenericFunctionReferences(field.Value))
                        yield return reference;
                }
                yield break;

            case ArrayLiteralExpr arrayLiteral:
                foreach (var element in arrayLiteral.Elements)
                {
                    foreach (var reference in CollectGenericFunctionReferences(element))
                        yield return reference;
                }
                yield break;

            case CatchExpr catchExpr:
                foreach (var reference in CollectGenericFunctionReferences(catchExpr.Left))
                    yield return reference;
                foreach (var statement in catchExpr.CatchBody)
                {
                    foreach (var reference in CollectGenericFunctionReferences(statement))
                        yield return reference;
                }
                yield break;
        }
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
        foreach (var reference in CollectGenericFunctionReferences(instance))
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
