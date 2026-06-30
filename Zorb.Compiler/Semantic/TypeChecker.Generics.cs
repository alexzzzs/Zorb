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
    private bool TryInferCallTypeArguments(
        SymbolInfo symbolInfo,
        CallExpr call,
        out List<TypeNode>? inferredArguments,
        out string? error)
    {
        inferredArguments = null;
        error = null;

        var parameters = symbolInfo.GetCallableParameters();
        if (parameters.Count != call.Args.Count)
            return true;

        var typeParameterNames = new HashSet<string>(symbolInfo.TypeParameters, StringComparer.Ordinal);
        var inferredByName = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        foreach (var (parameter, argument) in parameters.Zip(call.Args))
        {
            var argumentType = GetExpressionType(argument, reportErrors: false);
            if (argumentType == null)
                return true;

            if (!TryInferTypeArgumentsFromTypes(parameter.TypeName, argumentType, typeParameterNames, inferredByName))
            {
                error = $"Could not infer type arguments for generic function '{symbolInfo.Name}' from the provided arguments.";
                return false;
            }
        }

        return TryResolveGenericTypeArgumentsFromInference(
            symbolInfo.TypeParameterSpecs,
            inferredByName,
            call,
            $"generic function '{symbolInfo.Name}'",
            out inferredArguments,
            out error);
    }
    private bool TryInferTypeArgumentsFromTypes(
        TypeNode parameterType,
        TypeNode argumentType,
        HashSet<string> typeParameterNames,
        Dictionary<string, TypeNode> inferredByName)
    {
        if (IsInferredTypeParameterReference(parameterType, typeParameterNames))
            return TryBindInferredType(parameterType.Name, argumentType, inferredByName);

        if (TryInferErrorUnionTypeArguments(parameterType, argumentType, typeParameterNames, inferredByName, out var inferredErrorUnion))
            return inferredErrorUnion;

        if (TryInferSliceTypeArguments(parameterType, argumentType, typeParameterNames, inferredByName, out var inferredSlice))
            return inferredSlice;

        if (TryInferPointerTypeArguments(parameterType, argumentType, typeParameterNames, inferredByName, out var inferredPointer))
            return inferredPointer;

        if (TryInferArrayTypeArguments(parameterType, argumentType, typeParameterNames, inferredByName, out var inferredArray))
            return inferredArray;

        if (TryInferFunctionTypeArguments(parameterType, argumentType, typeParameterNames, inferredByName, out var inferredFunction))
            return inferredFunction;

        return TryInferNamedTypeArguments(parameterType, argumentType, typeParameterNames, inferredByName);
    }
    private static bool IsInferredTypeParameterReference(TypeNode parameterType, ISet<string> typeParameterNames)
    {
        return typeParameterNames.Contains(parameterType.Name) &&
            parameterType.NamespacePath.Count == 0 &&
            parameterType.TypeArguments.Count == 0 &&
            !parameterType.IsFunction &&
            !parameterType.IsSlice &&
            !parameterType.IsPointer &&
            !parameterType.IsErrorUnion &&
            parameterType.ArraySize == null;
    }
    private bool TryInferErrorUnionTypeArguments(
        TypeNode parameterType,
        TypeNode argumentType,
        HashSet<string> typeParameterNames,
        Dictionary<string, TypeNode> inferredByName,
        out bool result)
    {
        if (parameterType.IsErrorUnion != argumentType.IsErrorUnion)
        {
            result = false;
            return true;
        }

        if (!parameterType.IsErrorUnion)
        {
            result = false;
            return false;
        }

        var parameterInner = parameterType.ErrorInnerType ?? parameterType;
        var argumentInner = argumentType.ErrorInnerType ?? argumentType;
        result = TryInferTypeArgumentsFromTypes(parameterInner, argumentInner, typeParameterNames, inferredByName);
        return true;
    }
    private bool TryInferSliceTypeArguments(
        TypeNode parameterType,
        TypeNode argumentType,
        HashSet<string> typeParameterNames,
        Dictionary<string, TypeNode> inferredByName,
        out bool result)
    {
        if (!parameterType.IsSlice)
        {
            result = false;
            return false;
        }

        var parameterElement = CreateInferenceElementType(parameterType, clearSlice: true, clearArraySize: true);
        if (argumentType.IsSlice)
        {
            var argumentElement = CreateInferenceElementType(argumentType, clearSlice: true, clearArraySize: true);
            result = TryInferTypeArgumentsFromTypes(parameterElement, argumentElement, typeParameterNames, inferredByName);
            return true;
        }

        if (argumentType.ArraySize != null)
        {
            var argumentElement = CreateInferenceElementType(argumentType, clearArraySize: true);
            result = TryInferTypeArgumentsFromTypes(parameterElement, argumentElement, typeParameterNames, inferredByName);
            return true;
        }

        result = false;
        return true;
    }
    private bool TryInferPointerTypeArguments(
        TypeNode parameterType,
        TypeNode argumentType,
        HashSet<string> typeParameterNames,
        Dictionary<string, TypeNode> inferredByName,
        out bool result)
    {
        if (!parameterType.IsPointer)
        {
            result = false;
            return false;
        }

        var parameterLevel = Math.Max(parameterType.PointerLevel, 1);
        if (argumentType.ArraySize != null && parameterLevel == 1)
        {
            var parameterElement = CreateInferenceElementType(parameterType, clearPointer: true);
            var argumentElement = CreateInferenceElementType(argumentType, clearArraySize: true);
            result = TryInferTypeArgumentsFromTypes(parameterElement, argumentElement, typeParameterNames, inferredByName);
            return true;
        }

        if (!argumentType.IsPointer)
        {
            result = false;
            return true;
        }

        var argumentLevel = Math.Max(argumentType.PointerLevel, 1);
        if (parameterLevel != argumentLevel)
        {
            result = false;
            return true;
        }

        var parameterElementType = GetPointerElementTypeForInference(parameterType);
        var argumentElementType = GetPointerElementTypeForInference(argumentType);
        result = TryInferTypeArgumentsFromTypes(parameterElementType, argumentElementType, typeParameterNames, inferredByName);
        return true;
    }
    private bool TryInferArrayTypeArguments(
        TypeNode parameterType,
        TypeNode argumentType,
        HashSet<string> typeParameterNames,
        Dictionary<string, TypeNode> inferredByName,
        out bool result)
    {
        if (parameterType.ArraySize == null && argumentType.ArraySize == null)
        {
            result = false;
            return false;
        }

        if (parameterType.ArraySize != argumentType.ArraySize ||
            parameterType.ArraySize == null ||
            argumentType.ArraySize == null)
        {
            result = false;
            return true;
        }

        var parameterElement = CreateInferenceElementType(parameterType, clearArraySize: true);
        var argumentElement = CreateInferenceElementType(argumentType, clearArraySize: true);
        result = TryInferTypeArgumentsFromTypes(parameterElement, argumentElement, typeParameterNames, inferredByName);
        return true;
    }
    private bool TryInferFunctionTypeArguments(
        TypeNode parameterType,
        TypeNode argumentType,
        HashSet<string> typeParameterNames,
        Dictionary<string, TypeNode> inferredByName,
        out bool result)
    {
        if (!parameterType.IsFunction && !argumentType.IsFunction)
        {
            result = false;
            return false;
        }

        if (!parameterType.IsFunction || !argumentType.IsFunction)
        {
            result = false;
            return true;
        }

        if (parameterType.ParamTypes.Count != argumentType.ParamTypes.Count)
        {
            result = false;
            return true;
        }

        for (var i = 0; i < parameterType.ParamTypes.Count; i++)
        {
            if (!TryInferTypeArgumentsFromTypes(parameterType.ParamTypes[i], argumentType.ParamTypes[i], typeParameterNames, inferredByName))
            {
                result = false;
                return true;
            }
        }

        result = TryInferTypeArgumentsFromTypes(
            parameterType.ReturnType ?? new TypeNode { Name = "void" },
            argumentType.ReturnType ?? new TypeNode { Name = "void" },
            typeParameterNames,
            inferredByName);
        return true;
    }
    private bool TryInferNamedTypeArguments(
        TypeNode parameterType,
        TypeNode argumentType,
        HashSet<string> typeParameterNames,
        Dictionary<string, TypeNode> inferredByName)
    {
        if (!string.Equals(parameterType.Name, argumentType.Name, StringComparison.Ordinal) ||
            !parameterType.NamespacePath.SequenceEqual(argumentType.NamespacePath) ||
            parameterType.TypeArguments.Count != argumentType.TypeArguments.Count)
        {
            return false;
        }

        for (var i = 0; i < parameterType.TypeArguments.Count; i++)
        {
            if (!TryInferTypeArgumentsFromTypes(parameterType.TypeArguments[i], argumentType.TypeArguments[i], typeParameterNames, inferredByName))
                return false;
        }

        return true;
    }
    private static TypeNode CreateInferenceElementType(
        TypeNode type,
        bool clearSlice = false,
        bool clearPointer = false,
        bool clearArraySize = false)
    {
        var elementType = type.Clone();
        if (clearSlice)
            elementType.IsSlice = false;
        if (clearPointer)
        {
            elementType.IsPointer = false;
            elementType.PointerLevel = 0;
        }
        if (clearArraySize)
        {
            elementType.ArraySize = null;
            elementType.ArraySizeExpr = null;
        }

        return elementType;
    }
    private static TypeNode GetPointerElementTypeForInference(TypeNode pointerType)
    {
        var level = Math.Max(pointerType.PointerLevel, 1);
        var element = pointerType.Clone();
        element.PointerLevel = level - 1;
        element.IsPointer = element.PointerLevel > 0;
        return element;
    }
    private static bool TryBindInferredType(string typeParameter, TypeNode inferredType, Dictionary<string, TypeNode> inferredByName)
    {
        if (inferredByName.TryGetValue(typeParameter, out var existing))
            return TypeHelpers.SameType(existing, inferredType);

        inferredByName[typeParameter] = inferredType.Clone();
        return true;
    }
    private void ValidateTypeParameterDeclarations(
        IReadOnlyList<GenericTypeParameter> parameters,
        Node context,
        string ownerDescription)
    {
        if (parameters.Count == 0)
            return;

        if (!ResolveTypeParameterReferences(parameters, context))
            return;

        var sawDefault = false;
        foreach (var parameter in parameters)
        {
            if (parameter.DefaultType != null)
            {
                sawDefault = true;
                continue;
            }

            if (sawDefault)
            {
                _errors.Error(
                    context,
                    $"Type parameter '{parameter.Name}' in {ownerDescription} must declare a default because an earlier type parameter already has one.");
                return;
            }
        }
    }
    private bool TryResolveGenericTypeArguments(
        IReadOnlyList<GenericTypeParameter> parameters,
        IReadOnlyList<TypeNode> providedArguments,
        Node context,
        string ownerDescription,
        out List<TypeNode> resolvedArguments)
    {
        resolvedArguments = new List<TypeNode>();

        foreach (var argument in providedArguments)
            ValidateTypeReference(argument, context);

        var requiredCount = GetMinimumGenericArgumentCount(parameters);
        if (providedArguments.Count < requiredCount || providedArguments.Count > parameters.Count)
        {
            _errors.Error(context, FormatGenericArityMessage(ownerDescription, requiredCount, parameters.Count, providedArguments.Count));
            return false;
        }

        var substitutions = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            TypeNode argument;
            if (index < providedArguments.Count)
            {
                argument = providedArguments[index].Clone();
            }
            else if (parameter.DefaultType != null)
            {
                argument = SubstituteTypeParameters(parameter.DefaultType, substitutions);
            }
            else
            {
                _errors.Error(context, FormatGenericArityMessage(ownerDescription, requiredCount, parameters.Count, providedArguments.Count));
                return false;
            }

            if (!CheckGenericConstraint(parameter, argument, substitutions, context, ownerDescription))
                return false;

            substitutions[parameter.Name] = argument.Clone();
            resolvedArguments.Add(argument);
        }

        return true;
    }
    private bool ResolveTypeParameterReferences(
        IReadOnlyList<GenericTypeParameter> parameters,
        Node context)
    {
        if (parameters.Count == 0)
            return true;

        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            if (seenNames.Add(parameter.Name))
                continue;

            _errors.Error(context, $"Duplicate type parameter '{parameter.Name}'.");
            return false;
        }

        _typeParameterScopes.Push(new HashSet<string>(StringComparer.Ordinal));
        try
        {
            foreach (var parameter in parameters)
            {
                if (parameter.Constraint != null)
                    ValidateTypeReference(parameter.Constraint, context);

                if (parameter.DefaultType != null)
                    ValidateTypeReference(parameter.DefaultType, context);

                _typeParameterScopes.Peek().Add(parameter.Name);
            }

            return true;
        }
        finally
        {
            _typeParameterScopes.Pop();
        }
    }
    private bool TryResolveGenericTypeArgumentsFromInference(
        IReadOnlyList<GenericTypeParameter> parameters,
        IReadOnlyDictionary<string, TypeNode> inferredByName,
        Node context,
        string ownerDescription,
        out List<TypeNode>? resolvedArguments,
        out string? error)
    {
        resolvedArguments = null;
        error = null;

        var missing = parameters
            .Where(parameter => parameter.DefaultType == null && !inferredByName.ContainsKey(parameter.Name))
            .Select(parameter => parameter.Name)
            .ToList();
        if (missing.Count > 0)
        {
            error = $"Could not infer type argument(s) {string.Join(", ", missing.Select(name => $"'{name}'"))} for {ownerDescription}.";
            return false;
        }

        var substitutions = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        var resolved = new List<TypeNode>(parameters.Count);
        foreach (var parameter in parameters)
        {
            var argument = inferredByName.TryGetValue(parameter.Name, out var inferred)
                ? inferred.Clone()
                : SubstituteTypeParameters(parameter.DefaultType!, substitutions);

            if (!TryCheckGenericConstraint(parameter, argument, substitutions, ownerDescription, out error))
                return false;

            substitutions[parameter.Name] = argument.Clone();
            resolved.Add(argument);
        }

        resolvedArguments = resolved;
        return true;
    }
    private bool CheckGenericConstraint(
        GenericTypeParameter parameter,
        TypeNode argument,
        IReadOnlyDictionary<string, TypeNode> substitutions,
        Node context,
        string ownerDescription)
    {
        if (TryCheckGenericConstraint(parameter, argument, substitutions, ownerDescription, out var error))
            return true;

        _errors.Error(context, error!);
        return false;
    }
    private bool TryCheckGenericConstraint(
        GenericTypeParameter parameter,
        TypeNode argument,
        IReadOnlyDictionary<string, TypeNode> substitutions,
        string ownerDescription,
        out string? error)
    {
        error = null;
        if (parameter.Constraint == null)
            return true;

        var resolvedConstraint = SubstituteTypeParameters(parameter.Constraint, substitutions);
        if (TypeHelpers.SameType(argument, resolvedConstraint))
            return true;

        error = $"Type argument '{parameter.Name}' for {ownerDescription} must satisfy constraint '{FormatType(resolvedConstraint)}', got '{FormatType(argument)}'.";
        return false;
    }
    private static string FormatGenericArityMessage(string ownerDescription, int requiredCount, int totalCount, int actualCount)
    {
        if (totalCount == 0)
            return $"{ownerDescription} is not generic and does not accept type arguments.";

        if (requiredCount == totalCount)
            return $"{ownerDescription} expects {totalCount} type argument(s), got {actualCount}.";

        return $"{ownerDescription} expects between {requiredCount} and {totalCount} type argument(s), got {actualCount}.";
    }
    private static int GetMinimumGenericArgumentCount(IReadOnlyList<GenericTypeParameter> parameters)
    {
        var requiredCount = parameters.Count;
        while (requiredCount > 0 && parameters[requiredCount - 1].DefaultType != null)
            requiredCount--;

        return requiredCount;
    }
    private void ValidateConcreteGenericStructLayout(TypeNode type, Node context, StructNode structNode)
    {
        if (type.TypeArguments.Count == 0)
            return;

        if (!StructLayout.HasPackedAttribute(structNode) && StructLayout.GetAlignment(structNode.Attributes) is not > 0)
            return;

        var concreteStruct = InstantiateStruct(structNode, type);
        if (!StructLayout.TryCompute(concreteStruct, ResolveConcreteStructForLayout, out _, out var layoutError) && layoutError != null)
            _errors.Error(context, layoutError);
    }
    private void PushTypeParameterScope(IEnumerable<string> typeParameters)
    {
        _typeParameterScopes.Push(new HashSet<string>(typeParameters, StringComparer.Ordinal));
    }
    private void PopTypeParameterScope()
    {
        if (_typeParameterScopes.Count > 0)
            _typeParameterScopes.Pop();
    }
    private bool IsTypeParameterReference(TypeNode type)
    {
        return type.NamespacePath.Count == 0 &&
            type.TypeArguments.Count == 0 &&
            !type.IsFunction &&
            _typeParameterScopes.Any(scope => scope.Contains(type.Name));
    }
    private List<(string Name, TypeNode Type)> GetStructFieldsForType(TypeNode structType)
    {
        var fullName = QualifiedNames.GetFullName(structType.NamespacePath, structType.Name);
        var structNode = _symbolTable.LookupStructNode(fullName);
        if (structNode == null)
            return new List<(string Name, TypeNode Type)>();

        var substitutions = BuildTypeSubstitutions(structNode.TypeParameters, structType.TypeArguments);
        return structNode.Fields
            .Select(field => (field.Name, SubstituteTypeParameters(field.TypeName, substitutions)))
            .ToList();
    }
    private List<UnionVariant> GetUnionVariantsForType(TypeNode unionType)
    {
        var fullName = QualifiedNames.GetFullName(unionType.NamespacePath, unionType.Name);
        var unionNode = _symbolTable.LookupUnionNode(fullName);
        if (unionNode == null)
            return new List<UnionVariant>();

        if (unionNode.TypeParameters.Count == 0)
            return unionNode.Variants.Select(CloneUnionVariant).ToList();

        var substitutions = BuildTypeSubstitutions(unionNode.TypeParameters, unionType.TypeArguments);
        return unionNode.Variants
            .Select(variant => new UnionVariant
            {
                File = variant.File,
                Line = variant.Line,
                Column = variant.Column,
                Length = variant.Length,
                Name = variant.Name,
                TypeName = SubstituteTypeParameters(variant.TypeName, substitutions)
            })
            .ToList();
    }
    private static Dictionary<string, TypeNode> BuildTypeSubstitutions(IReadOnlyList<string> parameters, IReadOnlyList<TypeNode> arguments)
    {
        var result = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        for (int i = 0; i < parameters.Count && i < arguments.Count; i++)
            result[parameters[i]] = arguments[i].Clone();
        return result;
    }
    private static TypeNode SubstituteTypeParameters(TypeNode type, IReadOnlyDictionary<string, TypeNode> substitutions)
    {
        if (type.NamespacePath.Count == 0 && type.TypeArguments.Count == 0 && substitutions.TryGetValue(type.Name, out var replacement))
        {
            var substituted = replacement.Clone();
            substituted.IsVolatile |= type.IsVolatile;
            substituted.IsPointer = type.IsPointer || substituted.IsPointer;
            substituted.PointerLevel += type.PointerLevel;
            substituted.IsSlice |= type.IsSlice;
            if (type.ArraySize != null)
            {
                substituted.ArraySize = type.ArraySize;
                substituted.ArraySizeExpr = type.ArraySizeExpr;
            }
            if (type.IsErrorUnion)
            {
                var innerType = substituted.Clone();
                substituted.IsErrorUnion = true;
                substituted.ErrorInnerType = innerType;
            }
            return substituted;
        }

        var clone = type.Clone();
        clone.TypeArguments = clone.TypeArguments.Select(argument => SubstituteTypeParameters(argument, substitutions)).ToList();
        if (clone.IsErrorUnion && clone.ErrorInnerType != null)
            clone.ErrorInnerType = SubstituteTypeParameters(clone.ErrorInnerType, substitutions);
        if (clone.IsFunction)
        {
            clone.ParamTypes = clone.ParamTypes.Select(param => SubstituteTypeParameters(param, substitutions)).ToList();
            if (clone.ReturnType != null)
                clone.ReturnType = SubstituteTypeParameters(clone.ReturnType, substitutions);
        }

        return clone;
    }
    private StructNode? ResolveConcreteStructForLayout(TypeNode type)
    {
        var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
        var definition = _symbolTable.LookupStructNode(fullName);
        if (definition == null)
            return null;
        return definition.TypeParameters.Count == 0 ? definition : InstantiateStruct(definition, type);
    }
    private static StructNode InstantiateStruct(StructNode definition, TypeNode concreteType)
    {
        var substitutions = BuildTypeSubstitutions(definition.TypeParameters, concreteType.TypeArguments);
        return new StructNode
        {
            File = definition.File,
            Line = definition.Line,
            Column = definition.Column,
            Length = definition.Length,
            IsExported = definition.IsExported,
            NamespacePath = new List<string>(definition.NamespacePath),
            Name = FormatNonErrorType(concreteType),
            Attributes = new List<string>(definition.Attributes),
            AlignExpr = definition.AlignExpr,
            Fields = definition.Fields.Select(field => new StructField
            {
                File = field.File,
                Line = field.Line,
                Column = field.Column,
                Length = field.Length,
                Name = field.Name,
                TypeName = SubstituteTypeParameters(field.TypeName, substitutions),
                Attributes = new List<string>(field.Attributes),
                OffsetExpr = field.OffsetExpr
            }).ToList()
        };
    }
    private static EnumNode InstantiateEnum(EnumNode definition, TypeNode concreteType)
    {
        return new EnumNode
        {
            File = definition.File,
            Line = definition.Line,
            Column = definition.Column,
            Length = definition.Length,
            IsExported = definition.IsExported,
            NamespacePath = new List<string>(definition.NamespacePath),
            Name = concreteType.Name,
            UnderlyingType = definition.UnderlyingType.Clone(),
            Members = definition.Members
                .Select(member => new EnumMember
                {
                    File = member.File,
                    Line = member.Line,
                    Column = member.Column,
                    Length = member.Length,
                    Name = member.Name,
                    Value = member.Value,
                    ResolvedValue = member.ResolvedValue
                })
                .ToList()
        };
    }
    private static UnionNode InstantiateUnion(UnionNode definition, TypeNode concreteType)
    {
        var substitutions = BuildTypeSubstitutions(definition.TypeParameters, concreteType.TypeArguments);
        return new UnionNode
        {
            File = definition.File,
            Line = definition.Line,
            Column = definition.Column,
            Length = definition.Length,
            IsExported = definition.IsExported,
            NamespacePath = new List<string>(definition.NamespacePath),
            Name = concreteType.Name,
            Variants = definition.Variants
                .Select(variant => new UnionVariant
                {
                    File = variant.File,
                    Line = variant.Line,
                    Column = variant.Column,
                    Length = variant.Length,
                    Name = variant.Name,
                    TypeName = SubstituteTypeParameters(variant.TypeName, substitutions)
                })
                .ToList()
        };
    }
    private static UnionVariant CloneUnionVariant(UnionVariant variant)
    {
        return new UnionVariant
        {
            File = variant.File,
            Line = variant.Line,
            Column = variant.Column,
            Length = variant.Length,
            Name = variant.Name,
            TypeName = variant.TypeName.Clone()
        };
    }
}
