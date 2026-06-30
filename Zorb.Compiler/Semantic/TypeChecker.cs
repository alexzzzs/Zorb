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
    private enum FlowOutcome
    {
        FallsThrough,
        Returns,
        Breaks,
        Continues
    }

    private sealed record ResolvedCallInfo(string DisplayName, List<Parameter> Parameters, TypeNode? ReturnType);
    private sealed record ResolvedFieldSymbolInfo(string SourceName, string ResolvedName, SymbolInfo SymbolInfo, bool ResolvedViaAlias);

    private readonly SymbolTable _symbolTable = new();
    private readonly ErrorReporter _errors = new();
    private readonly HashSet<string> _numericTypes = new() { "i8", "i16", "i32", "i64", "u8", "u16", "u32", "u64" };
    private readonly HashSet<string> _processedImports = new();
    private readonly Stack<Dictionary<string, HashSet<string>>> _importAliasScopes = new();
    private readonly Stack<Dictionary<string, long>> _constValueScopes = new();
    private readonly Stack<Dictionary<string, Node>> _declarationNodeScopes = new();
    private readonly Stack<HashSet<string>> _catchErrorVarScopes = new();
    private readonly Stack<HashSet<string>> _typeParameterScopes = new();
    private readonly Dictionary<string, string> _errorSymbols = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _errorValues = new();
    private readonly Dictionary<Expr, TypeNode> _checkedExpressionTypes = new();
    private IReadOnlyDictionary<string, IReadOnlyList<Node>>? _parsedFilesByPath;
    private string _currentDir = ".";
    private FunctionDecl? _currentFunction;
    private int _loopDepth;

    private readonly Stack<HashSet<string>> _fileScopes = new();
    private readonly Dictionary<string, List<string>> _fileExports = new();

    public SymbolTable SymbolTable => _symbolTable;
    public ErrorReporter Errors => _errors;

    public TypeNode? GetCheckedExpressionType(Expr expression)
    {
        return _checkedExpressionTypes.TryGetValue(expression, out var type)
            ? type.Clone()
            : null;
    }

    private static readonly HashSet<string> NumericOperators = new()
    {
        "+", "-", "*", "/", "%", "<<", ">>", "&", "|", "^"
    };

    private static readonly HashSet<string> ComparisonOperators = new()
    {
        ">", "<", ">=", "<=", "==", "!="
    };

    private static readonly HashSet<string> LogicalOperators = new()
    {
        "&&", "||"
    };

    private void MakeVisible(string name)
    {
        if (_fileScopes.Count > 0)
            _fileScopes.Peek().Add(name);
    }

    private void RegisterImportAlias(string alias, IEnumerable<string> exports)
    {
        if (_importAliasScopes.Count == 0)
            return;

        _importAliasScopes.Peek()[alias] = new HashSet<string>(exports, StringComparer.Ordinal);
    }

    private bool TryResolveAliasQualifiedName(string name, out string resolvedName)
    {
        resolvedName = name;

        if (!TrySplitAliasQualifiedName(name, out var alias, out var remainder))
            return false;

        if (!TryResolveAliasExport(alias, remainder))
            return false;

        resolvedName = remainder;
        return true;
    }

    private static bool TrySplitAliasQualifiedName(string name, out string alias, out string remainder)
    {
        var dotIndex = name.IndexOf('.');
        if (dotIndex < 0)
        {
            alias = "";
            remainder = "";
            return false;
        }

        alias = name[..dotIndex];
        remainder = name[(dotIndex + 1)..];
        return true;
    }

    private bool TryResolveAliasExport(string alias, string remainder)
    {
        foreach (var scope in _importAliasScopes)
        {
            if (!scope.TryGetValue(alias, out var exports))
                continue;

            return exports.Contains(remainder);
        }

        return false;
    }

    private string ResolveQualifiedName(string name)
    {
        return TryResolveAliasQualifiedName(name, out var resolvedName)
            ? resolvedName
            : name;
    }

    private void NormalizeTypeReferenceInPlace(TypeNode type)
    {
        NormalizeContainedTypeReferences(type);
        if (!TryResolveNormalizedTypeName(type, out var resolvedName))
            return;

        QualifiedNames.ApplyResolvedQualifiedName(type, resolvedName);
        NormalizeSelfReferentialErrorUnionType(type);
    }

    private void NormalizeContainedTypeReferences(TypeNode type)
    {
        NormalizeArraySizeReference(type);

        foreach (var typeArgument in type.TypeArguments)
            NormalizeTypeReferenceInPlace(typeArgument);

        if (NormalizeFunctionTypeReference(type))
            return;

        NormalizeErrorUnionInnerType(type);
    }

    private void NormalizeArraySizeReference(TypeNode type)
    {
        if (type.ArraySizeExpr != null)
            type.ArraySizeExpr = NormalizeAliasReferences(type.ArraySizeExpr);
    }

    private bool NormalizeFunctionTypeReference(TypeNode type)
    {
        if (!type.IsFunction)
            return false;

        if (type.ReturnType != null)
            NormalizeTypeReferenceInPlace(type.ReturnType);

        foreach (var paramType in type.ParamTypes)
            NormalizeTypeReferenceInPlace(paramType);

        return true;
    }

    private void NormalizeErrorUnionInnerType(TypeNode type)
    {
        if (type.IsErrorUnion && type.ErrorInnerType != null && !ReferenceEquals(type.ErrorInnerType, type))
            NormalizeTypeReferenceInPlace(type.ErrorInnerType);
    }

    private bool TryResolveNormalizedTypeName(TypeNode type, out string resolvedName)
    {
        var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
        return TryResolveAliasQualifiedName(fullName, out resolvedName);
    }

    private static void NormalizeSelfReferentialErrorUnionType(TypeNode type)
    {
        if (type.IsErrorUnion && type.ErrorInnerType != null && ReferenceEquals(type.ErrorInnerType, type))
            type.ErrorInnerType = type.Clone();
    }

    private Expr NormalizeAliasReferences(Expr expr)
    {
        switch (expr)
        {
            case BinaryExpr bin:
                bin.Left = NormalizeAliasReferences(bin.Left);
                bin.Right = NormalizeAliasReferences(bin.Right);
                return bin;

            case CallExpr call:
                foreach (var typeArgument in call.TypeArguments)
                    NormalizeTypeReferenceInPlace(typeArgument);
                for (int i = 0; i < call.Args.Count; i++)
                    call.Args[i] = NormalizeAliasReferences(call.Args[i]);

                if (call.TargetExpr != null)
                {
                    call.TargetExpr = NormalizeAliasReferences(call.TargetExpr);
                    return call;
                }

                if (!call.NamespacePath.Any() && TryResolveAliasQualifiedName(call.Name, out var resolvedBareCallName))
                    QualifiedNames.ApplyResolvedQualifiedName(call, resolvedBareCallName);

                return call;

            case IndexExpr idx:
                idx.Target = NormalizeAliasReferences(idx.Target);
                idx.Index = NormalizeAliasReferences(idx.Index);
                return idx;

            case FieldExpr field:
                field.Target = NormalizeAliasReferences(field.Target);
                foreach (var typeArgument in field.TypeArguments)
                    NormalizeTypeReferenceInPlace(typeArgument);
                if (QualifiedNames.TryGetQualifiedName(field) is string qualifiedName &&
                    TryResolveAliasQualifiedName(qualifiedName, out var resolvedQualifiedName))
                    field.ResolvedQualifiedName = resolvedQualifiedName;
                else
                    field.ResolvedQualifiedName = null;
                return field;

            case IdentifierExpr identifier:
                foreach (var typeArgument in identifier.TypeArguments)
                    NormalizeTypeReferenceInPlace(typeArgument);
                identifier.Name = ResolveQualifiedName(identifier.Name);
                return identifier;

            case UnaryExpr unary:
                unary.Operand = NormalizeAliasReferences(unary.Operand);
                return unary;

            case CastExpr cast:
                cast.Expr = NormalizeAliasReferences(cast.Expr);
                return cast;

            case SizeofExpr sizeofExpr:
                return sizeofExpr;

            case TypeReferenceExpr typeReference:
                NormalizeTypeReferenceInPlace(typeReference.TypeName);
                return typeReference;

            case StructLiteralExpr structLiteral:
                NormalizeTypeReferenceInPlace(structLiteral.TypeName);
                for (int i = 0; i < structLiteral.Fields.Count; i++)
                    structLiteral.Fields[i].Value = NormalizeAliasReferences(structLiteral.Fields[i].Value);
                return structLiteral;

            case ArrayLiteralExpr arrayLiteral:
                NormalizeTypeReferenceInPlace(arrayLiteral.TypeName);
                for (int i = 0; i < arrayLiteral.Elements.Count; i++)
                    arrayLiteral.Elements[i] = NormalizeAliasReferences(arrayLiteral.Elements[i]);
                return arrayLiteral;

            case CatchExpr catchExpr:
                catchExpr.Left = NormalizeAliasReferences(catchExpr.Left);
                for (int i = 0; i < catchExpr.CatchBody.Count; i++)
                    catchExpr.CatchBody[i] = NormalizeAliasReferences(catchExpr.CatchBody[i]);
                return catchExpr;
            default:
                return expr;
        }
    }

    private Statement NormalizeAliasReferences(Statement stmt)
    {
        switch (stmt)
        {
            case VariableDeclarationNode varDecl:
                if (varDecl.Value != null)
                    varDecl.Value = NormalizeAliasReferences(varDecl.Value);
                return varDecl;

            case ExpressionStatement exprStmt:
                exprStmt.Expression = NormalizeAliasReferences(exprStmt.Expression);
                return exprStmt;

            case AssignStmt assign:
                assign.Target = NormalizeAliasReferences(assign.Target);
                assign.Value = NormalizeAliasReferences(assign.Value);
                return assign;

            case ReturnNode returnNode:
                if (returnNode.Value != null)
                    returnNode.Value = NormalizeAliasReferences(returnNode.Value);
                return returnNode;

            case IfStmt ifStmt:
                ifStmt.Condition = NormalizeAliasReferences(ifStmt.Condition);
                for (int i = 0; i < ifStmt.Body.Count; i++)
                    ifStmt.Body[i] = NormalizeAliasReferences(ifStmt.Body[i]);
                for (int i = 0; i < ifStmt.ElseBody.Count; i++)
                    ifStmt.ElseBody[i] = NormalizeAliasReferences(ifStmt.ElseBody[i]);
                return ifStmt;

            case WhileStmt whileStmt:
                whileStmt.Condition = NormalizeAliasReferences(whileStmt.Condition);
                for (int i = 0; i < whileStmt.Body.Count; i++)
                    whileStmt.Body[i] = NormalizeAliasReferences(whileStmt.Body[i]);
                return whileStmt;

            case ForStmt forStmt:
                if (forStmt.Initializer != null)
                    forStmt.Initializer = NormalizeAliasReferences(forStmt.Initializer);
                if (forStmt.Condition != null)
                    forStmt.Condition = NormalizeAliasReferences(forStmt.Condition);
                if (forStmt.Update != null)
                    forStmt.Update = NormalizeAliasReferences(forStmt.Update);
                for (int i = 0; i < forStmt.Body.Count; i++)
                    forStmt.Body[i] = NormalizeAliasReferences(forStmt.Body[i]);
                return forStmt;

            case SwitchStmt switchStmt:
                switchStmt.Expression = NormalizeAliasReferences(switchStmt.Expression);
                for (int i = 0; i < switchStmt.Cases.Count; i++)
                {
                    switchStmt.Cases[i].Value = NormalizeAliasReferences(switchStmt.Cases[i].Value);
                    for (int j = 0; j < switchStmt.Cases[i].Body.Count; j++)
                        switchStmt.Cases[i].Body[j] = NormalizeAliasReferences(switchStmt.Cases[i].Body[j]);
                }
                for (int i = 0; i < switchStmt.ElseBody.Count; i++)
                    switchStmt.ElseBody[i] = NormalizeAliasReferences(switchStmt.ElseBody[i]);
                return switchStmt;

            case MatchStmt matchStmt:
                matchStmt.Expression = NormalizeAliasReferences(matchStmt.Expression);
                for (int i = 0; i < matchStmt.Cases.Count; i++)
                {
                    matchStmt.Cases[i].Pattern = NormalizeAliasReferences(matchStmt.Cases[i].Pattern);
                    for (int j = 0; j < matchStmt.Cases[i].Body.Count; j++)
                        matchStmt.Cases[i].Body[j] = NormalizeAliasReferences(matchStmt.Cases[i].Body[j]);
                }
                for (int i = 0; i < matchStmt.ElseBody.Count; i++)
                    matchStmt.ElseBody[i] = NormalizeAliasReferences(matchStmt.ElseBody[i]);
                return matchStmt;

            case BreakStmt breakStmt:
                return breakStmt;

            case AsmStatementNode asmStmt:
                for (int i = 0; i < asmStmt.Outputs.Count; i++)
                    asmStmt.Outputs[i].Expression = NormalizeAliasReferences(asmStmt.Outputs[i].Expression);
                for (int i = 0; i < asmStmt.Inputs.Count; i++)
                    asmStmt.Inputs[i].Expression = NormalizeAliasReferences(asmStmt.Inputs[i].Expression);
                return asmStmt;

            default:
                return stmt;
        }
    }

    private MatchPattern NormalizeAliasReferences(MatchPattern pattern)
    {
        switch (pattern)
        {
            case QualifiedMatchPattern qualifiedPattern:
                qualifiedPattern.Value = NormalizeAliasReferences(qualifiedPattern.Value);
                return qualifiedPattern;

            case UnionMatchPattern unionPattern:
                unionPattern.Variant = NormalizeAliasReferences(unionPattern.Variant);
                return unionPattern;

            default:
                return pattern;
        }
    }

    private bool CheckVisibility(string fullName)
    {
        if (_symbolTable.IsLocal(fullName)) return true;
        if (_fileScopes.Count > 0 && _fileScopes.Peek().Contains(fullName)) return true;
        return false;
    }

    private void ReportNotVisible(Node context, string subjectKind, string fullName)
    {
        _errors.Error(
            context,
            $"{subjectKind} '{fullName}' is not visible from this file. It may be private or not re-exported by an import.");
    }

    private ResolvedFieldSymbolInfo? ResolveQualifiedFieldSymbol(FieldExpr field)
    {
        if (!TryGetResolvableQualifiedFieldName(field, out var sourceQualifiedName))
            return null;

        if (IsLocalQualifiedFieldReference(sourceQualifiedName))
            return null;

        var resolvedFieldName = ResolveQualifiedFieldLookupName(field, sourceQualifiedName, out var resolvedViaAlias);
        if (!_symbolTable.TryLookup(resolvedFieldName, out var qualifiedFieldInfo) || qualifiedFieldInfo == null)
            return null;

        field.ResolvedQualifiedName = resolvedFieldName;
        return new ResolvedFieldSymbolInfo(
            sourceQualifiedName,
            resolvedFieldName,
            qualifiedFieldInfo,
            resolvedViaAlias);
    }

    private bool TryGetResolvableQualifiedFieldName(FieldExpr field, out string sourceQualifiedName)
    {
        sourceQualifiedName = QualifiedNames.TryGetQualifiedName(field) ?? "";
        return !string.IsNullOrEmpty(sourceQualifiedName);
    }

    private bool IsLocalQualifiedFieldReference(string sourceQualifiedName)
    {
        return _symbolTable.IsLocal(GetLeftmostQualifiedIdentifier(sourceQualifiedName));
    }

    private static string GetLeftmostQualifiedIdentifier(string qualifiedName)
    {
        var leftmostSeparator = qualifiedName.IndexOf('.');
        return leftmostSeparator >= 0
            ? qualifiedName[..leftmostSeparator]
            : qualifiedName;
    }

    private string ResolveQualifiedFieldLookupName(FieldExpr field, string sourceQualifiedName, out bool resolvedViaAlias)
    {
        var qualifiedFieldName = field.ResolvedQualifiedName ?? sourceQualifiedName;
        resolvedViaAlias = field.ResolvedQualifiedName != null;
        if (resolvedViaAlias || !string.Equals(sourceQualifiedName, qualifiedFieldName, StringComparison.Ordinal))
            return qualifiedFieldName;

        return TryResolveAliasQualifiedName(qualifiedFieldName, out var aliasResolvedFieldName)
            ? MarkResolvedViaAlias(aliasResolvedFieldName, out resolvedViaAlias)
            : qualifiedFieldName;
    }

    private static string MarkResolvedViaAlias(string resolvedFieldName, out bool resolvedViaAlias)
    {
        resolvedViaAlias = true;
        return resolvedFieldName;
    }

    private bool IsVisibleResolvedFieldSymbol(ResolvedFieldSymbolInfo resolvedField)
    {
        return resolvedField.ResolvedViaAlias || CheckVisibility(resolvedField.ResolvedName);
    }

    private static bool IsErrorConstantName(string name)
    {
        return name.StartsWith("Error_", StringComparison.Ordinal) && name.Length > "Error_".Length;
    }

    private static string GetErrorCodeName(string symbolName)
    {
        return symbolName["Error_".Length..];
    }

    private static string GetErrorSymbolName(string errorCode)
    {
        return $"Error_{errorCode}";
    }

    private static bool TryGetErrorDeclarationValue(VariableDeclarationNode declaration, out long value)
    {
        switch (declaration.Value)
        {
            case NumberExpr number:
                value = number.Value;
                return true;

            case UnaryExpr { Operator: "-", Operand: NumberExpr number }:
                value = -number.Value;
                return true;

            default:
                value = 0;
                return false;
        }
    }

    private void RegisterErrorDeclaration(VariableDeclarationNode declaration)
    {
        if (!IsErrorConstantName(declaration.Name))
            return;

        if (!ValidateErrorDeclarationType(declaration) ||
            !TryGetValidatedErrorDeclarationValue(declaration, out var errorValue))
            return;

        var errorCode = GetErrorCodeName(declaration.Name);
        if (!TryRegisterErrorSymbol(declaration, errorCode) ||
            !TryRegisterErrorValue(declaration, errorValue))
            return;

        _errorSymbols[errorCode] = declaration.Name;
        _errorValues[errorValue] = declaration.Name;
    }

    private bool ValidateErrorDeclarationType(VariableDeclarationNode declaration)
    {
        if (declaration.TypeName.Name == "i32" &&
            !declaration.TypeName.IsPointer &&
            declaration.TypeName.ArraySize == null &&
            !declaration.TypeName.IsErrorUnion &&
            !declaration.TypeName.IsFunction)
        {
            return true;
        }

        _errors.Error(declaration, $"Error declaration '{declaration.Name}' must have type i32.");
        return false;
    }

    private bool TryGetValidatedErrorDeclarationValue(VariableDeclarationNode declaration, out long errorValue)
    {
        if (TryGetErrorDeclarationValue(declaration, out errorValue))
            return true;

        _errors.Error(declaration, $"Error declaration '{declaration.Name}' must use a numeric literal initializer.");
        return false;
    }

    private bool TryRegisterErrorSymbol(VariableDeclarationNode declaration, string errorCode)
    {
        if (!_errorSymbols.TryGetValue(errorCode, out var existingSymbol) ||
            string.Equals(existingSymbol, declaration.Name, StringComparison.Ordinal))
        {
            return true;
        }

        _errors.Error(declaration, $"Error code '{errorCode}' is already declared as '{existingSymbol}'.");
        return false;
    }

    private bool TryRegisterErrorValue(VariableDeclarationNode declaration, long errorValue)
    {
        if (!_errorValues.TryGetValue(errorValue, out var existingValueSymbol) ||
            string.Equals(existingValueSymbol, declaration.Name, StringComparison.Ordinal))
        {
            return true;
        }

        _errors.Error(
            declaration,
            $"Error value '{errorValue}' is already declared as '{existingValueSymbol}'. Distinct error names must use distinct numeric values.");
        return false;
    }

    private bool TryResolveDeclaredErrorSymbol(string errorCode, out string symbolName)
    {
        symbolName = GetErrorSymbolName(errorCode);
        if (!TryGetRegisteredErrorSymbol(errorCode, out var declaredSymbol))
            return false;

        symbolName = declaredSymbol;
        return IsVisibleDeclaredErrorSymbol(declaredSymbol);
    }

    private bool TryGetRegisteredErrorSymbol(string errorCode, out string declaredSymbol)
    {
        return _errorSymbols.TryGetValue(errorCode, out declaredSymbol!);
    }

    private bool IsVisibleDeclaredErrorSymbol(string declaredSymbol)
    {
        return _symbolTable.IsDefined(declaredSymbol) && CheckVisibility(declaredSymbol);
    }

    private static string? TryGetQualifiedName(Expr expr)
    {
        return QualifiedNames.TryGetQualifiedName(expr);
    }

    public void Check(IReadOnlyList<Node> nodes, string currentDir = ".", IReadOnlyDictionary<string, IReadOnlyList<Node>>? parsedFilesByPath = null)
    {
        ResetCompilationState();
        _currentDir = currentDir;
        _parsedFilesByPath = parsedFilesByPath;
        _fileScopes.Push(new HashSet<string>());
        _importAliasScopes.Push(new Dictionary<string, HashSet<string>>(StringComparer.Ordinal));
        _constValueScopes.Push(new Dictionary<string, long>(StringComparer.Ordinal));
        _declarationNodeScopes.Push(new Dictionary<string, Node>(StringComparer.Ordinal));
        try
        {
            InitializeBuiltins();
            CheckNodes(nodes, currentDir);
        }
        finally
        {
            _declarationNodeScopes.Pop();
            _constValueScopes.Pop();
            _importAliasScopes.Pop();
            _fileScopes.Pop();
            _parsedFilesByPath = null;
        }
    }

    private void ResetCompilationState()
    {
        _symbolTable.Clear();
        _errors.Clear();
        _processedImports.Clear();
        _importAliasScopes.Clear();
        _constValueScopes.Clear();
        _declarationNodeScopes.Clear();
        _catchErrorVarScopes.Clear();
        _typeParameterScopes.Clear();
        _errorSymbols.Clear();
        _errorValues.Clear();
        _checkedExpressionTypes.Clear();
        _fileScopes.Clear();
        _fileExports.Clear();
        _parsedFilesByPath = null;
        _currentDir = ".";
        _currentFunction = null;
        _loopDepth = 0;
    }

    public void CheckNodes(IReadOnlyList<Node> nodes, string currentDir = ".")
    {
        var declaredInThisFile = new Dictionary<string, Node>(StringComparer.Ordinal);
        RegisterTopLevelDeclarations(nodes, currentDir, declaredInThisFile);
        ValidateTopLevelDeclarationsAndBodies(nodes);
    }















































































































































































































































































































}
