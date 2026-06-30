using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections;
using System.Reflection;
using Zorb.Compiler.AST;
using Zorb.Compiler.AST.Expressions;
using Zorb.Compiler.AST.Statements;
using Zorb.Compiler.Layouts;
using Zorb.Compiler.Semantic;
using Zorb.Compiler.Utils;

namespace Zorb.Compiler.Codegen;

public sealed partial class ZigBackendIrWriter
{
    private BackendFunction LowerFunction(
        FunctionDecl function,
        IReadOnlyDictionary<string, uint> functionIds,
        IReadOnlyDictionary<string, TypeNode> functionTypes,
        IReadOnlyDictionary<string, uint> globalIds,
        IReadOnlyList<VariableDeclarationNode> globals,
        ZigBackendTarget target,
        TypeInterner typeInterner)
    {
        var fullName = QualifiedNames.GetFullName(function.NamespacePath, function.Name);
        var functionId = functionIds[fullName];
        var nextValueId = 1u;
        var parameterIds = new Dictionary<string, uint>(StringComparer.Ordinal);
        var parameters = new List<BackendParameter>(function.Parameters.Count);
        foreach (var parameter in function.Parameters)
        {
            var parameterId = nextValueId++;
            parameterIds.Add(parameter.Name, parameterId);
            parameters.Add(new BackendParameter
            {
                Id = parameterId,
                Name = parameter.Name,
                Type = typeInterner.Intern(parameter.TypeName)
            });
        }

        if (function.IsExtern)
        {
            return new BackendFunction
            {
                Id = functionId,
                Name = fullName,
                Linkage = "external",
                ReturnType = typeInterner.Intern(function.ReturnType),
                Parameters = parameters
            };
        }

        var lowerer = new ScalarFunctionLowerer(
            _typeChecker,
            functionIds,
            functionTypes,
            globalIds,
            globals.ToDictionary(global => global.Name, StringComparer.Ordinal),
            parameterIds,
            nextValueId,
            function,
            target,
            typeInterner);
        var blocks = lowerer.Lower();

        return new BackendFunction
        {
            Id = functionId,
            Name = fullName,
            Linkage = "external",
            ReturnType = typeInterner.Intern(function.ReturnType),
            Parameters = parameters,
            Blocks = blocks
        };
    }
    private static BackendGlobal LowerGlobal(
        VariableDeclarationNode global,
        uint id,
        IReadOnlyDictionary<string, uint> functionIds,
        TypeInterner typeInterner)
    {
        return new BackendGlobal
        {
            Id = id,
            Name = global.Name,
            Type = typeInterner.Intern(global.TypeName),
            Linkage = global.IsExported ? "external" : "internal",
            Constant = global.IsConst,
            Initializer = LowerGlobalInitializer(global.Value, functionIds)
        };
    }
    private static BackendConstant LowerGlobalInitializer(
        Expr? expression,
        IReadOnlyDictionary<string, uint> functionIds)
    {
        return expression switch
        {
            null => new BackendConstant { Kind = "zero" },
            NumberExpr number => new BackendConstant { Kind = "integer", Integer = number.Value },
            StringExpr text => new BackendConstant { Kind = "string", Text = text.Value },
            BuiltinExpr builtin when builtin.Name == "true"
                => new BackendConstant { Kind = "integer", Integer = 1 },
            BuiltinExpr builtin when builtin.Name == "false"
                => new BackendConstant { Kind = "integer", Integer = 0 },
            UnaryExpr { Operator: "-", Operand: NumberExpr number }
                => new BackendConstant { Kind = "integer", Integer = -number.Value },
            CastExpr { Expr: NumberExpr { Value: 0 } }
                => new BackendConstant { Kind = "zero" },
            CastExpr { Expr: StringExpr text }
                => new BackendConstant { Kind = "string", Text = text.Value },
            CastExpr { Expr: NumberExpr number }
                => new BackendConstant { Kind = "pointer_integer", Integer = number.Value },
            IdentifierExpr identifier when functionIds.TryGetValue(
                ResolveFunctionValueName(identifier.Name, identifier.TypeArguments),
                out var functionId)
                => new BackendConstant { Kind = "function", Function = functionId },
            FieldExpr field when field.ResolvedQualifiedName is string resolvedFunction &&
                functionIds.TryGetValue(ResolveFunctionValueName(resolvedFunction, field.TypeArguments), out var resolvedFunctionId)
                => new BackendConstant { Kind = "function", Function = resolvedFunctionId },
            ArrayLiteralExpr array
                => new BackendConstant
                {
                    Kind = "aggregate",
                    Elements = array.Elements
                        .Select(element => LowerGlobalInitializer(element, functionIds))
                        .ToList()
                },
            _ => throw new ZorbCompilerException(
                $"Zig backend global initializer lowering does not support {expression.GetType().Name}.")
        };
    }
    private static string ResolveFunctionValueName(string resolvedName, IReadOnlyList<TypeNode> typeArguments)
    {
        return typeArguments.Count > 0
            ? GenericFunctionName(resolvedName, typeArguments)
            : resolvedName;
    }
}
