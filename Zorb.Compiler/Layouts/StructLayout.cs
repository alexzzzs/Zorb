using System;
using System.Collections.Generic;
using System.Linq;
using Zorb.Compiler.AST.Statements;

namespace Zorb.Compiler.Layouts;

public sealed class StructLayoutField
{
    public StructField Field { get; init; } = null!;
    public int Offset { get; init; }
    public int Size { get; init; }
}

public sealed class StructLayoutInfo
{
    public bool IsPacked { get; init; }
    public bool IsExplicit { get; init; }
    public int Alignment { get; init; }
    public int Size { get; init; }
    public List<StructLayoutField> Fields { get; init; } = new();
}

public static class StructLayout
{
    public static bool HasPackedAttribute(StructNode node)
    {
        return node.Attributes.Contains("packed") || HasExplicitLayout(node);
    }

    public static bool HasExplicitLayout(StructNode node)
    {
        return node.Attributes.Contains("layout(explicit)");
    }

    public static int? GetExplicitOffset(StructField field)
    {
        foreach (var attr in field.Attributes)
        {
            if (attr.StartsWith("offset(", StringComparison.Ordinal) && attr.EndsWith(")", StringComparison.Ordinal))
            {
                var text = attr.Substring(7, attr.Length - 8);
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(text.Substring(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var val))
                        return val;
                }
                else
                {
                    if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var val))
                        return val;
                }
                return null;
            }
        }

        return null;
    }

    public static int? GetAlignment(List<string> attributes)
    {
        foreach (var attr in attributes)
        {
            if (attr.StartsWith("align(", StringComparison.Ordinal) && attr.EndsWith(")", StringComparison.Ordinal))
            {
                var text = attr.Substring(6, attr.Length - 7);
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(text.Substring(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var val))
                        return val;
                }
                else
                {
                    if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var val))
                        return val;
                }
                return null;
            }
        }

        return null;
    }

    public static string? GetSectionName(List<string> attributes)
    {
        foreach (var attr in attributes)
        {
            if (attr.StartsWith("section:", StringComparison.Ordinal))
                return attr.Substring("section:".Length);
        }

        return null;
    }

    public static bool TryCompute(
        StructNode node,
        Func<string, StructNode?> resolveStruct,
        out StructLayoutInfo? layout,
        out string? error)
    {
        return TryCompute(node, resolveStruct, new HashSet<string>(StringComparer.Ordinal), out layout, out error);
    }

    private static bool TryCompute(
        StructNode node,
        Func<string, StructNode?> resolveStruct,
        HashSet<string> activeStructs,
        out StructLayoutInfo? layout,
        out string? error)
    {
        layout = null;
        error = null;

        var fullName = GetFullName(node.NamespacePath, node.Name);
        if (!activeStructs.Add(fullName))
        {
            error = $"Struct '{fullName}' has a recursive layout that cannot be computed.";
            return false;
        }

        try
        {
            var isExplicit = HasExplicitLayout(node);
            var isPacked = HasPackedAttribute(node);
            var forcedAlignment = GetAlignment(node.Attributes);
            var fields = new List<StructLayoutField>(node.Fields.Count);
            var offset = 0;
            var structAlignment = isPacked ? 1 : 1;

            foreach (var field in node.Fields)
            {
                if (!TryGetTypeLayout(field.TypeName, resolveStruct, activeStructs, out var fieldSize, out var fieldAlignment, out error))
                {
                    error = $"Field '{field.Name}' in struct '{fullName}' cannot be laid out: {error}";
                    return false;
                }

                var explicitOffset = GetExplicitOffset(field);
                if (isExplicit)
                {
                    if (explicitOffset == null)
                    {
                        error = $"Explicit-layout struct '{fullName}' requires every field to declare an [offset(N)] attribute.";
                        return false;
                    }

                    if (explicitOffset.Value < offset)
                    {
                        error = $"Field '{field.Name}' in struct '{fullName}' overlaps a previous field. Offset {explicitOffset.Value} is before byte {offset}.";
                        return false;
                    }

                    offset = explicitOffset.Value;
                }
                else if (explicitOffset != null)
                {
                    error = $"Field '{field.Name}' in struct '{fullName}' uses [offset(...)] but the struct is not marked [layout(explicit)].";
                    return false;
                }
                else if (!isPacked)
                {
                    offset = AlignUp(offset, fieldAlignment);
                }

                fields.Add(new StructLayoutField
                {
                    Field = field,
                    Offset = offset,
                    Size = fieldSize
                });

                offset += fieldSize;
                if (!isPacked)
                    structAlignment = Math.Max(structAlignment, fieldAlignment);
            }

            if (forcedAlignment is > 0)
                structAlignment = Math.Max(structAlignment, forcedAlignment.Value);

            if (structAlignment <= 0)
                structAlignment = 1;

            var size = isPacked ? offset : AlignUp(offset, structAlignment);
            if (forcedAlignment is > 0)
                size = AlignUp(size, forcedAlignment.Value);

            layout = new StructLayoutInfo
            {
                IsPacked = isPacked,
                IsExplicit = isExplicit,
                Alignment = structAlignment,
                Size = size,
                Fields = fields
            };
            return true;
        }
        finally
        {
            activeStructs.Remove(fullName);
        }
    }

    private static bool TryGetTypeLayout(
        TypeNode type,
        Func<string, StructNode?> resolveStruct,
        HashSet<string> activeStructs,
        out int size,
        out int alignment,
        out string? error)
    {
        size = 0;
        alignment = 1;
        error = null;

        if (type.IsFunction)
        {
            error = "function types are not supported in byte-precise struct layouts";
            return false;
        }

        if (type.IsErrorUnion)
        {
            error = "error unions are not supported in byte-precise struct layouts";
            return false;
        }

        if (type.IsSlice)
        {
            error = "slice types are not supported in byte-precise struct layouts";
            return false;
        }

        if (type.Name == "void")
        {
            error = "void is not a valid field type";
            return false;
        }

        if (type.ArraySize is > 0)
        {
            var elementType = type.Clone();
            elementType.ArraySize = null;
            if (!TryGetTypeLayout(elementType, resolveStruct, activeStructs, out var elementSize, out var elementAlignment, out error))
                return false;

            size = checked(elementSize * type.ArraySize.Value);
            alignment = elementAlignment;
            return true;
        }

        if (type.IsPointer || type.Name == "string")
        {
            size = 8;
            alignment = 8;
            return true;
        }

        switch (type.Name)
        {
            case "i8":
            case "u8":
            case "char":
                size = 1;
                alignment = 1;
                return true;
            case "i16":
            case "u16":
                size = 2;
                alignment = 2;
                return true;
            case "i32":
            case "u32":
            case "bool":
                size = 4;
                alignment = 4;
                return true;
            case "i64":
            case "u64":
                size = 8;
                alignment = 8;
                return true;
        }

        var fullName = GetFullName(type.NamespacePath, type.Name);
        var structNode = resolveStruct(fullName);
        if (structNode == null)
        {
            error = $"unknown struct type '{fullName}'";
            return false;
        }

        if (!TryCompute(structNode, resolveStruct, activeStructs, out var layout, out error))
            return false;

        size = layout!.Size;
        alignment = layout.Alignment;
        return true;
    }

    private static int AlignUp(int value, int alignment)
    {
        if (alignment <= 1)
            return value;

        var remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    private static string GetFullName(List<string> namespacePath, string name)
    {
        return namespacePath.Count > 0
            ? string.Join(".", namespacePath) + "." + name
            : name;
    }
}