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
    private void ValidateTypeReference(TypeNode type, Node context)
    {
        ValidateNestedTypeArguments(type, context);
        ResolveTypeReferenceName(type);
        ValidateArraySizeExpression(type);

        if (ValidateFunctionOrErrorUnionTypeReference(type, context))
            return;

        if (IsTypeParameterReference(type))
            return;

        if (ValidateBuiltinTypeReference(type, context))
            return;

        ValidateNamedTypeReference(type, context);
    }
    private void ValidateNestedTypeArguments(TypeNode type, Node context)
    {
        foreach (var typeArgument in type.TypeArguments)
            ValidateTypeReference(typeArgument, context);
    }
    private void ResolveTypeReferenceName(TypeNode type)
    {
        var sourceFullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
        if (TryResolveAliasQualifiedName(sourceFullName, out var resolvedTypeName))
        {
            QualifiedNames.ApplyResolvedQualifiedName(type, resolvedTypeName);
            return;
        }

        NormalizeTypeReferenceInPlace(type);
    }
    private void ValidateArraySizeExpression(TypeNode type)
    {
        if (type.ArraySizeExpr == null)
            return;

        type.ArraySizeExpr = NormalizeAliasReferences(type.ArraySizeExpr);
        CheckExpression(type.ArraySizeExpr);

        var sizeType = GetExpressionType(type.ArraySizeExpr, reportErrors: false);
        if (sizeType == null || !IsNumericType(sizeType))
        {
            _errors.Error(type.ArraySizeExpr, $"Array size must have integer type, got '{FormatType(sizeType)}'.");
            return;
        }

        ValidateConstantArraySize(type);
    }
    private void ValidateConstantArraySize(TypeNode type)
    {
        if (type.ArraySizeExpr == null)
            return;

        if (TryEvaluateConstIntExpr(type.ArraySizeExpr, out var resolvedSize, out var constError))
        {
            if (resolvedSize < 0)
                _errors.Error(type.ArraySizeExpr, $"Array size '{resolvedSize}' must be non-negative.");
            else if (resolvedSize > int.MaxValue)
                _errors.Error(type.ArraySizeExpr, $"Array size '{resolvedSize}' does not fit in compiler-supported array bounds.");
            else
                type.ArraySize = (int)resolvedSize;
        }
        else
        {
            _errors.Error(type.ArraySizeExpr, constError ?? "Array size must be a constant integer expression.");
        }

        if (type.IsErrorUnion && type.ErrorInnerType != null)
            type.ErrorInnerType.ArraySize = type.ArraySize;
    }
    private bool ValidateFunctionOrErrorUnionTypeReference(TypeNode type, Node context)
    {
        if (type.IsFunction)
        {
            ValidateFunctionTypeReference(type, context);
            return true;
        }

        if (!type.IsErrorUnion || type.ErrorInnerType == null)
            return false;

        ValidateTypeReference(type.ErrorInnerType, context);
        return true;
    }
    private void ValidateFunctionTypeReference(TypeNode type, Node context)
    {
        if (type.IsVolatile)
            _errors.Error(context, "Function types cannot be volatile-qualified.");

        if (type.ReturnType != null)
            ValidateTypeReference(type.ReturnType, context);

        foreach (var paramType in type.ParamTypes)
            ValidateTypeReference(paramType, context);
    }
    private bool ValidateBuiltinTypeReference(TypeNode type, Node context)
    {
        if (type.Name != "void" &&
            type.Name != "string" &&
            type.Name != "bool" &&
            type.Name != "char" &&
            !_numericTypes.Contains(type.Name))
        {
            return false;
        }

        if (type.TypeArguments.Count > 0)
            _errors.Error(context, $"Type '{type.Name}' does not accept type arguments.");

        return true;
    }
    private void ValidateNamedTypeReference(TypeNode type, Node context)
    {
        var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);

        if (ValidateEnumTypeReference(type, context, fullName))
            return;

        if (ValidateUnionTypeReference(type, context, fullName))
            return;

        if (ValidateExternTypeReference(type, context, fullName))
            return;

        ValidateStructTypeReference(type, context, fullName);
    }
    private bool ValidateEnumTypeReference(TypeNode type, Node context, string fullName)
    {
        if (_symbolTable.LookupEnumNode(fullName) is not EnumNode enumNode)
            return false;

        if (!TryResolveGenericTypeArguments(enumNode.TypeParameterSpecs, type.TypeArguments, context, $"Enum '{fullName}'", out var resolvedTypeArguments))
            return true;

        type.TypeArguments = resolvedTypeArguments;
        ValidateTypeVisibility(type, context, "Enum", fullName);
        return true;
    }
    private bool ValidateUnionTypeReference(TypeNode type, Node context, string fullName)
    {
        if (_symbolTable.LookupUnionNode(fullName) is not UnionNode unionNode)
            return false;

        if (!TryResolveGenericTypeArguments(unionNode.TypeParameterSpecs, type.TypeArguments, context, $"Union '{fullName}'", out var resolvedTypeArguments))
            return true;

        type.TypeArguments = resolvedTypeArguments;
        ValidateTypeVisibility(type, context, "Union", fullName);
        return true;
    }
    private bool ValidateExternTypeReference(TypeNode type, Node context, string fullName)
    {
        if (!_symbolTable.IsExternType(fullName))
            return false;

        if (type.TypeArguments.Count > 0)
            _errors.Error(context, $"Extern type '{fullName}' does not accept type arguments.");

        ValidateTypeVisibility(type, context, "Extern type", fullName);
        return true;
    }
    private void ValidateStructTypeReference(TypeNode type, Node context, string fullName)
    {
        var structNode = _symbolTable.LookupStructNode(fullName);
        if (structNode == null)
        {
            _errors.Error(context, $"Unknown type '{fullName}'");
            return;
        }

        if (!TryResolveGenericTypeArguments(structNode.TypeParameterSpecs, type.TypeArguments, context, $"Struct '{fullName}'", out var resolvedTypeArguments))
            return;

        type.TypeArguments = resolvedTypeArguments;
        ValidateConcreteGenericStructLayout(type, context, structNode);
        ValidateTypeVisibility(type, context, "Struct", fullName);
    }
    private void ValidateTypeVisibility(TypeNode type, Node context, string kind, string fullName)
    {
        if (!type.IsAliasQualifiedReference && !CheckVisibility(fullName))
            ReportNotVisible(context, kind, fullName);
    }
    private bool IsSwitchOperandType(TypeNode type)
    {
        return IsNumericType(type) || IsBoolType(type) || IsEnumType(type);
    }
    private bool TryGetSwitchCaseKey(Expr expr, out string key)
    {
        if (expr is BuiltinExpr { Name: "true" })
        {
            key = "true";
            return true;
        }

        if (expr is BuiltinExpr { Name: "false" })
        {
            key = "false";
            return true;
        }

        if (TryEvaluateConstIntExpr(expr, out var value, out _))
        {
            key = value.ToString();
            return true;
        }

        key = "";
        return false;
    }
    private bool AreEqualityComparableTypes(TypeNode leftType, TypeNode rightType)
    {
        if (IsEnumType(leftType) || IsEnumType(rightType))
            return IsEnumType(leftType) && IsEnumType(rightType) && TypeHelpers.SameType(leftType, rightType);

        if (IsNumericType(leftType) && IsNumericType(rightType))
            return true;

        if (IsBoolType(leftType) && IsBoolType(rightType))
            return true;

        if (IsStringType(leftType) && IsStringType(rightType))
            return true;

        if (leftType.IsPointer && rightType.IsPointer && TypeHelpers.SameType(leftType, rightType))
            return true;

        return false;
    }
    private bool IsPointerArithmetic(string op, TypeNode leftType, TypeNode rightType)
    {
        if (op == "+")
            return (leftType.IsPointer && IsNumericType(rightType)) ||
                (IsNumericType(leftType) && rightType.IsPointer);

        if (op == "-")
            return leftType.IsPointer && IsNumericType(rightType);

        return false;
    }
    private static bool IsNumericType(TypeNode? type) => TypePredicates.IsNumericType(type);

    private static bool IsBoolType(TypeNode? type)
    {
        return type != null
            && !type.IsSlice
            && !type.IsPointer
            && !type.IsErrorUnion
            && !type.IsFunction
            && type.ArraySize == null
            && type.Name == "bool";
    }
    private static bool IsStringType(TypeNode? type)
    {
        return type != null && !type.IsSlice && !type.IsPointer && !type.IsErrorUnion && type.Name == "string";
    }
    private bool IsEnumType(TypeNode? type)
    {
        if (type == null ||
            type.IsSlice ||
            type.IsPointer ||
            type.IsErrorUnion ||
            type.IsFunction ||
            type.ArraySize != null)
        {
            return false;
        }

        var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
        return _symbolTable.LookupEnumNode(fullName) != null;
    }
    private EnumNode? LookupEnumDefinition(TypeNode? type)
    {
        if (!IsEnumType(type))
            return null;

        var fullName = QualifiedNames.GetFullName(type!.NamespacePath, type.Name);
        var definition = _symbolTable.LookupEnumNode(fullName);
        if (definition == null)
            return null;

        return definition.TypeParameters.Count == 0 ? definition : InstantiateEnum(definition, type);
    }
    private UnionNode? LookupUnionDefinition(TypeNode? type)
    {
        if (!IsUnionType(type))
            return null;

        var fullName = QualifiedNames.GetFullName(type!.NamespacePath, type.Name);
        var definition = _symbolTable.LookupUnionNode(fullName);
        if (definition == null)
            return null;

        return definition.TypeParameters.Count == 0 ? definition : InstantiateUnion(definition, type);
    }
    private bool IsUnionType(TypeNode? type)
    {
        if (type == null ||
            type.IsSlice ||
            type.IsPointer ||
            type.IsErrorUnion ||
            type.IsFunction ||
            type.ArraySize != null)
        {
            return false;
        }

        var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
        return _symbolTable.LookupUnionNode(fullName) != null;
    }
    private bool IsValidUnionField(TypeNode unionType, string fieldName)
    {
        var fullName = QualifiedNames.GetFullName(unionType.NamespacePath, unionType.Name);
        var unionDefinition = _symbolTable.LookupUnionNode(fullName);
        if (unionDefinition == null)
            return false;

        return fieldName == "tag" || unionDefinition.Variants.Any(variant => variant.Name == fieldName);
    }
    private bool TryResolveStaticTypeReference(Expr expr, out TypeNode type)
    {
        switch (expr)
        {
            case TypeReferenceExpr typeReference:
                return TryResolveStaticTypeReferenceExpr(typeReference, out type);

            case IdentifierExpr identifier:
                return TryResolveStaticIdentifierReference(identifier, out type);

            case FieldExpr { Field: "Tag" } field:
                if (TryResolveStaticUnionTagReference(field, out type))
                    return true;
                break;

            case FieldExpr field:
                if (TryResolveStaticQualifiedFieldReference(field, out type))
                    return true;
                break;
        }

        type = null!;
        return false;
    }
    private bool TryResolveStaticTypeReferenceExpr(TypeReferenceExpr typeReference, out TypeNode type)
    {
        type = typeReference.TypeName.Clone();
        ValidateTypeReference(type, typeReference);
        return true;
    }
    private bool TryResolveStaticIdentifierReference(IdentifierExpr identifier, out TypeNode type)
    {
        var resolvedIdentifier = ResolveQualifiedName(identifier.Name);
        if (_symbolTable.TryLookup(resolvedIdentifier, out var identifierSymbol) &&
            identifierSymbol != null &&
            identifierSymbol.Kind is SymbolKind.Enum or SymbolKind.Union)
        {
            type = identifierSymbol.Type.Clone();
            return true;
        }

        type = null!;
        return false;
    }
    private bool TryResolveStaticUnionTagReference(FieldExpr field, out TypeNode type)
    {
        if (field.Field == "Tag" &&
            TryResolveStaticTypeReference(field.Target, out var unionType) &&
            IsUnionType(unionType))
        {
            type = GetUnionTagType(unionType);
            return true;
        }

        type = null!;
        return false;
    }
    private bool TryResolveStaticQualifiedFieldReference(FieldExpr field, out TypeNode type)
    {
        if (ResolveQualifiedFieldSymbol(field) is ResolvedFieldSymbolInfo resolvedField &&
            resolvedField.SymbolInfo.Kind is SymbolKind.Enum or SymbolKind.Union)
        {
            type = resolvedField.SymbolInfo.Type.Clone();
            return true;
        }

        type = null!;
        return false;
    }
    private bool TryResolveStaticEnumMember(FieldExpr field, out TypeNode enumType, out long value)
    {
        enumType = null!;
        value = 0;

        if (!TryResolveStaticEnumOwnerType(field, out var ownerType))
            return false;

        if (!TryResolveStaticEnumMemberValue(ownerType, field.Field, out value))
            return false;

        enumType = ownerType.Clone();
        return true;
    }
    private bool TryResolveStaticEnumOwnerType(FieldExpr field, out TypeNode ownerType)
    {
        if (TryResolveStaticTypeReference(field.Target, out ownerType) && IsEnumType(ownerType))
            return true;

        ownerType = null!;
        return false;
    }
    private bool TryResolveStaticEnumMemberValue(TypeNode ownerType, string memberName, out long value)
    {
        var enumDefinition = LookupEnumDefinition(ownerType);
        var member = enumDefinition?.Members.FirstOrDefault(candidate => candidate.Name == memberName);
        if (member?.ResolvedValue is long resolvedValue)
        {
            value = resolvedValue;
            return true;
        }

        value = 0;
        return false;
    }
    private bool TryResolveStaticVariantReference(Expr expr, out TypeNode ownerType, out string variantName)
    {
        if (expr is not FieldExpr field)
        {
            ownerType = null!;
            variantName = "";
            return false;
        }

        if (!TryResolveStaticTypeReference(field.Target, out ownerType))
        {
            variantName = "";
            return false;
        }

        variantName = field.Field;
        return true;
    }
    private static string FormatType(TypeNode? type)
    {
        if (type == null)
            return "unknown";

        if (type.IsFunction)
        {
            var parameters = string.Join(", ", type.ParamTypes.Select(FormatType));
            var returnType = type.ReturnType != null ? FormatType(type.ReturnType) : "void";
            return $"fn({parameters}) -> {returnType}";
        }

        if (type.IsErrorUnion)
        {
            var innerType = type.ErrorInnerType ?? type;
            return "!" + FormatNonErrorType(innerType);
        }

        return FormatNonErrorType(type);
    }
    private static string FormatNonErrorType(TypeNode type)
    {
        var baseName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
        if (type.TypeArguments.Count > 0)
            baseName += "<" + string.Join(", ", type.TypeArguments.Select(FormatType)) + ">";

        if (type.IsPointer)
        {
            var level = type.PointerLevel > 0 ? type.PointerLevel : 1;
            baseName = new string('*', level) + baseName;
        }

        if (type.ArraySize != null)
            baseName = $"[{type.ArraySize}]{baseName}";

        if (type.IsSlice)
            baseName = $"[]{baseName}";

        if (type.IsVolatile)
            baseName = "volatile " + baseName;

        return baseName;
    }
    private static bool CanDecayArrayToPointer(TypeNode target, TypeNode source)
    {
        if (target.IsSlice || !target.IsPointer || source.ArraySize == null)
            return false;

        if (source.IsErrorUnion || source.IsFunction)
            return false;

        var targetLevel = target.PointerLevel > 0 ? target.PointerLevel : 1;
        if (targetLevel != 1)
            return false;

        return target.Name == source.Name &&
            target.NamespacePath.SequenceEqual(source.NamespacePath) &&
            TypeArgumentListsMatch(target.TypeArguments, source.TypeArguments);
    }
    private static bool CanCoerceImplicitly(TypeNode target, TypeNode source)
    {
        return CanDecayArrayToPointer(target, source) || CanCoerceToSlice(target, source);
    }
    private static bool CanCoerceToSlice(TypeNode target, TypeNode source)
    {
        if (!target.IsSlice || source.IsSlice || source.ArraySize == null)
            return false;

        if (source.IsPointer || source.IsErrorUnion || source.IsFunction)
            return false;

        var targetElement = target.Clone();
        targetElement.IsSlice = false;

        var sourceElement = source.Clone();
        sourceElement.ArraySize = null;

        return TypeHelpers.SameType(targetElement, sourceElement);
    }
    private bool IsAssignableTo(TypeNode target, Expr? sourceExpr, TypeNode? source)
    {
        if (source == null)
            return true;

        if (TrySpecializeGenericFunctionValueForTarget(target, sourceExpr, source, out var specializedSource))
            source = specializedSource;

        if (source.IsErrorUnion && !target.IsErrorUnion)
            return false;

        if (TryMatchFunctionAssignability(target, source, out var functionAssignable))
            return functionAssignable;

        if (HasDirectAssignability(target, source))
            return true;

        if (HasFailedFixedArrayAssignability(target, source))
            return false;

        if (CanAssignPointerToVoidPointer(target, source))
            return true;

        if (TryMatchNumericAssignability(target, source, sourceExpr, out var numericAssignable))
            return numericAssignable;

        if (TryMatchNominalAssignability(target, source, out var nominalAssignable))
            return nominalAssignable;

        return false;
    }
    private static bool TryMatchFunctionAssignability(TypeNode target, TypeNode source, out bool assignable)
    {
        if (!target.IsFunction && !source.IsFunction)
        {
            assignable = false;
            return false;
        }

        if (!target.IsFunction || !source.IsFunction)
        {
            assignable = false;
            return true;
        }

        if (!TypeHelpers.SameType(target.ReturnType, source.ReturnType) ||
            target.ParamTypes.Count != source.ParamTypes.Count)
        {
            assignable = false;
            return true;
        }

        for (var i = 0; i < target.ParamTypes.Count; i++)
        {
            if (!TypeHelpers.SameType(target.ParamTypes[i], source.ParamTypes[i]))
            {
                assignable = false;
                return true;
            }
        }

        assignable = true;
        return true;
    }
    private bool HasDirectAssignability(TypeNode target, TypeNode source)
    {
        return TypeHelpers.SameType(target, source) ||
            CanAddVolatileQualifier(target, source) ||
            CanCoerceImplicitly(target, source);
    }
    private bool HasFailedFixedArrayAssignability(TypeNode target, TypeNode source)
    {
        var targetIsFixedArray = target.ArraySize != null;
        var sourceIsFixedArray = source.ArraySize != null;
        if (!targetIsFixedArray && !sourceIsFixedArray)
            return false;

        // Once exact fixed-array identity fails, the only remaining implicit path is array-to-slice coercion.
        return !CanCoerceImplicitly(target, source);
    }
    private static bool CanAssignPointerToVoidPointer(TypeNode target, TypeNode source)
    {
        return target.IsPointer && target.Name == "void" && source.IsPointer;
    }
    private bool TryMatchNumericAssignability(TypeNode target, TypeNode source, Expr? sourceExpr, out bool assignable)
    {
        if (target.IsSlice || source.IsSlice || target.IsPointer || source.IsPointer ||
            !_numericTypes.Contains(target.Name) || !_numericTypes.Contains(source.Name))
        {
            assignable = false;
            return false;
        }

        if (TryGetIntegerLiteralValue(sourceExpr, out var literalValue))
        {
            assignable = NumericLiteralFits(target, literalValue);
            return true;
        }

        assignable = IsWideningNumericConversion(target, source);
        return true;
    }
    private bool TryMatchNominalAssignability(TypeNode target, TypeNode source, out bool assignable)
    {
        if (target.IsSlice || source.IsSlice || _numericTypes.Contains(target.Name) || _numericTypes.Contains(source.Name))
        {
            assignable = false;
            return false;
        }

        assignable = target.IsPointer == source.IsPointer &&
            target.PointerLevel == source.PointerLevel &&
            target.IsErrorUnion == source.IsErrorUnion &&
            target.IsFunction == source.IsFunction &&
            target.ArraySize == source.ArraySize &&
            target.Name == source.Name &&
            target.NamespacePath.SequenceEqual(source.NamespacePath) &&
            TypeArgumentListsMatch(target.TypeArguments, source.TypeArguments);

        if (assignable && target.IsErrorUnion)
            assignable = TypeHelpers.SameType(target.ErrorInnerType, source.ErrorInnerType);

        if (assignable && target.IsFunction)
            assignable = TypeHelpers.SameType(target.ReturnType, source.ReturnType) &&
                TypeArgumentListsMatch(target.ParamTypes, source.ParamTypes);
        return true;
    }
    private static bool CanAddVolatileQualifier(TypeNode target, TypeNode source)
    {
        if (!target.IsVolatile || source.IsVolatile)
            return false;

        var unqualifiedTarget = target.Clone();
        unqualifiedTarget.IsVolatile = false;
        return TypeHelpers.SameType(unqualifiedTarget, source);
    }
    private static bool TypeArgumentListsMatch(IReadOnlyList<TypeNode> left, IReadOnlyList<TypeNode> right)
    {
        if (left.Count != right.Count)
            return false;
        for (int i = 0; i < left.Count; i++)
        {
            if (!TypeHelpers.SameType(left[i], right[i]))
                return false;
        }
        return true;
    }
    private static bool SameNumericType(TypeNode target, TypeNode source)
    {
        return target.Name == source.Name
            && target.NamespacePath.SequenceEqual(source.NamespacePath)
            && !target.IsPointer
            && !source.IsPointer
            && target.ArraySize == null
            && source.ArraySize == null
            && !target.IsErrorUnion
            && !source.IsErrorUnion
            && !target.IsFunction
            && !source.IsFunction;
    }
    private static bool IsWideningNumericConversion(TypeNode target, TypeNode source)
    {
        if (SameNumericType(target, source))
            return true;

        if (!TryGetIntegerTypeInfo(target.Name, out var targetInfo) || !TryGetIntegerTypeInfo(source.Name, out var sourceInfo))
            return false;

        if (sourceInfo.IsSigned == targetInfo.IsSigned)
            return sourceInfo.Bits <= targetInfo.Bits;

        if (!sourceInfo.IsSigned && targetInfo.IsSigned)
            return sourceInfo.Bits < targetInfo.Bits;

        return false;
    }
    private static bool NumericLiteralFits(TypeNode target, long value)
    {
        return target.Name switch
        {
            "i8" => value >= sbyte.MinValue && value <= sbyte.MaxValue,
            "i16" => value >= short.MinValue && value <= short.MaxValue,
            "i32" => value >= int.MinValue && value <= int.MaxValue,
            "i64" => true,
            "u8" => value >= byte.MinValue && value <= byte.MaxValue,
            "u16" => value >= ushort.MinValue && value <= ushort.MaxValue,
            "u32" => value >= uint.MinValue && value <= uint.MaxValue,
            "u64" => value >= 0,
            _ => false
        };
    }
    private static bool TryGetIntegerLiteralValue(Expr? expr, out long value)
    {
        switch (expr)
        {
            case NumberExpr number:
                value = number.Value;
                return true;
            case UnaryExpr { Operator: "-", Operand: NumberExpr number }:
                value = -number.Value;
                return true;
            default:
                value = 0;
                return false;
        }
    }
    private void WarnOnMixedSignednessComparison(BinaryExpr bin, TypeNode leftType, Expr leftExpr, TypeNode rightType, Expr rightExpr)
    {
        if (!TryGetIntegerTypeInfo(leftType.Name, out var leftInfo) || !TryGetIntegerTypeInfo(rightType.Name, out var rightInfo))
            return;

        if (leftInfo.IsSigned == rightInfo.IsSigned)
            return;

        if (TryGetIntegerLiteralValue(leftExpr, out var leftLiteral) && NumericLiteralFits(rightType, leftLiteral))
            return;

        if (TryGetIntegerLiteralValue(rightExpr, out var rightLiteral) && NumericLiteralFits(leftType, rightLiteral))
            return;

        var leftLabel = leftInfo.IsSigned ? "signed" : "unsigned";
        var rightLabel = rightInfo.IsSigned ? "signed" : "unsigned";
        _errors.Warning(bin, $"Comparison '{bin.Operator}' mixes {leftLabel} type '{FormatType(leftType)}' with {rightLabel} type '{FormatType(rightType)}'. The comparison uses the usual integer conversion rules.");
    }
    private bool TryBuildNumericConversionDiagnostic(TypeNode targetType, Expr? sourceExpr, TypeNode? sourceType, out string message)
    {
        message = "";

        if (sourceType == null)
            return false;

        if (!IsIntegerScalarType(targetType) || !IsIntegerScalarType(sourceType))
            return false;

        if (TryGetIntegerLiteralValue(sourceExpr, out var literalValue))
        {
            if (NumericLiteralFits(targetType, literalValue))
                return false;

            message = $"Literal value '{literalValue}' does not fit target integer type '{FormatType(targetType)}'.";
            return true;
        }

        if (!TryGetIntegerTypeInfo(targetType.Name, out var targetInfo) || !TryGetIntegerTypeInfo(sourceType.Name, out var sourceInfo))
            return false;

        var targetTypeText = FormatType(targetType);
        var sourceTypeText = FormatType(sourceType);

        if (sourceInfo.IsSigned != targetInfo.IsSigned)
        {
            var sourceSignedness = sourceInfo.IsSigned ? "signed" : "unsigned";
            var targetSignedness = targetInfo.IsSigned ? "signed" : "unsigned";
            message = $"Implicit conversion from {sourceSignedness} integer type '{sourceTypeText}' to {targetSignedness} integer type '{targetTypeText}' is not allowed. Use cast({targetTypeText}, ...) if this change of signedness is intentional.";
            return true;
        }

        if (sourceInfo.Bits > targetInfo.Bits)
        {
            message = $"Implicit narrowing conversion from integer type '{sourceTypeText}' to '{targetTypeText}' is not allowed. Use cast({targetTypeText}, ...) if truncation is intentional.";
            return true;
        }

        return false;
    }
    private bool TryEvaluateConstIntExpr(Expr expr, out long value, out string? error)
    {
        switch (expr)
        {
            case NumberExpr number:
                return TryEvaluateNumberConstExpr(number, out value, out error);

            case IdentifierExpr identifier:
                return TryEvaluateIdentifierConstExpr(identifier, out value, out error);

            case FieldExpr field:
                return TryEvaluateFieldConstExpr(field, out value, out error);

            case UnaryExpr { Operator: "-", Operand: var operand }:
                return TryEvaluateUnaryNegationConstExpr(operand, out value, out error);

            case BinaryExpr binary when NumericOperators.Contains(binary.Operator):
                return TryEvaluateBinaryConstExpr(binary, out value, out error);

            default:
                value = 0;
                error = null;
                return false;
        }
    }
    private static bool TryEvaluateNumberConstExpr(NumberExpr number, out long value, out string? error)
    {
        value = number.Value;
        error = null;
        return true;
    }
    private bool TryEvaluateIdentifierConstExpr(IdentifierExpr identifier, out long value, out string? error)
    {
        identifier.Name = ResolveQualifiedName(identifier.Name);
        if (TryLookupConstValue(identifier.Name, out value))
        {
            error = null;
            return true;
        }

        value = 0;
        error = null;
        return false;
    }
    private bool TryEvaluateFieldConstExpr(FieldExpr field, out long value, out string? error)
    {
        if (TryResolveStaticEnumMember(field, out _, out value))
        {
            error = null;
            return true;
        }

        if (TryResolveFieldConstQualifiedName(field, out var resolvedQualifiedName) &&
            TryLookupConstValue(resolvedQualifiedName, out value))
        {
            error = null;
            return true;
        }

        value = 0;
        error = null;
        return false;
    }
    private bool TryResolveFieldConstQualifiedName(FieldExpr field, out string resolvedQualifiedName)
    {
        if (TryGetQualifiedName(field) is not string qualifiedName)
        {
            resolvedQualifiedName = "";
            return false;
        }

        resolvedQualifiedName = TryResolveAliasQualifiedName(qualifiedName, out var aliasResolvedQualifiedName)
            ? aliasResolvedQualifiedName
            : qualifiedName;
        return true;
    }
    private bool TryEvaluateUnaryNegationConstExpr(Expr operand, out long value, out string? error)
    {
        if (!TryEvaluateConstIntExpr(operand, out var innerValue, out error))
        {
            value = 0;
            return false;
        }

        try
        {
            value = checked(-innerValue);
            error = null;
            return true;
        }
        catch (OverflowException)
        {
            value = 0;
            error = "Constant integer expression overflowed i64.";
            return false;
        }
    }
    private bool TryEvaluateBinaryConstExpr(BinaryExpr binary, out long value, out string? error)
    {
        if (!TryEvaluateConstBinaryOperands(binary, out var left, out var right, out error))
        {
            value = 0;
            return false;
        }

        return TryComputeConstBinaryValue(binary.Operator, left, right, out value, out error);
    }
    private bool TryEvaluateConstBinaryOperands(BinaryExpr binary, out long left, out long right, out string? error)
    {
        if (!TryEvaluateConstIntExpr(binary.Left, out left, out error))
        {
            right = 0;
            return false;
        }

        if (!TryEvaluateConstIntExpr(binary.Right, out right, out error))
            return false;

        return true;
    }
    private static bool TryComputeConstBinaryValue(string op, long left, long right, out long value, out string? error)
    {
        try
        {
            value = op switch
            {
                "+" => checked(left + right),
                "-" => checked(left - right),
                "*" => checked(left * right),
                "/" => right == 0 ? throw new DivideByZeroException() : left / right,
                "%" => right == 0 ? throw new DivideByZeroException() : left % right,
                "<<" => checked(left << checked((int)right)),
                ">>" => left >> checked((int)right),
                "&" => left & right,
                "|" => left | right,
                "^" => left ^ right,
                _ => throw new InvalidOperationException()
            };
            error = null;
            return true;
        }
        catch (DivideByZeroException)
        {
            value = 0;
            error = "Constant integer expression divides by zero.";
            return false;
        }
        catch (OverflowException)
        {
            value = 0;
            error = "Constant integer expression overflowed i64.";
            return false;
        }
    }
    private static bool TryGetIntegerTypeInfo(string typeName, out (bool IsSigned, int Bits) info)
    {
        info = typeName switch
        {
            "i8" => (true, 8),
            "i16" => (true, 16),
            "i32" => (true, 32),
            "i64" => (true, 64),
            "u8" => (false, 8),
            "u16" => (false, 16),
            "u32" => (false, 32),
            "u64" => (false, 64),
            _ => default
        };

        return typeName is "i8" or "i16" or "i32" or "i64" or "u8" or "u16" or "u32" or "u64";
    }
}
