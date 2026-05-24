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

public class TypeChecker
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
    private readonly Dictionary<string, string> _errorSymbols = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _errorValues = new();
    private IReadOnlyDictionary<string, IReadOnlyList<Node>>? _parsedFilesByPath;
    private string _currentDir = ".";
    private FunctionDecl? _currentFunction;
    private int _loopDepth;

    private readonly Stack<HashSet<string>> _fileScopes = new();
    private readonly Dictionary<string, List<string>> _fileExports = new();

    public SymbolTable SymbolTable => _symbolTable;
    public ErrorReporter Errors => _errors;

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

        var dotIndex = name.IndexOf('.');
        if (dotIndex < 0)
            return false;

        var alias = name[..dotIndex];
        var remainder = name[(dotIndex + 1)..];

        foreach (var scope in _importAliasScopes)
        {
            if (!scope.TryGetValue(alias, out var exports))
                continue;

            if (!exports.Contains(remainder))
                return false;

            resolvedName = remainder;
            return true;
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
        if (type.ArraySizeExpr != null)
            type.ArraySizeExpr = NormalizeAliasReferences(type.ArraySizeExpr);

        if (type.IsFunction)
        {
            if (type.ReturnType != null)
                NormalizeTypeReferenceInPlace(type.ReturnType);
            foreach (var paramType in type.ParamTypes)
                NormalizeTypeReferenceInPlace(paramType);
            return;
        }

        if (type.IsErrorUnion && type.ErrorInnerType != null && !ReferenceEquals(type.ErrorInnerType, type))
            NormalizeTypeReferenceInPlace(type.ErrorInnerType);

        var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);

        if (!TryResolveAliasQualifiedName(fullName, out var resolvedName))
            return;

        QualifiedNames.ApplyResolvedQualifiedName(type, resolvedName);

        if (type.IsErrorUnion && type.ErrorInnerType != null && ReferenceEquals(type.ErrorInnerType, type))
        {
            type.ErrorInnerType = type.Clone();
        }
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
                if (QualifiedNames.TryGetQualifiedName(field) is string qualifiedName &&
                    TryResolveAliasQualifiedName(qualifiedName, out var resolvedQualifiedName))
                    field.ResolvedQualifiedName = resolvedQualifiedName;
                else
                    field.ResolvedQualifiedName = null;
                return field;

            case UnaryExpr unary:
                unary.Operand = NormalizeAliasReferences(unary.Operand);
                return unary;

            case CastExpr cast:
                cast.Expr = NormalizeAliasReferences(cast.Expr);
                return cast;

            case SizeofExpr sizeofExpr:
                return sizeofExpr;

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

            case IdentifierExpr ident:
                ident.Name = ResolveQualifiedName(ident.Name);
                return ident;

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
        var sourceQualifiedName = QualifiedNames.TryGetQualifiedName(field);
        if (string.IsNullOrEmpty(sourceQualifiedName))
            return null;

        var leftmostSeparator = sourceQualifiedName.IndexOf('.');
        var leftmostIdentifier = leftmostSeparator >= 0
            ? sourceQualifiedName[..leftmostSeparator]
            : sourceQualifiedName;
        if (_symbolTable.IsLocal(leftmostIdentifier))
            return null;

        var qualifiedFieldName = field.ResolvedQualifiedName ?? sourceQualifiedName;
        var resolvedFieldName = qualifiedFieldName;
        var aliasResolvedFieldName = resolvedFieldName;
        var resolvedViaAlias = field.ResolvedQualifiedName != null;
        if (!resolvedViaAlias && string.Equals(sourceQualifiedName, qualifiedFieldName, StringComparison.Ordinal))
            resolvedViaAlias = TryResolveAliasQualifiedName(qualifiedFieldName, out aliasResolvedFieldName);
        if (resolvedViaAlias)
            resolvedFieldName = aliasResolvedFieldName;

        if (!_symbolTable.TryLookup(resolvedFieldName, out var qualifiedFieldInfo) || qualifiedFieldInfo == null)
            return null;

        field.ResolvedQualifiedName = resolvedFieldName;
        return new ResolvedFieldSymbolInfo(
            sourceQualifiedName ?? qualifiedFieldName,
            resolvedFieldName,
            qualifiedFieldInfo,
            resolvedViaAlias);
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

        if (declaration.TypeName.Name != "i32" ||
            declaration.TypeName.IsPointer ||
            declaration.TypeName.ArraySize != null ||
            declaration.TypeName.IsErrorUnion ||
            declaration.TypeName.IsFunction)
        {
            _errors.Error(declaration, $"Error declaration '{declaration.Name}' must have type i32.");
            return;
        }

        if (!TryGetErrorDeclarationValue(declaration, out var errorValue))
        {
            _errors.Error(declaration, $"Error declaration '{declaration.Name}' must use a numeric literal initializer.");
            return;
        }

        var errorCode = GetErrorCodeName(declaration.Name);
        if (_errorSymbols.TryGetValue(errorCode, out var existingSymbol) && !string.Equals(existingSymbol, declaration.Name, StringComparison.Ordinal))
        {
            _errors.Error(declaration, $"Error code '{errorCode}' is already declared as '{existingSymbol}'.");
            return;
        }

        _errorSymbols[errorCode] = declaration.Name;

        if (_errorValues.TryGetValue(errorValue, out var existingValueSymbol) &&
            !string.Equals(existingValueSymbol, declaration.Name, StringComparison.Ordinal))
        {
            _errors.Error(
                declaration,
                $"Error value '{errorValue}' is already declared as '{existingValueSymbol}'. Distinct error names must use distinct numeric values.");
            return;
        }

        _errorValues[errorValue] = declaration.Name;
    }

    private bool TryResolveDeclaredErrorSymbol(string errorCode, out string symbolName)
    {
        symbolName = GetErrorSymbolName(errorCode);
        if (!_errorSymbols.TryGetValue(errorCode, out var declaredSymbol))
            return false;

        symbolName = declaredSymbol;
        return _symbolTable.IsDefined(declaredSymbol) && CheckVisibility(declaredSymbol);
    }

    private static string? TryGetQualifiedName(Expr expr)
    {
        return QualifiedNames.TryGetQualifiedName(expr);
    }

    public void Check(IReadOnlyList<Node> nodes, string currentDir = ".", IReadOnlyDictionary<string, IReadOnlyList<Node>>? parsedFilesByPath = null)
    {
        _currentDir = currentDir;
        _parsedFilesByPath = parsedFilesByPath;
        _fileScopes.Push(new HashSet<string>());
        _importAliasScopes.Push(new Dictionary<string, HashSet<string>>(StringComparer.Ordinal));
        _constValueScopes.Push(new Dictionary<string, long>(StringComparer.Ordinal));
        _declarationNodeScopes.Push(new Dictionary<string, Node>(StringComparer.Ordinal));
        InitializeBuiltins();

        CheckNodes(nodes, currentDir);
        
        _declarationNodeScopes.Pop();
        _constValueScopes.Pop();
        _importAliasScopes.Pop();
        _fileScopes.Pop();
        _parsedFilesByPath = null;
    }

    public void CheckNodes(IReadOnlyList<Node> nodes, string currentDir = ".")
    {
        var declaredInThisFile = new Dictionary<string, Node>(StringComparer.Ordinal);

        // Pass 1: Register all declarations so they are visible within the current file
        foreach (var node in nodes)
        {
            if (node is ImportNode importNode)
            {
                ProcessImport(importNode, currentDir);
            }
            else if (node is FunctionDecl fn) {
                NormalizeTypeReferenceInPlace(fn.ReturnType);
                foreach (var param in fn.Parameters)
                    NormalizeTypeReferenceInPlace(param.TypeName);
                var fullName = QualifiedNames.GetFullName(fn.NamespacePath, fn.Name);
                if (!declaredInThisFile.TryAdd(fullName, fn))
                    _errors.Error(fn, $"Duplicate top-level declaration '{fullName}'.{FormatPreviousDeclarationSuffix(declaredInThisFile[fullName])}");
                MakeVisible(fullName);
                if (!_symbolTable.IsDefined(fullName)) _symbolTable.DefineFunction(fullName, fn.ReturnType, fn.Parameters);
            }
            else if (node is StructNode sn) {
                var fullName = QualifiedNames.GetFullName(sn.NamespacePath, sn.Name);
                if (!declaredInThisFile.TryAdd(fullName, sn))
                    _errors.Error(sn, $"Duplicate top-level declaration '{fullName}'.{FormatPreviousDeclarationSuffix(declaredInThisFile[fullName])}");
                MakeVisible(fullName);
                if (!_symbolTable.IsDefined(fullName)) _symbolTable.DefineStruct(fullName, sn);
                foreach (var field in sn.Fields)
                    NormalizeTypeReferenceInPlace(field.TypeName);
            }
            else if (node is EnumNode enumNode) {
                NormalizeTypeReferenceInPlace(enumNode.UnderlyingType);
                var fullName = QualifiedNames.GetFullName(enumNode.NamespacePath, enumNode.Name);
                if (!declaredInThisFile.TryAdd(fullName, enumNode))
                    _errors.Error(enumNode, $"Duplicate top-level declaration '{fullName}'.{FormatPreviousDeclarationSuffix(declaredInThisFile[fullName])}");
                MakeVisible(fullName);
                if (!_symbolTable.IsDefined(fullName))
                    _symbolTable.DefineEnum(fullName, enumNode);
                RegisterEnumMembers(enumNode);
            }
            else if (node is UnionNode unionNode) {
                var fullName = QualifiedNames.GetFullName(unionNode.NamespacePath, unionNode.Name);
                if (!declaredInThisFile.TryAdd(fullName, unionNode))
                    _errors.Error(unionNode, $"Duplicate top-level declaration '{fullName}'.{FormatPreviousDeclarationSuffix(declaredInThisFile[fullName])}");
                MakeVisible(fullName);
                if (!_symbolTable.IsDefined(fullName))
                    _symbolTable.DefineUnion(fullName, unionNode);
                RegisterUnionTagEnum(unionNode);
            }
            else if (node is VariableDeclarationNode vd) {
                NormalizeTypeReferenceInPlace(vd.TypeName);
                ValidateVariableAttributes(vd, isGlobal: true);
                RegisterErrorDeclaration(vd);
                if (!declaredInThisFile.TryAdd(vd.Name, vd))
                    _errors.Error(vd, $"Duplicate top-level declaration '{vd.Name}'.{FormatPreviousDeclarationSuffix(declaredInThisFile[vd.Name])}");
                MakeVisible(vd.Name);
                if (!_symbolTable.IsDefined(vd.Name)) _symbolTable.DefineVariable(vd.Name, vd.TypeName, vd.IsConst);
                TryRecordConstValue(vd);
            }
        }

        // Pass 2: Process imports and check bodies
        foreach (var node in nodes)
        {
            if (node is VariableDeclarationNode varDecl)
            {
                ResolveAlignmentAttribute(varDecl.Attributes, varDecl.AlignExpr, varDecl, $"Variable '{varDecl.Name}' Alignment");
                ValidateTypeReference(varDecl.TypeName, varDecl);
                CheckVariableInitializer(varDecl);
            }
            else if (node is StructNode structNode)
            {
                ResolveStructAttributes(structNode);
                foreach (var field in structNode.Fields)
                    ValidateTypeReference(field.TypeName, field);
                var fullName = QualifiedNames.GetFullName(structNode.NamespacePath, structNode.Name);
                ValidateStructAttributes(structNode, fullName);
            }
            else if (node is EnumNode enumNode)
            {
                ValidateEnumDeclaration(enumNode);
            }
            else if (node is UnionNode unionNode)
            {
                ValidateUnionDeclaration(unionNode);
            }
            else if (node is FunctionDecl functionDecl)
            {
                ResolveAlignmentAttribute(functionDecl.Attributes, functionDecl.AlignExpr, functionDecl, "Function Alignment");
                ValidateTypeReference(functionDecl.ReturnType, functionDecl);
                foreach (var param in functionDecl.Parameters)
                    ValidateTypeReference(param.TypeName, functionDecl);

                if (functionDecl.IsExtern) continue;

                _currentFunction = functionDecl;
                PushScopedState();

                foreach (var param in functionDecl.Parameters)
                {
                    if (_declarationNodeScopes.Peek().TryGetValue(param.Name, out var previousParameter))
                    {
                        _errors.Error(functionDecl, $"Duplicate parameter '{param.Name}' in function '{functionDecl.Name}'. Earlier parameter is in the same function header.");
                        continue;
                    }
                    _symbolTable.DefineParameter(param.Name, param.TypeName);
                    _declarationNodeScopes.Peek()[param.Name] = functionDecl;
                }

                var flow = CheckBlock(functionDecl.Body, pushScope: false);
                if (FunctionRequiresReturn(functionDecl) && flow != FlowOutcome.Returns)
                {
                    _errors.Error(functionDecl, $"Function '{functionDecl.Name}' may exit without returning a value");
                }

                PopScopedState();
                _currentFunction = null;
            }
        }
    }

    private void ProcessImport(ImportNode importNode, string currentDir)
    {
        if (importNode.Alias == "c") return;

        var fullPath = Path.GetFullPath(Path.IsPathRooted(importNode.Path)
            ? importNode.Path
            : Path.Combine(currentDir, importNode.Path));

        if (_fileExports.TryGetValue(fullPath, out var cachedExports))
        {
            if (!string.IsNullOrEmpty(importNode.Alias))
                RegisterImportAlias(importNode.Alias, cachedExports);
            else
                foreach (var sym in cachedExports) MakeVisible(sym);
            return;
        }

        if (_processedImports.Contains(fullPath))
            return;

        if (!File.Exists(fullPath))
        {
            _processedImports.Add(fullPath);
            _errors.Error(importNode, $"Import file not found: '{importNode.Path}' resolved to '{fullPath}'.");
            return;
        }

        var dir = Path.GetDirectoryName(fullPath) ?? ".";
        IReadOnlyList<Node> importedNodes;
        if (_parsedFilesByPath != null && _parsedFilesByPath.TryGetValue(fullPath, out var preParsedNodes))
        {
            importedNodes = preParsedNodes;
        }
        else
        {
            var source = File.ReadAllText(fullPath);
            List<Token> tokens;
            try
            {
                var lexer = new Zorb.Compiler.Lexer.Lexer(source, fullPath);
                tokens = lexer.Tokenize();
            }
            catch (LexerException ex)
            {
                _errors.Error(ex.Message, ex.Line, ex.Column, ex.File);
                return;
            }
            var parser = new Zorb.Compiler.Parser.Parser(tokens, fullPath, _errors);
            importedNodes = parser.ParseProgram();
        }

        var exports = new List<string>();
        foreach (var node in importedNodes)
        {
            if (node is FunctionDecl fn && fn.IsExported) exports.Add(QualifiedNames.GetFullName(fn.NamespacePath, fn.Name));
            else if (node is StructNode sn && sn.IsExported) exports.Add(QualifiedNames.GetFullName(sn.NamespacePath, sn.Name));
            else if (node is EnumNode en && en.IsExported)
            {
                var enumName = QualifiedNames.GetFullName(en.NamespacePath, en.Name);
                exports.Add(enumName);
                exports.AddRange(en.Members.Select(member => $"{enumName}.{member.Name}"));
            }
            else if (node is UnionNode unionNode && unionNode.IsExported)
            {
                var unionName = QualifiedNames.GetFullName(unionNode.NamespacePath, unionNode.Name);
                var tagEnumName = GetUnionTagEnumFullName(unionNode);
                exports.Add(unionName);
                exports.Add(tagEnumName);
                exports.AddRange(unionNode.Variants.Select(variant => $"{tagEnumName}.{variant.Name}"));
            }
            else if (node is VariableDeclarationNode vd && vd.IsExported) exports.Add(vd.Name);
        }
        
        _fileExports[fullPath] = exports;
        _processedImports.Add(fullPath);

        if (!string.IsNullOrEmpty(importNode.Alias))
            RegisterImportAlias(importNode.Alias, exports);
        else
            foreach (var sym in exports) MakeVisible(sym);
        var previousDir = _currentDir;
        _currentDir = dir;

        _fileScopes.Push(new HashSet<string>());
        _importAliasScopes.Push(new Dictionary<string, HashSet<string>>(StringComparer.Ordinal));
        MakeVisible("syscall");
        MakeVisible("Builtin.IsLinux");
        MakeVisible("Builtin.IsWindows");
        MakeVisible("Builtin.IsBareMetal");
        MakeVisible("Builtin.IsX86_64");
        MakeVisible("Builtin.IsAArch64");

        CheckNodes(importedNodes, dir);

        _importAliasScopes.Pop();
        _fileScopes.Pop();
        _currentDir = previousDir;
    }

    private void InitializeBuiltins()
    {
        var syscallParams = new List<Parameter>
        {
            new Parameter("arg0", new TypeNode { Name = "i64" }),
            new Parameter("arg1", new TypeNode { Name = "i64" }),
            new Parameter("arg2", new TypeNode { Name = "i64" }),
            new Parameter("arg3", new TypeNode { Name = "i64" }),
            new Parameter("arg4", new TypeNode { Name = "i64" }),
            new Parameter("arg5", new TypeNode { Name = "i64" }),
        };
        _symbolTable.DefineFunction("syscall", new TypeNode { Name = "i64" }, syscallParams);
        MakeVisible("syscall");

        _symbolTable.DefineVariable("Builtin.IsLinux", new TypeNode { Name = "bool" });
        MakeVisible("Builtin.IsLinux");
        
        _symbolTable.DefineVariable("Builtin.IsWindows", new TypeNode { Name = "bool" });
        MakeVisible("Builtin.IsWindows");

        _symbolTable.DefineVariable("Builtin.IsBareMetal", new TypeNode { Name = "bool" });
        MakeVisible("Builtin.IsBareMetal");

        _symbolTable.DefineVariable("Builtin.IsX86_64", new TypeNode { Name = "bool" });
        MakeVisible("Builtin.IsX86_64");

        _symbolTable.DefineVariable("Builtin.IsAArch64", new TypeNode { Name = "bool" });
        MakeVisible("Builtin.IsAArch64");

        var numericTypes = new[] { "i8", "i16", "i32", "i64", "u8", "u16", "u32", "u64", "bool", "void" };
        foreach (var t in numericTypes)
        {
            _symbolTable.DefineVariable(t, new TypeNode { Name = t });
            MakeVisible(t);
        }
    }

    private static bool FunctionRequiresReturn(FunctionDecl functionDecl)
    {
        return functionDecl.ReturnType.Name != "void" || functionDecl.ReturnType.IsPointer || functionDecl.ReturnType.IsErrorUnion;
    }

    private void CheckBoolCondition(Expr condition)
    {
        CheckExpression(condition);
        var conditionType = GetExpressionType(condition, reportErrors: false);
        if (conditionType == null)
            return;

        if (!IsBoolType(conditionType))
        {
            _errors.Error(condition, $"Condition must have type 'bool', got '{FormatType(conditionType)}'. Compare explicitly if you meant truthiness.");
        }
    }

    private static List<string> GetMissingEnumMembers(EnumNode enumDefinition, HashSet<long> seenEnumCaseValues)
    {
        return enumDefinition.Members
            .Where(member => member.ResolvedValue.HasValue && !seenEnumCaseValues.Contains(member.ResolvedValue.Value))
            .Select(member => $"{enumDefinition.Name}.{member.Name}")
            .ToList();
    }

    private ResolvedCallInfo? ResolveCallInfo(CallExpr call, bool reportErrors)
    {
        call.ResolvedFunctionType = null;

        if (call.TargetExpr != null)
        {
            call.ResolvedQualifiedName = null;
            var qualifiedName = QualifiedNames.TryGetQualifiedName(call.TargetExpr);
            var resolvedQualifiedName = qualifiedName;
            var targetResolvedViaAlias = !string.IsNullOrEmpty(qualifiedName) &&
                TryResolveAliasQualifiedName(qualifiedName, out resolvedQualifiedName);
            call.ResolvedTargetQualifiedName = null;

            if (!string.IsNullOrEmpty(resolvedQualifiedName) && _symbolTable.TryLookup(resolvedQualifiedName, out var qualifiedInfo))
            {
                if (!targetResolvedViaAlias && !CheckVisibility(resolvedQualifiedName))
                {
                    if (reportErrors)
                        ReportNotVisible(call, "Function", qualifiedName ?? resolvedQualifiedName);
                    return null;
                }

                if (!qualifiedInfo!.IsCallable())
                {
                    if (reportErrors)
                        _errors.Error(call, $"'{qualifiedName}' is not a function or callable variable");
                    return null;
                }

                var resolvedTargetName = targetResolvedViaAlias
                    ? resolvedQualifiedName
                    : qualifiedInfo!.Name;
                call.ResolvedTargetQualifiedName = resolvedTargetName;
                var callableInfo = qualifiedInfo!;
                call.ResolvedFunctionType = callableInfo.GetCallableFunctionType();
                return new ResolvedCallInfo(
                    qualifiedName ?? "call target",
                    callableInfo.GetCallableParameters(),
                    callableInfo.GetCallableReturnType());
            }

            var targetType = GetExpressionType(call.TargetExpr, reportErrors: false);
            if (targetType == null || !targetType.IsFunction)
            {
                if (reportErrors)
                {
                    _errors.Error(call, !string.IsNullOrEmpty(qualifiedName)
                        ? $"Function '{qualifiedName}' is not declared or is not visible from this file."
                        : "Expression is not a function or callable");
                }
                return null;
            }

            call.ResolvedFunctionType = targetType.Clone();
            return new ResolvedCallInfo(
                !string.IsNullOrEmpty(qualifiedName) ? qualifiedName : "function pointer",
                targetType.ParamTypes.Select(type => new Parameter("", type)).ToList(),
                targetType.ReturnType?.Clone());
        }

        call.ResolvedTargetQualifiedName = null;
        var displayName = call.Name;
        var resolvedViaAlias = false;
        if (call.NamespacePath.Any())
        {
            var sourceName = QualifiedNames.GetFullName(call.NamespacePath, call.Name);
            if (TryResolveAliasQualifiedName(sourceName, out var resolvedCallName))
            {
                QualifiedNames.ApplyResolvedQualifiedName(call, resolvedCallName);
                displayName = sourceName;
                resolvedViaAlias = true;
            }
        }

        SymbolInfo? symbolInfo = null;
        var fullName = QualifiedNames.GetFullName(call.NamespacePath, call.Name);
        if (_symbolTable.TryLookup(fullName, out var qualifiedSymbol))
        {
            symbolInfo = qualifiedSymbol;
            if (call.NamespacePath.Any() && !resolvedViaAlias && !CheckVisibility(fullName))
            {
                if (reportErrors)
                    ReportNotVisible(call, "Function", fullName);
                return null;
            }
        }
        else if (_symbolTable.TryLookup(call.Name, out var bareSymbol))
        {
            symbolInfo = bareSymbol;
            if (!CheckVisibility(call.Name))
            {
                if (reportErrors)
                    ReportNotVisible(call, "Function", call.Name);
                return null;
            }
        }
        else
        {
            if (reportErrors)
                _errors.Error(call, $"Call to undeclared function '{fullName}'");
            return null;
        }

        if (!symbolInfo!.IsCallable())
        {
            if (reportErrors)
                _errors.Error(call, $"'{call.Name}' is not a function or callable variable");
            return null;
        }

        var resolvedSymbolInfo = symbolInfo!;
        call.ResolvedQualifiedName = resolvedSymbolInfo.Name;
        call.ResolvedFunctionType = resolvedSymbolInfo.GetCallableFunctionType();

        return new ResolvedCallInfo(
            displayName,
            resolvedSymbolInfo.GetCallableParameters(),
            resolvedSymbolInfo.GetCallableReturnType());
    }

    private FlowOutcome CheckStatement(Statement stmt)
    {
        switch (stmt)
        {
            case VariableDeclarationNode varDecl:
                CheckVariableDeclaration(varDecl);
                return FlowOutcome.FallsThrough;

            case ExpressionStatement exprStmt:
                CheckExpression(exprStmt.Expression);
                return FlowOutcome.FallsThrough;

            case AssignStmt assign:
                CheckAssignment(assign);
                return FlowOutcome.FallsThrough;

            case ReturnNode returnNode:
                if (returnNode.Value != null)
                {
                    CheckExpression(returnNode.Value);

                    if (_currentFunction != null)
                    {
                        var expectedType = _currentFunction.ReturnType;

                        if (returnNode.Value is ErrorExpr)
                        {
                            if (!expectedType.IsErrorUnion)
                            {
                                _errors.Error(returnNode.Value, $"Function '{_currentFunction.Name}' does not return an error union (!), so you cannot return an error code.");
                            }
                            else if (returnNode.Value is ErrorExpr errExpr && !TryResolveDeclaredErrorSymbol(errExpr.ErrorCode, out _))
                            {
                                _errors.Error(returnNode, $"Use of undeclared error 'error.{errExpr.ErrorCode}'. Declare it first with 'error {errExpr.ErrorCode} = ...'.");
                            }
                        }
                        else
                        {
                            var exprType = GetExpressionType(returnNode.Value);

                            if (expectedType.IsErrorUnion)
                            {
                                var successType = expectedType.ErrorInnerType ?? expectedType;
                                if (!IsAssignableTo(successType, returnNode.Value, exprType))
                                {
                                    if (TryBuildNumericConversionDiagnostic(successType, returnNode.Value, exprType, out var numericDiagnostic))
                                        _errors.Error(returnNode, $"Cannot return expression of type '{FormatType(exprType)}' from function '{_currentFunction.Name}' with result type '{FormatType(expectedType)}'. {numericDiagnostic}");
                                    else
                                        _errors.Error(returnNode, $"Cannot return expression of type '{FormatType(exprType)}' from function '{_currentFunction.Name}' with result type '{FormatType(expectedType)}'. Return a success value of type '{FormatType(successType)}' or an error.");
                                }

                            }
                            else
                            {
                                if (!IsAssignableTo(expectedType, returnNode.Value, exprType))
                                {
                                    if (TryBuildNumericConversionDiagnostic(expectedType, returnNode.Value, exprType, out var numericDiagnostic))
                                        _errors.Error(returnNode, $"Function '{_currentFunction.Name}' returns '{FormatType(expectedType)}', but this return expression has type '{FormatType(exprType)}'. {numericDiagnostic}");
                                    else
                                        _errors.Error(returnNode, $"Function '{_currentFunction.Name}' returns '{FormatType(expectedType)}', but this return expression has type '{FormatType(exprType)}'.");
                                }
                            }
                        }
                    }
                }
                return FlowOutcome.Returns;

            case IfStmt ifStmt:
                CheckBoolCondition(ifStmt.Condition);
                var ifFlow = CheckBlock(ifStmt.Body);
                if (ifStmt.ElseBody.Count == 0)
                    return FlowOutcome.FallsThrough;

                var elseFlow = CheckBlock(ifStmt.ElseBody);
                if (ifFlow == elseFlow && ifFlow != FlowOutcome.FallsThrough)
                    return ifFlow;

                return FlowOutcome.FallsThrough;

            case WhileStmt whileStmt:
                CheckBoolCondition(whileStmt.Condition);
                _loopDepth++;
                try
                {
                    CheckBlock(whileStmt.Body);
                }
                finally
                {
                    _loopDepth--;
                }
                return FlowOutcome.FallsThrough;

            case ForStmt forStmt:
                PushScopedState();
                try
                {
                    if (forStmt.Initializer != null)
                        CheckStatement(forStmt.Initializer);

                    if (forStmt.Condition != null)
                    {
                        CheckBoolCondition(forStmt.Condition);
                    }

                    _loopDepth++;
                    try
                    {
                        CheckBlock(forStmt.Body);
                    }
                    finally
                    {
                        _loopDepth--;
                    }

                    if (forStmt.Update != null)
                        CheckStatement(forStmt.Update);
                }
                finally
                {
                    PopScopedState();
                }
                return FlowOutcome.FallsThrough;

            case SwitchStmt switchStmt:
                CheckExpression(switchStmt.Expression);
                var switchType = GetExpressionType(switchStmt.Expression);
                if (switchType != null && !IsSwitchOperandType(switchType))
                {
                    _errors.Error(switchStmt.Expression, $"Switch expression must have numeric, bool, or enum type, got '{FormatType(switchType)}'.");
                }

                var seenCaseValues = new Dictionary<string, Expr>(StringComparer.Ordinal);
                var seenEnumCaseValues = new HashSet<long>();
                var caseOutcomes = new List<FlowOutcome>();
                var isExhaustiveEnumSwitch = false;
                foreach (var switchCase in switchStmt.Cases)
                {
                    CheckExpression(switchCase.Value);
                    var caseType = GetExpressionType(switchCase.Value);
                    if (switchType != null && caseType != null && !AreEqualityComparableTypes(switchType, caseType))
                    {
                        _errors.Error(switchCase.Value, $"Switch case expression of type '{FormatType(caseType)}' is not comparable to switch expression type '{FormatType(switchType)}'.");
                    }

                    if (TryGetSwitchCaseKey(switchCase.Value, out var caseKey))
                    {
                        if (!seenCaseValues.TryAdd(caseKey, switchCase.Value))
                            _errors.Error(switchCase.Value, $"Duplicate switch case value '{caseKey}'.{FormatPreviousDeclarationSuffix(seenCaseValues[caseKey])}");
                    }

                    if (switchType != null && IsEnumType(switchType) && TryEvaluateConstIntExpr(switchCase.Value, out var enumCaseValue, out _))
                        seenEnumCaseValues.Add(enumCaseValue);

                    var caseFlow = CheckBlock(switchCase.Body);
                    caseOutcomes.Add(caseFlow);
                }

                if (switchType != null && IsEnumType(switchType) && switchStmt.ElseBody.Count == 0)
                {
                    var enumDefinition = LookupEnumDefinition(switchType);
                    if (enumDefinition != null)
                    {
                            var missingMembers = GetMissingEnumMembers(enumDefinition, seenEnumCaseValues);
                        if (missingMembers.Count > 0)
                            _errors.Error(switchStmt, $"Switch over enum '{FormatType(switchType)}' must cover all members or provide an else branch. Missing: {string.Join(", ", missingMembers)}.");
                        else
                            isExhaustiveEnumSwitch = true;
                    }
                }

                if (switchStmt.ElseBody.Count == 0)
                {
                    if (isExhaustiveEnumSwitch &&
                        caseOutcomes.Count > 0 &&
                        caseOutcomes.All(outcome => outcome == caseOutcomes[0] && outcome != FlowOutcome.FallsThrough))
                    {
                        return caseOutcomes[0];
                    }

                    return FlowOutcome.FallsThrough;
                }

                var switchElseFlow = CheckBlock(switchStmt.ElseBody);
                caseOutcomes.Add(switchElseFlow);

                if (caseOutcomes.Count > 0 && caseOutcomes.All(outcome => outcome == caseOutcomes[0] && outcome != FlowOutcome.FallsThrough))
                    return caseOutcomes[0];

                return FlowOutcome.FallsThrough;

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

    private FlowOutcome CheckMatchStatement(MatchStmt matchStmt)
    {
        CheckExpression(matchStmt.Expression);
        var matchType = GetExpressionType(matchStmt.Expression);
        if (matchType == null)
            return FlowOutcome.FallsThrough;

        if (!IsEnumType(matchType) && !IsUnionType(matchType))
        {
            _errors.Error(matchStmt.Expression, $"Match expression must have enum or union type, got '{FormatType(matchType)}'.");
            return FlowOutcome.FallsThrough;
        }

        return IsEnumType(matchType)
            ? CheckEnumMatchStatement(matchStmt, matchType)
            : CheckUnionMatchStatement(matchStmt, matchType);
    }

    private FlowOutcome CheckEnumMatchStatement(MatchStmt matchStmt, TypeNode matchType)
    {
        var enumDefinition = LookupEnumDefinition(matchType);
        var seenCaseValues = new Dictionary<string, MatchPattern>(StringComparer.Ordinal);
        var seenEnumCaseValues = new HashSet<long>();
        var caseOutcomes = new List<FlowOutcome>();
        var isExhaustive = false;

        foreach (var matchCase in matchStmt.Cases)
        {
            if (matchCase.Pattern is not QualifiedMatchPattern qualifiedPattern)
            {
                _errors.Error(matchCase.Pattern, $"Match over enum '{FormatType(matchType)}' requires enum-member patterns like '{enumDefinition?.Name}.Member'.");
                caseOutcomes.Add(CheckBlock(matchCase.Body));
                continue;
            }

            CheckExpression(qualifiedPattern.Value);
            var patternType = GetExpressionType(qualifiedPattern.Value);
            if (patternType != null && !TypeHelpers.SameType(matchType, patternType))
                _errors.Error(qualifiedPattern.Value, $"Match case pattern of type '{FormatType(patternType)}' does not match enum type '{FormatType(matchType)}'.");

            if (TryGetSwitchCaseKey(qualifiedPattern.Value, out var caseKey))
            {
                if (!seenCaseValues.TryAdd(caseKey, qualifiedPattern))
                    _errors.Error(qualifiedPattern.Value, $"Duplicate match case value '{caseKey}'.{FormatPreviousDeclarationSuffix(seenCaseValues[caseKey])}");
            }

            if (TryEvaluateConstIntExpr(qualifiedPattern.Value, out var enumCaseValue, out _))
                seenEnumCaseValues.Add(enumCaseValue);

            caseOutcomes.Add(CheckBlock(matchCase.Body));
        }

        if (enumDefinition != null && matchStmt.ElseBody.Count == 0)
        {
            var missingMembers = GetMissingEnumMembers(enumDefinition, seenEnumCaseValues);
            if (missingMembers.Count > 0)
                _errors.Error(matchStmt, $"Match over enum '{FormatType(matchType)}' must cover all members or provide an else branch. Missing: {string.Join(", ", missingMembers)}.");
            else
                isExhaustive = true;
        }

        return CombineMatchFlow(caseOutcomes, matchStmt.ElseBody, isExhaustive);
    }

    private FlowOutcome CheckUnionMatchStatement(MatchStmt matchStmt, TypeNode matchType)
    {
        var unionDefinition = LookupUnionDefinition(matchType);
        var seenVariants = new Dictionary<string, MatchPattern>(StringComparer.Ordinal);
        var caseOutcomes = new List<FlowOutcome>();
        var isExhaustive = false;

        foreach (var matchCase in matchStmt.Cases)
        {
            Expr variantExpr;
            UnionMatchPattern? bindingPattern = null;
            if (matchCase.Pattern is QualifiedMatchPattern qualifiedPattern)
            {
                variantExpr = qualifiedPattern.Value;
            }
            else if (matchCase.Pattern is UnionMatchPattern unionPattern)
            {
                variantExpr = unionPattern.Variant;
                bindingPattern = unionPattern;
            }
            else
            {
                _errors.Error(matchCase.Pattern, $"Match over union '{FormatType(matchType)}' requires union-variant patterns like '{unionDefinition?.Name}.Variant(payload)'.");
                caseOutcomes.Add(CheckBlock(matchCase.Body));
                continue;
            }

            var variantQualifiedName = TryGetQualifiedName(variantExpr);
            if (string.IsNullOrEmpty(variantQualifiedName))
            {
                _errors.Error(variantExpr, "Union match patterns must be qualified variant names.");
                caseOutcomes.Add(CheckBlock(matchCase.Body));
                continue;
            }

            var resolvedVariantName = TryResolveAliasQualifiedName(variantQualifiedName, out var aliasResolvedVariantName)
                ? aliasResolvedVariantName
                : variantQualifiedName;
            var lastDot = resolvedVariantName.LastIndexOf('.');
            if (lastDot < 0)
            {
                _errors.Error(variantExpr, "Union match patterns must reference a specific variant.");
                caseOutcomes.Add(CheckBlock(matchCase.Body));
                continue;
            }

            var resolvedUnionName = resolvedVariantName[..lastDot];
            var variantName = resolvedVariantName[(lastDot + 1)..];
            if (bindingPattern != null)
            {
                bindingPattern.ResolvedUnionName = resolvedUnionName;
                bindingPattern.VariantName = variantName;
            }

            var expectedUnionName = QualifiedNames.GetFullName(matchType.NamespacePath, matchType.Name);
            var expectedTagUnionName = expectedUnionName + ".Tag";
            if (!string.Equals(resolvedUnionName, expectedUnionName, StringComparison.Ordinal) &&
                !string.Equals(resolvedUnionName, expectedTagUnionName, StringComparison.Ordinal))
            {
                _errors.Error(variantExpr, $"Match case variant '{resolvedVariantName}' does not belong to union '{FormatType(matchType)}'.");
                caseOutcomes.Add(CheckBlock(matchCase.Body));
                continue;
            }

            var variant = unionDefinition?.Variants.FirstOrDefault(candidate => candidate.Name == variantName);
            if (variant == null)
            {
                _errors.Error(variantExpr, $"Union '{FormatType(matchType)}' does not have a variant named '{variantName}'.");
                caseOutcomes.Add(CheckBlock(matchCase.Body));
                continue;
            }

            if (bindingPattern != null)
                bindingPattern.BindingType = variant.TypeName.Clone();
            if (!seenVariants.TryAdd(variantName, matchCase.Pattern))
                _errors.Error(variantExpr, $"Duplicate match case variant '{variantName}'.{FormatPreviousDeclarationSuffix(seenVariants[variantName])}");

            PushScopedState();
            try
            {
                if (!string.IsNullOrEmpty(bindingPattern?.BindingName))
                {
                    _symbolTable.DefineVariable(bindingPattern.BindingName!, bindingPattern.BindingType!.Clone());
                    _declarationNodeScopes.Peek()[bindingPattern.BindingName!] = bindingPattern;
                }
                caseOutcomes.Add(CheckBlock(matchCase.Body, pushScope: false));
            }
            finally
            {
                PopScopedState();
            }
        }

        if (unionDefinition != null && matchStmt.ElseBody.Count == 0)
        {
            var missingVariants = unionDefinition.Variants
                .Where(variant => !seenVariants.ContainsKey(variant.Name))
                .Select(variant => $"{unionDefinition.Name}.{variant.Name}")
                .ToList();
            if (missingVariants.Count > 0)
                _errors.Error(matchStmt, $"Match over union '{FormatType(matchType)}' must cover all variants or provide an else branch. Missing: {string.Join(", ", missingVariants)}.");
            else
                isExhaustive = true;
        }

        return CombineMatchFlow(caseOutcomes, matchStmt.ElseBody, isExhaustive);
    }

    private FlowOutcome CombineMatchFlow(List<FlowOutcome> caseOutcomes, List<Statement> elseBody, bool isExhaustive)
    {
        if (elseBody.Count == 0)
        {
            if (isExhaustive &&
                caseOutcomes.Count > 0 &&
                caseOutcomes.All(outcome => outcome == caseOutcomes[0] && outcome != FlowOutcome.FallsThrough))
            {
                return caseOutcomes[0];
            }

            return FlowOutcome.FallsThrough;
        }

        var elseFlow = CheckBlock(elseBody);
        caseOutcomes.Add(elseFlow);
        if (caseOutcomes.Count > 0 && caseOutcomes.All(outcome => outcome == caseOutcomes[0] && outcome != FlowOutcome.FallsThrough))
            return caseOutcomes[0];

        return FlowOutcome.FallsThrough;
    }

    private FlowOutcome CheckBlock(List<Statement> statements, bool pushScope = true)
    {
        if (pushScope)
            PushScopedState();

        var flow = FlowOutcome.FallsThrough;
        foreach (var stmt in statements)
        {
            if (flow != FlowOutcome.FallsThrough)
            {
                _errors.Warning(stmt, "Unreachable statement.");
                break;
            }

            NormalizeAliasReferences(stmt);
            flow = CheckStatement(stmt);
        }

        if (pushScope)
            PopScopedState();

        return flow;
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
    }

    private void PopScopedState()
    {
        _symbolTable.PopScope();
        if (_constValueScopes.Count > 1)
            _constValueScopes.Pop();
        if (_declarationNodeScopes.Count > 1)
            _declarationNodeScopes.Pop();
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
        if (!declaration.IsConst || declaration.Value == null || _constValueScopes.Count == 0)
            return;

        if (!IsIntegerTypeName(declaration.TypeName.Name) ||
            declaration.TypeName.IsPointer ||
            declaration.TypeName.IsSlice ||
            declaration.TypeName.IsFunction ||
            declaration.TypeName.IsErrorUnion ||
            declaration.TypeName.ArraySize != null)
        {
            return;
        }

        if (!TryEvaluateConstIntExpr(declaration.Value, out var value, out _))
            return;

        _constValueScopes.Peek()[declaration.Name] = value;
    }

    private void RegisterEnumMembers(EnumNode enumNode)
    {
        if (_constValueScopes.Count == 0)
            return;

        var enumFullName = QualifiedNames.GetFullName(enumNode.NamespacePath, enumNode.Name);

        var enumType = new TypeNode
        {
            Name = enumNode.Name,
            NamespacePath = new List<string>(enumNode.NamespacePath)
        };

        var seenNames = new Dictionary<string, EnumMember>(StringComparer.Ordinal);
        long nextValue = 0;
        foreach (var member in enumNode.Members)
        {
            if (!seenNames.TryAdd(member.Name, member))
            {
                _errors.Error(member, $"Duplicate enum member '{enumFullName}.{member.Name}'.{FormatPreviousDeclarationSuffix(seenNames[member.Name])}");
                continue;
            }

            long resolvedValue;
            if (member.Value != null)
            {
                member.Value = NormalizeAliasReferences(member.Value);
                CheckExpression(member.Value);
                if (!TryEvaluateConstIntExpr(member.Value, out resolvedValue, out var constError))
                {
                    _errors.Error(member.Value, constError ?? $"Enum member '{enumFullName}.{member.Name}' must use a constant integer expression.");
                    resolvedValue = nextValue;
                }
            }
            else
            {
                resolvedValue = nextValue;
            }

            member.ResolvedValue = resolvedValue;
            nextValue = resolvedValue + 1;

            var memberFullName = $"{enumFullName}.{member.Name}";
            MakeVisible(memberFullName);
            if (!_symbolTable.IsDefined(memberFullName))
                _symbolTable.DefineVariable(memberFullName, enumType.Clone(), isConst: true);

            _constValueScopes.Peek()[memberFullName] = resolvedValue;
        }
    }

    private void RegisterUnionTagEnum(UnionNode unionNode)
    {
        var tagEnum = BuildUnionTagEnum(unionNode);
        var tagEnumFullName = GetUnionTagEnumFullName(unionNode);
        MakeVisible(tagEnumFullName);
        if (!_symbolTable.IsDefined(tagEnumFullName))
            _symbolTable.DefineEnum(tagEnumFullName, tagEnum);
        RegisterEnumMembers(tagEnum);
    }

    private void ValidateUnionDeclaration(UnionNode unionNode)
    {
        var unionFullName = QualifiedNames.GetFullName(unionNode.NamespacePath, unionNode.Name);

        var seenNames = new Dictionary<string, UnionVariant>(StringComparer.Ordinal);
        foreach (var variant in unionNode.Variants)
        {
            NormalizeTypeReferenceInPlace(variant.TypeName);
            ValidateTypeReference(variant.TypeName, variant);

            if (!seenNames.TryAdd(variant.Name, variant))
            {
                _errors.Error(variant, $"Union '{unionFullName}' declares variant '{variant.Name}' more than once.{FormatPreviousDeclarationSuffix(seenNames[variant.Name])}");
                continue;
            }

            if (variant.Name == "tag")
                _errors.Error(variant, $"Union '{unionFullName}' cannot declare a variant named 'tag' because '.tag' is reserved for the discriminator.");

            if (variant.TypeName.Name == "void" && !variant.TypeName.IsPointer && !variant.TypeName.IsSlice && variant.TypeName.ArraySize == null)
                _errors.Error(variant, $"Union variant '{unionFullName}.{variant.Name}' cannot have type 'void'.");
        }
    }

    private static string GetUnionTagEnumFullName(UnionNode unionNode)
    {
        return $"{QualifiedNames.GetFullName(unionNode.NamespacePath, unionNode.Name)}.Tag";
    }

    private static TypeNode GetUnionTagType(UnionNode unionNode)
    {
        return new TypeNode
        {
            Name = "Tag",
            NamespacePath = unionNode.NamespacePath
                .Concat(new[] { unionNode.Name })
                .ToList()
        };
    }

    private static EnumNode BuildUnionTagEnum(UnionNode unionNode)
    {
        return new EnumNode
        {
            Name = "Tag",
            NamespacePath = unionNode.NamespacePath
                .Concat(new[] { unionNode.Name })
                .ToList(),
            UnderlyingType = new TypeNode { Name = "i32" },
            Members = unionNode.Variants
                .Select((variant, index) => new EnumMember
                {
                    Name = variant.Name,
                    ResolvedValue = index
                })
                .ToList()
        };
    }

    private void ValidateEnumDeclaration(EnumNode enumNode)
    {
        ValidateTypeReference(enumNode.UnderlyingType, enumNode);
        var enumFullName = QualifiedNames.GetFullName(enumNode.NamespacePath, enumNode.Name);

        if (!IsIntegerScalarType(enumNode.UnderlyingType))
        {
            _errors.Error(enumNode, $"Enum '{enumFullName}' must use a built-in integer underlying type, got '{FormatType(enumNode.UnderlyingType)}'.");
            return;
        }

        var seenValues = new Dictionary<long, EnumMember>();
        foreach (var member in enumNode.Members)
        {
            if (member.Value != null)
            {
                member.Value = NormalizeAliasReferences(member.Value);
                CheckExpression(member.Value);
                if (!TryEvaluateConstIntExpr(member.Value, out var resolvedValue, out var constError))
                {
                    _errors.Error(member.Value, constError ?? $"Enum member '{enumFullName}.{member.Name}' must use a constant integer expression.");
                    continue;
                }

                member.ResolvedValue = resolvedValue;
            }

            if (member.ResolvedValue is not long memberValue)
                continue;

            if (!NumericLiteralFits(enumNode.UnderlyingType, memberValue))
                _errors.Error(member, $"Enum member '{enumFullName}.{member.Name}' value '{memberValue}' does not fit underlying type '{FormatType(enumNode.UnderlyingType)}'.");

            if (!seenValues.TryAdd(memberValue, member))
                _errors.Error(member, $"Duplicate enum value '{memberValue}' in enum '{enumFullName}'.{FormatPreviousDeclarationSuffix(seenValues[memberValue])}");
        }
    }

    private static bool IsIntegerTypeName(string typeName)
    {
        return typeName is "i8" or "i16" or "i32" or "i64" or "u8" or "u16" or "u32" or "u64";
    }

    private static bool IsIntegerScalarType(TypeNode type)
    {
        return IsIntegerTypeName(type.Name)
            && !type.IsPointer
            && !type.IsSlice
            && !type.IsFunction
            && !type.IsErrorUnion
            && type.ArraySize == null;
    }

    private void ResolveStructAttributes(StructNode structNode)
    {
        ResolveAlignmentAttribute(structNode.Attributes, structNode.AlignExpr, structNode, $"Struct '{structNode.Name}' Alignment");
        foreach (var field in structNode.Fields)
            ResolveOffsetAttribute(field);
    }

    private void ResolveOffsetAttribute(StructField field)
    {
        if (field.OffsetExpr == null)
            return;

        field.OffsetExpr = NormalizeAliasReferences(field.OffsetExpr);
        CheckExpression(field.OffsetExpr);

        var offsetType = GetExpressionType(field.OffsetExpr, reportErrors: false);
        if (offsetType == null || !IsNumericType(offsetType))
        {
            _errors.Error(field.OffsetExpr, $"Offset must have integer type, got '{FormatType(offsetType)}'.");
            return;
        }

        if (!TryEvaluateConstIntExpr(field.OffsetExpr, out var resolvedOffset, out var constError))
        {
            _errors.Error(field.OffsetExpr, constError ?? "Offset must be a constant integer expression.");
            return;
        }

        if (resolvedOffset < 0 || resolvedOffset > int.MaxValue)
        {
            _errors.Error(field.OffsetExpr, $"Offset '{resolvedOffset}' does not fit in compiler-supported byte offsets.");
            return;
        }

        field.Attributes.RemoveAll(attr => attr.StartsWith("offset(", StringComparison.Ordinal));
        field.Attributes.Add($"offset({resolvedOffset})");
    }

    private void ResolveAlignmentAttribute(List<string> attributes, Expr? alignExpr, Node context, string description)
    {
        if (alignExpr == null)
            return;

        alignExpr = NormalizeAliasReferences(alignExpr);
        CheckExpression(alignExpr);

        var alignType = GetExpressionType(alignExpr, reportErrors: false);
        if (alignType == null || !IsNumericType(alignType))
        {
            _errors.Error(alignExpr, $"{description} must have integer type, got '{FormatType(alignType)}'.");
            return;
        }

        if (!TryEvaluateConstIntExpr(alignExpr, out var resolvedAlignment, out var constError))
        {
            _errors.Error(alignExpr, constError ?? $"{description} must be a constant integer expression.");
            return;
        }

        if (resolvedAlignment <= 0 || resolvedAlignment > int.MaxValue)
        {
            _errors.Error(alignExpr, $"{description} '{resolvedAlignment}' does not fit in compiler-supported alignments.");
            return;
        }

        attributes.RemoveAll(attr => attr.StartsWith("align(", StringComparison.Ordinal));
        attributes.Add($"align({resolvedAlignment})");
    }

    private void ValidateVariableAttributes(VariableDeclarationNode varDecl, bool isGlobal)
    {
        var sectionName = StructLayout.GetSectionName(varDecl.Attributes);
        if (sectionName != null && !isGlobal)
            _errors.Error(varDecl, $"Variable '{varDecl.Name}' uses [section(\"{sectionName}\")], but section placement is only supported for global variables.");
    }

    private void ValidateStructAttributes(StructNode structNode, string fullName)
    {
        var seenFields = new Dictionary<string, StructField>(StringComparer.Ordinal);
        foreach (var field in structNode.Fields)
        {
            if (!seenFields.TryAdd(field.Name, field))
                _errors.Error(field, $"Struct '{fullName}' declares field '{field.Name}' more than once.{FormatPreviousDeclarationSuffix(seenFields[field.Name])}");
        }

        var explicitLayout = StructLayout.HasExplicitLayout(structNode);

        foreach (var field in structNode.Fields)
        {
            var explicitOffset = StructLayout.GetExplicitOffset(field);
            if (!explicitLayout && explicitOffset != null)
                _errors.Error(field, $"Field '{field.Name}' in struct '{fullName}' uses [offset(...)] but the struct is not marked [layout(explicit)].");
        }

        if (!explicitLayout && !structNode.Attributes.Contains("packed") && StructLayout.GetAlignment(structNode.Attributes) == null)
            return;

        if (!StructLayout.TryCompute(structNode, name => _symbolTable.LookupStructNode(name), out _, out var error) && error != null)
            _errors.Error(structNode, error);
    }

    private static string FormatPreviousDeclarationSuffix(Node previous)
    {
        return $" First declaration was at {Path.GetFileName(previous.File)}:{previous.Line}:{previous.Column}.";
    }

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
                CheckBinaryExpression(bin);
                break;

            case CallExpr call:
                CheckCallExpression(call);
                break;

            case IdentifierExpr ident:
                ident.Name = ResolveQualifiedName(ident.Name);
                if (!_symbolTable.IsDefined(ident.Name))
                {
                    _errors.Error(ident, $"Use of undeclared identifier '{ident.Name}'.");
                }
                else if (!CheckVisibility(ident.Name))
                {
                    ReportNotVisible(ident, "Symbol", ident.Name);
                }
                else if (_symbolTable.TryLookup(ident.Name, out var symbolInfo) && symbolInfo!.Kind == SymbolKind.Enum)
                {
                    _errors.Error(ident, $"Enum type '{ident.Name}' is not a value. Use a member such as '{ident.Name}.Member'.");
                }
                else if (_symbolTable.TryLookup(ident.Name, out symbolInfo) && symbolInfo!.Kind == SymbolKind.Union)
                {
                    _errors.Error(ident, $"Union type '{ident.Name}' is not a value. Construct it with a literal such as '{ident.Name}{{ Variant: value }}'.");
                }
                break;

            case ErrorNamespaceExpr:
                _errors.Error(expr, "Expected '.Name' after 'error' in expression.");
                break;

            case IndexExpr idx:
                CheckExpression(idx.Target);
                CheckExpression(idx.Index);
                var indexTargetType = GetExpressionType(idx.Target, reportErrors: false);
                if (indexTargetType != null && indexTargetType.ArraySize == null && !indexTargetType.IsPointer && !indexTargetType.IsSlice)
                    _errors.Error(idx.Target, $"Cannot index expression of type '{FormatType(indexTargetType)}'.");
                break;

            case FieldExpr field:
                if (ResolveQualifiedFieldSymbol(field) is ResolvedFieldSymbolInfo resolvedField)
                {
                    if (resolvedField.SymbolInfo.Kind == SymbolKind.Variable || resolvedField.SymbolInfo.Kind == SymbolKind.Function)
                    {
                        if (!IsVisibleResolvedFieldSymbol(resolvedField))
                            ReportNotVisible(field, "Symbol", resolvedField.ResolvedName);
                        break;
                    }

                    if (resolvedField.SymbolInfo.Kind == SymbolKind.Enum || resolvedField.SymbolInfo.Kind == SymbolKind.Union)
                    {
                        if (!IsVisibleResolvedFieldSymbol(resolvedField))
                        {
                            ReportNotVisible(field, resolvedField.SymbolInfo.Kind == SymbolKind.Enum ? "Enum" : "Union", resolvedField.ResolvedName);
                        }
                        else if (resolvedField.SymbolInfo.Kind == SymbolKind.Enum)
                        {
                            _errors.Error(field, $"Enum type '{resolvedField.ResolvedName}' is not a value. Use a member such as '{resolvedField.ResolvedName}.Member'.");
                        }
                        else
                        {
                            _errors.Error(field, $"Union type '{resolvedField.ResolvedName}' is not a value. Construct it with a literal such as '{resolvedField.ResolvedName}{{ Variant: value }}'.");
                        }
                        break;
                    }
                }

                CheckExpression(field.Target);
                var fieldTargetType = GetExpressionType(field.Target, reportErrors: false);
                if (fieldTargetType != null && fieldTargetType.IsSlice && field.Field is not ("ptr" or "len"))
                    _errors.Error(field, $"Slice values expose only '.ptr' and '.len', not '{field.Field}'.");
                else if (IsUnionType(fieldTargetType) && !IsValidUnionField(fieldTargetType!, field.Field))
                    _errors.Error(field, $"Union values expose '.tag' and declared variant fields. '{FormatType(fieldTargetType)}' does not have '{field.Field}'.");
                break;

            case UnaryExpr un:
                CheckExpression(un.Operand);
                var unaryOperandType = GetExpressionType(un.Operand, reportErrors: false);
                if (un.Operator == "!" && unaryOperandType != null && !IsBoolType(unaryOperandType))
                {
                    _errors.Error(un, $"Operator '!' requires a bool operand, got '{FormatType(unaryOperandType)}'.");
                }
                break;

            case CastExpr cast:
                CheckExpression(cast.Expr);
                var sourceType = GetExpressionType(cast.Expr);
                if (sourceType != null && sourceType.IsErrorUnion && !cast.TargetType.IsPointer && !cast.TargetType.IsErrorUnion)
                {
                    _errors.Error(cast, "Cannot cast Error Union to non-pointer type. Use .value field to unwrap.");
                }
                break;

            case StructLiteralExpr structLiteral:
                CheckStructLiteralExpression(structLiteral);
                break;

            case ArrayLiteralExpr arrayLiteral:
                CheckArrayLiteralExpression(arrayLiteral);
                break;

            case CatchExpr catchExpr:
                CheckExpression(catchExpr.Left);
                var catchType = GetExpressionType(catchExpr.Left);
                if (catchType == null || !catchType.IsErrorUnion)
                {
                    _errors.Error(catchExpr, "Catch requires an error-union expression");
                    break;
                }

                PushScopedState();
                _symbolTable.DefineVariable(catchExpr.ErrorVar, new TypeNode { Name = "i32" });
                foreach (var stmt in catchExpr.CatchBody)
                    CheckStatement(stmt);
                PopScopedState();
                break;

            case SizeofExpr sizeofExpr:
                ValidateTypeReference(sizeofExpr.TargetType, sizeofExpr);
                break;

            case BuiltinExpr builtin:
                break;

            case ErrorExpr errorExpr:
                if (!TryResolveDeclaredErrorSymbol(errorExpr.ErrorCode, out _))
                    _errors.Error(errorExpr, $"Use of undeclared error 'error.{errorExpr.ErrorCode}'. Declare it first with 'error {errorExpr.ErrorCode} = ...'.");
                break;
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
        if (varDecl.Value == null)
        {
            if (varDecl.IsConst)
                _errors.Error(varDecl, $"Const declaration '{varDecl.Name}' requires an initializer.");
            return;
        }

        varDecl.Value = NormalizeAliasReferences(varDecl.Value);

        if (_currentFunction == null && ContainsCatchExpression(varDecl.Value))
        {
            _errors.Error(varDecl.Value, "Catch expressions are not supported in global initializers.");
            return;
        }

        CheckExpression(varDecl.Value);

        if (_currentFunction == null && varDecl.TypeName.ArraySize != null && varDecl.Value is not ArrayLiteralExpr)
        {
            _errors.Error(varDecl, "Global array initializers must use array literals.");
            return;
        }

        var exprType = GetExpressionType(varDecl.Value);
        if (!IsAssignableTo(varDecl.TypeName, varDecl.Value, exprType))
        {
            if (TryBuildNumericConversionDiagnostic(varDecl.TypeName, varDecl.Value, exprType, out var numericDiagnostic))
                _errors.Error(varDecl, $"Cannot initialize variable '{varDecl.Name}' of type '{FormatType(varDecl.TypeName)}' from expression of type '{FormatType(exprType)}'. {numericDiagnostic}");
            else
                _errors.Error(varDecl, $"Cannot assign expression of type '{FormatType(exprType)}' to variable '{varDecl.Name}' of type '{FormatType(varDecl.TypeName)}'.");
            return;
        }

        if (_currentFunction == null && IsIntegerScalarType(varDecl.TypeName))
        {
            if (TryEvaluateConstIntExpr(varDecl.Value, out var foldedValue, out var constError))
            {
                varDecl.Value = new NumberExpr
                {
                    Value = foldedValue,
                    File = varDecl.Value.File,
                    Line = varDecl.Value.Line,
                    Column = varDecl.Value.Column,
                    Length = varDecl.Value.Length
                };
                TryRecordConstValue(varDecl);
            }
            else if (constError != null)
            {
                _errors.Error(varDecl.Value, constError);
            }
        }
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

    private void ValidateTypeReference(TypeNode type, Node context)
    {
        var sourceFullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
        var wasAliasQualified = TryResolveAliasQualifiedName(sourceFullName, out var resolvedTypeName);

        if (wasAliasQualified)
        {
            QualifiedNames.ApplyResolvedQualifiedName(type, resolvedTypeName);
        }
        else
        {
            NormalizeTypeReferenceInPlace(type);
        }

        if (type.ArraySizeExpr != null)
        {
            type.ArraySizeExpr = NormalizeAliasReferences(type.ArraySizeExpr);
            CheckExpression(type.ArraySizeExpr);

            var sizeType = GetExpressionType(type.ArraySizeExpr, reportErrors: false);
            if (sizeType == null || !IsNumericType(sizeType))
            {
                _errors.Error(type.ArraySizeExpr, $"Array size must have integer type, got '{FormatType(sizeType)}'.");
            }
            else if (TryEvaluateConstIntExpr(type.ArraySizeExpr, out var resolvedSize, out var constError))
            {
                if (resolvedSize < 0)
                    _errors.Error(type.ArraySizeExpr, $"Array size '{resolvedSize}' must be non-negative.");
                else if (resolvedSize > int.MaxValue)
                    _errors.Error(type.ArraySizeExpr, $"Array size '{resolvedSize}' does not fit in compiler-supported array bounds.");
                else
                    type.ArraySize = (int)resolvedSize;
            }
            else
            {
                _errors.Error(type.ArraySizeExpr, constError ?? "Array size must be a constant integer expression.");
            }

            if (type.IsErrorUnion && type.ErrorInnerType != null)
                type.ErrorInnerType.ArraySize = type.ArraySize;
        }

        if (type.IsFunction)
        {
            if (type.IsVolatile)
                _errors.Error(context, "Function types cannot be volatile-qualified.");
            if (type.ReturnType != null)
                ValidateTypeReference(type.ReturnType, context);
            foreach (var paramType in type.ParamTypes)
                ValidateTypeReference(paramType, context);
            return;
        }

        if (type.IsErrorUnion && type.ErrorInnerType != null)
        {
            ValidateTypeReference(type.ErrorInnerType, context);
            return;
        }

        if (type.Name == "void" || type.Name == "string" || type.Name == "bool" || _numericTypes.Contains(type.Name))
            return;

        var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);

        if (_symbolTable.LookupEnumNode(fullName) != null)
        {
            if (!type.IsAliasQualifiedReference && !CheckVisibility(fullName))
                ReportNotVisible(context, "Enum", fullName);
            return;
        }

        if (_symbolTable.LookupUnionNode(fullName) != null)
        {
            if (!type.IsAliasQualifiedReference && !CheckVisibility(fullName))
                ReportNotVisible(context, "Union", fullName);
            return;
        }

        if (!_symbolTable.TryLookupStruct(fullName, out _))
        {
            _errors.Error(context, $"Unknown type '{fullName}'");
            return;
        }

        if (!type.IsAliasQualifiedReference && !CheckVisibility(fullName))
            ReportNotVisible(context, "Struct", fullName);
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

    private bool IsSwitchOperandType(TypeNode type)
    {
        return IsNumericType(type) || IsBoolType(type) || IsEnumType(type);
    }

    private bool TryGetSwitchCaseKey(Expr expr, out string key)
    {
        if (expr is BuiltinExpr { Name: "true" })
        {
            key = "true";
            return true;
        }

        if (expr is BuiltinExpr { Name: "false" })
        {
            key = "false";
            return true;
        }

        if (TryEvaluateConstIntExpr(expr, out var value, out _))
        {
            key = value.ToString();
            return true;
        }

        key = "";
        return false;
    }

    private bool AreEqualityComparableTypes(TypeNode leftType, TypeNode rightType)
    {
        if (IsEnumType(leftType) || IsEnumType(rightType))
            return IsEnumType(leftType) && IsEnumType(rightType) && TypeHelpers.SameType(leftType, rightType);

        if (IsNumericType(leftType) && IsNumericType(rightType))
            return true;

        if (IsBoolType(leftType) && IsBoolType(rightType))
            return true;

        if (IsStringType(leftType) && IsStringType(rightType))
            return true;

        if (leftType.IsPointer && rightType.IsPointer && TypeHelpers.SameType(leftType, rightType))
            return true;

        return false;
    }

    private bool IsPointerArithmetic(string op, TypeNode leftType, TypeNode rightType)
    {
        if (op == "+")
            return (leftType.IsPointer && IsNumericType(rightType)) ||
                (IsNumericType(leftType) && rightType.IsPointer);

        if (op == "-")
            return leftType.IsPointer && IsNumericType(rightType);

        return false;
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

        var fieldMap = structFields!.ToDictionary(field => field.Name, field => field.Type, StringComparer.Ordinal);
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

        foreach (var field in structFields!)
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

        var variant = unionNode.Variants.FirstOrDefault(candidate => candidate.Name == field.Name);
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
            CheckExpression(call.Args[i]);
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

            if (CanDecayArrayToPointer(paramType, argType))
                continue;

            if (CanCoerceToSlice(paramType, argType))
                continue;

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
        switch (expr)
        {
            case NumberExpr _:
                return new TypeNode { Name = "i64" };

            case StringExpr _:
                return new TypeNode { Name = "string" };

            case IdentifierExpr ident:
                var resolvedName = ResolveQualifiedName(ident.Name);
                var info = _symbolTable.Lookup(resolvedName);
                return info?.Type;

            case BinaryExpr bin:
                if (ComparisonOperators.Contains(bin.Operator) || LogicalOperators.Contains(bin.Operator))
                    return new TypeNode { Name = "bool" };

                var leftType = GetExpressionType(bin.Left, reportErrors);
                var rightType = GetExpressionType(bin.Right, reportErrors);
                if (leftType != null && rightType != null)
                {
                    if (bin.Operator == "+" && IsNumericType(leftType) && rightType.IsPointer)
                        return rightType.Clone();

                    if ((bin.Operator == "+" || bin.Operator == "-") && leftType.IsPointer && IsNumericType(rightType))
                        return leftType.Clone();
                }

                return leftType ?? GetExpressionType(bin.Left, reportErrors);

            case CallExpr call:
                return ResolveCallInfo(call, reportErrors)?.ReturnType;

            case CastExpr cast:
                return cast.TargetType;

            case IndexExpr idx:
                var targetType = GetExpressionType(idx.Target, reportErrors);
                if (targetType == null)
                    return null;
                if (targetType.ArraySize != null)
                    return new TypeNode
                    {
                        Name = targetType.Name,
                        NamespacePath = new List<string>(targetType.NamespacePath),
                        IsVolatile = targetType.IsVolatile
                    };
                if (targetType.IsSlice)
                {
                    var elementType = targetType.Clone();
                    elementType.IsSlice = false;
                    elementType.ArraySize = null;
                    return elementType;
                }
                if (targetType.IsPointer)
                {
                    int level = targetType.PointerLevel > 0 ? targetType.PointerLevel : 1;
                    if (level > 1)
                    {
                        return new TypeNode
                        {
                            Name = targetType.Name,
                            NamespacePath = new List<string>(targetType.NamespacePath),
                            IsVolatile = targetType.IsVolatile,
                            IsPointer = true,
                            PointerLevel = level - 1
                        };
                    }
                    return new TypeNode
                    {
                        Name = targetType.Name,
                        NamespacePath = new List<string>(targetType.NamespacePath),
                        IsVolatile = targetType.IsVolatile
                    };
                }
                return null;

            case FieldExpr field:
                if (ResolveQualifiedFieldSymbol(field) is ResolvedFieldSymbolInfo resolvedFieldSymbol)
                {
                    if (resolvedFieldSymbol.SymbolInfo.Kind == SymbolKind.Variable || resolvedFieldSymbol.SymbolInfo.Kind == SymbolKind.Function)
                    {
                        if (!IsVisibleResolvedFieldSymbol(resolvedFieldSymbol)) {
                            ReportNotVisible(field, "Symbol", resolvedFieldSymbol.ResolvedName);
                            return null;
                        }
                        return resolvedFieldSymbol.SymbolInfo.Type.Clone();
                    }
                }

                var ft = GetExpressionType(field.Target, reportErrors);
                if (ft == null)
                {
                    if (reportErrors)
                        _errors.Error(field, $"Cannot determine type of target in field access '{field.Field}'.");
                    return null;
                }

                string structName = QualifiedNames.GetFullName(ft.NamespacePath, ft.Name);

                if (ft.IsSlice)
                {
                    if (field.Field == "len")
                        return new TypeNode { Name = "i64" };

                    if (field.Field == "ptr")
                    {
                        var elementType = ft.Clone();
                        elementType.IsSlice = false;
                        elementType.ArraySize = null;
                        return TypeHelpers.AddressOfType(elementType);
                    }

                    if (reportErrors)
                        _errors.Error(field, $"Slice values expose only '.ptr' and '.len', not '{field.Field}'.");
                    return null;
                }

                if (_numericTypes.Contains(structName))
                {
                    return new TypeNode { Name = structName, IsVolatile = ft.IsVolatile, IsPointer = ft.IsPointer, PointerLevel = ft.PointerLevel };
                }

                if (_symbolTable.TryLookupStruct(structName, out var structDef))
                {
                    if (!ft.IsAliasQualifiedReference && !CheckVisibility(structName)) {
                        if (reportErrors)
                            ReportNotVisible(field, "Struct", structName);
                        return null;
                    }
                    var fieldDef = structDef!.FirstOrDefault(f => f.Name == field.Field);
                    if (!string.IsNullOrEmpty(fieldDef.Name))
                    {
                        return fieldDef.Type.Clone();
                    }
                    if (reportErrors)
                        _errors.Error(field, $"Struct '{structName}' does not have a field named '{field.Field}'.");
                }
                else if (_symbolTable.LookupUnionNode(structName) is UnionNode unionDefinition)
                {
                    if (!ft.IsAliasQualifiedReference && !CheckVisibility(structName)) {
                        if (reportErrors)
                            ReportNotVisible(field, "Union", structName);
                        return null;
                    }

                    if (field.Field == "tag")
                        return GetUnionTagType(unionDefinition);

                    var variant = unionDefinition.Variants.FirstOrDefault(candidate => candidate.Name == field.Field);
                    if (variant != null)
                        return variant.TypeName.Clone();

                    if (reportErrors)
                        _errors.Error(field, $"Union '{structName}' does not have a field named '{field.Field}'.");
                }
                else
                {
                    if (reportErrors)
                        _errors.Error(field, $"Type '{structName}' is not a known struct or union");
                }
                return null;

            case UnaryExpr un:
                if (un.Operator == "&")
                {
                    var operandType = GetExpressionType(un.Operand, reportErrors);
                    if (operandType != null)
                        return TypeHelpers.AddressOfType(operandType);
                }
                else if (un.Operator == "!")
                {
                    return new TypeNode { Name = "bool" };
                }
                else if (un.Operator == "-")
                {
                    return GetExpressionType(un.Operand, reportErrors);
                }
                return null;

            case StructLiteralExpr structLiteral:
                return structLiteral.TypeName.Clone();

            case ArrayLiteralExpr arrayLiteral:
                return arrayLiteral.TypeName.Clone();

            case BuiltinExpr builtin:
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

            case ErrorNamespaceExpr:
                return null;

            case ErrorExpr _:
                return new TypeNode { Name = "i32" };

            case SizeofExpr _:
                return new TypeNode { Name = "i64" };

            case CatchExpr catchExpr:
                var catchLeftType = GetExpressionType(catchExpr.Left, reportErrors);
                if (catchLeftType == null || !catchLeftType.IsErrorUnion)
                    return null;
                return (catchLeftType.ErrorInnerType ?? catchLeftType).Clone();

            default:
                return null;
        }
    }

    private static bool IsNumericType(TypeNode? type) => TypePredicates.IsNumericType(type);

    private static bool IsBoolType(TypeNode? type)
    {
        return type != null
            && !type.IsSlice
            && !type.IsPointer
            && !type.IsErrorUnion
            && !type.IsFunction
            && type.ArraySize == null
            && type.Name == "bool";
    }

    private static bool IsStringType(TypeNode? type)
    {
        return type != null && !type.IsSlice && !type.IsPointer && !type.IsErrorUnion && type.Name == "string";
    }

    private bool IsEnumType(TypeNode? type)
    {
        if (type == null ||
            type.IsSlice ||
            type.IsPointer ||
            type.IsErrorUnion ||
            type.IsFunction ||
            type.ArraySize != null)
        {
            return false;
        }

        var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
        return _symbolTable.LookupEnumNode(fullName) != null;
    }

    private EnumNode? LookupEnumDefinition(TypeNode? type)
    {
        if (!IsEnumType(type))
            return null;

        var fullName = QualifiedNames.GetFullName(type!.NamespacePath, type.Name);
        return _symbolTable.LookupEnumNode(fullName);
    }

    private UnionNode? LookupUnionDefinition(TypeNode? type)
    {
        if (!IsUnionType(type))
            return null;

        var fullName = QualifiedNames.GetFullName(type!.NamespacePath, type.Name);
        return _symbolTable.LookupUnionNode(fullName);
    }

    private bool IsUnionType(TypeNode? type)
    {
        if (type == null ||
            type.IsSlice ||
            type.IsPointer ||
            type.IsErrorUnion ||
            type.IsFunction ||
            type.ArraySize != null)
        {
            return false;
        }

        var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
        return _symbolTable.LookupUnionNode(fullName) != null;
    }

    private bool IsValidUnionField(TypeNode unionType, string fieldName)
    {
        var fullName = QualifiedNames.GetFullName(unionType.NamespacePath, unionType.Name);
        var unionDefinition = _symbolTable.LookupUnionNode(fullName);
        if (unionDefinition == null)
            return false;

        return fieldName == "tag" || unionDefinition.Variants.Any(variant => variant.Name == fieldName);
    }

    private static string FormatType(TypeNode? type)
    {
        if (type == null)
            return "unknown";

        if (type.IsFunction)
        {
            var parameters = string.Join(", ", type.ParamTypes.Select(FormatType));
            var returnType = type.ReturnType != null ? FormatType(type.ReturnType) : "void";
            return $"fn({parameters}) -> {returnType}";
        }

        if (type.IsErrorUnion)
        {
            var innerType = type.ErrorInnerType ?? type;
            return "!" + FormatNonErrorType(innerType);
        }

        return FormatNonErrorType(type);
    }

    private static string FormatNonErrorType(TypeNode type)
    {
        var baseName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);

        if (type.IsPointer)
        {
            var level = type.PointerLevel > 0 ? type.PointerLevel : 1;
            baseName = new string('*', level) + baseName;
        }

        if (type.ArraySize != null)
            baseName = $"[{type.ArraySize}]{baseName}";

        if (type.IsSlice)
            baseName = $"[]{baseName}";

        if (type.IsVolatile)
            baseName = "volatile " + baseName;

        return baseName;
    }

    private static bool CanDecayArrayToPointer(TypeNode target, TypeNode source)
    {
        if (target.IsSlice || !target.IsPointer || source.ArraySize == null)
            return false;

        if (source.IsErrorUnion || source.IsFunction)
            return false;

        var targetLevel = target.PointerLevel > 0 ? target.PointerLevel : 1;
        if (targetLevel != 1)
            return false;

        return target.Name == source.Name && target.NamespacePath.SequenceEqual(source.NamespacePath);
    }

    private static bool CanCoerceToSlice(TypeNode target, TypeNode source)
    {
        if (!target.IsSlice || source.IsSlice || source.ArraySize == null)
            return false;

        if (source.IsPointer || source.IsErrorUnion || source.IsFunction)
            return false;

        var targetElement = target.Clone();
        targetElement.IsSlice = false;

        var sourceElement = source.Clone();
        sourceElement.ArraySize = null;

        return TypeHelpers.SameType(targetElement, sourceElement);
    }

    private bool IsAssignableTo(TypeNode target, Expr? sourceExpr, TypeNode? source)
    {
        if (source == null)
            return true;

        if (source.IsErrorUnion && !target.IsErrorUnion)
            return false;

        if (target.IsFunction && source.IsFunction)
        {
            if (!TypeHelpers.SameType(target.ReturnType, source.ReturnType))
                return false;
            if (target.ParamTypes.Count != source.ParamTypes.Count)
                return false;
            for (int i = 0; i < target.ParamTypes.Count; i++)
            {
                if (!TypeHelpers.SameType(target.ParamTypes[i], source.ParamTypes[i]))
                    return false;
            }
            return true;
        }

        if (TypeHelpers.SameType(target, source))
            return true;

        if (CanAddVolatileQualifier(target, source))
            return true;

        var targetIsFixedArray = target.ArraySize != null;
        var sourceIsFixedArray = source.ArraySize != null;
        if (targetIsFixedArray || sourceIsFixedArray)
        {
            // Once exact fixed-array identity fails, the only remaining implicit path is array-to-slice coercion.
            if (!(target.IsSlice && sourceIsFixedArray))
                return false;
        }

        if (CanCoerceToSlice(target, source))
            return true;

        if (target.IsPointer && target.Name == "void" && source.IsPointer)
            return true;

        if (!target.IsSlice && !source.IsSlice &&
            !target.IsPointer && !source.IsPointer &&
            _numericTypes.Contains(target.Name) && _numericTypes.Contains(source.Name))
        {
            if (TryGetIntegerLiteralValue(sourceExpr, out var literalValue))
                return NumericLiteralFits(target, literalValue);

            return IsWideningNumericConversion(target, source);
        }

        if (!target.IsSlice && !source.IsSlice &&
            !_numericTypes.Contains(target.Name) && !_numericTypes.Contains(source.Name))
            return target.Name == source.Name && target.NamespacePath.SequenceEqual(source.NamespacePath);

        return false;
    }

    private static bool CanAddVolatileQualifier(TypeNode target, TypeNode source)
    {
        if (!target.IsVolatile || source.IsVolatile)
            return false;

        var unqualifiedTarget = target.Clone();
        unqualifiedTarget.IsVolatile = false;
        return TypeHelpers.SameType(unqualifiedTarget, source);
    }

    private static bool SameNumericType(TypeNode target, TypeNode source)
    {
        return target.Name == source.Name
            && target.NamespacePath.SequenceEqual(source.NamespacePath)
            && !target.IsPointer
            && !source.IsPointer
            && target.ArraySize == null
            && source.ArraySize == null
            && !target.IsErrorUnion
            && !source.IsErrorUnion
            && !target.IsFunction
            && !source.IsFunction;
    }

    private static bool IsWideningNumericConversion(TypeNode target, TypeNode source)
    {
        if (SameNumericType(target, source))
            return true;

        if (!TryGetIntegerTypeInfo(target.Name, out var targetInfo) || !TryGetIntegerTypeInfo(source.Name, out var sourceInfo))
            return false;

        if (sourceInfo.IsSigned == targetInfo.IsSigned)
            return sourceInfo.Bits <= targetInfo.Bits;

        if (!sourceInfo.IsSigned && targetInfo.IsSigned)
            return sourceInfo.Bits < targetInfo.Bits;

        return false;
    }

    private static bool NumericLiteralFits(TypeNode target, long value)
    {
        return target.Name switch
        {
            "i8" => value >= sbyte.MinValue && value <= sbyte.MaxValue,
            "i16" => value >= short.MinValue && value <= short.MaxValue,
            "i32" => value >= int.MinValue && value <= int.MaxValue,
            "i64" => true,
            "u8" => value >= byte.MinValue && value <= byte.MaxValue,
            "u16" => value >= ushort.MinValue && value <= ushort.MaxValue,
            "u32" => value >= uint.MinValue && value <= uint.MaxValue,
            "u64" => value >= 0,
            _ => false
        };
    }

    private static bool TryGetIntegerLiteralValue(Expr? expr, out long value)
    {
        switch (expr)
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

    private void WarnOnMixedSignednessComparison(BinaryExpr bin, TypeNode leftType, Expr leftExpr, TypeNode rightType, Expr rightExpr)
    {
        if (!TryGetIntegerTypeInfo(leftType.Name, out var leftInfo) || !TryGetIntegerTypeInfo(rightType.Name, out var rightInfo))
            return;

        if (leftInfo.IsSigned == rightInfo.IsSigned)
            return;

        if (TryGetIntegerLiteralValue(leftExpr, out var leftLiteral) && NumericLiteralFits(rightType, leftLiteral))
            return;

        if (TryGetIntegerLiteralValue(rightExpr, out var rightLiteral) && NumericLiteralFits(leftType, rightLiteral))
            return;

        var leftLabel = leftInfo.IsSigned ? "signed" : "unsigned";
        var rightLabel = rightInfo.IsSigned ? "signed" : "unsigned";
        _errors.Warning(bin, $"Comparison '{bin.Operator}' mixes {leftLabel} type '{FormatType(leftType)}' with {rightLabel} type '{FormatType(rightType)}'. Generated C follows the platform's usual signed/unsigned comparison rules.");
    }

    private bool TryBuildNumericConversionDiagnostic(TypeNode targetType, Expr? sourceExpr, TypeNode? sourceType, out string message)
    {
        message = "";

        if (sourceType == null)
            return false;

        if (!IsIntegerScalarType(targetType) || !IsIntegerScalarType(sourceType))
            return false;

        if (TryGetIntegerLiteralValue(sourceExpr, out var literalValue))
        {
            if (NumericLiteralFits(targetType, literalValue))
                return false;

            message = $"Literal value '{literalValue}' does not fit target integer type '{FormatType(targetType)}'.";
            return true;
        }

        if (!TryGetIntegerTypeInfo(targetType.Name, out var targetInfo) || !TryGetIntegerTypeInfo(sourceType.Name, out var sourceInfo))
            return false;

        var targetTypeText = FormatType(targetType);
        var sourceTypeText = FormatType(sourceType);

        if (sourceInfo.IsSigned != targetInfo.IsSigned)
        {
            var sourceSignedness = sourceInfo.IsSigned ? "signed" : "unsigned";
            var targetSignedness = targetInfo.IsSigned ? "signed" : "unsigned";
            message = $"Implicit conversion from {sourceSignedness} integer type '{sourceTypeText}' to {targetSignedness} integer type '{targetTypeText}' is not allowed. Use cast({targetTypeText}, ...) if this change of signedness is intentional.";
            return true;
        }

        if (sourceInfo.Bits > targetInfo.Bits)
        {
            message = $"Implicit narrowing conversion from integer type '{sourceTypeText}' to '{targetTypeText}' is not allowed. Use cast({targetTypeText}, ...) if truncation is intentional.";
            return true;
        }

        return false;
    }

    private bool TryEvaluateConstIntExpr(Expr expr, out long value, out string? error)
    {
        switch (expr)
        {
            case NumberExpr number:
                value = number.Value;
                error = null;
                return true;

            case IdentifierExpr identifier:
                identifier.Name = ResolveQualifiedName(identifier.Name);
                if (TryLookupConstValue(identifier.Name, out value))
                {
                    error = null;
                    return true;
                }

                value = 0;
                error = null;
                return false;

            case FieldExpr field:
                if (TryGetQualifiedName(field) is string qualifiedName)
                {
                    var resolvedQualifiedName = TryResolveAliasQualifiedName(qualifiedName, out var aliasResolvedQualifiedName)
                        ? aliasResolvedQualifiedName
                        : qualifiedName;
                    if (TryLookupConstValue(resolvedQualifiedName, out value))
                    {
                        error = null;
                        return true;
                    }
                }

                value = 0;
                error = null;
                return false;

            case UnaryExpr { Operator: "-", Operand: var operand }:
                if (!TryEvaluateConstIntExpr(operand, out var innerValue, out error))
                {
                    value = 0;
                    return false;
                }

                try
                {
                    value = checked(-innerValue);
                    error = null;
                    return true;
                }
                catch (OverflowException)
                {
                    value = 0;
                    error = "Constant integer expression overflowed i64.";
                    return false;
                }

            case BinaryExpr binary when NumericOperators.Contains(binary.Operator):
                if (!TryEvaluateConstIntExpr(binary.Left, out var left, out error))
                {
                    value = 0;
                    return false;
                }

                if (!TryEvaluateConstIntExpr(binary.Right, out var right, out error))
                {
                    value = 0;
                    return false;
                }

                try
                {
                    value = binary.Operator switch
                    {
                        "+" => checked(left + right),
                        "-" => checked(left - right),
                        "*" => checked(left * right),
                        "/" => right == 0 ? throw new DivideByZeroException() : left / right,
                        "%" => right == 0 ? throw new DivideByZeroException() : left % right,
                        "<<" => checked(left << checked((int)right)),
                        ">>" => left >> checked((int)right),
                        "&" => left & right,
                        "|" => left | right,
                        "^" => left ^ right,
                        _ => throw new InvalidOperationException()
                    };
                    error = null;
                    return true;
                }
                catch (DivideByZeroException)
                {
                    value = 0;
                    error = "Constant integer expression divides by zero.";
                    return false;
                }
                catch (OverflowException)
                {
                    value = 0;
                    error = "Constant integer expression overflowed i64.";
                    return false;
                }

            default:
                value = 0;
                error = null;
                return false;
        }
    }

    private static bool TryGetIntegerTypeInfo(string typeName, out (bool IsSigned, int Bits) info)
    {
        info = typeName switch
        {
            "i8" => (true, 8),
            "i16" => (true, 16),
            "i32" => (true, 32),
            "i64" => (true, 64),
            "u8" => (false, 8),
            "u16" => (false, 16),
            "u32" => (false, 32),
            "u64" => (false, 64),
            _ => default
        };

        return typeName is "i8" or "i16" or "i32" or "i64" or "u8" or "u16" or "u32" or "u64";
    }

}
