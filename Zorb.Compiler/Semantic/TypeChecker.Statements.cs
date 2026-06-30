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
    private FlowOutcome CheckStatement(Statement stmt)
    {
        switch (stmt)
        {
            case VariableDeclarationNode varDecl:
                return CheckVariableDeclarationStatement(varDecl);

            case ExpressionStatement exprStmt:
                return CheckExpressionStatement(exprStmt);

            case AssignStmt assign:
                return CheckAssignmentStatement(assign);

            case ReturnNode returnNode:
                return CheckReturnStatement(returnNode);

            case IfStmt ifStmt:
                return CheckIfStatement(ifStmt);

            case WhileStmt whileStmt:
                return CheckWhileStatement(whileStmt);

            case ForStmt forStmt:
                return CheckForStatement(forStmt);

            case SwitchStmt switchStmt:
                return CheckSwitchStatement(switchStmt);

            case MatchStmt matchStmt:
                return CheckMatchStatement(matchStmt);

            case ContinueStmt continueStmt:
                if (_loopDepth == 0)
                    _errors.Error(continueStmt, "'continue' is only allowed inside a while or for loop.");
                return _loopDepth == 0 ? FlowOutcome.FallsThrough : FlowOutcome.Continues;

            case BreakStmt breakStmt:
                if (_loopDepth == 0)
                    _errors.Error(breakStmt, "'break' is only allowed inside a while or for loop.");
                return _loopDepth == 0 ? FlowOutcome.FallsThrough : FlowOutcome.Breaks;

            case AsmStatementNode asmStmt:
                CheckAsmStatement(asmStmt);
                return FlowOutcome.FallsThrough;
        }

        return FlowOutcome.FallsThrough;
    }
    private FlowOutcome CheckVariableDeclarationStatement(VariableDeclarationNode variableDeclaration)
    {
        CheckVariableDeclaration(variableDeclaration);
        return FlowOutcome.FallsThrough;
    }
    private FlowOutcome CheckExpressionStatement(ExpressionStatement expressionStatement)
    {
        if (expressionStatement.Expression is CatchExpr catchExpr)
            CheckCatchExpression(catchExpr, allowStatementFallthroughWithoutValue: true);
        else
            CheckExpression(expressionStatement.Expression);

        return FlowOutcome.FallsThrough;
    }
    private FlowOutcome CheckAssignmentStatement(AssignStmt assignment)
    {
        CheckAssignment(assignment);
        return FlowOutcome.FallsThrough;
    }
    private FlowOutcome CheckReturnStatement(ReturnNode returnNode)
    {
        if (returnNode.Value == null)
            return FlowOutcome.Returns;

        CheckExpression(returnNode.Value);
        if (_currentFunction != null)
            CheckReturnValue(returnNode, _currentFunction.ReturnType);

        return FlowOutcome.Returns;
    }
    private void CheckReturnValue(ReturnNode returnNode, TypeNode expectedType)
    {
        if (returnNode.Value is ErrorExpr errorExpr)
        {
            CheckErrorReturnValue(returnNode, errorExpr, expectedType);
            return;
        }

        var exprType = GetExpressionType(returnNode.Value!);
        if (expectedType.IsErrorUnion)
        {
            CheckErrorUnionReturnValue(returnNode, expectedType, exprType);
            return;
        }

        CheckDirectReturnValue(returnNode, expectedType, exprType);
    }
    private void CheckErrorReturnValue(ReturnNode returnNode, ErrorExpr errorExpr, TypeNode expectedType)
    {
        if (!expectedType.IsErrorUnion)
        {
            _errors.Error(returnNode.Value!, $"Function '{_currentFunction!.Name}' does not return an error union (!), so you cannot return an error code.");
            return;
        }

        if (!TryResolveDeclaredErrorSymbol(errorExpr.ErrorCode, out _))
            _errors.Error(returnNode, $"Use of undeclared error 'error.{errorExpr.ErrorCode}'. Declare it first with 'error {errorExpr.ErrorCode} = ...'.");
    }
    private void CheckErrorUnionReturnValue(ReturnNode returnNode, TypeNode expectedType, TypeNode? exprType)
    {
        if (returnNode.Value is IdentifierExpr identifier && IsCatchErrorVar(identifier.Name))
            return;

        var successType = expectedType.ErrorInnerType ?? expectedType;
        if (!IsAssignableTo(successType, returnNode.Value!, exprType))
        {
            if (TryBuildNumericConversionDiagnostic(successType, returnNode.Value!, exprType, out var numericDiagnostic))
                _errors.Error(returnNode, $"Cannot return expression of type '{FormatType(exprType)}' from function '{_currentFunction!.Name}' with result type '{FormatType(expectedType)}'. {numericDiagnostic}");
            else
                _errors.Error(returnNode, $"Cannot return expression of type '{FormatType(exprType)}' from function '{_currentFunction!.Name}' with result type '{FormatType(expectedType)}'. Return a success value of type '{FormatType(successType)}' or an error.");
        }
    }
    private void CheckDirectReturnValue(ReturnNode returnNode, TypeNode expectedType, TypeNode? exprType)
    {
        if (IsAssignableTo(expectedType, returnNode.Value!, exprType))
            return;

        if (TryBuildNumericConversionDiagnostic(expectedType, returnNode.Value!, exprType, out var numericDiagnostic))
            _errors.Error(returnNode, $"Function '{_currentFunction!.Name}' returns '{FormatType(expectedType)}', but this return expression has type '{FormatType(exprType)}'. {numericDiagnostic}");
        else
            _errors.Error(returnNode, $"Function '{_currentFunction!.Name}' returns '{FormatType(expectedType)}', but this return expression has type '{FormatType(exprType)}'.");
    }
    private FlowOutcome CheckIfStatement(IfStmt ifStatement)
    {
        CheckBoolCondition(ifStatement.Condition);
        var ifFlow = CheckBlock(ifStatement.Body);
        if (ifStatement.ElseBody.Count == 0)
            return FlowOutcome.FallsThrough;

        var elseFlow = CheckBlock(ifStatement.ElseBody);
        if (ifFlow == elseFlow && ifFlow != FlowOutcome.FallsThrough)
            return ifFlow;

        return FlowOutcome.FallsThrough;
    }
    private FlowOutcome CheckWhileStatement(WhileStmt whileStatement)
    {
        CheckBoolCondition(whileStatement.Condition);
        CheckLoopBody(whileStatement.Body);
        return FlowOutcome.FallsThrough;
    }
    private FlowOutcome CheckForStatement(ForStmt forStatement)
    {
        PushScopedState();
        try
        {
            if (forStatement.Initializer != null)
                CheckStatement(forStatement.Initializer);

            if (forStatement.Condition != null)
                CheckBoolCondition(forStatement.Condition);

            CheckLoopBody(forStatement.Body);

            if (forStatement.Update != null)
                CheckStatement(forStatement.Update);
        }
        finally
        {
            PopScopedState();
        }

        return FlowOutcome.FallsThrough;
    }
    private void CheckLoopBody(List<Statement> body)
    {
        _loopDepth++;
        try
        {
            CheckBlock(body);
        }
        finally
        {
            _loopDepth--;
        }
    }
    private FlowOutcome CheckSwitchStatement(SwitchStmt switchStatement)
    {
        CheckExpression(switchStatement.Expression);
        var switchType = GetExpressionType(switchStatement.Expression);
        ValidateSwitchOperandType(switchStatement, switchType);

        var seenCaseValues = new Dictionary<string, Expr>(StringComparer.Ordinal);
        var seenEnumCaseValues = new HashSet<long>();
        var caseOutcomes = new List<FlowOutcome>();
        CheckSwitchCases(switchStatement, switchType, seenCaseValues, seenEnumCaseValues, caseOutcomes);

        var isExhaustiveEnumSwitch = CheckSwitchEnumExhaustiveness(switchStatement, switchType, seenEnumCaseValues);
        return DetermineSwitchFlowOutcome(switchStatement, caseOutcomes, isExhaustiveEnumSwitch);
    }
    private void ValidateSwitchOperandType(SwitchStmt switchStatement, TypeNode? switchType)
    {
        if (switchType != null && !IsSwitchOperandType(switchType))
            _errors.Error(switchStatement.Expression, $"Switch expression must have numeric, bool, or enum type, got '{FormatType(switchType)}'.");
    }
    private void CheckSwitchCases(
        SwitchStmt switchStatement,
        TypeNode? switchType,
        Dictionary<string, Expr> seenCaseValues,
        HashSet<long> seenEnumCaseValues,
        List<FlowOutcome> caseOutcomes)
    {
        foreach (var switchCase in switchStatement.Cases)
            CheckSwitchCase(switchCase, switchType, seenCaseValues, seenEnumCaseValues, caseOutcomes);
    }
    private void CheckSwitchCase(
        SwitchCase switchCase,
        TypeNode? switchType,
        Dictionary<string, Expr> seenCaseValues,
        HashSet<long> seenEnumCaseValues,
        List<FlowOutcome> caseOutcomes)
    {
        CheckExpression(switchCase.Value);
        var caseType = GetExpressionType(switchCase.Value);
        ValidateSwitchCaseComparability(switchCase, switchType, caseType);
        TrackSeenSwitchCaseValue(switchCase, seenCaseValues);
        TrackSeenEnumSwitchCaseValue(switchCase, switchType, seenEnumCaseValues);
        caseOutcomes.Add(CheckBlock(switchCase.Body));
    }
    private void ValidateSwitchCaseComparability(SwitchCase switchCase, TypeNode? switchType, TypeNode? caseType)
    {
        if (switchType != null && caseType != null && !AreEqualityComparableTypes(switchType, caseType))
            _errors.Error(switchCase.Value, $"Switch case expression of type '{FormatType(caseType)}' is not comparable to switch expression type '{FormatType(switchType)}'.");
    }
    private void TrackSeenSwitchCaseValue(SwitchCase switchCase, Dictionary<string, Expr> seenCaseValues)
    {
        if (TryGetSwitchCaseKey(switchCase.Value, out var caseKey) && !seenCaseValues.TryAdd(caseKey, switchCase.Value))
            _errors.Error(switchCase.Value, $"Duplicate switch case value '{caseKey}'.{FormatPreviousDeclarationSuffix(seenCaseValues[caseKey])}");
    }
    private void TrackSeenEnumSwitchCaseValue(SwitchCase switchCase, TypeNode? switchType, HashSet<long> seenEnumCaseValues)
    {
        if (switchType != null && IsEnumType(switchType) && TryEvaluateConstIntExpr(switchCase.Value, out var enumCaseValue, out _))
            seenEnumCaseValues.Add(enumCaseValue);
    }
    private bool CheckSwitchEnumExhaustiveness(SwitchStmt switchStatement, TypeNode? switchType, HashSet<long> seenEnumCaseValues)
    {
        if (switchType == null || !IsEnumType(switchType) || switchStatement.ElseBody.Count > 0)
            return false;

        var enumDefinition = LookupEnumDefinition(switchType);
        if (enumDefinition == null)
            return false;

        var missingMembers = GetMissingEnumMembers(enumDefinition, seenEnumCaseValues);
        if (missingMembers.Count > 0)
        {
            _errors.Error(switchStatement, $"Switch over enum '{FormatType(switchType)}' must cover all members or provide an else branch. Missing: {string.Join(", ", missingMembers)}.");
            return false;
        }

        return true;
    }
    private FlowOutcome DetermineSwitchFlowOutcome(SwitchStmt switchStatement, List<FlowOutcome> caseOutcomes, bool isExhaustiveEnumSwitch)
    {
        if (switchStatement.ElseBody.Count == 0)
        {
            if (isExhaustiveEnumSwitch && HasUniformNonFallthroughFlow(caseOutcomes))
                return caseOutcomes[0];

            return FlowOutcome.FallsThrough;
        }

        caseOutcomes.Add(CheckBlock(switchStatement.ElseBody));
        if (HasUniformNonFallthroughFlow(caseOutcomes))
            return caseOutcomes[0];

        return FlowOutcome.FallsThrough;
    }
    private static bool HasUniformNonFallthroughFlow(List<FlowOutcome> caseOutcomes)
    {
        return caseOutcomes.Count > 0 &&
            caseOutcomes.All(outcome => outcome == caseOutcomes[0] && outcome != FlowOutcome.FallsThrough);
    }
    private FlowOutcome CheckMatchStatement(MatchStmt matchStmt)
    {
        CheckExpression(matchStmt.Expression);
        var matchType = GetExpressionType(matchStmt.Expression);
        if (matchType == null)
            return FlowOutcome.FallsThrough;

        if (!IsSwitchOperandType(matchType) && !IsUnionType(matchType))
        {
            _errors.Error(matchStmt.Expression, $"Match expression must have numeric, bool, enum, or union type, got '{FormatType(matchType)}'.");
            return FlowOutcome.FallsThrough;
        }

        return IsUnionType(matchType)
            ? CheckUnionMatchStatement(matchStmt, matchType)
            : CheckScalarMatchStatement(matchStmt, matchType);
    }
    private FlowOutcome CheckScalarMatchStatement(MatchStmt matchStmt, TypeNode matchType)
    {
        var seenCaseValues = new Dictionary<string, MatchPattern>(StringComparer.Ordinal);
        var seenEnumCaseValues = new HashSet<long>();
        var seenBoolCaseValues = new HashSet<bool>();
        var caseOutcomes = new List<FlowOutcome>();
        var enumDefinition = LookupEnumDefinition(matchType);

        foreach (var matchCase in matchStmt.Cases)
            CheckScalarMatchCase(matchCase, matchType, enumDefinition, seenCaseValues, seenEnumCaseValues, seenBoolCaseValues, caseOutcomes);

        var isExhaustive = CheckScalarMatchExhaustiveness(matchStmt, matchType, enumDefinition, seenEnumCaseValues, seenBoolCaseValues);
        return CombineMatchFlow(caseOutcomes, matchStmt.ElseBody, isExhaustive);
    }
    private FlowOutcome CheckUnionMatchStatement(MatchStmt matchStmt, TypeNode matchType)
    {
        var unionDefinition = LookupUnionDefinition(matchType);
        var seenVariants = new Dictionary<string, MatchPattern>(StringComparer.Ordinal);
        var caseOutcomes = new List<FlowOutcome>();

        foreach (var matchCase in matchStmt.Cases)
            CheckUnionMatchCase(matchCase, matchType, unionDefinition, seenVariants, caseOutcomes);

        var isExhaustive = CheckUnionMatchExhaustiveness(matchStmt, matchType, unionDefinition, seenVariants);
        return CombineMatchFlow(caseOutcomes, matchStmt.ElseBody, isExhaustive);
    }
    private void CheckScalarMatchCase(
        MatchCase matchCase,
        TypeNode matchType,
        EnumNode? enumDefinition,
        Dictionary<string, MatchPattern> seenCaseValues,
        HashSet<long> seenEnumCaseValues,
        HashSet<bool> seenBoolCaseValues,
        List<FlowOutcome> caseOutcomes)
    {
        if (matchCase.Pattern is not QualifiedMatchPattern qualifiedPattern)
        {
            ReportInvalidScalarMatchPattern(matchCase, matchType, enumDefinition);
            caseOutcomes.Add(CheckBlock(matchCase.Body));
            return;
        }

        CheckScalarMatchPattern(qualifiedPattern, matchType, seenCaseValues, seenEnumCaseValues, seenBoolCaseValues);
        caseOutcomes.Add(CheckBlock(matchCase.Body));
    }
    private void ReportInvalidScalarMatchPattern(MatchCase matchCase, TypeNode matchType, EnumNode? enumDefinition)
    {
        var expectedPattern = IsEnumType(matchType)
            ? $"enum-member patterns like '{enumDefinition?.Name}.Member'"
            : "expression patterns like '0' or 'true'";
        _errors.Error(matchCase.Pattern, $"Match over '{FormatType(matchType)}' requires {expectedPattern}.");
    }
    private void CheckScalarMatchPattern(
        QualifiedMatchPattern qualifiedPattern,
        TypeNode matchType,
        Dictionary<string, MatchPattern> seenCaseValues,
        HashSet<long> seenEnumCaseValues,
        HashSet<bool> seenBoolCaseValues)
    {
        CheckExpression(qualifiedPattern.Value);
        var patternType = GetExpressionType(qualifiedPattern.Value);
        if (patternType != null && !AreEqualityComparableTypes(matchType, patternType))
            _errors.Error(qualifiedPattern.Value, $"Match case pattern of type '{FormatType(patternType)}' is not comparable to match expression type '{FormatType(matchType)}'.");

        if (TryGetSwitchCaseKey(qualifiedPattern.Value, out var caseKey) && !seenCaseValues.TryAdd(caseKey, qualifiedPattern))
            _errors.Error(qualifiedPattern.Value, $"Duplicate match case value '{caseKey}'.{FormatPreviousDeclarationSuffix(seenCaseValues[caseKey])}");

        if (IsEnumType(matchType) && TryEvaluateConstIntExpr(qualifiedPattern.Value, out var enumCaseValue, out _))
            seenEnumCaseValues.Add(enumCaseValue);
        else if (IsBoolType(matchType) && qualifiedPattern.Value is BuiltinExpr { Name: "true" or "false" } builtin)
            seenBoolCaseValues.Add(builtin.Name == "true");
    }
    private bool CheckScalarMatchExhaustiveness(
        MatchStmt matchStmt,
        TypeNode matchType,
        EnumNode? enumDefinition,
        HashSet<long> seenEnumCaseValues,
        HashSet<bool> seenBoolCaseValues)
    {
        if (matchStmt.ElseBody.Count > 0)
            return false;

        if (enumDefinition != null)
            return CheckScalarEnumMatchExhaustiveness(matchStmt, matchType, enumDefinition, seenEnumCaseValues);

        if (IsBoolType(matchType))
            return CheckScalarBoolMatchExhaustiveness(matchStmt, seenBoolCaseValues);

        return false;
    }
    private bool CheckScalarEnumMatchExhaustiveness(
        MatchStmt matchStmt,
        TypeNode matchType,
        EnumNode enumDefinition,
        HashSet<long> seenEnumCaseValues)
    {
        var missingMembers = GetMissingEnumMembers(enumDefinition, seenEnumCaseValues);
        if (missingMembers.Count > 0)
        {
            _errors.Error(matchStmt, $"Match over enum '{FormatType(matchType)}' must cover all members or provide an else branch. Missing: {string.Join(", ", missingMembers)}.");
            return false;
        }

        return true;
    }
    private bool CheckScalarBoolMatchExhaustiveness(MatchStmt matchStmt, HashSet<bool> seenBoolCaseValues)
    {
        var missingValues = new List<string>();
        if (!seenBoolCaseValues.Contains(false))
            missingValues.Add("false");
        if (!seenBoolCaseValues.Contains(true))
            missingValues.Add("true");

        if (missingValues.Count > 0)
        {
            _errors.Error(matchStmt, $"Match over bool must cover both values or provide an else branch. Missing: {string.Join(", ", missingValues)}.");
            return false;
        }

        return true;
    }
    private void CheckUnionMatchCase(
        MatchCase matchCase,
        TypeNode matchType,
        UnionNode? unionDefinition,
        Dictionary<string, MatchPattern> seenVariants,
        List<FlowOutcome> caseOutcomes)
    {
        if (!TryResolveUnionMatchCase(matchCase, matchType, unionDefinition, out var resolvedCase))
        {
            caseOutcomes.Add(CheckBlock(matchCase.Body));
            return;
        }

        TrackSeenUnionVariant(matchCase.Pattern, resolvedCase.VariantExpr, resolvedCase.VariantName, seenVariants);
        caseOutcomes.Add(CheckUnionMatchCaseBody(matchCase, resolvedCase.BindingPattern));
    }
    private bool TryResolveUnionMatchCase(
        MatchCase matchCase,
        TypeNode matchType,
        UnionNode? unionDefinition,
        out (Expr VariantExpr, string VariantName, UnionMatchPattern? BindingPattern) resolvedCase)
    {
        resolvedCase = default;
        if (!TryGetUnionMatchPattern(matchCase, matchType, unionDefinition, out var variantExpr, out var bindingPattern))
            return false;

        if (!TryResolveUnionMatchVariantReference(variantExpr, out var ownerType, out var variantName))
            return false;

        UpdateUnionMatchBindingMetadata(bindingPattern, ownerType, variantName);

        if (!ValidateUnionMatchVariantOwner(variantExpr, ownerType, matchType, variantName))
            return false;

        if (!TryBindUnionMatchVariant(bindingPattern, variantExpr, variantName, matchType, unionDefinition))
            return false;

        resolvedCase = (variantExpr, variantName, bindingPattern);
        return true;
    }
    private bool TryResolveUnionMatchVariantReference(Expr variantExpr, out TypeNode ownerType, out string variantName)
    {
        if (TryResolveStaticVariantReference(variantExpr, out ownerType, out variantName))
            return true;

        _errors.Error(variantExpr, "Union match patterns must be qualified variant names.");
        ownerType = null!;
        variantName = "";
        return false;
    }
    private static void UpdateUnionMatchBindingMetadata(UnionMatchPattern? bindingPattern, TypeNode ownerType, string variantName)
    {
        if (bindingPattern == null)
            return;

        bindingPattern.ResolvedUnionName = QualifiedNames.GetFullName(ownerType.NamespacePath, ownerType.Name);
        bindingPattern.VariantName = variantName;
    }
    private bool TryGetUnionMatchPattern(
        MatchCase matchCase,
        TypeNode matchType,
        UnionNode? unionDefinition,
        out Expr variantExpr,
        out UnionMatchPattern? bindingPattern)
    {
        bindingPattern = null;
        if (matchCase.Pattern is QualifiedMatchPattern qualifiedPattern)
        {
            variantExpr = qualifiedPattern.Value;
            return true;
        }

        if (matchCase.Pattern is UnionMatchPattern unionPattern)
        {
            variantExpr = unionPattern.Variant;
            bindingPattern = unionPattern;
            return true;
        }

        _errors.Error(matchCase.Pattern, $"Match over union '{FormatType(matchType)}' requires union-variant patterns like '{unionDefinition?.Name}.Variant(payload)'.");
        variantExpr = new InvalidExpr();
        return false;
    }
    private bool ValidateUnionMatchVariantOwner(Expr variantExpr, TypeNode ownerType, TypeNode matchType, string variantName)
    {
        var expectedTagType = GetUnionTagType(matchType);
        var ownerMatches = TypeHelpers.SameType(ownerType, matchType) || TypeHelpers.SameType(ownerType, expectedTagType);
        if (ownerMatches)
            return true;

        _errors.Error(variantExpr, $"Match case variant '{FormatType(ownerType)}.{variantName}' does not belong to union '{FormatType(matchType)}'.");
        return false;
    }
    private bool TryBindUnionMatchVariant(
        UnionMatchPattern? bindingPattern,
        Expr variantExpr,
        string variantName,
        TypeNode matchType,
        UnionNode? unionDefinition)
    {
        var variant = unionDefinition?.Variants.FirstOrDefault(candidate => candidate.Name == variantName);
        if (variant == null)
        {
            _errors.Error(variantExpr, $"Union '{FormatType(matchType)}' does not have a variant named '{variantName}'.");
            return false;
        }

        if (bindingPattern != null)
            bindingPattern.BindingType = variant.TypeName.Clone();

        return true;
    }
    private void TrackSeenUnionVariant(
        MatchPattern matchPattern,
        Expr variantExpr,
        string variantName,
        Dictionary<string, MatchPattern> seenVariants)
    {
        if (!seenVariants.TryAdd(variantName, matchPattern))
            _errors.Error(variantExpr, $"Duplicate match case variant '{variantName}'.{FormatPreviousDeclarationSuffix(seenVariants[variantName])}");
    }
    private FlowOutcome CheckUnionMatchCaseBody(MatchCase matchCase, UnionMatchPattern? bindingPattern)
    {
        PushScopedState();
        try
        {
            RegisterUnionMatchBinding(bindingPattern);
            return CheckUnionMatchCaseBlock(matchCase);
        }
        finally
        {
            PopScopedState();
        }
    }
    private void RegisterUnionMatchBinding(UnionMatchPattern? bindingPattern)
    {
        if (string.IsNullOrEmpty(bindingPattern?.BindingName))
            return;

        _symbolTable.DefineVariable(bindingPattern.BindingName!, bindingPattern.BindingType!.Clone());
        _declarationNodeScopes.Peek()[bindingPattern.BindingName!] = bindingPattern;
    }
    private FlowOutcome CheckUnionMatchCaseBlock(MatchCase matchCase)
    {
        return CheckBlock(matchCase.Body, pushScope: false);
    }
    private bool CheckUnionMatchExhaustiveness(
        MatchStmt matchStmt,
        TypeNode matchType,
        UnionNode? unionDefinition,
        Dictionary<string, MatchPattern> seenVariants)
    {
        if (unionDefinition == null || matchStmt.ElseBody.Count > 0)
            return false;

        var missingVariants = unionDefinition.Variants
            .Where(variant => !seenVariants.ContainsKey(variant.Name))
            .Select(variant => $"{unionDefinition.Name}.{variant.Name}")
            .ToList();
        if (missingVariants.Count > 0)
        {
            _errors.Error(matchStmt, $"Match over union '{FormatType(matchType)}' must cover all variants or provide an else branch. Missing: {string.Join(", ", missingVariants)}.");
            return false;
        }

        return true;
    }
    private FlowOutcome CombineMatchFlow(List<FlowOutcome> caseOutcomes, List<Statement> elseBody, bool isExhaustive)
    {
        if (elseBody.Count == 0)
            return isExhaustive ? ReduceUniformTerminatingMatchFlow(caseOutcomes) : FlowOutcome.FallsThrough;

        AppendElseMatchFlow(caseOutcomes, elseBody);
        return ReduceUniformTerminatingMatchFlow(caseOutcomes);
    }
    private void AppendElseMatchFlow(List<FlowOutcome> caseOutcomes, List<Statement> elseBody)
    {
        var elseFlow = CheckBlock(elseBody);
        caseOutcomes.Add(elseFlow);
    }
    private static FlowOutcome ReduceUniformTerminatingMatchFlow(List<FlowOutcome> caseOutcomes)
    {
        if (caseOutcomes.Count > 0 &&
            caseOutcomes.All(outcome => outcome == caseOutcomes[0] && outcome != FlowOutcome.FallsThrough))
        {
            return caseOutcomes[0];
        }

        return FlowOutcome.FallsThrough;
    }
    private FlowOutcome CombineSwitchFlow(List<FlowOutcome> caseOutcomes, List<Statement> elseBody)
    {
        if (elseBody.Count == 0)
            return FlowOutcome.FallsThrough;

        AppendElseMatchFlow(caseOutcomes, elseBody);
        return ReduceUniformTerminatingMatchFlow(caseOutcomes);
    }
    private FlowOutcome CheckBlock(List<Statement> statements, bool pushScope = true)
    {
        EnterBlockScope(pushScope);

        try
        {
            return CheckBlockStatements(statements);
        }
        finally
        {
            ExitBlockScope(pushScope);
        }
    }
    private void EnterBlockScope(bool pushScope)
    {
        if (pushScope)
            PushScopedState();
    }
    private void ExitBlockScope(bool pushScope)
    {
        if (pushScope)
            PopScopedState();
    }
    private FlowOutcome CheckBlockStatements(List<Statement> statements)
    {
        var flow = FlowOutcome.FallsThrough;
        foreach (var statement in statements)
        {
            if (flow != FlowOutcome.FallsThrough)
            {
                ReportUnreachableStatement(statement);
                break;
            }

            flow = CheckBlockStatement(statement);
        }

        return flow;
    }
    private FlowOutcome CheckBlockStatement(Statement statement)
    {
        NormalizeAliasReferences(statement);
        return CheckStatement(statement);
    }
    private void ReportUnreachableStatement(Statement statement)
    {
        _errors.Warning(statement, "Unreachable statement.");
    }
    private void CheckVariableDeclaration(VariableDeclarationNode varDecl)
    {
        ResolveAlignmentAttribute(varDecl.Attributes, varDecl.AlignExpr, varDecl, $"Variable '{varDecl.Name}' Alignment");
        ValidateTypeReference(varDecl.TypeName, varDecl);
        ValidateVariableAttributes(varDecl, isGlobal: false);
        if (_declarationNodeScopes.Peek().TryGetValue(varDecl.Name, out var previousDeclaration))
        {
            _errors.Error(varDecl, $"Duplicate local declaration '{varDecl.Name}'.{FormatPreviousDeclarationSuffix(previousDeclaration)}");
            return;
        }

        _symbolTable.DefineVariable(varDecl.Name, varDecl.TypeName, varDecl.IsConst);
        _declarationNodeScopes.Peek()[varDecl.Name] = varDecl;

        CheckVariableInitializer(varDecl);
        TryRecordConstValue(varDecl);
    }
    private void PushScopedState()
    {
        _symbolTable.PushScope();
        _constValueScopes.Push(new Dictionary<string, long>(StringComparer.Ordinal));
        _declarationNodeScopes.Push(new Dictionary<string, Node>(StringComparer.Ordinal));
        _catchErrorVarScopes.Push(new HashSet<string>(StringComparer.Ordinal));
    }
    private void PopScopedState()
    {
        _symbolTable.PopScope();
        if (_constValueScopes.Count > 1)
            _constValueScopes.Pop();
        if (_declarationNodeScopes.Count > 1)
            _declarationNodeScopes.Pop();
        if (_catchErrorVarScopes.Count > 0)
            _catchErrorVarScopes.Pop();
    }
    private bool IsCatchErrorVar(string name)
    {
        return _catchErrorVarScopes.Any(scope => scope.Contains(name));
    }
    private bool TryLookupConstValue(string name, out long value)
    {
        foreach (var scope in _constValueScopes)
        {
            if (scope.TryGetValue(name, out value))
                return true;
        }

        value = 0;
        return false;
    }
    private void TryRecordConstValue(VariableDeclarationNode declaration)
    {
        if (!CanRecordConstValue(declaration))
            return;

        if (!HasRecordableConstType(declaration.TypeName))
            return;

        if (!TryEvaluateRecordedConstValue(declaration, out var value))
            return;

        _constValueScopes.Peek()[declaration.Name] = value;
    }
    private bool CanRecordConstValue(VariableDeclarationNode declaration)
    {
        return declaration.IsConst &&
            declaration.Value != null &&
            _constValueScopes.Count > 0;
    }
    private bool HasRecordableConstType(TypeNode type)
    {
        return IsIntegerTypeName(type.Name) &&
            !type.IsPointer &&
            !type.IsSlice &&
            !type.IsFunction &&
            !type.IsErrorUnion &&
            type.ArraySize == null;
    }
    private bool TryEvaluateRecordedConstValue(VariableDeclarationNode declaration, out long value)
    {
        return TryEvaluateConstIntExpr(declaration.Value!, out value, out _);
    }
}
