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
