using Zorb.Compiler.AST;
using Zorb.Compiler.AST.Expressions;
using Zorb.Compiler.AST.Statements;

namespace Zorb.Compiler.Utils;

internal static class QualifiedNames
{
    public static string GetFullName(IReadOnlyList<string> namespacePath, string name)
    {
        return namespacePath.Count > 0
            ? string.Join(".", namespacePath) + "." + name
            : name;
    }

    public static (List<string> NamespacePath, string Name) SplitQualifiedName(string qualifiedName)
    {
        var parts = qualifiedName.Split('.');
        return (
            parts.Length > 1 ? parts[..^1].ToList() : new List<string>(),
            parts[^1]);
    }

    public static string? TryGetQualifiedName(Expr expr)
    {
        return expr switch
        {
            IdentifierExpr id => id.Name,
            FieldExpr field => TryGetQualifiedName(field.Target) is string targetName
                ? $"{targetName}.{field.Field}"
                : null,
            _ => null
        };
    }

    public static void ApplyResolvedQualifiedName(TypeNode type, string resolvedName)
    {
        var (namespacePath, name) = SplitQualifiedName(resolvedName);
        type.Name = name;
        type.NamespacePath = namespacePath;
        type.IsAliasQualifiedReference = true;
    }

    public static void ApplyResolvedQualifiedName(CallExpr call, string resolvedName)
    {
        var (namespacePath, name) = SplitQualifiedName(resolvedName);
        call.Name = name;
        call.NamespacePath = namespacePath;
    }
}
