using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Zorb.Compiler.AST;
using Zorb.Compiler.AST.Expressions;
using Zorb.Compiler.AST.Statements;
using Zorb.Compiler.Layouts;
using Zorb.Compiler.Lexer;
using Zorb.Compiler.Parser;
using Zorb.Compiler.Utils;

namespace Zorb.Compiler.Semantic;

public partial class TypeChecker
{
    private void RegisterEnumMembers(EnumNode enumNode)
    {
        if (_constValueScopes.Count == 0)
            return;

        var enumFullName = QualifiedNames.GetFullName(enumNode.NamespacePath, enumNode.Name);
        var enumType = CreateEnumTypeReference(enumNode);

        var seenNames = new Dictionary<string, EnumMember>(StringComparer.Ordinal);
        long nextValue = 0;
        foreach (var member in enumNode.Members)
        {
            if (!TryRegisterEnumMemberName(enumFullName, member, seenNames))
                continue;

            var resolvedValue = ResolveEnumMemberValue(enumFullName, member, nextValue);

            member.ResolvedValue = resolvedValue;
            nextValue = resolvedValue + 1;

            RegisterConcreteEnumMember(enumNode, enumFullName, enumType, member, resolvedValue);
        }
    }

    private static TypeNode CreateEnumTypeReference(EnumNode enumNode)
    {
        return new TypeNode
        {
            Name = enumNode.Name,
            NamespacePath = new List<string>(enumNode.NamespacePath)
        };
    }

    private bool TryRegisterEnumMemberName(
        string enumFullName,
        EnumMember member,
        Dictionary<string, EnumMember> seenNames)
    {
        if (seenNames.TryAdd(member.Name, member))
            return true;

        _errors.Error(member, $"Duplicate enum member '{enumFullName}.{member.Name}'.{FormatPreviousDeclarationSuffix(seenNames[member.Name])}");
        return false;
    }

    private long ResolveEnumMemberValue(string enumFullName, EnumMember member, long fallbackValue)
    {
        if (member.Value == null)
            return fallbackValue;

        member.Value = NormalizeAliasReferences(member.Value);
        CheckExpression(member.Value);
        if (TryEvaluateConstIntExpr(member.Value, out var resolvedValue, out var constError))
            return resolvedValue;

        _errors.Error(member.Value, constError ?? $"Enum member '{enumFullName}.{member.Name}' must use a constant integer expression.");
        return fallbackValue;
    }

    private void RegisterConcreteEnumMember(
        EnumNode enumNode,
        string enumFullName,
        TypeNode enumType,
        EnumMember member,
        long resolvedValue)
    {
        if (enumNode.TypeParameters.Count != 0)
            return;

        var memberFullName = $"{enumFullName}.{member.Name}";
        MakeVisible(memberFullName);
        if (!_symbolTable.IsDefined(memberFullName))
            _symbolTable.DefineVariable(memberFullName, enumType.Clone(), isConst: true);

        _constValueScopes.Peek()[memberFullName] = resolvedValue;
    }

    private void RegisterUnionTagEnum(UnionNode unionNode)
    {
        var tagEnum = BuildUnionTagEnum(unionNode);
        var tagEnumFullName = GetUnionTagEnumFullName(unionNode);
        MakeVisible(tagEnumFullName);
        if (!_symbolTable.IsDefined(tagEnumFullName))
            _symbolTable.DefineEnum(tagEnumFullName, tagEnum);
        RegisterEnumMembers(tagEnum);
    }

    private void ValidateUnionDeclaration(UnionNode unionNode)
    {
        var unionFullName = QualifiedNames.GetFullName(unionNode.NamespacePath, unionNode.Name);

        var seenNames = new Dictionary<string, UnionVariant>(StringComparer.Ordinal);
        foreach (var variant in unionNode.Variants)
        {
            ValidateUnionVariantType(variant);

            if (!TryRegisterUnionVariantName(unionFullName, variant, seenNames))
                continue;

            ValidateUnionVariantName(unionFullName, variant);
            ValidateUnionVariantTypeRestrictions(unionFullName, variant);
        }
    }

    private void ValidateUnionVariantType(UnionVariant variant)
    {
        NormalizeTypeReferenceInPlace(variant.TypeName);
        ValidateTypeReference(variant.TypeName, variant);
    }

    private bool TryRegisterUnionVariantName(
        string unionFullName,
        UnionVariant variant,
        Dictionary<string, UnionVariant> seenNames)
    {
        if (seenNames.TryAdd(variant.Name, variant))
            return true;

        _errors.Error(variant, $"Union '{unionFullName}' declares variant '{variant.Name}' more than once.{FormatPreviousDeclarationSuffix(seenNames[variant.Name])}");
        return false;
    }

    private void ValidateUnionVariantName(string unionFullName, UnionVariant variant)
    {
        if (variant.Name == "tag")
            _errors.Error(variant, $"Union '{unionFullName}' cannot declare a variant named 'tag' because '.tag' is reserved for the discriminator.");
    }

    private void ValidateUnionVariantTypeRestrictions(string unionFullName, UnionVariant variant)
    {
        if (IsVoidLikeUnionVariantType(variant.TypeName))
            _errors.Error(variant, $"Union variant '{unionFullName}.{variant.Name}' cannot have type 'void'.");
    }

    private static bool IsVoidLikeUnionVariantType(TypeNode type)
    {
        return type.Name == "void" &&
            !type.IsPointer &&
            !type.IsSlice &&
            type.ArraySize == null;
    }

    private static string GetUnionTagEnumFullName(UnionNode unionNode)
    {
        return $"{QualifiedNames.GetFullName(unionNode.NamespacePath, unionNode.Name)}.Tag";
    }

    private static TypeNode GetUnionTagType(UnionNode unionNode)
    {
        return new TypeNode
        {
            Name = "Tag",
            NamespacePath = unionNode.NamespacePath
                .Concat(new[] { unionNode.Name })
                .ToList(),
            TypeArguments = unionNode.TypeParameters
                .Select(parameter => new TypeNode { Name = parameter })
                .ToList()
        };
    }

    private static TypeNode GetUnionTagType(TypeNode unionType)
    {
        return new TypeNode
        {
            Name = "Tag",
            NamespacePath = unionType.NamespacePath
                .Concat(new[] { unionType.Name })
                .ToList(),
            TypeArguments = unionType.TypeArguments
                .Select(argument => argument.Clone())
                .ToList()
        };
    }

    private EnumNode BuildUnionTagEnum(UnionNode unionNode)
    {
        return new EnumNode
        {
            Name = "Tag",
            NamespacePath = unionNode.NamespacePath
                .Concat(new[] { unionNode.Name })
                .ToList(),
            TypeParameters = new List<string>(unionNode.TypeParameters),
            TypeParameterSpecs = CloneAndNormalizeTypeParameterSpecs(unionNode.TypeParameterSpecs),
            UnderlyingType = new TypeNode { Name = "i32" },
            Members = unionNode.Variants
                .Select((variant, index) => new EnumMember
                {
                    Name = variant.Name,
                    ResolvedValue = index
                })
                .ToList()
        };
    }

    private List<GenericTypeParameter> CloneAndNormalizeTypeParameterSpecs(
        IReadOnlyList<GenericTypeParameter> parameters)
    {
        var clonedParameters = new List<GenericTypeParameter>(parameters.Count);
        foreach (var parameter in parameters)
        {
            var clonedParameter = new GenericTypeParameter
            {
                Name = parameter.Name,
                Constraint = parameter.Constraint?.Clone(),
                DefaultType = parameter.DefaultType?.Clone()
            };

            if (clonedParameter.Constraint != null)
                NormalizeTypeReferenceInPlace(clonedParameter.Constraint);

            if (clonedParameter.DefaultType != null)
                NormalizeTypeReferenceInPlace(clonedParameter.DefaultType);

            clonedParameters.Add(clonedParameter);
        }

        return clonedParameters;
    }

    private void ValidateEnumDeclaration(EnumNode enumNode)
    {
        ValidateTypeReference(enumNode.UnderlyingType, enumNode);
        var enumFullName = QualifiedNames.GetFullName(enumNode.NamespacePath, enumNode.Name);

        if (!IsIntegerScalarType(enumNode.UnderlyingType))
        {
            _errors.Error(enumNode, $"Enum '{enumFullName}' must use a built-in integer underlying type, got '{FormatType(enumNode.UnderlyingType)}'.");
            return;
        }

        var seenValues = new Dictionary<long, EnumMember>();
        foreach (var member in enumNode.Members)
        {
            if (member.Value != null)
            {
                member.Value = NormalizeAliasReferences(member.Value);
                CheckExpression(member.Value);
                if (!TryEvaluateConstIntExpr(member.Value, out var resolvedValue, out var constError))
                {
                    _errors.Error(member.Value, constError ?? $"Enum member '{enumFullName}.{member.Name}' must use a constant integer expression.");
                    continue;
                }

                member.ResolvedValue = resolvedValue;
            }

            if (member.ResolvedValue is not long memberValue)
                continue;

            if (!NumericLiteralFits(enumNode.UnderlyingType, memberValue))
                _errors.Error(member, $"Enum member '{enumFullName}.{member.Name}' value '{memberValue}' does not fit underlying type '{FormatType(enumNode.UnderlyingType)}'.");

            if (!seenValues.TryAdd(memberValue, member))
                _errors.Error(member, $"Duplicate enum value '{memberValue}' in enum '{enumFullName}'.{FormatPreviousDeclarationSuffix(seenValues[memberValue])}");
        }
    }

    private static bool IsIntegerTypeName(string typeName)
    {
        return typeName is "i8" or "i16" or "i32" or "i64" or "u8" or "u16" or "u32" or "u64";
    }

    private static bool IsIntegerScalarType(TypeNode type)
    {
        return IsIntegerTypeName(type.Name)
            && !type.IsPointer
            && !type.IsSlice
            && !type.IsFunction
            && !type.IsErrorUnion
            && type.ArraySize == null;
    }

    private void ResolveStructAttributes(StructNode structNode)
    {
        ResolveAlignmentAttribute(structNode.Attributes, structNode.AlignExpr, structNode, $"Struct '{structNode.Name}' Alignment");
        foreach (var field in structNode.Fields)
            ResolveOffsetAttribute(field);
    }

    private void ResolveOffsetAttribute(StructField field)
    {
        if (field.OffsetExpr == null)
            return;

        field.OffsetExpr = NormalizeAliasReferences(field.OffsetExpr);
        CheckExpression(field.OffsetExpr);

        var offsetType = GetExpressionType(field.OffsetExpr, reportErrors: false);
        if (offsetType == null || !IsNumericType(offsetType))
        {
            _errors.Error(field.OffsetExpr, $"Offset must have integer type, got '{FormatType(offsetType)}'.");
            return;
        }

        if (!TryEvaluateConstIntExpr(field.OffsetExpr, out var resolvedOffset, out var constError))
        {
            _errors.Error(field.OffsetExpr, constError ?? "Offset must be a constant integer expression.");
            return;
        }

        if (resolvedOffset < 0 || resolvedOffset > int.MaxValue)
        {
            _errors.Error(field.OffsetExpr, $"Offset '{resolvedOffset}' does not fit in compiler-supported byte offsets.");
            return;
        }

        field.Attributes.RemoveAll(attr => attr.StartsWith("offset(", StringComparison.Ordinal));
        field.Attributes.Add($"offset({resolvedOffset})");
    }

    private void ResolveAlignmentAttribute(List<string> attributes, Expr? alignExpr, Node context, string description)
    {
        if (alignExpr == null)
            return;

        alignExpr = NormalizeAliasReferences(alignExpr);
        CheckExpression(alignExpr);

        var alignType = GetExpressionType(alignExpr, reportErrors: false);
        if (alignType == null || !IsNumericType(alignType))
        {
            _errors.Error(alignExpr, $"{description} must have integer type, got '{FormatType(alignType)}'.");
            return;
        }

        if (!TryEvaluateConstIntExpr(alignExpr, out var resolvedAlignment, out var constError))
        {
            _errors.Error(alignExpr, constError ?? $"{description} must be a constant integer expression.");
            return;
        }

        if (resolvedAlignment <= 0 || resolvedAlignment > int.MaxValue)
        {
            _errors.Error(alignExpr, $"{description} '{resolvedAlignment}' does not fit in compiler-supported alignments.");
            return;
        }

        attributes.RemoveAll(attr => attr.StartsWith("align(", StringComparison.Ordinal));
        attributes.Add($"align({resolvedAlignment})");
    }

    private void ValidateVariableAttributes(VariableDeclarationNode varDecl, bool isGlobal)
    {
        var sectionName = StructLayout.GetSectionName(varDecl.Attributes);
        if (sectionName != null && !isGlobal)
            _errors.Error(varDecl, $"Variable '{varDecl.Name}' uses [section(\"{sectionName}\")], but section placement is only supported for global variables.");
    }

    private void ValidateStructAttributes(StructNode structNode, string fullName)
    {
        var seenFields = new Dictionary<string, StructField>(StringComparer.Ordinal);
        foreach (var field in structNode.Fields)
        {
            if (!seenFields.TryAdd(field.Name, field))
                _errors.Error(field, $"Struct '{fullName}' declares field '{field.Name}' more than once.{FormatPreviousDeclarationSuffix(seenFields[field.Name])}");
        }

        var explicitLayout = StructLayout.HasExplicitLayout(structNode);

        foreach (var field in structNode.Fields)
        {
            var explicitOffset = StructLayout.GetExplicitOffset(field);
            if (!explicitLayout && explicitOffset != null)
                _errors.Error(field, $"Field '{field.Name}' in struct '{fullName}' uses [offset(...)] but the struct is not marked [layout(explicit)].");
        }

        if (!explicitLayout && !structNode.Attributes.Contains("packed") && StructLayout.GetAlignment(structNode.Attributes) == null)
            return;

        if (structNode.TypeParameters.Count > 0)
        {
            if (explicitLayout && structNode.Fields.Any(field => StructLayout.GetExplicitOffset(field) == null))
                _errors.Error(structNode, $"Explicit-layout struct '{fullName}' requires every field to declare an [offset(N)] attribute.");
            return;
        }

        if (!StructLayout.TryCompute(structNode, ResolveConcreteStructForLayout, out _, out var error) && error != null)
            _errors.Error(structNode, error);
    }

    private static string FormatPreviousDeclarationSuffix(Node previous)
    {
        return $" First declaration was at {Path.GetFileName(previous.File)}:{previous.Line}:{previous.Column}.";
    }
}
