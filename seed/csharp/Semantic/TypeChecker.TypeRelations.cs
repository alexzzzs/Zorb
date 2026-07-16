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
}
