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
        private void LowerStatements(IReadOnlyList<Statement> statements)
        {
            foreach (var statement in statements)
            {
                if (_currentBlock == null)
                    break;
                LowerStatement(statement);
            }
        }
        private void LowerStatement(Statement statement)
        {
            switch (statement)
            {
                case ReturnNode returnNode:
                    LowerReturnStatement(returnNode);
                    return;

                case ExpressionStatement expressionStatement:
                    LowerExpressionStatement(expressionStatement);
                    return;

                case VariableDeclarationNode variable:
                    LowerVariableDeclaration(variable);
                    return;

                case AssignStmt assignment:
                    LowerAssignmentStatement(assignment);
                    return;

                case IfStmt ifStatement:
                    LowerIf(ifStatement);
                    return;

                case WhileStmt whileStatement:
                    LowerWhile(whileStatement);
                    return;

                case ForStmt forStatement:
                    LowerFor(forStatement);
                    return;

                case SwitchStmt switchStatement:
                    LowerSwitch(switchStatement);
                    return;

                case MatchStmt matchStatement:
                    LowerMatch(matchStatement);
                    return;

                case AsmStatementNode asmStatement:
                    LowerAsm(asmStatement);
                    return;

                case BreakStmt:
                    LowerLoopControlStatement(statement, isBreak: true);
                    return;

                case ContinueStmt:
                    LowerLoopControlStatement(statement, isBreak: false);
                    return;

                default:
                    throw Unsupported(statement, statement.GetType().Name);
            }
        }
        private void LowerReturnStatement(ReturnNode returnNode)
        {
            if (returnNode.Value == null)
            {
                Terminate(new BackendTerminator { Op = "return_void" });
                return;
            }

            if (_function.ReturnType.IsErrorUnion)
            {
                LowerErrorUnionReturnStatement(returnNode);
                return;
            }

            LowerValueReturnStatement(returnNode);
        }
        private void LowerErrorUnionReturnStatement(ReturnNode returnNode)
        {
            var innerType = _function.ReturnType.ErrorInnerType
                ?? throw Unsupported(returnNode, "error union has no success type");

            var (successValue, errorValue) = LowerErrorUnionReturnValues(returnNode, innerType);
            var result = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = result,
                Op = "aggregate",
                Type = _typeInterner.Intern(_function.ReturnType),
                Arguments = new List<uint> { successValue, errorValue }
            });
            Terminate(new BackendTerminator { Op = "return_value", Value = result });
        }
        private (uint SuccessValue, uint ErrorValue) LowerErrorUnionReturnValues(ReturnNode returnNode, TypeNode innerType)
        {
            if (returnNode.Value is ErrorExpr error)
                return (EmitZeroConstant(innerType), LowerErrorValue(error));

            if (returnNode.Value is IdentifierExpr identifier &&
                LookupLocal(identifier.Name) is { IsCatchError: true } catchError)
            {
                var loadedError = EmitInstruction("load", catchError.Type, lhs: catchError.Address);
                return (EmitZeroConstant(innerType), loadedError);
            }

            var successType = GetCheckedType(returnNode.Value!);
            var successValue = IsScalarInteger(successType) && IsScalarInteger(innerType)
                ? LowerIntegerOperand(returnNode.Value!, successType, innerType)
                : LowerExpression(returnNode.Value!, innerType);
            var errorValue = EmitIntegerConstant(0, new TypeNode { Name = "i32" });
            return (successValue, errorValue);
        }
        private void LowerValueReturnStatement(ReturnNode returnNode)
        {
            var valueType = _typeChecker.GetCheckedExpressionType(returnNode.Value!)
                ?? _function.ReturnType;
            var value = IsScalarInteger(valueType) && IsScalarInteger(_function.ReturnType)
                ? LowerIntegerOperand(returnNode.Value!, valueType, _function.ReturnType)
                : LowerExpression(returnNode.Value!, _function.ReturnType);
            Terminate(new BackendTerminator { Op = "return_value", Value = value });
        }
        private void LowerExpressionStatement(ExpressionStatement expressionStatement)
        {
            if (expressionStatement.Expression is CatchExpr catchExpression)
                _ = LowerCatch(catchExpression, discardResult: true);
            else
                _ = LowerExpression(expressionStatement.Expression);
        }
        private void LowerVariableDeclaration(VariableDeclarationNode variable)
        {
            var address = EmitInstruction("alloca", variable.TypeName);
            DeclareLocal(variable.Name, variable.TypeName, address);
            if (variable.Value == null)
                return;

            var valueType = GetCheckedType(variable.Value);
            var value = IsScalarInteger(valueType) && IsScalarInteger(variable.TypeName)
                ? LowerIntegerOperand(variable.Value, valueType, variable.TypeName)
                : LowerExpression(variable.Value, variable.TypeName);
            _ = EmitInstruction("store", variable.TypeName, lhs: address, rhs: value);
        }
        private void LowerAssignmentStatement(AssignStmt assignment)
        {
            var targetType = GetCheckedType(assignment.Target);
            var address = LowerAddress(assignment.Target);
            var valueType = GetCheckedType(assignment.Value);
            var value = IsScalarInteger(valueType) && IsScalarInteger(targetType)
                ? LowerIntegerOperand(assignment.Value, valueType, targetType)
                : LowerExpression(assignment.Value, targetType);
            _ = EmitInstruction("store", targetType, lhs: address, rhs: value);
        }
        private void LowerLoopControlStatement(Statement statement, bool isBreak)
        {
            if (_loopTargets.Count == 0)
                throw Unsupported(statement, isBreak ? "break outside a loop" : "continue outside a loop");

            Terminate(new BackendTerminator
            {
                Op = "branch",
                Target = isBreak
                    ? _loopTargets.Peek().BreakBlock
                    : _loopTargets.Peek().ContinueBlock
            });
        }
        private void LowerIf(IfStmt statement)
        {
            if (TryEvaluateTargetCondition(statement.Condition, out var fixedCondition))
            {
                LowerFixedConditionIfBranch(statement, fixedCondition);
                return;
            }

            var condition = LowerExpression(statement.Condition, new TypeNode { Name = "bool" });
            var thenBlock = CreateBlock("if.then");
            var elseBlock = CreateBlock("if.else");
            Terminate(new BackendTerminator
            {
                Op = "conditional_branch",
                Condition = condition,
                TrueTarget = thenBlock.Id,
                FalseTarget = elseBlock.Id
            });

            var (thenFallsThrough, thenEnd) = LowerIfBranchBody(thenBlock, statement.Body);
            var (elseFallsThrough, elseEnd) = LowerIfBranchBody(elseBlock, statement.ElseBody);

            if (!thenFallsThrough && !elseFallsThrough)
            {
                _currentBlock = null;
                return;
            }

            FinalizeIfMergeBlock(thenEnd, elseEnd);
        }
        private void LowerFixedConditionIfBranch(IfStmt statement, bool fixedCondition)
        {
            PushScope();
            LowerStatements(fixedCondition ? statement.Body : statement.ElseBody);
            PopScope();
        }
        private (bool FallsThrough, BackendBlock? EndBlock) LowerIfBranchBody(
            BackendBlock startBlock,
            IReadOnlyList<Statement> statements)
        {
            _currentBlock = startBlock;
            PushScope();
            LowerStatements(statements);
            PopScope();
            return (_currentBlock != null, _currentBlock);
        }
        private void FinalizeIfMergeBlock(BackendBlock? thenEnd, BackendBlock? elseEnd)
        {
            var mergeBlock = CreateBlock("if.end");
            if (thenEnd != null)
            {
                _currentBlock = thenEnd;
                Terminate(new BackendTerminator { Op = "branch", Target = mergeBlock.Id });
            }
            if (elseEnd != null)
            {
                _currentBlock = elseEnd;
                Terminate(new BackendTerminator { Op = "branch", Target = mergeBlock.Id });
            }
            _currentBlock = mergeBlock;
        }
        private void LowerAsm(AsmStatementNode statement)
        {
            var outputTypes = new List<uint>(statement.Outputs.Count);
            var outputAddresses = new List<uint>(statement.Outputs.Count);
            foreach (var output in statement.Outputs)
            {
                outputTypes.Add(_typeInterner.Intern(GetCheckedType(output.Expression)));
                outputAddresses.Add(LowerAddress(output.Expression));
            }

            var arguments = statement.Inputs
                .Select(input =>
                {
                    var inputType = GetCheckedType(input.Expression);
                    var value = LowerExpression(input.Expression, inputType);
                    if (IsScalarInteger(inputType) && GetScalarBitWidth(inputType) < 32)
                    {
                        value = CoerceInteger(
                            value,
                            inputType,
                            new TypeNode { Name = inputType.Name.StartsWith('i') ? "i32" : "u32" });
                    }
                    return value;
                })
                .ToList();
            var constraints = statement.Outputs.Select(output => ConvertAsmConstraint(output.Constraint))
                .Concat(statement.Inputs.Select(input => ConvertAsmConstraint(input.Constraint)))
                .Concat(statement.Clobbers.Select(clobber => $"~{{{clobber}}}"));

            var id = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = id,
                Op = "inline_asm",
                Type = _typeInterner.Intern(new TypeNode { Name = "void" }),
                AsmTemplate = ConvertAsmTemplate(string.Join("\n\t", statement.Code)),
                Constraints = string.Join(",", constraints),
                Arguments = arguments,
                OutputTypes = outputTypes,
                OutputAddresses = outputAddresses
            });
        }
        private static string ConvertAsmTemplate(string template)
        {
            const string percentSentinel = "\u0001";
            var converted = template
                .Replace("%%", percentSentinel, StringComparison.Ordinal)
                .Replace("$", "$$", StringComparison.Ordinal);
            converted = System.Text.RegularExpressions.Regex.Replace(
                converted,
                @"%b([0-9]+)",
                match => $"${{{match.Groups[1].Value}:b}}");
            converted = System.Text.RegularExpressions.Regex.Replace(
                converted,
                @"%([0-9]+)",
                match => $"${match.Groups[1].Value}");
            return converted.Replace(percentSentinel, "%", StringComparison.Ordinal);
        }
        private static string ConvertAsmConstraint(string constraint)
        {
            var prefixLength = 0;
            while (prefixLength < constraint.Length &&
                   constraint[prefixLength] is '=' or '+' or '&')
            {
                prefixLength++;
            }
            var prefix = constraint[..prefixLength];
            var body = constraint[prefixLength..];
            var register = body switch
            {
                "a" => "ax",
                "b" => "bx",
                "c" => "cx",
                "d" => "dx",
                "S" => "si",
                "D" => "di",
                _ => null
            };
            return register == null ? constraint : $"{prefix}{{{register}}}";
        }
        private bool TryEvaluateTargetCondition(Expr expression, out bool value)
        {
            switch (expression)
            {
                case BuiltinExpr builtin when builtin.Name is
                    "true" or
                    "false" or
                    "Builtin.IsLinux" or
                    "Builtin.IsWindows" or
                    "Builtin.IsBareMetal" or
                    "Builtin.IsX86_64" or
                    "Builtin.IsAArch64":
                    value = GetBuiltinValue(builtin.Name);
                    return true;

                case UnaryExpr { Operator: "!" } unary
                    when TryEvaluateTargetCondition(unary.Operand, out var operand):
                    value = !operand;
                    return true;

                case BinaryExpr { Operator: "&&" } binary
                    when TryEvaluateTargetCondition(binary.Left, out var left)
                         && TryEvaluateTargetCondition(binary.Right, out var right):
                    value = left && right;
                    return true;

                case BinaryExpr { Operator: "||" } binary
                    when TryEvaluateTargetCondition(binary.Left, out var left)
                         && TryEvaluateTargetCondition(binary.Right, out var right):
                    value = left || right;
                    return true;

                default:
                    value = false;
                    return false;
            }
        }
        private void LowerWhile(WhileStmt statement)
        {
            var conditionBlock = CreateBlock("while.cond");
            var bodyBlock = CreateBlock("while.body");
            var exitBlock = CreateBlock("while.end");
            Terminate(new BackendTerminator { Op = "branch", Target = conditionBlock.Id });

            LowerWhileCondition(statement, conditionBlock, bodyBlock, exitBlock);

            _loopTargets.Push(new LoopTargets(exitBlock.Id, conditionBlock.Id));
            LowerWhileBody(statement, bodyBlock, conditionBlock);
            _loopTargets.Pop();

            _currentBlock = exitBlock;
        }
        private void LowerWhileCondition(
            WhileStmt statement,
            BackendBlock conditionBlock,
            BackendBlock bodyBlock,
            BackendBlock exitBlock)
        {
            _currentBlock = conditionBlock;
            var condition = LowerExpression(statement.Condition, new TypeNode { Name = "bool" });
            Terminate(new BackendTerminator
            {
                Op = "conditional_branch",
                Condition = condition,
                TrueTarget = bodyBlock.Id,
                FalseTarget = exitBlock.Id
            });
        }
        private void LowerWhileBody(
            WhileStmt statement,
            BackendBlock bodyBlock,
            BackendBlock conditionBlock)
        {
            _currentBlock = bodyBlock;
            PushScope();
            LowerStatements(statement.Body);
            PopScope();
            if (_currentBlock != null)
                Terminate(new BackendTerminator { Op = "branch", Target = conditionBlock.Id });
        }
        private void LowerFor(ForStmt statement)
        {
            PushScope();
            if (!LowerForInitializer(statement))
            {
                PopScope();
                return;
            }

            var conditionBlock = CreateBlock("for.cond");
            var bodyBlock = CreateBlock("for.body");
            var updateBlock = CreateBlock("for.update");
            var exitBlock = CreateBlock("for.end");
            Terminate(new BackendTerminator { Op = "branch", Target = conditionBlock.Id });

            _currentBlock = conditionBlock;
            var condition = LowerForCondition(statement);
            Terminate(new BackendTerminator
            {
                Op = "conditional_branch",
                Condition = condition,
                TrueTarget = bodyBlock.Id,
                FalseTarget = exitBlock.Id
            });

            _loopTargets.Push(new LoopTargets(exitBlock.Id, updateBlock.Id));
            _currentBlock = bodyBlock;
            PushScope();
            LowerStatements(statement.Body);
            PopScope();
            if (_currentBlock != null)
                Terminate(new BackendTerminator { Op = "branch", Target = updateBlock.Id });

            _currentBlock = updateBlock;
            LowerForUpdate(statement, conditionBlock);
            _loopTargets.Pop();

            _currentBlock = exitBlock;
            PopScope();
        }
        private bool LowerForInitializer(ForStmt statement)
        {
            if (statement.Initializer != null)
                LowerStatement(statement.Initializer);

            return _currentBlock != null;
        }
        private uint LowerForCondition(ForStmt statement)
        {
            return statement.Condition != null
                ? LowerExpression(statement.Condition, new TypeNode { Name = "bool" })
                : EmitIntegerConstant(1, new TypeNode { Name = "bool" });
        }
        private void LowerForUpdate(ForStmt statement, BackendBlock conditionBlock)
        {
            if (statement.Update != null)
                LowerStatement(statement.Update);

            if (_currentBlock != null)
                Terminate(new BackendTerminator { Op = "branch", Target = conditionBlock.Id });
        }
        private void LowerSwitch(SwitchStmt statement)
        {
            var switchType = GetCheckedType(statement.Expression);
            var switchValue = LowerExpression(statement.Expression, switchType);
            var exitBlock = CreateBlock("switch.end");
            var hasExitPredecessor = false;

            foreach (var switchCase in statement.Cases)
            {
                if (LowerSwitchCase(switchCase, switchType, switchValue, exitBlock))
                    hasExitPredecessor = true;
            }

            if (LowerSwitchElseBody(statement, switchType, exitBlock))
                hasExitPredecessor = true;

            FinalizeSwitchExitBlock(exitBlock, hasExitPredecessor);
        }
        private bool LowerSwitchCase(
            SwitchCase switchCase,
            TypeNode switchType,
            uint switchValue,
            BackendBlock exitBlock)
        {
            var caseBody = CreateBlock("switch.case");
            var nextCase = CreateBlock("switch.next");
            var caseValue = LowerExpression(switchCase.Value, switchType);
            var comparison = EmitComparison("equal", switchValue, caseValue);
            Terminate(new BackendTerminator
            {
                Op = "conditional_branch",
                Condition = comparison,
                TrueTarget = caseBody.Id,
                FalseTarget = nextCase.Id
            });

            _currentBlock = caseBody;
            PushScope();
            LowerStatements(switchCase.Body);
            PopScope();
            var hasExitPredecessor = TryTerminateSwitchCaseBody(exitBlock);
            _currentBlock = nextCase;
            return hasExitPredecessor;
        }
        private bool TryTerminateSwitchCaseBody(BackendBlock exitBlock)
        {
            if (_currentBlock == null)
                return false;

            Terminate(new BackendTerminator { Op = "branch", Target = exitBlock.Id });
            return true;
        }
        private bool LowerSwitchElseBody(
            SwitchStmt statement,
            TypeNode switchType,
            BackendBlock exitBlock)
        {
            PushScope();
            LowerStatements(statement.ElseBody);
            PopScope();
            if (_currentBlock == null)
                return false;

            if (statement.ElseBody.Count == 0 && _typeInterner.IsEnumType(switchType))
            {
                Terminate(new BackendTerminator { Op = "unreachable" });
                return false;
            }

            Terminate(new BackendTerminator { Op = "branch", Target = exitBlock.Id });
            return true;
        }
        private void FinalizeSwitchExitBlock(BackendBlock exitBlock, bool hasExitPredecessor)
        {
            if (hasExitPredecessor)
            {
                _currentBlock = exitBlock;
                return;
            }

            _blocks.Remove(exitBlock);
            _currentBlock = null;
        }
        private void LowerMatch(MatchStmt statement)
        {
            var matchType = GetCheckedType(statement.Expression);
            var matchValue = LowerExpression(statement.Expression, matchType);
            if (_typeInterner.IsUnionType(matchType))
            {
                LowerUnionMatch(statement, matchType, matchValue);
                return;
            }

            LowerScalarMatch(statement, matchType, matchValue);
        }
        private void LowerScalarMatch(MatchStmt statement, TypeNode matchType, uint matchValue)
        {
            var cases = statement.Cases.Select(matchCase =>
            {
                if (matchCase.Pattern is not QualifiedMatchPattern pattern)
                    throw Unsupported(matchCase.Pattern, "non-scalar match pattern");
                return (Value: pattern.Value, matchCase.Body);
            }).ToList();
            LowerOrderedCases(
                cases.Select(item => (
                    CaseValue: LowerExpression(item.Value, matchType),
                    item.Body,
                    Binding: (Action?)null)).ToList(),
                matchValue,
                statement.ElseBody,
                exhaustiveWithoutElse: statement.ElseBody.Count == 0 &&
                    (_typeInterner.IsEnumType(matchType) || IsBoolType(matchType)));
        }
        private void LowerUnionMatch(MatchStmt statement, TypeNode matchType, uint matchValue)
        {
            var tagType = new TypeNode { Name = "i32" };
            var tag = LowerUnionMatchTag(matchValue, tagType);
            var cases = statement.Cases
                .Select(matchCase => BuildUnionMatchCase(matchCase, matchType, matchValue, tagType))
                .ToList();
            LowerOrderedCases(cases, tag, statement.ElseBody, statement.ElseBody.Count == 0);
        }
        private uint LowerUnionMatchTag(uint matchValue, TypeNode tagType)
        {
            var tag = NextValueId();
            AddInstruction(new BackendInstruction
            {
                Id = tag,
                Op = "extract_value",
                Type = _typeInterner.Intern(tagType),
                Lhs = matchValue,
                FieldIndex = 0
            });
            return tag;
        }
        private (uint CaseValue, List<Statement> Body, Action? Binding) BuildUnionMatchCase(
            MatchCase matchCase,
            TypeNode matchType,
            uint matchValue,
            TypeNode tagType)
        {
            var (variantName, bindingName, bindingType) = ResolveUnionMatchPattern(matchCase);
            var variantIndex = _typeInterner.GetUnionVariantIndex(matchType, variantName);
            var binding = CreateUnionMatchBinding(
                matchType,
                matchValue,
                variantName,
                variantIndex,
                bindingName,
                bindingType);
            return (
                EmitIntegerConstant(variantIndex, tagType),
                matchCase.Body,
                binding);
        }
        private (string VariantName, string? BindingName, TypeNode? BindingType) ResolveUnionMatchPattern(
            MatchCase matchCase)
        {
            if (matchCase.Pattern is UnionMatchPattern unionPattern &&
                unionPattern.VariantName != null)
            {
                return (unionPattern.VariantName, unionPattern.BindingName, unionPattern.BindingType);
            }

            if (matchCase.Pattern is QualifiedMatchPattern qualified &&
                QualifiedNames.TryGetQualifiedName(qualified.Value) is string name)
            {
                return (name[(name.LastIndexOf('.') + 1)..], null, null);
            }

            throw Unsupported(matchCase.Pattern, "invalid union match pattern");
        }
        private Action? CreateUnionMatchBinding(
            TypeNode matchType,
            uint matchValue,
            string variantName,
            uint variantIndex,
            string? bindingName,
            TypeNode? bindingType)
        {
            if (bindingName == null)
                return null;

            var capturedName = bindingName;
            var capturedType = bindingType
                ?? _typeInterner.GetUnionVariantType(matchType, variantName);
            return () =>
            {
                var payload = NextValueId();
                AddInstruction(new BackendInstruction
                {
                    Id = payload,
                    Op = "extract_value",
                    Type = _typeInterner.Intern(capturedType),
                    Lhs = matchValue,
                    FieldIndex = variantIndex + 1
                });
                var address = EmitInstruction("alloca", capturedType);
                _ = EmitInstruction("store", capturedType, lhs: address, rhs: payload);
                DeclareLocal(capturedName, capturedType, address);
            };
        }
        private void LowerOrderedCases(
            IReadOnlyList<(uint CaseValue, List<Statement> Body, Action? Binding)> cases,
            uint comparedValue,
            IReadOnlyList<Statement> elseBody,
            bool exhaustiveWithoutElse)
        {
            var exitBlock = CreateBlock("match.end");
            var hasExitPredecessor = false;
            foreach (var matchCase in cases)
            {
                if (LowerOrderedCase(matchCase, comparedValue, exitBlock))
                    hasExitPredecessor = true;
            }

            if (LowerOrderedCasesElseBody(elseBody, exhaustiveWithoutElse, exitBlock))
                hasExitPredecessor = true;

            FinalizeOrderedCasesExitBlock(exitBlock, hasExitPredecessor);
        }
        private bool LowerOrderedCase(
            (uint CaseValue, List<Statement> Body, Action? Binding) matchCase,
            uint comparedValue,
            BackendBlock exitBlock)
        {
            var caseBody = CreateBlock("match.case");
            var nextCase = CreateBlock("match.next");
            var comparison = EmitComparison("equal", comparedValue, matchCase.CaseValue);
            Terminate(new BackendTerminator
            {
                Op = "conditional_branch",
                Condition = comparison,
                TrueTarget = caseBody.Id,
                FalseTarget = nextCase.Id
            });

            _currentBlock = caseBody;
            PushScope();
            matchCase.Binding?.Invoke();
            LowerStatements(matchCase.Body);
            PopScope();
            var hasExitPredecessor = TryTerminateOrderedCaseBody(exitBlock);
            _currentBlock = nextCase;
            return hasExitPredecessor;
        }
        private bool TryTerminateOrderedCaseBody(BackendBlock exitBlock)
        {
            if (_currentBlock == null)
                return false;

            Terminate(new BackendTerminator { Op = "branch", Target = exitBlock.Id });
            return true;
        }
        private bool LowerOrderedCasesElseBody(
            IReadOnlyList<Statement> elseBody,
            bool exhaustiveWithoutElse,
            BackendBlock exitBlock)
        {
            PushScope();
            LowerStatements(elseBody);
            PopScope();
            if (_currentBlock == null)
                return false;

            if (exhaustiveWithoutElse)
            {
                Terminate(new BackendTerminator { Op = "unreachable" });
                return false;
            }

            Terminate(new BackendTerminator { Op = "branch", Target = exitBlock.Id });
            return true;
        }
        private void FinalizeOrderedCasesExitBlock(BackendBlock exitBlock, bool hasExitPredecessor)
        {
            if (hasExitPredecessor)
            {
                _currentBlock = exitBlock;
                return;
            }

            _blocks.Remove(exitBlock);
            _currentBlock = null;
        }
    }
}
