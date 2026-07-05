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
    private ResolvedCallInfo? ResolveCallInfo(CallExpr call, bool reportErrors)
    {
        call.ResolvedFunctionType = null;
        return call.TargetExpr != null
            ? ResolveTargetExpressionCallInfo(call, reportErrors)
            : ResolveNamedCallInfo(call, reportErrors);
    }

    private ResolvedCallInfo? ResolveTargetExpressionCallInfo(CallExpr call, bool reportErrors)
    {
        var targetExpr = call.TargetExpr!;
        call.ResolvedQualifiedName = null;
        var qualifiedName = QualifiedNames.TryGetQualifiedName(targetExpr);
        if (IsInvalidPostfixTarget(targetExpr))
            return null;

        call.ResolvedTargetQualifiedName = null;
        var displayName = qualifiedName ?? "call target";
        if (TryResolveCallableTargetSymbol(call, qualifiedName, reportErrors, out var callableInfo, out var targetResolutionHandled))
        {
            return InstantiateCallableInfo(callableInfo, call, displayName, reportErrors);
        }

        if (targetResolutionHandled)
            return null;

        return ResolveFunctionPointerCallInfo(call, qualifiedName, reportErrors);
    }

    private bool TryResolveCallableTargetSymbol(
        CallExpr call,
        string? qualifiedName,
        bool reportErrors,
        out SymbolInfo callableInfo,
        out bool resolutionHandled)
    {
        callableInfo = null!;
        resolutionHandled = false;

        var resolvedQualifiedName = qualifiedName;
        var targetResolvedViaAlias = !string.IsNullOrEmpty(qualifiedName) &&
            TryResolveAliasQualifiedName(qualifiedName, out resolvedQualifiedName);
        if (string.IsNullOrEmpty(resolvedQualifiedName) || !_symbolTable.TryLookup(resolvedQualifiedName, out var qualifiedInfo))
            return false;

        var resolvedQualifiedInfo = qualifiedInfo!;

        resolutionHandled = true;
        if (!targetResolvedViaAlias && !CheckVisibility(resolvedQualifiedName))
        {
            if (reportErrors)
                ReportNotVisible(call, "Function", qualifiedName ?? resolvedQualifiedName);
            return false;
        }

        if (!resolvedQualifiedInfo.IsCallable())
        {
            if (reportErrors)
                _errors.Error(call, $"'{qualifiedName}' is not a function or callable variable");
            return false;
        }

        call.ResolvedTargetQualifiedName = targetResolvedViaAlias
            ? resolvedQualifiedName
            : resolvedQualifiedInfo.Name;
        callableInfo = resolvedQualifiedInfo;
        return true;
    }

    private ResolvedCallInfo? ResolveFunctionPointerCallInfo(CallExpr call, string? qualifiedName, bool reportErrors)
    {
        var targetType = GetExpressionType(call.TargetExpr!, reportErrors: false);
        if (targetType == null || !targetType.IsFunction)
        {
            if (reportErrors)
            {
                _errors.Error(call, !string.IsNullOrEmpty(qualifiedName)
                    ? $"Function '{qualifiedName}' is not declared or is not visible from this file."
                    : "Expression is not a function or callable");
            }
            return null;
        }

        call.ResolvedFunctionType = targetType.Clone();
        if (call.TypeArguments.Count > 0)
        {
            if (reportErrors)
                _errors.Error(call, "Function pointer calls do not accept type arguments.");
            return null;
        }

        return new ResolvedCallInfo(
            !string.IsNullOrEmpty(qualifiedName) ? qualifiedName : "function pointer",
            targetType.ParamTypes.Select(type => new Parameter("", type)).ToList(),
            targetType.ReturnType?.Clone());
    }

    private ResolvedCallInfo? ResolveNamedCallInfo(CallExpr call, bool reportErrors)
    {
        call.ResolvedTargetQualifiedName = null;
        var displayName = ResolveNamedCallDisplayName(call, out var resolvedViaAlias);
        var fullName = QualifiedNames.GetFullName(call.NamespacePath, call.Name);
        var symbolInfo = LookupNamedCallSymbol(call, fullName, resolvedViaAlias, reportErrors);
        if (symbolInfo == null)
            return null;

        if (!symbolInfo.IsCallable())
        {
            if (reportErrors)
                _errors.Error(call, $"'{call.Name}' is not a function or callable variable");
            return null;
        }

        call.ResolvedQualifiedName = symbolInfo.Name;
        return InstantiateCallableInfo(symbolInfo, call, displayName, reportErrors);
    }

    private string ResolveNamedCallDisplayName(CallExpr call, out bool resolvedViaAlias)
    {
        resolvedViaAlias = false;
        var displayName = call.Name;
        if (!call.NamespacePath.Any())
            return displayName;

        var sourceName = QualifiedNames.GetFullName(call.NamespacePath, call.Name);
        if (!TryResolveAliasQualifiedName(sourceName, out var resolvedCallName))
            return displayName;

        QualifiedNames.ApplyResolvedQualifiedName(call, resolvedCallName);
        resolvedViaAlias = true;
        return sourceName;
    }

    private SymbolInfo? LookupNamedCallSymbol(CallExpr call, string fullName, bool resolvedViaAlias, bool reportErrors)
    {
        if (_symbolTable.TryLookup(fullName, out var qualifiedSymbol))
        {
            if (call.NamespacePath.Any() && !resolvedViaAlias && !CheckVisibility(fullName))
            {
                if (reportErrors)
                    ReportNotVisible(call, "Function", fullName);
                return null;
            }

            return qualifiedSymbol;
        }

        if (call.NamespacePath.Any())
        {
            if (reportErrors)
                _errors.Error(call, $"Call to undeclared function '{fullName}'");
            return null;
        }

        if (_symbolTable.TryLookup(call.Name, out var bareSymbol))
        {
            if (!CheckVisibility(call.Name))
            {
                if (reportErrors)
                    ReportNotVisible(call, "Function", call.Name);
                return null;
            }

            return bareSymbol;
        }

        if (reportErrors)
            _errors.Error(call, $"Call to undeclared function '{fullName}'");
        return null;
    }

    private ResolvedCallInfo? InstantiateCallableInfo(SymbolInfo symbolInfo, CallExpr call, string displayName, bool reportErrors)
    {
        if (symbolInfo.TypeParameters.Count == 0 && call.TypeArguments.Count > 0)
        {
            if (reportErrors)
                _errors.Error(call, $"Function '{displayName}' is not generic and does not accept type arguments.");
            return null;
        }

        if (symbolInfo.TypeParameters.Count > 0 && call.TypeArguments.Count == 0)
        {
            if (!TryInferCallTypeArguments(symbolInfo, call, out var inferredArguments, out var inferenceError))
            {
                if (reportErrors && !string.IsNullOrEmpty(inferenceError))
                    _errors.Error(call, inferenceError);
                return null;
            }

            if (inferredArguments != null && inferredArguments.Count > 0)
                call.TypeArguments = inferredArguments;
        }

        if (symbolInfo.TypeParameters.Count > 0)
        {
            if (!TryResolveGenericTypeArguments(
                    symbolInfo.TypeParameterSpecs,
                    call.TypeArguments,
                    call,
                    $"Function '{displayName}'",
                    out var resolvedTypeArguments))
            {
                return null;
            }

            call.TypeArguments = resolvedTypeArguments;
        }

        var substitutions = BuildTypeSubstitutions(symbolInfo.TypeParameters, call.TypeArguments);
        var parameters = symbolInfo.GetCallableParameters()
            .Select(parameter => new Parameter(parameter.Name, SubstituteTypeParameters(parameter.TypeName, substitutions)))
            .ToList();
        var returnType = symbolInfo.GetCallableReturnType() is TypeNode type
            ? SubstituteTypeParameters(type, substitutions)
            : null;

        call.ResolvedFunctionType = new TypeNode
        {
            Name = symbolInfo.Name,
            IsFunction = true,
            ReturnType = returnType?.Clone(),
            ParamTypes = parameters.Select(parameter => parameter.TypeName.Clone()).ToList()
        };
        return new ResolvedCallInfo(
            displayName,
            parameters,
            returnType);
    }

    private bool TrySpecializeGenericFunctionValueForTarget(
        TypeNode target,
        Expr? sourceExpr,
        TypeNode source,
        out TypeNode specializedSource)
    {
        specializedSource = source;
        if (!target.IsFunction || sourceExpr == null || !source.IsFunction)
            return false;

        if (!TryResolveGenericFunctionValueSourceInfo(sourceExpr, out var symbolInfo, out var typeArguments))
            return false;

        if (symbolInfo.TypeParameters.Count == 0)
            return false;

        if (typeArguments.Count == 0)
        {
            var typeParameterNames = new HashSet<string>(symbolInfo.TypeParameters, StringComparer.Ordinal);
            var inferredByName = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            if (!TryInferTypeArgumentsFromTypes(symbolInfo.GetCallableFunctionType(), target, typeParameterNames, inferredByName))
                return false;

            if (!TryResolveGenericTypeArgumentsFromInference(
                    symbolInfo.TypeParameterSpecs,
                    inferredByName,
                    sourceExpr,
                    $"generic function '{symbolInfo.Name}'",
                    out var inferredArguments,
                    out _)
                || inferredArguments == null)
            {
                return false;
            }

            ApplyFunctionValueTypeArguments(sourceExpr, inferredArguments);
            typeArguments = inferredArguments;
        }

        if (!TryResolveSpecializedFunctionValueType(symbolInfo, typeArguments, sourceExpr, reportErrors: false, out specializedSource))
            return false;

        _checkedExpressionTypes[sourceExpr] = specializedSource.Clone();
        return true;
    }

    private bool TryResolveGenericFunctionValueSourceInfo(
        Expr sourceExpr,
        out SymbolInfo symbolInfo,
        out IReadOnlyList<TypeNode> typeArguments)
    {
        switch (sourceExpr)
        {
            case IdentifierExpr identifier:
                identifier.Name = ResolveQualifiedName(identifier.Name);
                if (_symbolTable.TryLookup(identifier.Name, out var identifierSymbol) &&
                    identifierSymbol?.Kind == SymbolKind.Function)
                {
                    symbolInfo = identifierSymbol;
                    typeArguments = identifier.TypeArguments;
                    return true;
                }

                break;

            case FieldExpr field:
                if (ResolveQualifiedFieldSymbol(field) is { SymbolInfo.Kind: SymbolKind.Function } resolvedField)
                {
                    symbolInfo = resolvedField.SymbolInfo;
                    typeArguments = field.TypeArguments;
                    return true;
                }

                break;
        }

        symbolInfo = null!;
        typeArguments = Array.Empty<TypeNode>();
        return false;
    }

    private static void ApplyFunctionValueTypeArguments(Node sourceExpr, IReadOnlyList<TypeNode> typeArguments)
    {
        var clonedArguments = typeArguments.Select(argument => argument.Clone()).ToList();
        switch (sourceExpr)
        {
            case IdentifierExpr identifier:
                identifier.TypeArguments = clonedArguments;
                break;

            case FieldExpr field:
                field.TypeArguments = clonedArguments;
                break;
        }
    }

    private bool TryResolveSpecializedFunctionValueType(
        SymbolInfo symbolInfo,
        IReadOnlyList<TypeNode> typeArguments,
        Node context,
        bool reportErrors,
        out TypeNode specializedType)
    {
        if (symbolInfo.TypeParameters.Count == 0)
        {
            specializedType = symbolInfo.GetCallableFunctionType();
            return true;
        }

        if (!TryResolveGenericTypeArguments(
                symbolInfo.TypeParameterSpecs,
                typeArguments,
                context,
                $"Function '{symbolInfo.Name}'",
                out var resolvedTypeArguments,
                reportErrors))
        {
            specializedType = null!;
            return false;
        }

        ApplyFunctionValueTypeArguments(context, resolvedTypeArguments);
        var substitutions = BuildTypeSubstitutions(symbolInfo.TypeParameters, resolvedTypeArguments);
        specializedType = new TypeNode
        {
            Name = symbolInfo.Name,
            IsFunction = true,
            ReturnType = symbolInfo.GetCallableReturnType() is TypeNode returnType
                ? SubstituteTypeParameters(returnType, substitutions)
                : null,
            ParamTypes = symbolInfo.GetCallableParameters()
                .Select(parameter => SubstituteTypeParameters(parameter.TypeName, substitutions))
                .ToList()
        };
        return true;
    }
}
