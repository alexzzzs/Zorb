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
}
