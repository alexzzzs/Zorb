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
        private uint LowerSliceIndexAddress(
            IndexExpr expression,
            TypeNode sliceType,
            TypeNode elementType,
            uint index)
        {
            var slice = LowerExpression(expression.Target, sliceType);
            var pointer = EmitExtractValue(slice, AddressOf(elementType), 0);
            var length = EmitExtractValue(slice, new TypeNode { Name = "i64" }, 1);
            var indexType = GetCheckedType(expression.Index);
            var normalizedIndex = index;
            var zero = EmitIntegerConstant(0, new TypeNode { Name = "i64" });
            uint failed;
            if (IsSignedScalar(indexType))
            {
                var negative = EmitComparison("signed_less", normalizedIndex, zero);
                var pastEnd = EmitComparison("unsigned_greater_equal", normalizedIndex, length);
                failed = EmitBinary("bit_or", negative, pastEnd, new TypeNode { Name = "bool" });
            }
            else
            {
                failed = EmitComparison("unsigned_greater_equal", normalizedIndex, length);
            }

            var failureBlock = CreateBlock("slice.oob");
            var successBlock = CreateBlock("slice.index");
            Terminate(new BackendTerminator
            {
                Op = "conditional_branch",
                Condition = failed,
                TrueTarget = failureBlock.Id,
                FalseTarget = successBlock.Id
            });

            _currentBlock = failureBlock;
            var exitCode = EmitIntegerConstant(1, new TypeNode { Name = "i32" });
            _ = EmitInstruction("process_exit", new TypeNode { Name = "void" }, lhs: exitCode);
            Terminate(new BackendTerminator { Op = "unreachable" });

            _currentBlock = successBlock;
            return EmitIndexAddress(pointer, normalizedIndex, elementType, elementType);
        }
        private uint LowerAddress(Expr expression)
        {
            switch (expression)
            {
                case IdentifierExpr identifier:
                    return LowerIdentifierAddress(identifier);

                case IndexExpr index:
                    return LowerIndexAddress(index);

                case FieldExpr field:
                    return LowerFieldAddress(field);

                default:
                    return LowerTemporaryAddress(expression);
            }
        }
        private uint LowerIdentifierAddress(IdentifierExpr identifier)
        {
            if (LookupLocal(identifier.Name) is LocalBinding local)
                return local.Address;

            if (_globalIds.TryGetValue(identifier.Name, out var globalId))
                return EmitGlobalAddress(globalId, _globals[identifier.Name].TypeName);

            throw Unsupported(identifier, $"identifier '{identifier.Name}' is not addressable");
        }
        private uint LowerIndexAddress(IndexExpr index)
        {
            var targetType = GetCheckedType(index.Target);
            var elementType = GetCheckedType(index);
            var indexType = GetCheckedType(index.Index);
            var indexValue = IsScalarInteger(indexType)
                ? LowerIntegerOperand(index.Index, indexType, new TypeNode { Name = "i64" })
                : LowerExpression(index.Index, new TypeNode { Name = "i64" });
            if (targetType.ArraySize != null)
            {
                return EmitIndexAddress(
                    LowerAddress(index.Target),
                    indexValue,
                    targetType,
                    elementType);
            }
            if (targetType.IsPointer && !targetType.IsErrorUnion)
            {
                return EmitIndexAddress(
                    LowerExpression(index.Target, targetType),
                    indexValue,
                    GetPointerElementType(targetType),
                    elementType);
            }
            if (targetType.IsSlice)
                return LowerSliceIndexAddress(index, targetType, elementType, indexValue);

            throw Unsupported(index, $"index address for type '{FormatType(targetType)}'");
        }
        private uint LowerFieldAddress(FieldExpr field)
        {
            if (TryLowerResolvedGlobalFieldAddress(field, out var globalAddress))
                return globalAddress;

            var (containerType, baseAddress) = ResolveFieldAddressBase(field);
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "field_address",
                Type = _typeInterner.Intern(GetCheckedType(field)),
                SourceType = _typeInterner.Intern(containerType),
                FieldIndex = _typeInterner.GetStructFieldIndex(containerType, field.Field),
                Lhs = baseAddress
            });
            return id;
        }
        private bool TryLowerResolvedGlobalFieldAddress(FieldExpr field, out uint address)
        {
            if (field.ResolvedQualifiedName is string resolvedGlobal &&
                _globalIds.TryGetValue(resolvedGlobal, out var resolvedGlobalId) &&
                _globals.TryGetValue(resolvedGlobal, out var resolvedGlobalDeclaration))
            {
                address = EmitGlobalAddress(resolvedGlobalId, resolvedGlobalDeclaration.TypeName);
                return true;
            }

            address = 0;
            return false;
        }
        private (TypeNode ContainerType, uint BaseAddress) ResolveFieldAddressBase(FieldExpr field)
        {
            var targetType = GetCheckedType(field.Target);
            if (targetType.IsPointer && !targetType.IsErrorUnion)
            {
                return (
                    GetPointerElementType(targetType),
                    LowerExpression(field.Target, targetType));
            }

            return (targetType, LowerAddress(field.Target));
        }
        private uint LowerTemporaryAddress(Expr expression)
        {
            var type = GetCheckedType(expression);
            var value = LowerExpression(expression, type);
            var address = EmitInstruction("alloca", type);
            _ = EmitInstruction("store", type, lhs: address, rhs: value);
            return address;
        }
        private uint EmitGlobalAddress(uint globalId, TypeNode type)
        {
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "global_address",
                Type = _typeInterner.Intern(type),
                Global = globalId
            });
            return id;
        }
        private uint LowerErrorValue(ErrorExpr error)
        {
            var symbolName = $"Error_{error.ErrorCode}";
            if (!_globalIds.TryGetValue(symbolName, out var globalId) ||
                !_globals.TryGetValue(symbolName, out var global))
            {
                throw Unsupported(error, $"declared error '{error.ErrorCode}' is missing from the backend module");
            }

            var address = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = address,
                Op = "global_address",
                Type = _typeInterner.Intern(global.TypeName),
                Global = globalId
            });
            return EmitInstruction("load", global.TypeName, lhs: address);
        }
        private uint LowerCatch(CatchExpr expression, bool discardResult = false)
        {
            var errorUnionType = GetCheckedType(expression.Left);
            if (!errorUnionType.IsErrorUnion)
                throw Unsupported(expression, "catch operand is not an error union");

            var successType = errorUnionType.ErrorInnerType
                ?? throw Unsupported(expression, "error union has no success type");
            var errorType = new TypeNode { Name = "i32" };
            var (successValue, errorValue) = LowerCatchOperands(expression, errorUnionType, successType, errorType);
            var catchBlock = CreateBlock("catch.error");
            var successBlock = CreateBlock("catch.success");
            var mergeBlock = CreateBlock("catch.end");
            LowerCatchDispatch(errorValue, errorType, catchBlock, successBlock, mergeBlock);
            var (fallbackValue, fallbackBlock) = LowerCatchBody(
                expression,
                discardResult,
                successType,
                errorType,
                errorValue,
                catchBlock,
                mergeBlock);
            _currentBlock = mergeBlock;
            return EmitCatchResultPhi(successType, successValue, successBlock, fallbackValue, fallbackBlock);
        }
        private (uint SuccessValue, uint ErrorValue) LowerCatchOperands(
            CatchExpr expression,
            TypeNode errorUnionType,
            TypeNode successType,
            TypeNode errorType)
        {
            var result = LowerExpression(expression.Left, errorUnionType);
            var successValue = EmitExtractValue(result, successType, 0);
            var errorValue = EmitExtractValue(result, errorType, 1);
            return (successValue, errorValue);
        }
        private void LowerCatchDispatch(
            uint errorValue,
            TypeNode errorType,
            BackendBlock catchBlock,
            BackendBlock successBlock,
            BackendBlock mergeBlock)
        {
            var zero = EmitIntegerConstant(0, errorType);
            var hasError = EmitComparison("not_equal", errorValue, zero);
            Terminate(new BackendTerminator
            {
                Op = "conditional_branch",
                Condition = hasError,
                TrueTarget = catchBlock.Id,
                FalseTarget = successBlock.Id
            });

            _currentBlock = successBlock;
            Terminate(new BackendTerminator { Op = "branch", Target = mergeBlock.Id });
        }
        private (uint? FallbackValue, uint? FallbackBlock) LowerCatchBody(
            CatchExpr expression,
            bool discardResult,
            TypeNode successType,
            TypeNode errorType,
            uint errorValue,
            BackendBlock catchBlock,
            BackendBlock mergeBlock)
        {
            _currentBlock = catchBlock;
            PushScope();
            RegisterCatchErrorLocal(expression.ErrorVar, errorType, errorValue);
            var fallbackExpression = GetCatchFallbackExpression(expression, discardResult);
            var statements = GetCatchStatements(expression, fallbackExpression);
            LowerStatements(statements);
            var result = LowerCatchFallbackResult(
                discardResult,
                fallbackExpression,
                successType,
                mergeBlock);
            PopScope();
            if (_currentBlock != null)
                throw Unsupported(expression, "catch body can fall through without producing a value");
            return result;
        }
        private void RegisterCatchErrorLocal(string errorVar, TypeNode errorType, uint errorValue)
        {
            var errorAddress = EmitInstruction("alloca", errorType);
            _ = EmitInstruction("store", errorType, lhs: errorAddress, rhs: errorValue);
            DeclareLocal(errorVar, errorType, errorAddress, isCatchError: true);
        }
        private Expr? GetCatchFallbackExpression(CatchExpr expression, bool discardResult)
        {
            return !discardResult && expression.CatchBody.LastOrDefault() is ExpressionStatement fallback
                ? fallback.Expression
                : null;
        }
        private IReadOnlyList<Statement> GetCatchStatements(CatchExpr expression, Expr? fallbackExpression)
        {
            return fallbackExpression == null
                ? expression.CatchBody
                : expression.CatchBody.Take(expression.CatchBody.Count - 1).ToList();
        }
        private (uint? FallbackValue, uint? FallbackBlock) LowerCatchFallbackResult(
            bool discardResult,
            Expr? fallbackExpression,
            TypeNode successType,
            BackendBlock mergeBlock)
        {
            if (_currentBlock != null && fallbackExpression != null)
            {
                var fallbackType = GetCheckedType(fallbackExpression);
                var fallbackValue = IsScalarInteger(fallbackType) && IsScalarInteger(successType)
                    ? LowerIntegerOperand(fallbackExpression, fallbackType, successType)
                    : LowerExpression(fallbackExpression, successType);
                var fallbackBlock = _currentBlock.Id;
                Terminate(new BackendTerminator { Op = "branch", Target = mergeBlock.Id });
                return (fallbackValue, fallbackBlock);
            }

            if (_currentBlock != null && discardResult)
                Terminate(new BackendTerminator { Op = "branch", Target = mergeBlock.Id });

            return (null, null);
        }
        private uint EmitCatchResultPhi(
            TypeNode successType,
            uint successValue,
            BackendBlock successBlock,
            uint? fallbackValue,
            uint? fallbackBlock)
        {
            if (!fallbackValue.HasValue || !fallbackBlock.HasValue)
                return successValue;

            var mergedValue = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = mergedValue,
                Op = "phi",
                Type = _typeInterner.Intern(successType),
                IncomingValues = [successValue, fallbackValue.Value],
                IncomingBlocks = [successBlock.Id, fallbackBlock.Value]
            });
            return mergedValue;
        }
        private uint CoerceInteger(uint value, TypeNode sourceType, TypeNode targetType)
        {
            var sourceWidth = GetScalarBitWidth(sourceType);
            var targetWidth = GetScalarBitWidth(targetType);
            if (sourceWidth == targetWidth)
                return value;

            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "cast",
                Type = _typeInterner.Intern(targetType),
                CastOp = targetWidth < sourceWidth
                    ? "truncate"
                    : IsSignedScalar(sourceType) ? "sign_extend" : "zero_extend",
                Lhs = value
            });
            return id;
        }
        private uint LowerIntegerOperand(Expr expression, TypeNode sourceType, TypeNode targetType)
        {
            if (expression is NumberExpr)
                return LowerExpression(expression, targetType);

            var value = LowerExpression(expression, sourceType);
            return CoerceInteger(value, sourceType, targetType);
        }
        private uint EmitComparison(string comparison, uint lhs, uint rhs)
        {
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "compare",
                Type = _typeInterner.Intern(new TypeNode { Name = "bool" }),
                CompareOp = comparison,
                Lhs = lhs,
                Rhs = rhs
            });
            return id;
        }
        private uint EmitBinary(string operation, uint lhs, uint rhs, TypeNode type)
        {
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "binary",
                Type = _typeInterner.Intern(type),
                BinaryOp = operation,
                Lhs = lhs,
                Rhs = rhs
            });
            return id;
        }
        private uint EmitExtractValue(uint aggregate, TypeNode type, uint fieldIndex)
        {
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "extract_value",
                Type = _typeInterner.Intern(type),
                Lhs = aggregate,
                FieldIndex = fieldIndex
            });
            return id;
        }
        private uint EmitIndexAddress(
            uint baseAddress,
            uint index,
            TypeNode sourceType,
            TypeNode elementType)
        {
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "index_address",
                Type = _typeInterner.Intern(elementType),
                SourceType = _typeInterner.Intern(sourceType),
                Lhs = baseAddress,
                Rhs = index
            });
            return id;
        }
    }
}
