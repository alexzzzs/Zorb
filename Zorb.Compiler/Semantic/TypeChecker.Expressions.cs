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
    private void CheckAssignment(AssignStmt assign)
    {
        assign.Target = NormalizeAliasReferences(assign.Target);
        assign.Value = NormalizeAliasReferences(assign.Value);
        CheckExpression(assign.Target);
        CheckExpression(assign.Value);

        if (!IsAssignableExpression(assign.Target))
            _errors.Error(assign.Target, "Assignment target must be an identifier, field, or index expression.");

        if (TryGetAssignmentRootName(assign.Target, out var rootName) &&
            _symbolTable.TryLookup(rootName, out var rootInfo) &&
            rootInfo?.IsConst == true)
        {
            _errors.Error(assign.Target, $"Cannot assign to const declaration '{rootName}'.");
        }

        var targetType = GetExpressionType(assign.Target);
        var valueType = GetExpressionType(assign.Value);

        if (targetType == null || valueType == null)
            return;

        if (!IsAssignableTo(targetType, assign.Value, valueType))
        {
            if (targetType.IsErrorUnion && valueType != null && !valueType.IsErrorUnion)
            {
                _errors.Error(assign, $"Cannot assign value of type '{FormatType(valueType)}' to '{FormatType(targetType)}'. Assign the whole error union or unwrap explicitly with '.value'.");
            }
            else if (TryBuildNumericConversionDiagnostic(targetType, assign.Value, valueType, out var numericDiagnostic))
            {
                _errors.Error(assign, $"Cannot assign expression of type '{FormatType(valueType)}' to target of type '{FormatType(targetType)}'. {numericDiagnostic}");
            }
            else
            {
                _errors.Error(assign, $"Cannot assign expression of type '{FormatType(valueType)}' to target of type '{FormatType(targetType)}'.");
            }
        }

        if (targetType!.Name == "u8" && targetType.IsPointer && valueType!.IsPointer && valueType.PointerLevel == 1)
        {
            if (assign.Value is UnaryExpr unary && unary.Operator == "&")
            {
                _errors.Warning(assign.Value, "Casting *u8 to **u8 may cause crashes if the address is not 8-byte aligned. Consider using mem.Align64 first.");
            }
        }
    }
    private void CheckExpression(Expr expr)
    {
        switch (expr)
        {
            case BinaryExpr bin:
                CheckBinaryExpressionCase(bin);
                break;

            case CallExpr call:
                CheckCallExpressionCase(call);
                break;

            case IdentifierExpr ident:
                CheckIdentifierExpression(ident);
                break;

            case TypeReferenceExpr typeReference:
                CheckTypeReferenceExpression(typeReference);
                break;

            case ErrorNamespaceExpr:
                CheckErrorNamespaceExpression(expr);
                break;

            case IndexExpr idx:
                CheckIndexExpression(idx);
                break;

            case FieldExpr field:
                CheckFieldExpression(field);
                break;

            case UnaryExpr un:
                CheckUnaryExpression(un);
                break;

            case CastExpr cast:
                CheckCastExpression(cast);
                break;

            case StructLiteralExpr structLiteral:
                CheckStructLiteralExpressionCase(structLiteral);
                break;

            case ArrayLiteralExpr arrayLiteral:
                CheckArrayLiteralExpressionCase(arrayLiteral);
                break;

            case CatchExpr catchExpr:
                CheckCatchExpressionCase(catchExpr);
                break;

            case SizeofExpr sizeofExpr:
                CheckSizeofExpression(sizeofExpr);
                break;

            case BuiltinExpr builtin:
                CheckBuiltinExpression(builtin);
                break;

            case ErrorExpr errorExpr:
                CheckErrorExpression(errorExpr);
                break;

            case InvalidExpr:
                CheckInvalidExpression();
                break;
        }
    }
    private void CheckBinaryExpressionCase(BinaryExpr binaryExpression)
    {
        CheckBinaryExpression(binaryExpression);
    }
    private void CheckCallExpressionCase(CallExpr callExpression)
    {
        CheckCallExpression(callExpression);
    }
    private void CheckIdentifierExpression(IdentifierExpr identifier)
    {
        identifier.Name = ResolveQualifiedName(identifier.Name);
        if (!_symbolTable.IsDefined(identifier.Name))
        {
            _errors.Error(identifier, $"Use of undeclared identifier '{identifier.Name}'.");
            return;
        }

        if (!CheckVisibility(identifier.Name))
        {
            ReportNotVisible(identifier, "Symbol", identifier.Name);
            return;
        }

        if (_symbolTable.TryLookup(identifier.Name, out var symbolInfo) && symbolInfo!.Kind == SymbolKind.Enum)
        {
            _errors.Error(identifier, $"Enum type '{identifier.Name}' is not a value. Use a member such as '{identifier.Name}.Member'.");
            return;
        }

        if (_symbolTable.TryLookup(identifier.Name, out symbolInfo) && symbolInfo!.Kind == SymbolKind.Union)
            _errors.Error(identifier, $"Union type '{identifier.Name}' is not a value. Construct it with a literal such as '{identifier.Name}{{ Variant: value }}'.");
    }
    private void CheckTypeReferenceExpression(TypeReferenceExpr typeReference)
    {
        ValidateTypeReference(typeReference.TypeName, typeReference);
    }
    private void CheckErrorNamespaceExpression(Expr errorNamespaceExpression)
    {
        _errors.Error(errorNamespaceExpression, "Expected '.Name' after 'error' in expression.");
    }
    private void CheckIndexExpression(IndexExpr indexExpression)
    {
        if (IsInvalidPostfixTarget(indexExpression.Target))
            return;

        CheckExpression(indexExpression.Target);
        CheckExpression(indexExpression.Index);
        var indexType = GetExpressionType(indexExpression.Index, reportErrors: false);
        if (indexType != null && !IsIntegerScalarType(indexType))
            _errors.Error(indexExpression.Index, $"Index expression must have an integer type, got '{FormatType(indexType)}'.");
        var indexTargetType = GetExpressionType(indexExpression.Target, reportErrors: false);
        if (indexTargetType != null && indexTargetType.ArraySize == null && !indexTargetType.IsPointer && !indexTargetType.IsSlice)
            _errors.Error(indexExpression.Target, $"Cannot index expression of type '{FormatType(indexTargetType)}'.");
    }
    private void CheckFieldExpression(FieldExpr fieldExpression)
    {
        if (IsInvalidPostfixTarget(fieldExpression.Target))
            return;

        if (TryResolveStaticEnumMember(fieldExpression, out _, out _))
            return;

        if (CheckInvalidStaticFieldAccess(fieldExpression))
            return;

        if (CheckResolvedQualifiedField(fieldExpression))
            return;

        CheckExpression(fieldExpression.Target);
        var fieldTargetType = GetExpressionType(fieldExpression.Target, reportErrors: false);
        if (fieldTargetType != null && fieldTargetType.IsSlice && fieldExpression.Field is not ("ptr" or "len"))
            _errors.Error(fieldExpression, $"Slice values expose only '.ptr' and '.len', not '{fieldExpression.Field}'.");
        else if (IsUnionType(fieldTargetType) && !IsValidUnionField(fieldTargetType!, fieldExpression.Field))
            _errors.Error(fieldExpression, $"Union values expose '.tag' and declared variant fields. '{FormatType(fieldTargetType)}' does not have '{fieldExpression.Field}'.");
    }
    private bool CheckInvalidStaticFieldAccess(FieldExpr fieldExpression)
    {
        if (!TryResolveStaticTypeReference(fieldExpression.Target, out var staticOwnerType))
            return false;

        if (IsEnumType(staticOwnerType))
        {
            _errors.Error(fieldExpression, $"Enum '{FormatType(staticOwnerType)}' does not have a member named '{fieldExpression.Field}'.");
            return true;
        }

        if (IsUnionType(staticOwnerType) && fieldExpression.Field != "Tag")
        {
            _errors.Error(fieldExpression, $"Union type '{FormatType(staticOwnerType)}' exposes only '.Tag' in static member position.");
            return true;
        }

        return false;
    }
    private bool CheckResolvedQualifiedField(FieldExpr fieldExpression)
    {
        if (ResolveQualifiedFieldSymbol(fieldExpression) is not ResolvedFieldSymbolInfo resolvedField)
            return false;

        if (TryCheckValueLikeResolvedField(fieldExpression, resolvedField, out var handledValueLike))
            return handledValueLike;

        if (!IsResolvedEnumOrUnionField(resolvedField))
            return false;

        CheckResolvedTypeFieldUsage(fieldExpression, resolvedField);
        return true;
    }
    private bool TryCheckValueLikeResolvedField(
        FieldExpr fieldExpression,
        ResolvedFieldSymbolInfo resolvedField,
        out bool handled)
    {
        if (resolvedField.SymbolInfo.Kind != SymbolKind.Variable &&
            resolvedField.SymbolInfo.Kind != SymbolKind.Function)
        {
            handled = false;
            return false;
        }

        if (!IsVisibleResolvedFieldSymbol(resolvedField))
            ReportNotVisible(fieldExpression, "Symbol", resolvedField.ResolvedName);

        handled = true;
        return true;
    }
    private static bool IsResolvedEnumOrUnionField(ResolvedFieldSymbolInfo resolvedField)
    {
        return resolvedField.SymbolInfo.Kind == SymbolKind.Enum ||
            resolvedField.SymbolInfo.Kind == SymbolKind.Union;
    }
    private void CheckResolvedTypeFieldUsage(FieldExpr fieldExpression, ResolvedFieldSymbolInfo resolvedField)
    {
        if (!IsVisibleResolvedFieldSymbol(resolvedField))
        {
            ReportNotVisible(
                fieldExpression,
                GetResolvedTypeFieldSubjectKind(resolvedField),
                resolvedField.ResolvedName);
            return;
        }

        ReportResolvedTypeFieldValueError(fieldExpression, resolvedField);
    }
    private static string GetResolvedTypeFieldSubjectKind(ResolvedFieldSymbolInfo resolvedField)
    {
        return resolvedField.SymbolInfo.Kind == SymbolKind.Enum ? "Enum" : "Union";
    }
    private void ReportResolvedTypeFieldValueError(FieldExpr fieldExpression, ResolvedFieldSymbolInfo resolvedField)
    {
        if (resolvedField.SymbolInfo.Kind == SymbolKind.Enum)
        {
            _errors.Error(fieldExpression, $"Enum type '{resolvedField.ResolvedName}' is not a value. Use a member such as '{resolvedField.ResolvedName}.Member'.");
            return;
        }

        _errors.Error(fieldExpression, $"Union type '{resolvedField.ResolvedName}' is not a value. Construct it with a literal such as '{resolvedField.ResolvedName}{{ Variant: value }}'.");
    }
    private void CheckUnaryExpression(UnaryExpr unaryExpression)
    {
        CheckExpression(unaryExpression.Operand);
        var unaryOperandType = GetExpressionType(unaryExpression.Operand, reportErrors: false);
        if (unaryOperandType == null)
            return;

        if (unaryExpression.Operator == "!" && !IsBoolType(unaryOperandType))
            _errors.Error(unaryExpression, $"Operator '!' requires a bool operand, got '{FormatType(unaryOperandType)}'.");
        else if (unaryExpression.Operator == "-" && !IsNumericType(unaryOperandType))
            _errors.Error(unaryExpression, $"Operator '-' requires a numeric operand, got '{FormatType(unaryOperandType)}'.");
        else if (unaryExpression.Operator == "&" && !IsAssignableExpression(unaryExpression.Operand))
            _errors.Error(unaryExpression, "Operator '&' requires an assignable operand.");
    }
    private void CheckCastExpression(CastExpr castExpression)
    {
        CheckExpression(castExpression.Expr);
        ValidateTypeReference(castExpression.TargetType, castExpression);
        var sourceType = GetExpressionType(castExpression.Expr);
        if (sourceType != null && sourceType.IsErrorUnion && !castExpression.TargetType.IsPointer && !castExpression.TargetType.IsErrorUnion)
            _errors.Error(castExpression, "Cannot cast Error Union to non-pointer type. Use .value field to unwrap.");
    }
    private void CheckStructLiteralExpressionCase(StructLiteralExpr structLiteral)
    {
        CheckStructLiteralExpression(structLiteral);
    }
    private void CheckArrayLiteralExpressionCase(ArrayLiteralExpr arrayLiteral)
    {
        CheckArrayLiteralExpression(arrayLiteral);
    }
    private void CheckCatchExpressionCase(CatchExpr catchExpression)
    {
        CheckCatchExpression(catchExpression);
    }
    private void CheckSizeofExpression(SizeofExpr sizeofExpression)
    {
        ValidateTypeReference(sizeofExpression.TargetType, sizeofExpression);
    }
    private static void CheckBuiltinExpression(BuiltinExpr builtinExpression)
    {
    }
    private void CheckErrorExpression(ErrorExpr errorExpression)
    {
        if (!TryResolveDeclaredErrorSymbol(errorExpression.ErrorCode, out _))
            _errors.Error(errorExpression, $"Use of undeclared error 'error.{errorExpression.ErrorCode}'. Declare it first with 'error {errorExpression.ErrorCode} = ...'.");
    }
    private static void CheckInvalidExpression()
    {
    }
    private void CheckCatchExpression(CatchExpr catchExpr, bool allowStatementFallthroughWithoutValue = false)
    {
        CheckExpression(catchExpr.Left);
        var catchType = GetExpressionType(catchExpr.Left);
        if (catchType == null || !catchType.IsErrorUnion)
        {
            _errors.Error(catchExpr, "Catch requires an error-union expression");
            return;
        }

        PushScopedState();
        try
        {
            _symbolTable.DefineVariable(catchExpr.ErrorVar, new TypeNode { Name = "i32" });
            _catchErrorVarScopes.Peek().Add(catchExpr.ErrorVar);
            var catchFlow = FlowOutcome.FallsThrough;
            foreach (var stmt in catchExpr.CatchBody)
            {
                if (catchFlow != FlowOutcome.FallsThrough)
                {
                    _errors.Warning(stmt, "Unreachable statement.");
                    break;
                }

                catchFlow = CheckStatement(stmt);
            }

            if (catchFlow != FlowOutcome.FallsThrough || allowStatementFallthroughWithoutValue)
                return;

            if (catchExpr.CatchBody.LastOrDefault() is not ExpressionStatement fallback)
            {
                _errors.Error(catchExpr, "Catch body must end with a fallback expression or transfer control with return, break, or continue.");
                return;
            }

            var successType = catchType.ErrorInnerType ?? catchType;
            var fallbackType = GetExpressionType(fallback.Expression);
            if (!IsAssignableTo(successType, fallback.Expression, fallbackType))
            {
                _errors.Error(
                    fallback.Expression,
                    $"Catch fallback expression of type '{FormatType(fallbackType)}' is not assignable to '{FormatType(successType)}'.");
            }
        }
        finally
        {
            PopScopedState();
        }
    }
    private void CheckAsmStatement(AsmStatementNode asmStmt)
    {
        foreach (var output in asmStmt.Outputs)
            CheckAsmOperand(output, isOutput: true);

        foreach (var input in asmStmt.Inputs)
            CheckAsmOperand(input, isOutput: false);
    }
    private void CheckAsmOperand(AsmOperand operand, bool isOutput)
    {
        CheckExpression(operand.Expression);

        if (ContainsCatchExpression(operand.Expression))
        {
            _errors.Error(operand.Expression, "Inline asm operands cannot contain catch expressions.");
            return;
        }

        var operandType = GetExpressionType(operand.Expression);
        if (!IsValidAsmOperandType(operandType))
        {
            var role = isOutput ? "output" : "input";
            _errors.Error(operand.Expression, $"Inline asm {role} operands must have scalar or pointer type, got '{FormatType(operandType)}'.");
            return;
        }

        if (isOutput && !IsAssignableExpression(operand.Expression))
        {
            _errors.Error(operand.Expression, "Inline asm output operands must be assignable expressions.");
        }
    }
    private static bool ContainsCatchExpression(Expr expr)
    {
        switch (expr)
        {
            case CatchExpr:
                return true;
            case BinaryExpr bin:
                return ContainsCatchExpression(bin.Left) || ContainsCatchExpression(bin.Right);
            case CallExpr call:
                return (call.TargetExpr != null && ContainsCatchExpression(call.TargetExpr))
                    || call.Args.Any(ContainsCatchExpression);
            case IndexExpr idx:
                return ContainsCatchExpression(idx.Target) || ContainsCatchExpression(idx.Index);
            case FieldExpr field:
                return ContainsCatchExpression(field.Target);
            case UnaryExpr unary:
                return ContainsCatchExpression(unary.Operand);
            case CastExpr cast:
                return ContainsCatchExpression(cast.Expr);
            case StructLiteralExpr structLiteral:
                return structLiteral.Fields.Any(field => ContainsCatchExpression(field.Value));
            case ArrayLiteralExpr arrayLiteral:
                return arrayLiteral.Elements.Any(ContainsCatchExpression);
            default:
                return false;
        }
    }
    private void CheckVariableInitializer(VariableDeclarationNode varDecl)
    {
        if (!ValidateVariableInitializerPresence(varDecl))
            return;

        varDecl.Value = NormalizeAliasReferences(varDecl.Value!);

        if (!ValidateGlobalInitializerRestrictions(varDecl))
            return;

        CheckExpression(varDecl.Value);

        if (!ValidateVariableInitializerAssignability(varDecl))
            return;

        FoldGlobalIntegerInitializer(varDecl);
    }
    private bool ValidateVariableInitializerPresence(VariableDeclarationNode varDecl)
    {
        if (varDecl.Value != null)
            return true;

        if (varDecl.IsConst)
            _errors.Error(varDecl, $"Const declaration '{varDecl.Name}' requires an initializer.");

        return false;
    }
    private bool ValidateGlobalInitializerRestrictions(VariableDeclarationNode varDecl)
    {
        if (_currentFunction != null)
            return true;

        if (ContainsCatchExpression(varDecl.Value!))
        {
            _errors.Error(varDecl.Value!, "Catch expressions are not supported in global initializers.");
            return false;
        }

        if (varDecl.TypeName.ArraySize != null && varDecl.Value is not ArrayLiteralExpr)
        {
            _errors.Error(varDecl, "Global array initializers must use array literals.");
            return false;
        }

        return true;
    }
    private bool ValidateVariableInitializerAssignability(VariableDeclarationNode varDecl)
    {
        var exprType = GetExpressionType(varDecl.Value!);
        if (IsAssignableTo(varDecl.TypeName, varDecl.Value, exprType))
            return true;

        ReportVariableInitializerAssignabilityError(varDecl, exprType);
        return false;
    }
    private void ReportVariableInitializerAssignabilityError(VariableDeclarationNode varDecl, TypeNode? exprType)
    {
        if (TryBuildNumericConversionDiagnostic(varDecl.TypeName, varDecl.Value!, exprType, out var numericDiagnostic))
        {
            _errors.Error(varDecl, $"Cannot initialize variable '{varDecl.Name}' of type '{FormatType(varDecl.TypeName)}' from expression of type '{FormatType(exprType)}'. {numericDiagnostic}");
            return;
        }

        _errors.Error(varDecl, $"Cannot assign expression of type '{FormatType(exprType)}' to variable '{varDecl.Name}' of type '{FormatType(varDecl.TypeName)}'.");
    }
    private void FoldGlobalIntegerInitializer(VariableDeclarationNode varDecl)
    {
        if (_currentFunction != null || !IsIntegerScalarType(varDecl.TypeName))
            return;

        if (TryEvaluateConstIntExpr(varDecl.Value!, out var foldedValue, out var constError))
        {
            ReplaceVariableInitializerWithFoldedNumber(varDecl, foldedValue);
            TryRecordConstValue(varDecl);
        }
        else if (constError != null)
        {
            _errors.Error(varDecl.Value!, constError);
        }
    }
    private static void ReplaceVariableInitializerWithFoldedNumber(VariableDeclarationNode varDecl, long foldedValue)
    {
        varDecl.Value = new NumberExpr
        {
            Value = foldedValue,
            File = varDecl.Value!.File,
            Line = varDecl.Value.Line,
            Column = varDecl.Value.Column,
            Length = varDecl.Value.Length
        };
    }
    private static bool IsAssignableExpression(Expr expr)
    {
        return expr is IdentifierExpr or FieldExpr or IndexExpr;
    }
    private static bool TryGetAssignmentRootName(Expr expr, out string name)
    {
        switch (expr)
        {
            case IdentifierExpr ident:
                name = ident.Name;
                return true;

            case FieldExpr field:
                return TryGetAssignmentRootName(field.Target, out name);

            case IndexExpr index:
                return TryGetAssignmentRootName(index.Target, out name);

            default:
                name = "";
                return false;
        }
    }
    private bool IsValidAsmOperandType(TypeNode? type)
    {
        if (type == null)
            return false;

        if (type.IsErrorUnion || type.IsFunction || type.IsSlice || type.ArraySize != null || type.Name == "void" || IsUnionType(type))
            return false;

        if (type.IsPointer)
            return true;

        if (type.Name == "bool")
            return true;

        return _numericTypes.Contains(type.Name);
    }
    private void CheckBinaryExpression(BinaryExpr bin)
    {
        CheckExpression(bin.Left);
        CheckExpression(bin.Right);

        var leftType = GetExpressionType(bin.Left);
        var rightType = GetExpressionType(bin.Right);

        if (leftType == null || rightType == null)
            return;

        if (LogicalOperators.Contains(bin.Operator))
        {
            if (!IsBoolType(leftType))
                _errors.Error(bin.Left, $"Left operand of '{bin.Operator}' must have type 'bool', got '{FormatType(leftType)}'.");

            if (!IsBoolType(rightType))
                _errors.Error(bin.Right, $"Right operand of '{bin.Operator}' must have type 'bool', got '{FormatType(rightType)}'.");

            return;
        }

        if (NumericOperators.Contains(bin.Operator))
        {
            if (IsPointerArithmetic(bin.Operator, leftType, rightType))
                return;

            if (!IsNumericType(leftType))
            {
                _errors.Error(bin.Left, $"Left operand of '{bin.Operator}' must be numeric, got '{FormatType(leftType)}'.");
            }

            if (!IsNumericType(rightType))
            {
                _errors.Error(bin.Right, $"Right operand of '{bin.Operator}' must be numeric, got '{FormatType(rightType)}'.");
            }
        }

        if (!ComparisonOperators.Contains(bin.Operator))
            return;

        if (bin.Operator is ">" or "<" or ">=" or "<=")
        {
            if (!IsNumericType(leftType) || !IsNumericType(rightType))
                _errors.Error(bin, $"Operator '{bin.Operator}' requires numeric operands, got '{FormatType(leftType)}' and '{FormatType(rightType)}'.");
            else
                WarnOnMixedSignednessComparison(bin, leftType, bin.Left, rightType, bin.Right);
            return;
        }

        if (bin.Operator is "==" or "!=")
        {
            if (AreEqualityComparableTypes(leftType, rightType))
            {
                WarnOnMixedSignednessComparison(bin, leftType, bin.Left, rightType, bin.Right);
                return;
            }

            _errors.Error(bin, $"Operator '{bin.Operator}' requires comparable operands, got '{FormatType(leftType)}' and '{FormatType(rightType)}'. Expected numeric operands, bool operands, matching pointer types, or two strings.");
        }
    }
    private void CheckStructLiteralExpression(StructLiteralExpr structLiteral)
    {
        ValidateTypeReference(structLiteral.TypeName, structLiteral);

        var fullName = QualifiedNames.GetFullName(structLiteral.TypeName.NamespacePath, structLiteral.TypeName.Name);

        if (_symbolTable.LookupUnionNode(fullName) is UnionNode unionNode)
        {
            CheckUnionLiteralExpression(structLiteral, unionNode, fullName);
            return;
        }

        if (!_symbolTable.TryLookupStruct(fullName, out var structFields))
            return;

        var fieldMap = GetStructFieldsForType(structLiteral.TypeName).ToDictionary(field => field.Name, field => field.Type, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in structLiteral.Fields)
        {
            CheckExpression(field.Value);

            if (!seen.Add(field.Name))
            {
                _errors.Error(field, $"Struct literal for '{fullName}' initializes field '{field.Name}' more than once.");
                continue;
            }

            if (!fieldMap.TryGetValue(field.Name, out var fieldType))
            {
                _errors.Error(field, $"Struct '{fullName}' does not have a field named '{field.Name}'.");
                continue;
            }

            var valueType = GetExpressionType(field.Value);
            if (!IsAssignableTo(fieldType, field.Value, valueType))
            {
                if (TryBuildNumericConversionDiagnostic(fieldType, field.Value, valueType, out var numericDiagnostic))
                    _errors.Error(field, $"Cannot initialize field '{field.Name}' of type '{FormatType(fieldType)}' from expression of type '{FormatType(valueType)}'. {numericDiagnostic}");
                else
                    _errors.Error(field, $"Cannot assign expression of type '{FormatType(valueType)}' to field '{field.Name}' of type '{FormatType(fieldType)}'.");
            }
        }

        foreach (var field in GetStructFieldsForType(structLiteral.TypeName))
        {
            if (!seen.Contains(field.Name))
                _errors.Error(structLiteral, $"Struct literal for '{fullName}' is missing required field '{field.Name}'.");
        }
    }
    private void CheckUnionLiteralExpression(StructLiteralExpr unionLiteral, UnionNode unionNode, string fullName)
    {
        if (unionLiteral.Fields.Count != 1)
        {
            _errors.Error(unionLiteral, $"Union literal for '{fullName}' must initialize exactly one variant.");
            return;
        }

        var field = unionLiteral.Fields[0];
        CheckExpression(field.Value);

        var variant = GetUnionVariantsForType(unionLiteral.TypeName).FirstOrDefault(candidate => candidate.Name == field.Name);
        if (variant == null)
        {
            _errors.Error(field, $"Union '{fullName}' does not have a variant named '{field.Name}'.");
            return;
        }

        var valueType = GetExpressionType(field.Value);
        if (!IsAssignableTo(variant.TypeName, field.Value, valueType))
        {
            if (TryBuildNumericConversionDiagnostic(variant.TypeName, field.Value, valueType, out var numericDiagnostic))
                _errors.Error(field, $"Cannot initialize union variant '{field.Name}' of type '{FormatType(variant.TypeName)}' from expression of type '{FormatType(valueType)}'. {numericDiagnostic}");
            else
                _errors.Error(field, $"Cannot assign expression of type '{FormatType(valueType)}' to union variant '{field.Name}' of type '{FormatType(variant.TypeName)}'.");
        }
    }
    private void CheckArrayLiteralExpression(ArrayLiteralExpr arrayLiteral)
    {
        ValidateTypeReference(arrayLiteral.TypeName, arrayLiteral);

        if (arrayLiteral.TypeName.ArraySize == null)
        {
            _errors.Error(arrayLiteral, "Array literals must use an array type like '[4]u8'.");
            return;
        }

        var expectedCount = arrayLiteral.TypeName.ArraySize.Value;
        if (arrayLiteral.Elements.Count != expectedCount)
            _errors.Error(arrayLiteral, $"Array literal for type '{FormatType(arrayLiteral.TypeName)}' must contain exactly {expectedCount} element(s), got {arrayLiteral.Elements.Count}.");

        var elementType = arrayLiteral.TypeName.Clone();
        elementType.ArraySize = null;

        foreach (var element in arrayLiteral.Elements)
        {
            CheckExpression(element);
            var elementValueType = GetExpressionType(element);
            if (!IsAssignableTo(elementType, element, elementValueType))
            {
                if (TryBuildNumericConversionDiagnostic(elementType, element, elementValueType, out var numericDiagnostic))
                    _errors.Error(arrayLiteral, $"Cannot initialize array element of type '{FormatType(elementType)}' from expression of type '{FormatType(elementValueType)}'. {numericDiagnostic}");
                else
                    _errors.Error(arrayLiteral, $"Cannot assign expression of type '{FormatType(elementValueType)}' to array element type '{FormatType(elementType)}'.");
            }
        }
    }
    private void CheckCallExpression(CallExpr call)
    {
        if (call.TargetExpr != null && IsInvalidPostfixTarget(call.TargetExpr))
            return;

        if (call.TargetExpr != null)
            CheckExpression(call.TargetExpr);

        foreach (var argument in call.Args)
            CheckExpression(argument);

        var callInfo = ResolveCallInfo(call, reportErrors: true);
        if (callInfo == null)
            return;

        var parameters = callInfo.Parameters;
        var errorName = callInfo.DisplayName;

        if (call.Name != "syscall" && parameters.Count != call.Args.Count)
        {
            _errors.Error(call, $"Function '{errorName}' expects {parameters.Count} arguments, got {call.Args.Count}");
            return;
        }

        var argCount = call.Name == "syscall" ? Math.Min(parameters.Count, call.Args.Count) : call.Args.Count;

        for (int i = 0; i < argCount; i++)
        {
            var argType = GetExpressionType(call.Args[i]);
            var paramType = parameters[i].TypeName;

            if (argType == null)
                continue;

            if (paramType.IsPointer && argType.IsPointer)
            {
                int targetLevel = paramType.PointerLevel > 0 ? paramType.PointerLevel : 1;
                int argLevel = argType.PointerLevel > 0 ? argType.PointerLevel : 1;
                if (targetLevel != argLevel)
                {
                    _errors.Error(call, $"Type Mismatch: Cannot pass {argLevel}-level pointer to {targetLevel}-level pointer parameter.");
                    continue;
                }
            }

            if (!IsAssignableTo(paramType, call.Args[i], argType))
            {
                if (TryBuildNumericConversionDiagnostic(paramType, call.Args[i], argType, out var numericDiagnostic))
                    _errors.Error(call, $"Argument {i + 1} of '{errorName}' expects type '{FormatType(paramType)}', got '{FormatType(argType)}'. {numericDiagnostic}");
                else
                    _errors.Error(call, $"Argument {i + 1} of '{errorName}' expects type '{FormatType(paramType)}', got '{FormatType(argType)}'.");
            }
        }
    }
    private TypeNode? GetExpressionType(Expr expr, bool reportErrors = true)
    {
        var type = ComputeExpressionType(expr, reportErrors);
        if (type != null)
            _checkedExpressionTypes[expr] = type.Clone();
        return type;
    }
    private TypeNode? ComputeExpressionType(Expr expr, bool reportErrors)
    {
        switch (expr)
        {
            case NumberExpr _:
                return ComputeNumberExpressionType();

            case StringExpr _:
                return ComputeStringExpressionType();

            case IdentifierExpr ident:
                return ComputeIdentifierExpressionType(ident);

            case TypeReferenceExpr:
                return ComputeTypeReferenceExpressionType();

            case BinaryExpr bin:
                return ComputeBinaryExpressionType(bin, reportErrors);

            case CallExpr call:
                return ComputeCallExpressionType(call, reportErrors);

            case CastExpr cast:
                return ComputeCastExpressionType(cast);

            case IndexExpr idx:
                return ComputeIndexExpressionType(idx, reportErrors);

            case FieldExpr field:
                return ComputeFieldExpressionType(field, reportErrors);

            case UnaryExpr un:
                return ComputeUnaryExpressionType(un, reportErrors);

            case StructLiteralExpr structLiteral:
                return ComputeStructLiteralExpressionType(structLiteral);

            case ArrayLiteralExpr arrayLiteral:
                return ComputeArrayLiteralExpressionType(arrayLiteral);

            case BuiltinExpr builtin:
                return ComputeBuiltinExpressionType(builtin);

            case ErrorNamespaceExpr:
                return ComputeErrorNamespaceExpressionType();

            case ErrorExpr _:
                return ComputeErrorExpressionType();

            case SizeofExpr _:
                return ComputeSizeofExpressionType();

            case CatchExpr catchExpr:
                return ComputeCatchExpressionType(catchExpr, reportErrors);

            case InvalidExpr:
                return ComputeInvalidExpressionType();

            default:
                return null;
        }
    }
    private static TypeNode ComputeNumberExpressionType()
    {
        return new TypeNode { Name = "i64" };
    }
    private static TypeNode ComputeStringExpressionType()
    {
        return new TypeNode { Name = "string" };
    }
    private TypeNode? ComputeIdentifierExpressionType(IdentifierExpr identifier)
    {
        var resolvedName = ResolveQualifiedName(identifier.Name);
        var info = _symbolTable.Lookup(resolvedName);
        return info?.Type;
    }
    private static TypeNode? ComputeTypeReferenceExpressionType()
    {
        return null;
    }
    private TypeNode? ComputeBinaryExpressionType(BinaryExpr binaryExpression, bool reportErrors)
    {
        if (ComparisonOperators.Contains(binaryExpression.Operator) || LogicalOperators.Contains(binaryExpression.Operator))
            return new TypeNode { Name = "bool" };

        var leftType = GetExpressionType(binaryExpression.Left, reportErrors);
        var rightType = GetExpressionType(binaryExpression.Right, reportErrors);
        if (leftType != null && rightType != null)
        {
            if (binaryExpression.Operator == "+" && IsNumericType(leftType) && rightType.IsPointer)
                return rightType.Clone();

            if ((binaryExpression.Operator == "+" || binaryExpression.Operator == "-") && leftType.IsPointer && IsNumericType(rightType))
                return leftType.Clone();
        }

        return leftType ?? GetExpressionType(binaryExpression.Left, reportErrors);
    }
    private TypeNode? ComputeCallExpressionType(CallExpr callExpression, bool reportErrors)
    {
        if (callExpression.TargetExpr != null && IsInvalidPostfixTarget(callExpression.TargetExpr))
            return null;

        return ResolveCallInfo(callExpression, reportErrors)?.ReturnType;
    }
    private static TypeNode ComputeCastExpressionType(CastExpr castExpression)
    {
        return castExpression.TargetType;
    }
    private TypeNode? ComputeIndexExpressionType(IndexExpr indexExpression, bool reportErrors)
    {
        if (IsInvalidPostfixTarget(indexExpression.Target))
            return null;

        var targetType = GetExpressionType(indexExpression.Target, reportErrors);
        if (targetType == null)
            return null;
        if (targetType.ArraySize != null)
            return CreateIndexedElementType(targetType);
        if (targetType.IsSlice)
            return CreateSliceElementType(targetType);
        if (targetType.IsPointer)
            return CreatePointedElementType(targetType);

        return null;
    }
    private static TypeNode CreateIndexedElementType(TypeNode targetType)
    {
        var elementType = targetType.Clone();
        elementType.ArraySize = null;
        elementType.ArraySizeExpr = null;
        return elementType;
    }
    private static TypeNode CreateSliceElementType(TypeNode targetType)
    {
        var elementType = targetType.Clone();
        elementType.IsSlice = false;
        elementType.ArraySize = null;
        elementType.ArraySizeExpr = null;
        return elementType;
    }
    private static TypeNode CreatePointedElementType(TypeNode targetType)
    {
        var elementType = targetType.Clone();
        var level = targetType.PointerLevel > 0 ? targetType.PointerLevel : 1;
        if (level > 1)
        {
            elementType.IsPointer = true;
            elementType.PointerLevel = level - 1;
            return elementType;
        }

        elementType.IsPointer = false;
        elementType.PointerLevel = 0;
        return elementType;
    }
    private TypeNode? ComputeFieldExpressionType(FieldExpr fieldExpression, bool reportErrors)
    {
        if (IsInvalidPostfixTarget(fieldExpression.Target))
            return null;

        if (TryResolveStaticEnumMember(fieldExpression, out var staticEnumType, out _))
            return staticEnumType;

        if (TryResolveStaticTypeReference(fieldExpression.Target, out var staticOwnerType) &&
            IsUnionType(staticOwnerType) &&
            fieldExpression.Field == "Tag")
        {
            return GetUnionTagType(staticOwnerType);
        }

        if (TryResolveFieldSymbolType(fieldExpression, reportErrors, out var resolvedFieldType))
            return resolvedFieldType;

        var targetType = GetExpressionType(fieldExpression.Target, reportErrors);
        if (targetType == null)
        {
            if (reportErrors)
                _errors.Error(fieldExpression, $"Cannot determine type of target in field access '{fieldExpression.Field}'.");
            return null;
        }

        return ComputeMemberAccessType(fieldExpression, targetType, reportErrors);
    }
    private bool TryResolveFieldSymbolType(FieldExpr fieldExpression, bool reportErrors, out TypeNode? resolvedFieldType)
    {
        resolvedFieldType = null;
        if (ResolveQualifiedFieldSymbol(fieldExpression) is not ResolvedFieldSymbolInfo resolvedFieldSymbol)
            return false;

        if (resolvedFieldSymbol.SymbolInfo.Kind != SymbolKind.Variable && resolvedFieldSymbol.SymbolInfo.Kind != SymbolKind.Function)
            return false;

        if (!IsVisibleResolvedFieldSymbol(resolvedFieldSymbol))
        {
            if (reportErrors)
                ReportNotVisible(fieldExpression, "Symbol", resolvedFieldSymbol.ResolvedName);
            return true;
        }

        resolvedFieldType = resolvedFieldSymbol.SymbolInfo.Type.Clone();
        return true;
    }
    private TypeNode? ComputeMemberAccessType(FieldExpr fieldExpression, TypeNode targetType, bool reportErrors)
    {
        var structName = QualifiedNames.GetFullName(targetType.NamespacePath, targetType.Name);
        if (targetType.IsSlice)
            return ComputeSliceFieldType(fieldExpression, targetType, reportErrors);

        if (_numericTypes.Contains(structName))
            return CreateNumericFieldType(targetType, structName);

        if (_symbolTable.TryLookupStruct(structName, out _))
            return ComputeStructFieldType(fieldExpression, targetType, structName, reportErrors);

        if (LookupUnionDefinition(targetType) is UnionNode unionDefinition)
            return ComputeUnionFieldType(fieldExpression, targetType, structName, unionDefinition, reportErrors);

        if (reportErrors)
            _errors.Error(fieldExpression, $"Type '{structName}' is not a known struct or union");
        return null;
    }
    private TypeNode? ComputeSliceFieldType(FieldExpr fieldExpression, TypeNode targetType, bool reportErrors)
    {
        if (fieldExpression.Field == "len")
            return new TypeNode { Name = "i64" };

        if (fieldExpression.Field == "ptr")
        {
            var elementType = targetType.Clone();
            elementType.IsSlice = false;
            elementType.ArraySize = null;
            return TypeHelpers.AddressOfType(elementType);
        }

        if (reportErrors)
            _errors.Error(fieldExpression, $"Slice values expose only '.ptr' and '.len', not '{fieldExpression.Field}'.");
        return null;
    }
    private static TypeNode CreateNumericFieldType(TypeNode targetType, string structName)
    {
        return new TypeNode
        {
            Name = structName,
            IsVolatile = targetType.IsVolatile,
            IsPointer = targetType.IsPointer,
            PointerLevel = targetType.PointerLevel
        };
    }
    private TypeNode? ComputeStructFieldType(FieldExpr fieldExpression, TypeNode targetType, string structName, bool reportErrors)
    {
        if (!targetType.IsAliasQualifiedReference && !CheckVisibility(structName))
        {
            if (reportErrors)
                ReportNotVisible(fieldExpression, "Struct", structName);
            return null;
        }

        var fieldDef = GetStructFieldsForType(targetType).FirstOrDefault(f => f.Name == fieldExpression.Field);
        if (!string.IsNullOrEmpty(fieldDef.Name))
            return fieldDef.Type.Clone();

        if (reportErrors)
            _errors.Error(fieldExpression, $"Struct '{structName}' does not have a field named '{fieldExpression.Field}'.");
        return null;
    }
    private TypeNode? ComputeUnionFieldType(
        FieldExpr fieldExpression,
        TypeNode targetType,
        string structName,
        UnionNode unionDefinition,
        bool reportErrors)
    {
        if (!targetType.IsAliasQualifiedReference && !CheckVisibility(structName))
        {
            if (reportErrors)
                ReportNotVisible(fieldExpression, "Union", structName);
            return null;
        }

        if (fieldExpression.Field == "tag")
            return GetUnionTagType(targetType);

        var variant = unionDefinition.Variants.FirstOrDefault(candidate => candidate.Name == fieldExpression.Field);
        if (variant != null)
            return variant.TypeName.Clone();

        if (reportErrors)
            _errors.Error(fieldExpression, $"Union '{structName}' does not have a field named '{fieldExpression.Field}'.");
        return null;
    }
    private TypeNode? ComputeUnaryExpressionType(UnaryExpr unaryExpression, bool reportErrors)
    {
        if (unaryExpression.Operator == "&")
        {
            var operandType = GetExpressionType(unaryExpression.Operand, reportErrors);
            if (operandType != null)
                return TypeHelpers.AddressOfType(operandType);
        }
        else if (unaryExpression.Operator == "!")
        {
            return new TypeNode { Name = "bool" };
        }
        else if (unaryExpression.Operator == "-")
        {
            return GetExpressionType(unaryExpression.Operand, reportErrors);
        }

        return null;
    }
    private static TypeNode ComputeStructLiteralExpressionType(StructLiteralExpr structLiteral)
    {
        return structLiteral.TypeName.Clone();
    }
    private static TypeNode ComputeArrayLiteralExpressionType(ArrayLiteralExpr arrayLiteral)
    {
        return arrayLiteral.TypeName.Clone();
    }
    private static TypeNode? ComputeBuiltinExpressionType(BuiltinExpr builtin)
    {
        return builtin.Name switch
        {
            "true" => new TypeNode { Name = "bool" },
            "false" => new TypeNode { Name = "bool" },
            "Builtin.IsLinux" => new TypeNode { Name = "bool" },
            "Builtin.IsWindows" => new TypeNode { Name = "bool" },
            "Builtin.IsBareMetal" => new TypeNode { Name = "bool" },
            "Builtin.IsX86_64" => new TypeNode { Name = "bool" },
            "Builtin.IsAArch64" => new TypeNode { Name = "bool" },
            "Builtin.CompileError" => new TypeNode { Name = "void" },
            _ => null
        };
    }
    private static TypeNode? ComputeErrorNamespaceExpressionType()
    {
        return null;
    }
    private static TypeNode ComputeErrorExpressionType()
    {
        return new TypeNode { Name = "i32" };
    }
    private static TypeNode ComputeSizeofExpressionType()
    {
        return new TypeNode { Name = "i64" };
    }
    private TypeNode? ComputeCatchExpressionType(CatchExpr catchExpression, bool reportErrors)
    {
        var catchLeftType = GetExpressionType(catchExpression.Left, reportErrors);
        if (catchLeftType == null || !catchLeftType.IsErrorUnion)
            return null;

        return (catchLeftType.ErrorInnerType ?? catchLeftType).Clone();
    }
    private static TypeNode? ComputeInvalidExpressionType()
    {
        return null;
    }
}
