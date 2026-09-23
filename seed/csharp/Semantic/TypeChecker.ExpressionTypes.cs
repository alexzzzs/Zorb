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
    private TypeNode? GetExpressionType(Expr expr, bool reportErrors = true)
    {
        var type = ComputeExpressionType(expr, reportErrors);
        if (type != null)
            _checkedExpressionTypes[expr] = type.Clone();
        return type;
    }
    private TypeNode? ComputeExpressionType(Expr expr, bool reportErrors)
    {
        switch (expr)
        {
            case NumberExpr _:
                return ComputeNumberExpressionType();

            case StringExpr _:
                return ComputeStringExpressionType();

            case IdentifierExpr ident:
                return ComputeIdentifierExpressionType(ident, reportErrors);

            case TypeReferenceExpr:
                return ComputeTypeReferenceExpressionType();

            case BinaryExpr bin:
                return ComputeBinaryExpressionType(bin, reportErrors);

            case CallExpr call:
                return ComputeCallExpressionType(call, reportErrors);

            case CastExpr cast:
                return ComputeCastExpressionType(cast);

            case IndexExpr idx:
                return ComputeIndexExpressionType(idx, reportErrors);

            case FieldExpr field:
                return ComputeFieldExpressionType(field, reportErrors);

            case UnaryExpr un:
                return ComputeUnaryExpressionType(un, reportErrors);

            case StructLiteralExpr structLiteral:
                return ComputeStructLiteralExpressionType(structLiteral);

            case ArrayLiteralExpr arrayLiteral:
                return ComputeArrayLiteralExpressionType(arrayLiteral);

            case BuiltinExpr builtin:
                return ComputeBuiltinExpressionType(builtin);

            case ErrorNamespaceExpr:
                return ComputeErrorNamespaceExpressionType();

            case ErrorExpr _:
                return ComputeErrorExpressionType();

            case SizeofExpr _:
                return ComputeSizeofExpressionType();

            case CatchExpr catchExpr:
                return ComputeCatchExpressionType(catchExpr, reportErrors);

            case InvalidExpr:
                return ComputeInvalidExpressionType();

            default:
                return null;
        }
    }
    private static TypeNode ComputeNumberExpressionType()
    {
        return new TypeNode { Name = "i64" };
    }
    private static TypeNode ComputeStringExpressionType()
    {
        return new TypeNode { Name = "string" };
    }
    private TypeNode? ComputeIdentifierExpressionType(IdentifierExpr identifier, bool reportErrors)
    {
        var resolvedName = ResolveQualifiedName(identifier.Name);
        var info = _symbolTable.Lookup(resolvedName);
        if (info == null)
            return null;

        identifier.Name = resolvedName;
        if (identifier.TypeArguments.Count == 0)
            return info.Type;

        return TryResolveSpecializedFunctionValueType(
            info,
            identifier.TypeArguments,
            identifier,
            reportErrors,
            out var specializedType)
            ? specializedType
            : info.Type;
    }
    private static TypeNode? ComputeTypeReferenceExpressionType()
    {
        return null;
    }
    private TypeNode? ComputeBinaryExpressionType(BinaryExpr binaryExpression, bool reportErrors)
    {
        if (ComparisonOperators.Contains(binaryExpression.Operator) || LogicalOperators.Contains(binaryExpression.Operator))
            return new TypeNode { Name = "bool" };

        var leftType = GetExpressionType(binaryExpression.Left, reportErrors);
        var rightType = GetExpressionType(binaryExpression.Right, reportErrors);
        if (leftType != null && rightType != null)
        {
            if (binaryExpression.Operator == "+" && IsNumericType(leftType) && rightType.IsPointer)
                return rightType.Clone();

            if ((binaryExpression.Operator == "+" || binaryExpression.Operator == "-") && leftType.IsPointer && IsNumericType(rightType))
                return leftType.Clone();
        }

        return leftType;
    }
    private TypeNode? ComputeCallExpressionType(CallExpr callExpression, bool reportErrors)
    {
        if (callExpression.TargetExpr != null && IsInvalidPostfixTarget(callExpression.TargetExpr))
            return null;

        return ResolveCallInfo(callExpression, reportErrors)?.ReturnType;
    }
    private static TypeNode ComputeCastExpressionType(CastExpr castExpression)
    {
        return castExpression.TargetType;
    }
    private TypeNode? ComputeIndexExpressionType(IndexExpr indexExpression, bool reportErrors)
    {
        if (IsInvalidPostfixTarget(indexExpression.Target))
            return null;

        var targetType = GetExpressionType(indexExpression.Target, reportErrors);
        if (targetType == null)
            return null;
        if (targetType.ArraySize != null)
            return CreateIndexedElementType(targetType);
        if (targetType.IsSlice)
            return CreateSliceElementType(targetType);
        if (targetType.IsPointer)
            return CreatePointedElementType(targetType);

        return null;
    }
    private static TypeNode CreateIndexedElementType(TypeNode targetType)
    {
        var elementType = targetType.Clone();
        elementType.ArraySize = null;
        elementType.ArraySizeExpr = null;
        return elementType;
    }
    private static TypeNode CreateSliceElementType(TypeNode targetType)
    {
        var elementType = targetType.Clone();
        elementType.IsSlice = false;
        elementType.ArraySize = null;
        elementType.ArraySizeExpr = null;
        return elementType;
    }
    private static TypeNode CreatePointedElementType(TypeNode targetType)
    {
        var elementType = targetType.Clone();
        var level = targetType.PointerLevel > 0 ? targetType.PointerLevel : 1;
        if (level > 1)
        {
            elementType.IsPointer = true;
            elementType.PointerLevel = level - 1;
            return elementType;
        }

        elementType.IsPointer = false;
        elementType.PointerLevel = 0;
        return elementType;
    }
    private TypeNode? ComputeFieldExpressionType(FieldExpr fieldExpression, bool reportErrors)
    {
        if (IsInvalidPostfixTarget(fieldExpression.Target))
            return null;

        if (TryResolveStaticEnumMember(fieldExpression, out var staticEnumType, out _))
            return staticEnumType;

        if (TryResolveStaticTypeReference(fieldExpression.Target, out var staticOwnerType) &&
            IsUnionType(staticOwnerType) &&
            fieldExpression.Field == "Tag")
        {
            return GetUnionTagType(staticOwnerType);
        }

        if (TryResolveFieldSymbolType(fieldExpression, reportErrors, out var resolvedFieldType))
        {
            if (fieldExpression.TypeArguments.Count == 0)
                return resolvedFieldType;

            if (ResolveQualifiedFieldSymbol(fieldExpression) is not ResolvedFieldSymbolInfo resolvedField)
            {
                return resolvedFieldType;
            }

            return TryResolveSpecializedFunctionValueType(
                resolvedField.SymbolInfo,
                fieldExpression.TypeArguments,
                fieldExpression,
                reportErrors,
                out var specializedType)
                ? specializedType
                : resolvedFieldType;
        }

        var targetType = GetExpressionType(fieldExpression.Target, reportErrors);
        if (targetType == null)
        {
            if (reportErrors)
                _errors.Error(fieldExpression, $"Cannot determine type of target in field access '{fieldExpression.Field}'.");
            return null;
        }

        return ComputeMemberAccessType(fieldExpression, targetType, reportErrors);
    }
    private bool TryResolveFieldSymbolType(FieldExpr fieldExpression, bool reportErrors, out TypeNode? resolvedFieldType)
    {
        resolvedFieldType = null;
        if (ResolveQualifiedFieldSymbol(fieldExpression) is not ResolvedFieldSymbolInfo resolvedFieldSymbol)
            return false;

        if (resolvedFieldSymbol.SymbolInfo.Kind != SymbolKind.Variable && resolvedFieldSymbol.SymbolInfo.Kind != SymbolKind.Function)
            return false;

        if (!IsVisibleResolvedFieldSymbol(resolvedFieldSymbol))
        {
            if (reportErrors)
                ReportNotVisible(fieldExpression, "Symbol", resolvedFieldSymbol.ResolvedName);
            return true;
        }

        resolvedFieldType = resolvedFieldSymbol.SymbolInfo.Type.Clone();
        return true;
    }
    private TypeNode? ComputeMemberAccessType(FieldExpr fieldExpression, TypeNode targetType, bool reportErrors)
    {
        var structName = QualifiedNames.GetFullName(targetType.NamespacePath, targetType.Name);
        if (targetType.IsSlice)
            return ComputeSliceFieldType(fieldExpression, targetType, reportErrors);

        if (_numericTypes.Contains(structName))
            return CreateNumericFieldType(targetType, structName);

        if (_symbolTable.TryLookupStruct(structName, out _))
            return ComputeStructFieldType(fieldExpression, targetType, structName, reportErrors);

        if (LookupUnionDefinition(targetType) is UnionNode unionDefinition)
            return ComputeUnionFieldType(fieldExpression, targetType, structName, unionDefinition, reportErrors);

        if (reportErrors)
            _errors.Error(fieldExpression, $"Type '{structName}' is not a known struct or union");
        return null;
    }
    private TypeNode? ComputeSliceFieldType(FieldExpr fieldExpression, TypeNode targetType, bool reportErrors)
    {
        if (fieldExpression.Field == "len")
            return new TypeNode { Name = "i64" };

        if (fieldExpression.Field == "ptr")
        {
            var elementType = targetType.Clone();
            elementType.IsSlice = false;
            elementType.ArraySize = null;
            return TypeHelpers.AddressOfType(elementType);
        }

        if (reportErrors)
            _errors.Error(fieldExpression, $"Slice values expose only '.ptr' and '.len', not '{fieldExpression.Field}'.");
        return null;
    }
    private static TypeNode CreateNumericFieldType(TypeNode targetType, string structName)
    {
        return new TypeNode
        {
            Name = structName,
            IsVolatile = targetType.IsVolatile,
            IsPointer = targetType.IsPointer,
            PointerLevel = targetType.PointerLevel
        };
    }
    private TypeNode? ComputeStructFieldType(FieldExpr fieldExpression, TypeNode targetType, string structName, bool reportErrors)
    {
        if (!targetType.IsAliasQualifiedReference && !CheckVisibility(structName))
        {
            if (reportErrors)
                ReportNotVisible(fieldExpression, "Struct", structName);
            return null;
        }

        var fieldDef = GetStructFieldsForType(targetType).FirstOrDefault(f => f.Name == fieldExpression.Field);
        if (!string.IsNullOrEmpty(fieldDef.Name))
            return fieldDef.Type.Clone();

        if (reportErrors)
            _errors.Error(fieldExpression, $"Struct '{structName}' does not have a field named '{fieldExpression.Field}'.");
        return null;
    }
    private TypeNode? ComputeUnionFieldType(
        FieldExpr fieldExpression,
        TypeNode targetType,
        string structName,
        UnionNode unionDefinition,
        bool reportErrors)
    {
        if (!targetType.IsAliasQualifiedReference && !CheckVisibility(structName))
        {
            if (reportErrors)
                ReportNotVisible(fieldExpression, "Union", structName);
            return null;
        }

        if (fieldExpression.Field == "tag")
            return GetUnionTagType(targetType);

        var variant = unionDefinition.Variants.FirstOrDefault(candidate => candidate.Name == fieldExpression.Field);
        if (variant != null)
            return variant.TypeName.Clone();

        if (reportErrors)
            _errors.Error(fieldExpression, $"Union '{structName}' does not have a field named '{fieldExpression.Field}'.");
        return null;
    }
    private TypeNode? ComputeUnaryExpressionType(UnaryExpr unaryExpression, bool reportErrors)
    {
        if (unaryExpression.Operator == "&")
        {
            var operandType = GetExpressionType(unaryExpression.Operand, reportErrors);
            if (operandType != null)
                return TypeHelpers.AddressOfType(operandType);
        }
        else if (unaryExpression.Operator == "!")
        {
            return new TypeNode { Name = "bool" };
        }
        else if (unaryExpression.Operator == "-")
        {
            return GetExpressionType(unaryExpression.Operand, reportErrors);
        }

        return null;
    }
    private static TypeNode ComputeStructLiteralExpressionType(StructLiteralExpr structLiteral)
    {
        return structLiteral.TypeName.Clone();
    }
    private static TypeNode ComputeArrayLiteralExpressionType(ArrayLiteralExpr arrayLiteral)
    {
        return arrayLiteral.TypeName.Clone();
    }
    private static TypeNode? ComputeBuiltinExpressionType(BuiltinExpr builtin)
    {
        return builtin.Name switch
        {
            "true" => new TypeNode { Name = "bool" },
            "false" => new TypeNode { Name = "bool" },
            "Builtin.IsLinux" => new TypeNode { Name = "bool" },
            "Builtin.IsWindows" => new TypeNode { Name = "bool" },
            "Builtin.IsBareMetal" => new TypeNode { Name = "bool" },
            "Builtin.IsX86_64" => new TypeNode { Name = "bool" },
            "Builtin.IsAArch64" => new TypeNode { Name = "bool" },
            "Builtin.CompileError" => new TypeNode { Name = "void" },
            _ => null
        };
    }
    private static TypeNode? ComputeErrorNamespaceExpressionType()
    {
        return null;
    }
    private static TypeNode ComputeErrorExpressionType()
    {
        return new TypeNode { Name = "i32" };
    }
    private static TypeNode ComputeSizeofExpressionType()
    {
        return new TypeNode { Name = "i64" };
    }
    private TypeNode? ComputeCatchExpressionType(CatchExpr catchExpression, bool reportErrors)
    {
        var catchLeftType = GetExpressionType(catchExpression.Left, reportErrors);
        if (catchLeftType == null || !catchLeftType.IsErrorUnion)
            return null;

        return (catchLeftType.ErrorInnerType ?? catchLeftType).Clone();
    }
    private static TypeNode? ComputeInvalidExpressionType()
    {
        return null;
    }
}
