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
    private sealed partial class ScalarFunctionLowerer
    {
        private uint LowerExpression(Expr expression, TypeNode? expectedType = null)
        {
            var actualType = expectedType != null ? GetCheckedType(expression) : null;
            if (actualType != null &&
                TryLowerContextualArrayCoercion(expression, actualType, expectedType, out var contextualValue))
            {
                return contextualValue;
            }

            switch (expression)
            {
                case IdentifierExpr identifier:
                    return LowerIdentifierExpression(identifier);

                case NumberExpr number:
                    return EmitIntegerConstant(number.Value, expectedType ?? GetCheckedType(expression));

                case StringExpr text:
                    return LowerStringExpression(text);

                case ArrayLiteralExpr arrayLiteral:
                    return LowerArrayLiteralExpression(arrayLiteral);

                case StructLiteralExpr structLiteral:
                    return LowerStructLiteralExpression(structLiteral);

                case IndexExpr index:
                    return EmitInstruction("load", GetCheckedType(index), lhs: LowerAddress(index));

                case FieldExpr field:
                    return LowerFieldExpression(field);

                case BuiltinExpr builtin when builtin.Name is
                    "true" or
                    "false" or
                    "Builtin.IsLinux" or
                    "Builtin.IsWindows" or
                    "Builtin.IsBareMetal" or
                    "Builtin.IsX86_64" or
                    "Builtin.IsAArch64":
                    {
                        return EmitIntegerConstant(
                            GetBuiltinValue(builtin.Name) ? 1 : 0,
                            new TypeNode { Name = "bool" });
                    }

                case ErrorExpr error:
                    return LowerErrorValue(error);

                case CatchExpr catchExpression:
                    return LowerCatch(catchExpression);

                case SizeofExpr sizeofExpression:
                    return LowerSizeofExpression(sizeofExpression);

                case UnaryExpr unary:
                    return LowerUnaryExpression(expression, unary);

                case CastExpr cast:
                    return LowerCastExpression(cast);

                case BinaryExpr binary:
                    return LowerBinaryExpression(expression, binary);

                case CallExpr call:
                    return LowerCallExpression(expression, call);

                default:
                    throw Unsupported(expression, expression.GetType().Name);
            }
        }
        private bool TryLowerContextualArrayCoercion(
            Expr expression,
            TypeNode actualType,
            TypeNode? expectedType,
            out uint value)
        {
            value = 0;
            if (actualType.ArraySize == null || expectedType == null)
                return false;

            if (expectedType.IsSlice)
            {
                value = LowerSliceContextualArrayCoercion(expression, actualType, expectedType);
                return true;
            }

            if (!CanLowerPointerContextualArrayCoercion(expectedType))
                return false;

            value = LowerPointerContextualArrayCoercion(expression, actualType);
            return true;
        }
        private uint LowerSliceContextualArrayCoercion(
            Expr expression,
            TypeNode actualType,
            TypeNode expectedType)
        {
            return LowerArrayToSlice(expression, actualType, expectedType);
        }
        private bool CanLowerPointerContextualArrayCoercion(TypeNode expectedType)
        {
            return expectedType.IsPointer &&
                !expectedType.IsSlice &&
                Math.Max(expectedType.PointerLevel, 1) == 1;
        }
        private uint LowerPointerContextualArrayCoercion(Expr expression, TypeNode actualType)
        {
            var zero = EmitIntegerConstant(0, new TypeNode { Name = "i64" });
            return EmitIndexAddress(
                LowerAddress(expression),
                zero,
                actualType,
                GetArrayElementType(actualType));
        }
        private uint LowerIdentifierExpression(IdentifierExpr identifier)
        {
            var type = GetCheckedType(identifier);
            if (type.IsFunction &&
                _functionIds.TryGetValue(ResolveFunctionValueLoweringName(identifier.Name, identifier.TypeArguments), out var functionId))
            {
                return EmitFunctionAddress(functionId, type);
            }

            return EmitInstruction("load", type, lhs: LowerAddress(identifier));
        }
        private uint LowerStringExpression(StringExpr text)
        {
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "string_constant",
                Type = _typeInterner.Intern(new TypeNode { Name = "string" }),
                Text = text.Value
            });
            return id;
        }
        private uint LowerArrayLiteralExpression(ArrayLiteralExpr arrayLiteral)
        {
            var elementType = GetArrayElementType(arrayLiteral.TypeName);
            var elements = arrayLiteral.Elements
                .Select(element => LowerExpression(element, elementType))
                .ToList();
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "aggregate",
                Type = _typeInterner.Intern(arrayLiteral.TypeName),
                Arguments = elements
            });
            return id;
        }
        private uint LowerStructLiteralExpression(StructLiteralExpr structLiteral)
        {
            if (_typeInterner.IsUnionType(structLiteral.TypeName))
                return LowerUnionLiteralExpression(structLiteral);

            var fields = _typeInterner.GetStructFields(structLiteral.TypeName);
            var valuesByName = BuildStructLiteralFieldMap(structLiteral);
            var values = LowerStructLiteralFieldValues(structLiteral, fields, valuesByName);
            return EmitAggregateLiteral(structLiteral.TypeName, values);
        }
        private Dictionary<string, StructLiteralField> BuildStructLiteralFieldMap(StructLiteralExpr structLiteral)
        {
            return structLiteral.Fields.ToDictionary(
                field => field.Name,
                StringComparer.Ordinal);
        }
        private List<uint> LowerStructLiteralFieldValues(
            StructLiteralExpr structLiteral,
            IReadOnlyList<StructField> fields,
            IReadOnlyDictionary<string, StructLiteralField> valuesByName)
        {
            var values = new List<uint>(fields.Count);
            foreach (var field in fields)
            {
                if (!valuesByName.TryGetValue(field.Name, out var initializer))
                    throw Unsupported(structLiteral, $"missing struct field '{field.Name}'");

                values.Add(LowerExpression(initializer.Value, field.TypeName));
            }

            return values;
        }
        private uint EmitAggregateLiteral(TypeNode type, List<uint> values)
        {
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "aggregate",
                Type = _typeInterner.Intern(type),
                Arguments = values
            });
            return id;
        }
        private uint LowerUnionLiteralExpression(StructLiteralExpr structLiteral)
        {
            if (structLiteral.Fields.Count != 1)
                throw Unsupported(structLiteral, "union literal must initialize one variant");

            var field = structLiteral.Fields[0];
            var variantIndex = _typeInterner.GetUnionVariantIndex(
                structLiteral.TypeName,
                field.Name);
            var variants = _typeInterner.GetUnionVariants(structLiteral.TypeName);
            var unionValues = new List<uint>(variants.Count + 1)
            {
                EmitIntegerConstant(variantIndex, new TypeNode { Name = "i32" })
            };
            for (var index = 0; index < variants.Count; index++)
            {
                unionValues.Add(index == variantIndex
                    ? LowerExpression(field.Value, variants[index].TypeName)
                    : EmitZeroConstant(variants[index].TypeName));
            }

            return EmitAggregateLiteral(structLiteral.TypeName, unionValues);
        }
        private uint LowerFieldExpression(FieldExpr field)
        {
            if (_typeInterner.TryGetEnumMember(field, out var enumType, out var enumValue))
                return EmitIntegerConstant(enumValue, enumType);

            var fieldType = GetCheckedType(field);
            if (fieldType.IsFunction &&
                field.ResolvedQualifiedName is string resolvedFunction &&
                _functionIds.TryGetValue(ResolveFunctionValueLoweringName(resolvedFunction, field.TypeArguments), out var resolvedFunctionId))
            {
                return EmitFunctionAddress(resolvedFunctionId, fieldType);
            }

            return EmitInstruction("load", fieldType, lhs: LowerAddress(field));
        }
        private uint LowerSizeofExpression(SizeofExpr sizeofExpression)
        {
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "size_of",
                Type = _typeInterner.Intern(new TypeNode { Name = "i64" }),
                SourceType = _typeInterner.Intern(sizeofExpression.TargetType)
            });
            return id;
        }
        private uint LowerUnaryExpression(Expr expression, UnaryExpr unary)
        {
            return unary.Operator switch
            {
                "-" => LowerUnaryNegation(expression, unary),
                "!" => LowerUnaryNot(unary),
                "&" => LowerAddressOf(unary),
                _ => throw Unsupported(unary, $"unary operator '{unary.Operator}'")
            };
        }
        private uint LowerUnaryNegation(Expr expression, UnaryExpr unary)
        {
            var type = GetCheckedType(expression);
            var operand = LowerExpression(unary.Operand, type);
            var zero = EmitIntegerConstant(0, type);
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "binary",
                Type = _typeInterner.Intern(type),
                BinaryOp = "sub",
                Lhs = zero,
                Rhs = operand
            });
            return id;
        }
        private uint LowerUnaryNot(UnaryExpr unary)
        {
            var operand = LowerExpression(unary.Operand, new TypeNode { Name = "bool" });
            var zero = EmitIntegerConstant(0, new TypeNode { Name = "bool" });
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "compare",
                Type = _typeInterner.Intern(new TypeNode { Name = "bool" }),
                CompareOp = "equal",
                Lhs = operand,
                Rhs = zero
            });
            return id;
        }
        private uint LowerAddressOf(UnaryExpr unary)
        {
            var operandType = GetCheckedType(unary.Operand);
            if (operandType.ArraySize != null)
            {
                var baseAddress = LowerAddress(unary.Operand);
                var zero = EmitIntegerConstant(0, new TypeNode { Name = "i64" });
                return EmitIndexAddress(
                    baseAddress,
                    zero,
                    operandType,
                    GetArrayElementType(operandType));
            }

            return LowerAddress(unary.Operand);
        }
        private uint LowerCastExpression(CastExpr cast)
        {
            var sourceType = GetCheckedType(cast.Expr);
            var targetType = cast.TargetType;
            var source = LowerExpression(cast.Expr, sourceType);
            var sourceIsPointer = IsPointerLike(sourceType);
            var targetIsPointer = IsPointerLike(targetType);
            if (sourceIsPointer && targetIsPointer)
            {
                var integerType = new TypeNode { Name = "i64" };
                var asInteger = EmitCastInstruction(source, integerType, "pointer_to_integer");
                return EmitCastInstruction(asInteger, targetType, "integer_to_pointer");
            }

            if (sourceIsPointer || targetIsPointer)
                return LowerPointerOrIntegerCast(source, sourceType, targetType);

            var sourceWidth = GetScalarBitWidth(sourceType);
            var targetWidth = GetScalarBitWidth(targetType);
            if (sourceWidth == targetWidth)
                return source;

            return LowerIntegerWidthCast(source, sourceType, targetType, sourceWidth, targetWidth);
        }
        private uint LowerPointerOrIntegerCast(uint source, TypeNode sourceType, TypeNode targetType)
        {
            return EmitCastInstruction(
                source,
                targetType,
                IsPointerLike(sourceType) ? "pointer_to_integer" : "integer_to_pointer");
        }
        private uint LowerIntegerWidthCast(
            uint source,
            TypeNode sourceType,
            TypeNode targetType,
            int sourceWidth,
            int targetWidth)
        {
            return EmitCastInstruction(
                source,
                targetType,
                targetWidth < sourceWidth
                    ? "truncate"
                    : IsSignedScalar(sourceType) ? "sign_extend" : "zero_extend");
        }
        private uint EmitCastInstruction(uint source, TypeNode targetType, string castOp)
        {
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "cast",
                Type = _typeInterner.Intern(targetType),
                CastOp = castOp,
                Lhs = source
            });
            return id;
        }
        private uint LowerBinaryExpression(Expr expression, BinaryExpr binary)
        {
            if (binary.Operator is "&&" or "||")
                return LowerLogical(binary);

            var leftType = GetCheckedType(binary.Left);
            var rightType = GetCheckedType(binary.Right);
            if (IsPointerArithmetic(binary, leftType, rightType))
                return LowerPointerArithmetic(binary, leftType, rightType);

            var resultType = GetCheckedType(expression);
            var operandType = GetBinaryOperandType(binary, leftType, rightType, resultType);
            var (lhs, rhs) = LowerBinaryOperands(binary, leftType, rightType, operandType);
            return EmitBinaryOperation(binary, resultType, operandType, lhs, rhs);
        }
        private bool IsPointerArithmetic(BinaryExpr binary, TypeNode leftType, TypeNode rightType)
        {
            if (binary.Operator == "+")
            {
                return (leftType.IsPointer && IsScalarInteger(rightType)) ||
                    (rightType.IsPointer && IsScalarInteger(leftType));
            }

            if (binary.Operator == "-")
                return leftType.IsPointer && IsScalarInteger(rightType);

            return false;
        }
        private TypeNode GetBinaryOperandType(
            BinaryExpr binary,
            TypeNode leftType,
            TypeNode rightType,
            TypeNode resultType)
        {
            return IsComparison(binary.Operator)
                ? GetComparisonOperandType(leftType, rightType)
                : resultType;
        }
        private (uint Lhs, uint Rhs) LowerBinaryOperands(
            BinaryExpr binary,
            TypeNode leftType,
            TypeNode rightType,
            TypeNode operandType)
        {
            if (IsScalarInteger(leftType) &&
                IsScalarInteger(rightType) &&
                IsScalarInteger(operandType))
            {
                return (
                    LowerIntegerOperand(binary.Left, leftType, operandType),
                    LowerIntegerOperand(binary.Right, rightType, operandType));
            }

            return (
                LowerExpression(binary.Left, leftType),
                LowerExpression(binary.Right, rightType));
        }
        private uint EmitBinaryOperation(
            BinaryExpr binary,
            TypeNode resultType,
            TypeNode operandType,
            uint lhs,
            uint rhs)
        {
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = IsComparison(binary.Operator) ? "compare" : "binary",
                Type = _typeInterner.Intern(resultType),
                BinaryOp = MapBinaryOp(binary.Operator, operandType),
                CompareOp = MapCompareOp(binary.Operator, operandType),
                Lhs = lhs,
                Rhs = rhs
            });
            return id;
        }
        private uint LowerPointerArithmetic(BinaryExpr binary, TypeNode leftType, TypeNode rightType)
        {
            var pointerExpression = leftType.IsPointer ? binary.Left : binary.Right;
            var pointerType = leftType.IsPointer ? leftType : rightType;
            var indexExpression = leftType.IsPointer ? binary.Right : binary.Left;
            var pointer = LowerExpression(pointerExpression, pointerType);
            var indexType = GetCheckedType(indexExpression);
            var index = LowerExpression(indexExpression, indexType);
            index = CoerceInteger(index, indexType, new TypeNode { Name = "i64" });
            if (binary.Operator == "-")
            {
                var zero = EmitIntegerConstant(0, new TypeNode { Name = "i64" });
                index = EmitBinary("sub", zero, index, new TypeNode { Name = "i64" });
            }

            var elementType = GetPointerElementType(pointerType);
            return EmitIndexAddress(pointer, index, elementType, elementType);
        }
        private uint LowerCallExpression(Expr expression, CallExpr call)
        {
            var resolvedName = ResolveCallLoweringName(call);
            if (TryLowerIndirectCall(call, resolvedName, out var indirectResult))
                return indirectResult;

            if (resolvedName == "syscall")
                return LowerSyscallCall(expression, call);

            return LowerDirectCall(expression, call, resolvedName);
        }
        private string ResolveCallLoweringName(CallExpr call)
        {
            var resolvedName = call.ResolvedTargetQualifiedName
                ?? call.ResolvedQualifiedName
                ?? QualifiedNames.GetFullName(call.NamespacePath, call.Name);
            if (call.TypeArguments.Count > 0)
                resolvedName = GenericFunctionName(resolvedName, call.TypeArguments);
            return resolvedName;
        }
        private Expr? TryResolveIndirectCallTarget(CallExpr call)
        {
            if (call.TargetExpr != null)
                return call.TargetExpr;

            return LookupLocal(call.Name) is { Type.IsFunction: true }
                ? new IdentifierExpr { Name = call.Name }
                : null;
        }
        private bool TryLowerIndirectCall(CallExpr call, string resolvedName, out uint result)
        {
            var indirectTarget = TryResolveIndirectCallTarget(call);
            if (indirectTarget == null || _functionIds.ContainsKey(resolvedName))
            {
                result = 0;
                return false;
            }

            var indirectFunctionType = call.ResolvedFunctionType
                ?? GetCheckedType(indirectTarget)
                ?? throw Unsupported(call, "indirect call has no checked function type");
            var callee = LowerExpression(indirectTarget, indirectFunctionType);
            var indirectArguments = LowerCallArguments(call, indirectFunctionType);
            var indirectId = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = indirectId,
                Op = "indirect_call",
                Type = _typeInterner.Intern(
                    indirectFunctionType.ReturnType ?? new TypeNode { Name = "void" }),
                SourceType = _typeInterner.Intern(indirectFunctionType),
                Lhs = callee,
                Arguments = indirectArguments
            });
            result = indirectId;
            return true;
        }
        private uint LowerDirectCall(Expr expression, CallExpr call, string resolvedName)
        {
            if (!_functionIds.TryGetValue(resolvedName, out var calleeId))
                throw Unsupported(expression, $"call target '{resolvedName}' is not in the backend module");

            var functionType = call.ResolvedFunctionType
                ?? ResolveFunctionType(call)
                ?? throw Unsupported(expression, $"call target '{resolvedName}' has no checked function type");
            var arguments = LowerCallArguments(call, functionType);

            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "call",
                Type = _typeInterner.Intern(functionType.ReturnType ?? new TypeNode { Name = "void" }),
                Callee = calleeId,
                Arguments = arguments
            });
            return id;
        }
        private uint LowerSyscallCall(Expr expression, CallExpr call)
        {
            var syscallType = new TypeNode { Name = "i64" };
            var syscallArguments = call.Args
                .Select(argument =>
                {
                    var argumentType = GetCheckedType(argument);
                    var value = LowerExpression(argument, argumentType);
                    return CoerceInteger(value, argumentType, syscallType);
                })
                .ToList();
            if (syscallArguments.Count is < 1 or > 7)
                throw Unsupported(expression, "syscall requires between one and seven arguments");

            var syscallId = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = syscallId,
                Op = "syscall",
                Type = _typeInterner.Intern(new TypeNode { Name = "i64" }),
                Arguments = syscallArguments
            });
            return syscallId;
        }
        private uint EmitFunctionAddress(uint functionId, TypeNode type)
        {
            var functionValue = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = functionValue,
                Op = "function_address",
                Type = _typeInterner.Intern(type),
                Callee = functionId
            });
            return functionValue;
        }
        private string ResolveFunctionValueLoweringName(string resolvedName, IReadOnlyList<TypeNode> typeArguments)
        {
            return typeArguments.Count > 0
                ? GenericFunctionName(resolvedName, typeArguments)
                : resolvedName;
        }
        private List<uint> LowerCallArguments(CallExpr call, TypeNode functionType)
        {
            var arguments = new List<uint>(call.Args.Count);
            for (var index = 0; index < call.Args.Count; index++)
            {
                var parameterType = GetCallParameterType(functionType, index);
                arguments.Add(LowerCallArgument(call.Args[index], parameterType));
            }
            return arguments;
        }
        private TypeNode? GetCallParameterType(TypeNode functionType, int index)
        {
            return index < functionType.ParamTypes.Count
                ? functionType.ParamTypes[index]
                : null;
        }
        private uint LowerCallArgument(Expr argument, TypeNode? parameterType)
        {
            if (parameterType != null &&
                parameterType.IsFunction &&
                TryLowerSpecializedFunctionValueArgument(argument, parameterType, out var specializedFunctionValue))
            {
                return specializedFunctionValue;
            }

            var argumentType = GetCheckedType(argument);
            if (TryLowerContextualArrayCoercion(argument, argumentType, parameterType, out var contextualValue))
                return contextualValue;

            if (parameterType != null &&
                IsScalarInteger(argumentType) &&
                IsScalarInteger(parameterType))
            {
                return LowerIntegerOperand(argument, argumentType, parameterType);
            }

            return LowerExpression(argument, parameterType);
        }
        private bool TryLowerSpecializedFunctionValueArgument(Expr argument, TypeNode parameterType, out uint functionValue)
        {
            switch (argument)
            {
                case IdentifierExpr identifier when _functionIds.TryGetValue(
                    ResolveFunctionValueLoweringName(identifier.Name, identifier.TypeArguments),
                    out var functionId):
                    functionValue = EmitFunctionAddress(functionId, parameterType.Clone());
                    return true;

                case FieldExpr { ResolvedQualifiedName: string resolvedName } field when _functionIds.TryGetValue(
                    ResolveFunctionValueLoweringName(resolvedName, field.TypeArguments),
                    out var resolvedFunctionId):
                    functionValue = EmitFunctionAddress(resolvedFunctionId, parameterType.Clone());
                    return true;
            }

            functionValue = 0;
            return false;
        }
        private uint LowerLogical(BinaryExpr binary)
        {
            var lhs = LowerExpression(binary.Left, new TypeNode { Name = "bool" });
            var lhsBlock = _currentBlock
                ?? throw new ZorbCompilerException("Logical expression lost its left-hand block.");
            var shortCircuitValue = EmitIntegerConstant(
                binary.Operator == "||" ? 1 : 0,
                new TypeNode { Name = "bool" });
            var rhsBlock = CreateBlock("logical.rhs");
            var mergeBlock = CreateBlock("logical.end");
            LowerLogicalShortCircuitBranch(binary, lhs, rhsBlock, mergeBlock);
            var (rhs, rhsEnd) = LowerLogicalRightHandSide(binary, rhsBlock, mergeBlock);
            return EmitLogicalPhi(shortCircuitValue, lhsBlock, rhs, rhsEnd, mergeBlock);
        }
        private void LowerLogicalShortCircuitBranch(
            BinaryExpr binary,
            uint lhs,
            BackendBlock rhsBlock,
            BackendBlock mergeBlock)
        {
            Terminate(new BackendTerminator
            {
                Op = "conditional_branch",
                Condition = lhs,
                TrueTarget = binary.Operator == "&&" ? rhsBlock.Id : mergeBlock.Id,
                FalseTarget = binary.Operator == "&&" ? mergeBlock.Id : rhsBlock.Id
            });
        }
        private (uint Value, BackendBlock EndBlock) LowerLogicalRightHandSide(
            BinaryExpr binary,
            BackendBlock rhsBlock,
            BackendBlock mergeBlock)
        {
            _currentBlock = rhsBlock;
            var rhs = LowerExpression(binary.Right, new TypeNode { Name = "bool" });
            var rhsEnd = _currentBlock
                ?? throw new ZorbCompilerException("Logical expression right-hand side terminated unexpectedly.");
            Terminate(new BackendTerminator { Op = "branch", Target = mergeBlock.Id });
            return (rhs, rhsEnd);
        }
        private uint EmitLogicalPhi(
            uint shortCircuitValue,
            BackendBlock lhsBlock,
            uint rhs,
            BackendBlock rhsEnd,
            BackendBlock mergeBlock)
        {
            _currentBlock = mergeBlock;
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "phi",
                Type = _typeInterner.Intern(new TypeNode { Name = "bool" }),
                IncomingValues = [shortCircuitValue, rhs],
                IncomingBlocks = [lhsBlock.Id, rhsEnd.Id]
            });
            return id;
        }
        private uint LowerArrayToSlice(Expr expression, TypeNode arrayType, TypeNode sliceType)
        {
            var elementType = GetArrayElementType(arrayType);
            var zero = EmitIntegerConstant(0, new TypeNode { Name = "i64" });
            var pointer = EmitIndexAddress(
                LowerAddress(expression),
                zero,
                arrayType,
                elementType);
            var length = EmitIntegerConstant(
                arrayType.ArraySize ?? throw Unsupported(expression, "array without a resolved length"),
                new TypeNode { Name = "i64" });
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "aggregate",
                Type = _typeInterner.Intern(sliceType),
                Arguments = [pointer, length]
            });
            return id;
        }
    }
}
