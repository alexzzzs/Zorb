using System.Collections;
using System.Reflection;
using Zorb.Compiler.AST;
using Zorb.Compiler.AST.Expressions;
using Zorb.Compiler.AST.Statements;
using Zorb.Compiler.Layouts;
using Zorb.Compiler.Utils;

namespace Zorb.Compiler.Codegen;

public sealed partial class ZigBackendIrWriter
{
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
