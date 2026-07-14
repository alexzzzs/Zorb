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
            if (ShouldDeferGenericFunctionValueInference(argument))
                continue;

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

    private bool ShouldDeferGenericFunctionValueInference(Expr argument)
    {
        return TryResolveGenericFunctionValueSourceInfo(argument, out var symbolInfo, out var typeArguments) &&
            symbolInfo.TypeParameters.Count > 0 &&
            typeArguments.Count == 0;
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
        out List<TypeNode> resolvedArguments,
        bool reportErrors = true)
    {
        resolvedArguments = new List<TypeNode>();

        if (reportErrors)
        {
            foreach (var argument in providedArguments)
                ValidateTypeReference(argument, context);
        }

        var requiredCount = GetMinimumGenericArgumentCount(parameters);
        if (providedArguments.Count < requiredCount || providedArguments.Count > parameters.Count)
        {
            if (reportErrors)
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
                if (reportErrors)
                    _errors.Error(context, FormatGenericArityMessage(ownerDescription, requiredCount, parameters.Count, providedArguments.Count));
                return false;
            }

            if (reportErrors)
            {
                if (!CheckGenericConstraint(parameter, argument, substitutions, context, ownerDescription))
                    return false;
            }
            else if (!TryCheckGenericConstraint(parameter, argument, substitutions, ownerDescription, out _))
            {
                return false;
            }

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

        if (!StructLayout.HasPackedAttribute(structNode) &&
            StructLayout.GetAlignment(structNode.Attributes) is not > 0 &&
            !StructLayout.HasExplicitLayout(structNode))
        {
            return;
        }

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
}
