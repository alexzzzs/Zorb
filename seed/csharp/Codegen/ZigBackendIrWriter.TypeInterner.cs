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
    private sealed class TypeInterner
    {
        private readonly Dictionary<string, uint> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StructNode> _structs;
        private readonly Dictionary<string, EnumNode> _enums;
        private readonly Dictionary<string, UnionNode> _unions;
        private readonly HashSet<string> _externTypes;
        private readonly List<BackendType> _types = new();

        public TypeInterner(IReadOnlyList<Node> nodes)
        {
            _structs = nodes.OfType<StructNode>().ToDictionary(
                node => QualifiedNames.GetFullName(node.NamespacePath, node.Name),
                StringComparer.Ordinal);
            _enums = nodes.OfType<EnumNode>().ToDictionary(
                node => QualifiedNames.GetFullName(node.NamespacePath, node.Name),
                StringComparer.Ordinal);
            _unions = nodes.OfType<UnionNode>().ToDictionary(
                node => QualifiedNames.GetFullName(node.NamespacePath, node.Name),
                StringComparer.Ordinal);
            _externTypes = nodes.OfType<ExternTypeDecl>()
                .Select(node => QualifiedNames.GetFullName(node.NamespacePath, node.Name))
                .ToHashSet(StringComparer.Ordinal);
        }

        public List<BackendType> Types => _types;

        public IReadOnlyList<StructField> GetStructFields(TypeNode type)
        {
            var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
            if (!_structs.TryGetValue(fullName, out var structNode))
                throw Unsupported(type, $"unknown struct '{fullName}'");
            if (structNode.TypeParameters.Count == 0)
                return structNode.Fields;

            var substitutions = AstSpecialization.BuildTypeSubstitutions(
                structNode.TypeParameters,
                type.TypeArguments);
            return structNode.Fields.Select(field => new StructField
            {
                File = field.File,
                Line = field.Line,
                Column = field.Column,
                Length = field.Length,
                Name = field.Name,
                TypeName = AstSpecialization.SubstituteTypeParameters(field.TypeName, substitutions),
                Attributes = new List<string>(field.Attributes),
                OffsetExpr = field.OffsetExpr
            }).ToList();
        }

        public bool IsEnumType(TypeNode type)
        {
            var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
            return _enums.ContainsKey(fullName) || TryGetTagUnion(fullName, out _);
        }

        public bool IsUnionType(TypeNode type)
        {
            var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
            return _unions.ContainsKey(fullName);
        }

        public IReadOnlyList<UnionVariant> GetUnionVariants(TypeNode type)
        {
            var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
            if (!_unions.TryGetValue(fullName, out var unionNode))
                throw Unsupported(type, $"unknown union '{fullName}'");

            if (unionNode.TypeParameters.Count == 0)
                return unionNode.Variants;

            var substitutions = AstSpecialization.BuildTypeSubstitutions(
                unionNode.TypeParameters,
                type.TypeArguments);
            return unionNode.Variants.Select(variant => new UnionVariant
            {
                File = variant.File,
                Line = variant.Line,
                Column = variant.Column,
                Length = variant.Length,
                Name = variant.Name,
                TypeName = AstSpecialization.SubstituteTypeParameters(variant.TypeName, substitutions)
            }).ToList();
        }

        public uint GetUnionVariantIndex(TypeNode type, string variantName)
        {
            var variants = GetUnionVariants(type);
            for (var index = 0; index < variants.Count; index++)
            {
                if (string.Equals(variants[index].Name, variantName, StringComparison.Ordinal))
                    return checked((uint)index);
            }
            throw Unsupported(type, $"unknown union variant '{variantName}'");
        }

        public TypeNode GetUnionVariantType(TypeNode type, string variantName)
        {
            return GetUnionVariants(type)
                       .FirstOrDefault(variant => string.Equals(
                           variant.Name,
                           variantName,
                           StringComparison.Ordinal))
                       ?.TypeName.Clone()
                   ?? throw Unsupported(type, $"unknown union variant '{variantName}'");
        }

        public uint GetStructFieldIndex(TypeNode type, string fieldName)
        {
            type = Dereference(type);
            if (type.IsErrorUnion)
            {
                return fieldName switch
                {
                    "value" => 0,
                    "error" => 1,
                    _ => throw Unsupported(type, $"unknown error-union field '{fieldName}'")
                };
            }
            if (type.IsSlice)
            {
                return fieldName switch
                {
                    "ptr" => 0,
                    "len" => 1,
                    _ => throw Unsupported(type, $"unknown slice field '{fieldName}'")
                };
            }
            if (IsUnionType(type))
            {
                if (fieldName == "tag")
                    return 0;
                return GetUnionVariantIndex(type, fieldName) + 1;
            }
            var fields = GetStructFields(type);
            for (var index = 0; index < fields.Count; index++)
            {
                if (string.Equals(fields[index].Name, fieldName, StringComparison.Ordinal))
                    return checked((uint)index);
            }
            throw Unsupported(type, $"unknown field '{fieldName}'");
        }

        public TypeNode GetStructFieldType(TypeNode type, string fieldName)
        {
            type = Dereference(type);
            if (type.IsErrorUnion)
            {
                return fieldName switch
                {
                    "value" => type.ErrorInnerType?.Clone()
                               ?? throw Unsupported(type, "error union has no success type"),
                    "error" => new TypeNode { Name = "i32" },
                    _ => throw Unsupported(type, $"unknown error-union field '{fieldName}'")
                };
            }
            if (type.IsSlice)
            {
                return fieldName switch
                {
                    "ptr" => SlicePointerType(type),
                    "len" => new TypeNode { Name = "i64" },
                    _ => throw Unsupported(type, $"unknown slice field '{fieldName}'")
                };
            }
            if (IsUnionType(type))
            {
                return fieldName == "tag"
                    ? new TypeNode
                    {
                        Name = QualifiedNames.GetFullName(type.NamespacePath, type.Name) + ".Tag"
                    }
                    : GetUnionVariantType(type, fieldName);
            }

            return GetStructFields(type)
                       .FirstOrDefault(field => string.Equals(field.Name, fieldName, StringComparison.Ordinal))
                       ?.TypeName.Clone()
                   ?? throw Unsupported(type, $"unknown field '{fieldName}'");
        }

        public bool TryGetEnumMember(FieldExpr field, out TypeNode enumType, out long value)
        {
            enumType = null!;
            value = 0;
            if (TryResolveStaticEnumMember(field, out enumType, out value))
                return true;

            var resolvedFieldName = field.ResolvedQualifiedName
                ?? QualifiedNames.TryGetQualifiedName(field);
            if (resolvedFieldName is string qualifiedName)
            {
                var tagMarker = qualifiedName.LastIndexOf(".Tag.", StringComparison.Ordinal);
                if (tagMarker >= 0)
                {
                    var unionName = qualifiedName[..tagMarker];
                    var variantName = qualifiedName[(tagMarker + ".Tag.".Length)..];
                    if (_unions.TryGetValue(unionName, out var unionNode))
                    {
                        for (var index = 0; index < unionNode.Variants.Count; index++)
                        {
                            if (!string.Equals(
                                    unionNode.Variants[index].Name,
                                    variantName,
                                    StringComparison.Ordinal))
                                continue;
                            enumType = new TypeNode { Name = unionName + ".Tag" };
                            value = index;
                            return true;
                        }
                    }
                }

                var memberSeparator = qualifiedName.LastIndexOf('.');
                if (memberSeparator > 0)
                {
                    var resolvedEnumName = qualifiedName[..memberSeparator];
                    var resolvedMemberName = qualifiedName[(memberSeparator + 1)..];
                    if (_enums.TryGetValue(resolvedEnumName, out var resolvedEnum))
                    {
                        var resolvedMember = resolvedEnum.Members.FirstOrDefault(candidate =>
                            string.Equals(candidate.Name, resolvedMemberName, StringComparison.Ordinal));
                        if (resolvedMember?.ResolvedValue is long resolvedMemberValue)
                        {
                            enumType = new TypeNode
                            {
                                Name = resolvedEnum.Name,
                                NamespacePath = new List<string>(resolvedEnum.NamespacePath)
                            };
                            value = resolvedMemberValue;
                            return true;
                        }
                    }
                }
            }
            if (field.Target is not IdentifierExpr identifier)
                return false;

            var enumName = identifier.Name;
            if (!_enums.TryGetValue(enumName, out var enumNode))
                return false;
            var member = enumNode.Members.FirstOrDefault(
                candidate => string.Equals(candidate.Name, field.Field, StringComparison.Ordinal));
            if (member?.ResolvedValue is not long resolvedValue)
                return false;

            enumType = new TypeNode
            {
                Name = enumNode.Name,
                NamespacePath = new List<string>(enumNode.NamespacePath)
            };
            value = resolvedValue;
            return true;
        }

        private bool TryResolveStaticEnumMember(FieldExpr field, out TypeNode enumType, out long value)
        {
            enumType = null!;
            value = 0;
            if (!TryResolveStaticTypeReference(field.Target, out var ownerType))
                return false;

            if (IsEnumType(ownerType))
            {
                var enumDefinition = GetEnumDefinition(ownerType);
                var member = enumDefinition?.Members.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, field.Field, StringComparison.Ordinal));
                if (member?.ResolvedValue is not long resolvedValue)
                    return false;

                enumType = ownerType.Clone();
                value = resolvedValue;
                return true;
            }

            return false;
        }

        private bool TryResolveStaticTypeReference(Expr expr, out TypeNode type)
        {
            switch (expr)
            {
                case TypeReferenceExpr typeReference:
                    type = typeReference.TypeName.Clone();
                    return true;

                case FieldExpr { Field: "Tag" } field when TryResolveStaticTypeReference(field.Target, out var unionType) && IsUnionType(unionType):
                    type = new TypeNode
                    {
                        Name = "Tag",
                        NamespacePath = unionType.NamespacePath.Concat(new[] { unionType.Name }).ToList(),
                        TypeArguments = unionType.TypeArguments.Select(argument => argument.Clone()).ToList()
                    };
                    return true;

                default:
                    type = null!;
                    return false;
            }
        }

        private EnumNode? GetEnumDefinition(TypeNode type)
        {
            var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
            if (TryGetTagUnion(fullName, out var unionNode))
                return BuildConcreteTagEnum(unionNode);

            if (!_enums.TryGetValue(fullName, out var enumNode))
                return null;

            return enumNode.TypeParameters.Count == 0
                ? enumNode
                : new EnumNode
                {
                    File = enumNode.File,
                    Line = enumNode.Line,
                    Column = enumNode.Column,
                    Length = enumNode.Length,
                    IsExported = enumNode.IsExported,
                    NamespacePath = new List<string>(enumNode.NamespacePath),
                    Name = enumNode.Name,
                    UnderlyingType = enumNode.UnderlyingType.Clone(),
                    Members = enumNode.Members.Select(member => new EnumMember
                    {
                        File = member.File,
                        Line = member.Line,
                        Column = member.Column,
                        Length = member.Length,
                        Name = member.Name,
                        Value = member.Value,
                        ResolvedValue = member.ResolvedValue
                    }).ToList()
                };
        }

        private static EnumNode BuildConcreteTagEnum(UnionNode unionNode)
        {
            return new EnumNode
            {
                Name = "Tag",
                NamespacePath = unionNode.NamespacePath.Concat(new[] { unionNode.Name }).ToList(),
                UnderlyingType = new TypeNode { Name = "i32" },
                Members = unionNode.Variants.Select((variant, index) => new EnumMember
                {
                    Name = variant.Name,
                    ResolvedValue = index
                }).ToList()
            };
        }

        private static TypeNode Dereference(TypeNode type)
        {
            if (!type.IsPointer)
                return type;
            var result = type.Clone();
            result.PointerLevel = Math.Max(0, result.PointerLevel - 1);
            result.IsPointer = result.PointerLevel > 0;
            return result;
        }

        private static TypeNode SlicePointerType(TypeNode sliceType)
        {
            var result = sliceType.Clone();
            result.IsSlice = false;
            result.ArraySize = null;
            result.ArraySizeExpr = null;
            result.IsPointer = true;
            result.PointerLevel = Math.Max(1, result.PointerLevel + 1);
            return result;
        }

        public uint Intern(TypeNode type)
        {
            var key = GetKey(type);
            if (_ids.TryGetValue(key, out var existing))
                return existing;

            var id = checked((uint)_types.Count + 1);
            _ids.Add(key, id);
            var backendType = new BackendType { Id = id };
            _types.Add(backendType);

            if (type.IsFunction)
            {
                backendType.Kind = "function";
                backendType.Name = $"zorb.fn.{id}";
                backendType.ElementType = Intern(
                    type.ReturnType ?? new TypeNode { Name = "void" });
                backendType.Fields = type.ParamTypes.Select((parameter, index) =>
                    new BackendTypeField
                    {
                        Name = $"arg{index}",
                        Type = Intern(parameter)
                    }).ToList();
                return id;
            }

            if (type.IsErrorUnion)
            {
                backendType.Kind = "error_union";
                backendType.Name = $"zorb.result.{id}";
                backendType.ElementType = Intern(type.ErrorInnerType ?? new TypeNode { Name = type.Name });
                return id;
            }

            if (type.IsSlice)
            {
                var elementType = type.Clone();
                elementType.IsSlice = false;
                elementType.ArraySize = null;
                backendType.Kind = "slice";
                backendType.Name = $"zorb.slice.{id}";
                backendType.ElementType = Intern(elementType);
                return id;
            }

            if (type.ArraySize is int arraySize)
            {
                var elementType = type.Clone();
                elementType.ArraySize = null;
                elementType.ArraySizeExpr = null;
                backendType.Kind = "array";
                backendType.ElementType = Intern(elementType);
                backendType.Length = checked((ulong)arraySize);
                return id;
            }

            if (type.IsPointer)
            {
                var pointee = type.Clone();
                var pointerLevel = Math.Max(type.PointerLevel, 1);
                pointee.PointerLevel = pointerLevel - 1;
                pointee.IsPointer = pointee.PointerLevel > 0;
                backendType.Kind = "pointer";
                backendType.ElementType = Intern(pointee);
                return id;
            }

            if (type.Name == "string")
            {
                backendType.Kind = "string";
                backendType.ElementType = Intern(new TypeNode { Name = "u8" });
                return id;
            }

            var scalar = NormalizeScalar(type.Name);
            if (scalar != null)
            {
                backendType.Kind = "scalar";
                backendType.Scalar = scalar;
                return id;
            }

            var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
            if (TryGetTagUnion(fullName, out _))
            {
                backendType.Kind = "enum";
                backendType.Name = type.TypeArguments.Count == 0
                    ? fullName
                    : fullName + "$" + string.Join("$", type.TypeArguments.Select(FormatTypeKey));
                backendType.ElementType = Intern(new TypeNode { Name = "i32" });
                return id;
            }
            if (_structs.TryGetValue(fullName, out var structNode))
            {
                backendType.Kind = "struct";
                backendType.Name = type.TypeArguments.Count == 0
                    ? fullName
                    : fullName + "$" + string.Join("$", type.TypeArguments.Select(FormatTypeKey));
                backendType.Packed = StructLayout.HasPackedAttribute(structNode);
                backendType.Fields = GetStructFields(type).Select(field => new BackendTypeField
                {
                    Name = field.Name,
                    Type = Intern(field.TypeName)
                }).ToList();
                return id;
            }

            if (_enums.TryGetValue(fullName, out var enumNode))
            {
                backendType.Kind = "enum";
                backendType.Name = type.TypeArguments.Count == 0
                    ? fullName
                    : fullName + "$" + string.Join("$", type.TypeArguments.Select(FormatTypeKey));
                backendType.ElementType = Intern(enumNode.UnderlyingType);
                return id;
            }

            if (_unions.TryGetValue(fullName, out var unionNode))
            {
                backendType.Kind = "union";
                backendType.Name = type.TypeArguments.Count == 0
                    ? fullName
                    : fullName + "$" + string.Join("$", type.TypeArguments.Select(FormatTypeKey));
                backendType.Fields = GetUnionVariants(type).Select(variant => new BackendTypeField
                {
                    Name = variant.Name,
                    Type = Intern(variant.TypeName)
                }).ToList();
                return id;
            }

            if (_externTypes.Contains(fullName))
            {
                backendType.Kind = "scalar";
                backendType.Scalar = fullName == "size_t" ? "u64" : "u64";
                return id;
            }

            throw Unsupported(type, $"type '{fullName}'");
        }

        private bool TryGetTagUnion(string typeName, out UnionNode unionNode)
        {
            unionNode = null!;
            if (!typeName.EndsWith(".Tag", StringComparison.Ordinal))
                return false;
            return _unions.TryGetValue(typeName[..^".Tag".Length], out unionNode!);
        }

        private static string? NormalizeScalar(string name)
        {
            return name switch
            {
                "void" or "bool" or
                "i8" or "u8" or
                "i16" or "u16" or
                "i32" or "u32" or
                "i64" or "u64" => name,
                "char" => "u8",
                _ => null
            };
        }

        private static string GetKey(TypeNode type)
        {
            if (type.IsFunction)
            {
                return $"fn({string.Join(",", type.ParamTypes.Select(GetKey))})->{GetKey(type.ReturnType ?? new TypeNode { Name = "void" })}";
            }
            if (type.IsErrorUnion)
                return $"!{GetKey(type.ErrorInnerType ?? new TypeNode { Name = type.Name })}";

            var baseName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
            if (type.TypeArguments.Count > 0)
                baseName += $"<{string.Join(",", type.TypeArguments.Select(GetKey))}>";
            if (type.IsSlice)
                baseName = $"[]{baseName}";
            if (type.ArraySize is int size)
                baseName = $"[{size}]{baseName}";
            if (type.IsPointer)
                baseName = $"{new string('*', Math.Max(type.PointerLevel, 1))}{baseName}";
            return baseName;
        }

        private static ZorbCompilerException Unsupported(TypeNode type, string detail)
        {
            return new ZorbCompilerException(
                $"Zig backend type interning does not support {detail} ({FormatType(type)}).");
        }
    }
}
