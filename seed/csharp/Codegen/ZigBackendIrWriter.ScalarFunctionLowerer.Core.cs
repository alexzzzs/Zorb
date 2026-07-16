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
        public ScalarFunctionLowerer(
            TypeChecker typeChecker,
            IReadOnlyDictionary<string, uint> functionIds,
            IReadOnlyDictionary<string, TypeNode> functionTypes,
            IReadOnlyDictionary<string, uint> globalIds,
            IReadOnlyDictionary<string, VariableDeclarationNode> globals,
            IReadOnlyDictionary<string, uint> parameterIds,
            uint nextValueId,
            FunctionDecl function,
            ZigBackendTarget target,
            TypeInterner typeInterner)
        {
            _typeChecker = typeChecker;
            _functionIds = functionIds;
            _functionTypes = functionTypes;
            _globalIds = globalIds;
            _globals = globals;
            _parameterIds = parameterIds;
            _nextValueId = nextValueId;
            _function = function;
            _target = target;
            _typeInterner = typeInterner;
        }
        public List<BackendBlock> Lower()
        {
            InitializeEntryBlock();
            PushScope();
            LowerParameters();
            LowerStatements(_function.Body);
            EnsureFunctionTerminated();
            ValidateLoweredBlocks();
            PopScope();
            return _blocks;
        }
        private void InitializeEntryBlock()
        {
            _currentBlock = CreateBlock("entry");
        }
        private void LowerParameters()
        {
            foreach (var parameter in _function.Parameters)
            {
                var parameterValue = _parameterIds[parameter.Name];
                var address = EmitInstruction("alloca", parameter.TypeName);
                _ = EmitInstruction("store", parameter.TypeName, lhs: address, rhs: parameterValue);
                DeclareLocal(parameter.Name, parameter.TypeName, address);
            }
        }
        private void EnsureFunctionTerminated()
        {
            if (_currentBlock == null || _currentBlock.Terminator != null)
                return;

            if (_function.ReturnType.Name == "void")
            {
                _currentBlock.Terminator = new BackendTerminator { Op = "return_void" };
                return;
            }

            throw Unsupported(_function, $"function '{_function.Name}' has an unterminated value-returning path");
        }
        private void ValidateLoweredBlocks()
        {
            foreach (var block in _blocks)
            {
                if (block.Terminator == null)
                    throw Unsupported(_function, $"block '{block.Name}' is not terminated");
            }
        }
        private BackendBlock CreateBlock(string name)
        {
            var id = _nextBlockId++;
            var block = new BackendBlock
            {
                Id = id,
                Name = $"{name}.{id}"
            };
            _blocks.Add(block);
            return block;
        }
        private void Terminate(BackendTerminator terminator)
        {
            var block = _currentBlock
                ?? throw new ZorbCompilerException("Attempted to terminate an unavailable LLVM block.");
            if (block.Terminator != null)
                throw new ZorbCompilerException($"LLVM block '{block.Name}' already has a terminator.");
            block.Terminator = terminator;
            _currentBlock = null;
        }
        private void AddInstruction(BackendInstruction instruction)
        {
            var block = _currentBlock
                ?? throw new ZorbCompilerException("Attempted to emit an instruction without an active LLVM block.");
            block.Instructions.Add(instruction);
        }
        private uint EmitInstruction(
            string op,
            TypeNode type,
            uint? lhs = null,
            uint? rhs = null)
        {
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = op,
                Type = _typeInterner.Intern(type),
                Lhs = lhs,
                Rhs = rhs
            });
            return id;
        }
        private uint EmitIntegerConstant(long value, TypeNode type)
        {
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "integer_constant",
                Type = _typeInterner.Intern(type),
                Integer = value
            });
            return id;
        }
        private uint EmitZeroConstant(TypeNode type)
        {
            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "zero_constant",
                Type = _typeInterner.Intern(type)
            });
            return id;
        }
        private static TypeNode GetArrayElementType(TypeNode arrayType)
        {
            var elementType = arrayType.Clone();
            elementType.ArraySize = null;
            elementType.ArraySizeExpr = null;
            return elementType;
        }
        private static TypeNode GetIndexedElementType(TypeNode type)
        {
            if (type.ArraySize != null)
                return GetArrayElementType(type);
            if (type.IsSlice)
            {
                var element = type.Clone();
                element.IsSlice = false;
                return element;
            }
            if (type.IsPointer)
                return GetPointerElementType(type);
            throw new ZorbCompilerException($"Type '{FormatType(type)}' is not indexable.");
        }
        private static TypeNode GetPointerElementType(TypeNode pointerType)
        {
            var elementType = pointerType.Clone();
            var pointerLevel = Math.Max(elementType.PointerLevel, 1);
            elementType.PointerLevel = pointerLevel - 1;
            elementType.IsPointer = elementType.PointerLevel > 0;
            return elementType;
        }
        private static TypeNode AddressOf(TypeNode type)
        {
            var pointer = type.Clone();
            pointer.IsPointer = true;
            pointer.PointerLevel = Math.Max(pointer.PointerLevel, 0) + 1;
            return pointer;
        }
        private bool GetBuiltinValue(string name)
        {
            var triple = _target.Triple;
            return name switch
            {
                "true" => true,
                "false" => false,
                "Builtin.IsLinux" => triple.Contains("linux", StringComparison.Ordinal),
                "Builtin.IsWindows" => triple.Contains("windows", StringComparison.Ordinal),
                "Builtin.IsBareMetal" => triple.Contains("-none-", StringComparison.Ordinal),
                "Builtin.IsX86_64" => triple.StartsWith("x86_64-", StringComparison.Ordinal),
                "Builtin.IsAArch64" => triple.StartsWith("aarch64-", StringComparison.Ordinal),
                _ => throw new ZorbCompilerException($"Unknown target builtin '{name}'.")
            };
        }
        private void PushScope()
        {
            _localScopes.Push(new Dictionary<string, LocalBinding>(StringComparer.Ordinal));
        }
        private void PopScope()
        {
            _localScopes.Pop();
        }
        private void DeclareLocal(
            string name,
            TypeNode type,
            uint address,
            bool isCatchError = false)
        {
            _localScopes.Peek().Add(
                name,
                new LocalBinding(address, type.Clone(), isCatchError));
        }
        private LocalBinding? LookupLocal(string name)
        {
            foreach (var scope in _localScopes)
            {
                if (scope.TryGetValue(name, out var local))
                    return local;
            }
            return null;
        }
        private TypeNode GetCheckedType(Expr expression)
        {
            if (_typeChecker.GetCheckedExpressionType(expression) is TypeNode checkedType)
                return checkedType;

            return expression switch
            {
                IdentifierExpr identifier when LookupLocal(identifier.Name) is LocalBinding local
                    => local.Type.Clone(),
                IdentifierExpr identifier when _globals.TryGetValue(identifier.Name, out var global)
                    => global.TypeName.Clone(),
                IdentifierExpr identifier when ResolveFunctionValueType(identifier.Name, identifier.TypeArguments) is TypeNode functionType
                    => functionType,
                IndexExpr index
                    => GetIndexedElementType(GetCheckedType(index.Target)),
                FieldExpr field when field.ResolvedQualifiedName is string resolvedGlobal &&
                                     _globals.TryGetValue(resolvedGlobal, out var global)
                    => global.TypeName.Clone(),
                FieldExpr field when field.ResolvedQualifiedName is string resolvedFunction &&
                                     ResolveFunctionValueType(resolvedFunction, field.TypeArguments) is TypeNode resolvedFunctionType
                    => resolvedFunctionType,
                FieldExpr field when GetCheckedType(field.Target).IsSlice && field.Field == "len"
                    => new TypeNode { Name = "i64" },
                FieldExpr field when GetCheckedType(field.Target).IsSlice && field.Field == "ptr"
                    => AddressOf(GetIndexedElementType(GetCheckedType(field.Target))),
                FieldExpr field
                    => _typeInterner.GetStructFieldType(GetCheckedType(field.Target), field.Field),
                NumberExpr
                    => new TypeNode { Name = "i64" },
                StringExpr
                    => new TypeNode { Name = "string" },
                BuiltinExpr builtin when builtin.Name is
                    "true" or
                    "false" or
                    "Builtin.IsLinux" or
                    "Builtin.IsWindows" or
                    "Builtin.IsBareMetal" or
                    "Builtin.IsX86_64" or
                    "Builtin.IsAArch64"
                    => new TypeNode { Name = "bool" },
                UnaryExpr { Operator: "!" }
                    => new TypeNode { Name = "bool" },
                UnaryExpr { Operator: "-" } unary
                    => GetCheckedType(unary.Operand),
                UnaryExpr { Operator: "&" } unary
                    => AddressOf(GetCheckedType(unary.Operand)),
                CastExpr cast
                    => cast.TargetType.Clone(),
                BinaryExpr binary when IsComparison(binary.Operator)
                    => new TypeNode { Name = "bool" },
                BinaryExpr binary when binary.Operator is "&&" or "||"
                    => new TypeNode { Name = "bool" },
                BinaryExpr binary
                    => GetCheckedType(binary.Left),
                CallExpr { ResolvedFunctionType: not null } call
                    => call.ResolvedFunctionType!.ReturnType?.Clone()
                       ?? throw Unsupported(call, "call has no return type"),
                CallExpr call when ResolveFunctionType(call) is TypeNode functionType
                    => functionType.ReturnType?.Clone()
                       ?? throw Unsupported(call, "call has no return type"),
                CallExpr { TargetExpr: not null } call
                    when GetCheckedType(call.TargetExpr).IsFunction
                    => GetCheckedType(call.TargetExpr).ReturnType?.Clone()
                       ?? throw Unsupported(call, "call has no return type"),
                ErrorExpr
                    => new TypeNode { Name = "i32" },
                SizeofExpr
                    => new TypeNode { Name = "i64" },
                CatchExpr catchExpression
                    => GetCheckedType(catchExpression.Left).ErrorInnerType?.Clone()
                       ?? throw Unsupported(catchExpression, "catch operand has no success type"),
                _ => throw Unsupported(expression, "missing checked expression type")
            };
        }
        private TypeNode? ResolveFunctionType(CallExpr call)
        {
            var resolvedName = call.ResolvedTargetQualifiedName
                ?? call.ResolvedQualifiedName
                ?? QualifiedNames.GetFullName(call.NamespacePath, call.Name);
            return ResolveFunctionValueType(resolvedName, call.TypeArguments);
        }
        private TypeNode? ResolveFunctionValueType(string resolvedName, IReadOnlyList<TypeNode> typeArguments)
        {
            var loweringName = ResolveFunctionValueLoweringName(resolvedName, typeArguments);
            return _functionTypes.TryGetValue(loweringName, out var type)
                ? type.Clone()
                : null;
        }
        private uint NextValueId()
        {
            return _nextValueId++;
        }
        private static bool IsComparison(string op)
        {
            return op is "==" or "!=" or "<" or "<=" or ">" or ">=";
        }
        private static bool IsBoolType(TypeNode type)
        {
            return !type.IsPointer &&
                   !type.IsSlice &&
                   !type.IsErrorUnion &&
                   !type.IsFunction &&
                   type.ArraySize == null &&
                   type.Name == "bool";
        }
        private static bool IsScalarInteger(TypeNode type)
        {
            return !type.IsPointer &&
                   !type.IsSlice &&
                   !type.IsErrorUnion &&
                   type.ArraySize == null &&
                   type.Name is "bool" or "i8" or "u8" or "i16" or "u16" or
                       "i32" or "u32" or "i64" or "u64";
        }
        private static int GetScalarBitWidth(TypeNode type)
        {
            return type.Name switch
            {
                "i8" or "u8" => 8,
                "i16" or "u16" => 16,
                "bool" or "i32" or "u32" => 32,
                "i64" or "u64" or "size_t" => 64,
                _ => throw new ZorbCompilerException(
                    $"Integer cast lowering does not support type '{FormatType(type)}'.")
            };
        }
        private static bool IsSignedScalar(TypeNode type)
        {
            return type.Name is "i8" or "i16" or "i32" or "i64";
        }
        private static bool IsPointerLike(TypeNode type)
        {
            return type.IsPointer || type.IsFunction || type.Name == "string";
        }
        private static TypeNode GetComparisonOperandType(TypeNode leftType, TypeNode rightType)
        {
            if (!IsScalarInteger(leftType) || !IsScalarInteger(rightType))
                return leftType;

            var promotedLeft = PromoteIntegerForComparison(leftType);
            var promotedRight = PromoteIntegerForComparison(rightType);
            var leftWidth = GetScalarBitWidth(promotedLeft);
            var rightWidth = GetScalarBitWidth(promotedRight);
            var leftSigned = IsSignedScalar(promotedLeft);
            var rightSigned = IsSignedScalar(promotedRight);

            if (leftSigned == rightSigned)
                return leftWidth >= rightWidth ? promotedLeft : promotedRight;

            var signedType = leftSigned ? promotedLeft : promotedRight;
            var unsignedType = leftSigned ? promotedRight : promotedLeft;
            var signedWidth = GetScalarBitWidth(signedType);
            var unsignedWidth = GetScalarBitWidth(unsignedType);

            if (signedWidth > unsignedWidth)
                return signedType;

            return new TypeNode { Name = UnsignedTypeName(Math.Max(signedWidth, unsignedWidth)) };
        }
        private static TypeNode PromoteIntegerForComparison(TypeNode type)
        {
            return GetScalarBitWidth(type) < 32
                ? new TypeNode { Name = "i32" }
                : type;
        }
        private static string UnsignedTypeName(int bitWidth)
        {
            return bitWidth switch
            {
                8 => "u8",
                16 => "u16",
                32 => "u32",
                64 => "u64",
                _ => throw new ZorbCompilerException(
                    $"Unsupported integer width '{bitWidth}' in comparison lowering.")
            };
        }
        private static string? MapBinaryOp(string op, TypeNode operandType)
        {
            if (IsComparison(op))
                return null;

            var signed = operandType.Name.StartsWith('i');
            return op switch
            {
                "+" => "add",
                "-" => "sub",
                "*" => "mul",
                "/" => signed ? "signed_div" : "unsigned_div",
                "%" => signed ? "signed_rem" : "unsigned_rem",
                "&" => "bit_and",
                "|" => "bit_or",
                "^" => "bit_xor",
                "<<" => "shift_left",
                ">>" => signed ? "arithmetic_shift_right" : "logical_shift_right",
                _ => throw new ZorbCompilerException($"Unsupported scalar binary operator '{op}'.")
            };
        }
        private static string? MapCompareOp(string op, TypeNode operandType)
        {
            if (!IsComparison(op))
                return null;

            var signed = operandType.Name.StartsWith('i');
            return op switch
            {
                "==" => "equal",
                "!=" => "not_equal",
                "<" => signed ? "signed_less" : "unsigned_less",
                "<=" => signed ? "signed_less_equal" : "unsigned_less_equal",
                ">" => signed ? "signed_greater" : "unsigned_greater",
                ">=" => signed ? "signed_greater_equal" : "unsigned_greater_equal",
                _ => throw new ZorbCompilerException($"Unsupported scalar comparison operator '{op}'.")
            };
        }
        private static ZorbCompilerException Unsupported(Node node, string detail)
        {
            return new ZorbCompilerException(
                $"Zig backend scalar expression lowering does not support {detail} at {node.File}:{node.Line}:{node.Column}.");
        }
    }
}
