using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Zorb.Compiler.AST;
using Zorb.Compiler.AST.Expressions;
using Zorb.Compiler.AST.Statements;
using Zorb.Compiler.Lexer;
using Zorb.Compiler.Parser;
using Zorb.Compiler.Utils;

namespace Zorb.Compiler.Semantic;

public class TypeChecker
{
    private enum FlowOutcome
    {
        FallsThrough,
        Returns
    }

    private readonly SymbolTable _symbolTable = new();
    private readonly ErrorReporter _errors = new();
    private readonly HashSet<string> _numericTypes = new() { "i8", "i16", "i32", "i64", "u8", "u16", "u32", "u64" };
    private readonly HashSet<string> _processedImports = new();
    private readonly Stack<Dictionary<string, HashSet<string>>> _importAliasScopes = new();
    private readonly Dictionary<string, string> _errorSymbols = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _errorValues = new();
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

        var fullName = type.NamespacePath.Any()
            ? string.Join(".", type.NamespacePath) + "." + type.Name
            : type.Name;

        if (!TryResolveAliasQualifiedName(fullName, out var resolvedName))
            return;

        var parts = resolvedName.Split('.');
        type.Name = parts[^1];
        type.NamespacePath = parts.Length > 1
            ? parts[..^1].ToList()
            : new List<string>();

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
                {
                    var parts = resolvedBareCallName.Split('.');
                    call.Name = parts[^1];
                    call.NamespacePath = parts.Length > 1
                        ? parts[..^1].ToList()
                        : new List<string>();
                }

                return call;

            case IndexExpr idx:
                idx.Target = NormalizeAliasReferences(idx.Target);
                idx.Index = NormalizeAliasReferences(idx.Index);
                return idx;

            case FieldExpr field:
                field.Target = NormalizeAliasReferences(field.Target);
                return field;

            case UnaryExpr unary:
                unary.Operand = NormalizeAliasReferences(unary.Operand);
                return unary;

            case CastExpr cast:
                cast.Expr = NormalizeAliasReferences(cast.Expr);
                return cast;

            case SizeofExpr sizeofExpr:
                return sizeofExpr;

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

    private bool CheckVisibility(string fullName)
    {
        if (_symbolTable.IsLocal(fullName)) return true;
        if (_fileScopes.Count > 0 && _fileScopes.Peek().Contains(fullName)) return true;
        return false;
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
        return expr switch
        {
            IdentifierExpr id => id.Name,
            FieldExpr field => TryGetQualifiedName(field.Target) is string targetName
                ? $"{targetName}.{field.Field}"
                : null,
            _ => null
        };
    }

    public void Check(List<Node> nodes, string currentDir = ".")
    {
        _currentDir = currentDir;
        _fileScopes.Push(new HashSet<string>());
        _importAliasScopes.Push(new Dictionary<string, HashSet<string>>(StringComparer.Ordinal));
        InitializeBuiltins();

        CheckNodes(nodes, currentDir);
        
        _importAliasScopes.Pop();
        _fileScopes.Pop();
    }

    public void CheckNodes(List<Node> nodes, string currentDir = ".")
    {
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
                var fullName = fn.NamespacePath.Any() ? string.Join(".", fn.NamespacePath) + "." + fn.Name : fn.Name;
                MakeVisible(fullName);
                if (!_symbolTable.IsDefined(fullName)) _symbolTable.DefineFunction(fullName, fn.ReturnType, fn.Parameters);
            }
            else if (node is StructNode sn) {
                var fullName = sn.NamespacePath.Any() ? string.Join(".", sn.NamespacePath) + "." + sn.Name : sn.Name;
                MakeVisible(fullName);
                if (!_symbolTable.IsDefined(fullName)) _symbolTable.DefineStruct(fullName, sn.Fields);

                foreach (var field in sn.Fields)
                {
                    NormalizeTypeReferenceInPlace(field.Type);
                    var fieldFullName = field.Type.NamespacePath.Any() 
                        ? string.Join(".", field.Type.NamespacePath) + "." + field.Type.Name 
                        : field.Type.Name;

                    if (field.Type.IsFunction || field.Type.Name == "void" || field.Type.Name == "string" || field.Type.Name == "bool") 
                        continue;

                    if (!_numericTypes.Contains(field.Type.Name) && !_symbolTable.TryLookupStruct(fieldFullName, out _))
                    {
                        _errors.Error(sn, $"Unknown type '{fieldFullName}' in struct '{fullName}'");
                    }
                }
            }
            else if (node is VariableDeclarationNode vd) {
                NormalizeTypeReferenceInPlace(vd.TypeName);
                RegisterErrorDeclaration(vd);
                MakeVisible(vd.Name);
                if (!_symbolTable.IsDefined(vd.Name)) _symbolTable.DefineVariable(vd.Name, vd.TypeName);
            }
        }

        // Pass 2: Process imports and check bodies
        foreach (var node in nodes)
        {
            if (node is ImportNode importNode) ProcessImport(importNode, currentDir);
            else if (node is VariableDeclarationNode varDecl && varDecl.Value != null)
            {
                CheckVariableInitializer(varDecl);
            }
            else if (node is FunctionDecl functionDecl)
            {
                if (functionDecl.IsExtern) continue;

                _currentFunction = functionDecl;
                _symbolTable.PushScope();

                foreach (var param in functionDecl.Parameters)
                    _symbolTable.DefineParameter(param.Name, param.TypeName);

                var flow = CheckBlock(functionDecl.Body, pushScope: false);
                if (FunctionRequiresReturn(functionDecl) && flow != FlowOutcome.Returns)
                {
                    _errors.Error(functionDecl, $"Function '{functionDecl.Name}' may exit without returning a value");
                }

                _symbolTable.PopScope();
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
            _errors.Error($"Import file not found: {importNode.Path}");
            return;
        }

        var dir = Path.GetDirectoryName(fullPath) ?? ".";
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
        var importedNodes = parser.ParseProgram();

        var exports = new List<string>();
        foreach (var node in importedNodes)
        {
            if (node is FunctionDecl fn && fn.IsExported) exports.Add(fn.NamespacePath.Any() ? string.Join(".", fn.NamespacePath) + "." + fn.Name : fn.Name);
            else if (node is StructNode sn && sn.IsExported) exports.Add(sn.NamespacePath.Any() ? string.Join(".", sn.NamespacePath) + "." + sn.Name : sn.Name);
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
                                _errors.Error($"Function '{_currentFunction.Name}' does not return an error union (!), so you cannot return an error code.");
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
                                    _errors.Error(returnNode, $"Cannot return expression of type '{FormatType(exprType)}' from function '{_currentFunction.Name}' with result type '{FormatType(expectedType)}'. Return a success value of type '{FormatType(successType)}' or an error.");
                                }

                                if (returnNode.Value is IdentifierExpr ident && ident.Name.Contains("Error"))
                                {
                                    _errors.Error($"Ambiguous return in '{_currentFunction.Name}'. Use lowercase 'return error.{ident.Name.Split('.').Last()}' to return an error code.");
                                }
                            }
                            else
                            {
                                if (!IsAssignableTo(expectedType, returnNode.Value, exprType))
                                {
                                    _errors.Error(returnNode, $"Function '{_currentFunction.Name}' returns '{FormatType(expectedType)}', but this return expression has type '{FormatType(exprType)}'.");
                                }
                            }
                        }
                    }
                }
                return FlowOutcome.Returns;

            case IfStmt ifStmt:
                CheckExpression(ifStmt.Condition);
                if (!IsBoolType(ifStmt.Condition))
                {
                    _errors.Error(ifStmt.Condition, $"Condition must have type 'bool', got '{FormatType(GetExpressionType(ifStmt.Condition, reportErrors: false))}'. Compare explicitly if you meant truthiness.");
                }
                var ifFlow = CheckBlock(ifStmt.Body);
                if (ifStmt.ElseBody.Count == 0)
                    return FlowOutcome.FallsThrough;

                var elseFlow = CheckBlock(ifStmt.ElseBody);
                return ifFlow == FlowOutcome.Returns && elseFlow == FlowOutcome.Returns
                    ? FlowOutcome.Returns
                    : FlowOutcome.FallsThrough;

            case WhileStmt whileStmt:
                CheckExpression(whileStmt.Condition);
                if (!IsBoolType(whileStmt.Condition))
                {
                    _errors.Error(whileStmt.Condition, $"Condition must have type 'bool', got '{FormatType(GetExpressionType(whileStmt.Condition, reportErrors: false))}'. Compare explicitly if you meant truthiness.");
                }
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

            case BreakStmt breakStmt:
                if (_loopDepth == 0)
                    _errors.Error(breakStmt, "'break' is only allowed inside a while loop.");
                return FlowOutcome.FallsThrough;

            case AsmStatementNode asmStmt:
                CheckAsmStatement(asmStmt);
                return FlowOutcome.FallsThrough;
        }

        return FlowOutcome.FallsThrough;
    }

    private FlowOutcome CheckBlock(List<Statement> statements, bool pushScope = true)
    {
        if (pushScope)
            _symbolTable.PushScope();

        var flow = FlowOutcome.FallsThrough;
        foreach (var stmt in statements)
        {
            NormalizeAliasReferences(stmt);
            flow = CheckStatement(stmt);
            if (flow == FlowOutcome.Returns)
                break;
        }

        if (pushScope)
            _symbolTable.PopScope();

        return flow;
    }

    private void CheckVariableDeclaration(VariableDeclarationNode varDecl)
    {
        ValidateTypeReference(varDecl.TypeName, varDecl);
        _symbolTable.DefineVariable(varDecl.Name, varDecl.TypeName);

        CheckVariableInitializer(varDecl);
    }

    private void CheckAssignment(AssignStmt assign)
    {
        assign.Target = NormalizeAliasReferences(assign.Target);
        assign.Value = NormalizeAliasReferences(assign.Value);
        CheckExpression(assign.Target);
        CheckExpression(assign.Value);

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
            else
            {
                _errors.Error(assign, $"Cannot assign expression of type '{FormatType(valueType)}' to target of type '{FormatType(targetType)}'.");
            }
        }

        if (targetType!.Name == "u8" && targetType.IsPointer && valueType!.IsPointer && valueType.PointerLevel == 1)
        {
            if (assign.Value is UnaryExpr unary && unary.Operator == "&")
            {
                _errors.Error($"Warning: casting *u8 to **u8 may cause crashes if address is not 8-byte aligned. Consider using mem.Align64 first.");
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
                    _errors.Error($"Use of undeclared identifier '{ident.Name}'");
                }
                break;

            case ErrorNamespaceExpr:
                _errors.Error(expr, "Expected '.Name' after 'error' in expression.");
                break;

            case IndexExpr idx:
                CheckExpression(idx.Target);
                CheckExpression(idx.Index);
                break;

            case FieldExpr field:
                CheckExpression(field.Target);
                break;

            case UnaryExpr un:
                CheckExpression(un.Operand);
                if (un.Operator == "!" && !IsBoolType(un.Operand))
                {
                    _errors.Error(un, $"Operator '!' requires a bool operand, got '{FormatType(GetExpressionType(un.Operand, reportErrors: false))}'.");
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

            case CatchExpr catchExpr:
                CheckExpression(catchExpr.Left);
                var catchType = GetExpressionType(catchExpr.Left);
                if (catchType == null || !catchType.IsErrorUnion)
                {
                    _errors.Error(catchExpr, "Catch requires an error-union expression");
                    break;
                }

                _symbolTable.PushScope();
                _symbolTable.DefineVariable(catchExpr.ErrorVar, new TypeNode { Name = "i32" });
                foreach (var stmt in catchExpr.CatchBody)
                    CheckStatement(stmt);
                _symbolTable.PopScope();
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

        if (isOutput && !IsAsmAssignableExpression(operand.Expression))
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
            default:
                return false;
        }
    }

    private void CheckVariableInitializer(VariableDeclarationNode varDecl)
    {
        if (varDecl.Value == null)
            return;

        varDecl.Value = NormalizeAliasReferences(varDecl.Value);

        if (_currentFunction == null && ContainsCatchExpression(varDecl.Value))
        {
            _errors.Error(varDecl.Value, "Catch expressions are not supported in global initializers.");
            return;
        }

        CheckExpression(varDecl.Value);
        var exprType = GetExpressionType(varDecl.Value);
        if (!IsAssignableTo(varDecl.TypeName, varDecl.Value, exprType))
        {
            _errors.Error(varDecl, $"Cannot assign expression of type '{FormatType(exprType)}' to variable '{varDecl.Name}' of type '{FormatType(varDecl.TypeName)}'.");
        }
    }

    private static bool IsAsmAssignableExpression(Expr expr)
    {
        return expr is IdentifierExpr or FieldExpr or IndexExpr;
    }

    private bool IsValidAsmOperandType(TypeNode? type)
    {
        if (type == null)
            return false;

        if (type.IsErrorUnion || type.IsFunction || type.ArraySize != null || type.Name == "void")
            return false;

        if (type.IsPointer)
            return true;

        if (type.Name == "bool")
            return true;

        return _numericTypes.Contains(type.Name);
    }

    private void ValidateTypeReference(TypeNode type, Node context)
    {
        var sourceFullName = type.NamespacePath.Any()
            ? string.Join(".", type.NamespacePath) + "." + type.Name
            : type.Name;
        var wasAliasQualified = TryResolveAliasQualifiedName(sourceFullName, out var resolvedTypeName);

        if (wasAliasQualified)
        {
            var parts = resolvedTypeName.Split('.');
            type.Name = parts[^1];
            type.NamespacePath = parts.Length > 1
                ? parts[..^1].ToList()
                : new List<string>();
        }
        else
        {
            NormalizeTypeReferenceInPlace(type);
        }

        if (type.IsFunction)
        {
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

        var fullName = type.NamespacePath.Any()
            ? string.Join(".", type.NamespacePath) + "." + type.Name
            : type.Name;

        if (!_symbolTable.TryLookupStruct(fullName, out _))
        {
            _errors.Error(context, $"Unknown type '{fullName}'");
            return;
        }

        if (!wasAliasQualified && !CheckVisibility(fullName))
            _errors.Error(context, $"Struct '{fullName}' is not visible. Did you forget an import?");
    }

    private void CheckBinaryExpression(BinaryExpr bin)
    {
        CheckExpression(bin.Left);
        CheckExpression(bin.Right);

        var leftType = GetExpressionType(bin.Left);
        var rightType = GetExpressionType(bin.Right);

        if (leftType == null || rightType == null)
            return;

        if (NumericOperators.Contains(bin.Operator))
        {
            if (!IsNumericType(leftType))
            {
                _errors.Error($"Left operand of '{bin.Operator}' must be numeric type");
            }

            if (!IsNumericType(rightType))
            {
                _errors.Error($"Right operand of '{bin.Operator}' must be numeric type");
            }
        }

        if (!ComparisonOperators.Contains(bin.Operator))
            return;

        if (bin.Operator is ">" or "<" or ">=" or "<=")
        {
            if (!IsNumericType(leftType) || !IsNumericType(rightType))
                _errors.Error($"Operator '{bin.Operator}' requires numeric operands");
            return;
        }

        if (bin.Operator is "==" or "!=")
        {
            if (IsNumericType(leftType) && IsNumericType(rightType))
                return;

            if (IsBoolType(leftType) && IsBoolType(rightType))
                return;

            if (IsStringType(leftType) && IsStringType(rightType))
                return;

            if (leftType.IsPointer && rightType.IsPointer && SameType(leftType, rightType))
                return;

            _errors.Error($"Operator '{bin.Operator}' requires numeric operands, bool operands, matching pointer types, or two strings");
        }
    }

    private void CheckCallExpression(CallExpr call)
    {
        List<Parameter>? parameters = null;
        string errorName = string.IsNullOrEmpty(call.Name) ? "call target" : call.Name;
        bool resolvedViaAlias = false;

        // Handle qualified calls like std.io.println(...) before falling back to function pointers.
        if (call.TargetExpr != null)
        {
            var qualifiedName = TryGetQualifiedName(call.TargetExpr);
            var resolvedQualifiedName = qualifiedName;
            var targetResolvedViaAlias = !string.IsNullOrEmpty(qualifiedName) &&
                TryResolveAliasQualifiedName(qualifiedName, out resolvedQualifiedName);
            call.ResolvedTargetQualifiedName = targetResolvedViaAlias ? resolvedQualifiedName : null;

            if (!string.IsNullOrEmpty(resolvedQualifiedName) && _symbolTable.TryLookup(resolvedQualifiedName, out var qualifiedInfo))
            {
                if (!targetResolvedViaAlias && !CheckVisibility(resolvedQualifiedName))
                {
                    _errors.Error(call, $"Function '{qualifiedName}' is not visible. Did you forget to import it?");
                    return;
                }

                bool isCallable = qualifiedInfo!.Kind == SymbolKind.Function ||
                                  (qualifiedInfo.Type != null && qualifiedInfo.Type.IsFunction);

                if (!isCallable)
                {
                    _errors.Error(call, $"'{qualifiedName}' is not a function or callable variable");
                    return;
                }

                parameters = qualifiedInfo.Kind == SymbolKind.Function
                    ? qualifiedInfo.Parameters
                    : qualifiedInfo.Type!.ParamTypes.Select(t => new Parameter("", t)).ToList();
                errorName = qualifiedName ?? "call target";
            }

            if (parameters == null)
            {
                var targetType = GetExpressionType(call.TargetExpr, reportErrors: false);
                if (targetType == null || !targetType.IsFunction)
                {
                    _errors.Error(call, !string.IsNullOrEmpty(qualifiedName)
                        ? $"Function '{qualifiedName}' is not visible. Did you forget to import it?"
                        : "Expression is not a function or callable");
                    return;
                }
                parameters = targetType.ParamTypes.Select(t => new Parameter("", t)).ToList();
                errorName = !string.IsNullOrEmpty(qualifiedName) ? qualifiedName : "function pointer";
            }
        }
        else // EXISTING: Handle standard named functions
        {
            if (call.NamespacePath.Any())
            {
                var sourceName = string.Join(".", call.NamespacePath) + "." + call.Name;
                if (TryResolveAliasQualifiedName(sourceName, out var resolvedCallName))
                {
                    var parts = resolvedCallName.Split('.');
                    call.Name = parts[^1];
                    call.NamespacePath = parts.Length > 1
                        ? parts[..^1].ToList()
                            : new List<string>();
                    errorName = sourceName;
                    resolvedViaAlias = true;
                }
            }

            if (!_symbolTable.TryLookup(call.Name, out var funcInfo))
            {
                if (call.NamespacePath.Any())
                {
                    var fullName = string.Join(".", call.NamespacePath) + "." + call.Name;
                    if (!_symbolTable.TryLookup(fullName, out funcInfo))
                    {
                        _errors.Error(call, $"Call to undeclared function '{fullName}'");
                        return;
                    }
                    if (!resolvedViaAlias && !CheckVisibility(fullName)) {
                        _errors.Error(call, $"Function '{fullName}' is not visible. Did you forget to import it?");
                        return;
                    }
                }
                else
                {
                    _errors.Error(call, $"Call to undeclared function '{call.Name}'");
                    return;
                }
            }
            else 
            {
                if (!CheckVisibility(call.Name)) {
                    _errors.Error(call, $"Function '{call.Name}' is not visible. Did you forget to import it?");
                    return;
                }
            }

            bool isCallable = funcInfo!.Kind == SymbolKind.Function || 
                             (funcInfo.Type != null && funcInfo.Type.IsFunction);

            if (!isCallable)
            {
                _errors.Error(call, $"'{call.Name}' is not a function or callable variable");
                return;
            }

            parameters = funcInfo.Kind == SymbolKind.Function 
                ? funcInfo.Parameters 
                : funcInfo.Type!.ParamTypes.Select(t => new Parameter("", t)).ToList();
        }

        if (parameters == null) parameters = new List<Parameter>();

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

            if (CanDecayArrayToPointer(paramType, argType))
                continue;

            if (!IsAssignableTo(paramType, call.Args[i], argType))
            {
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
                ident.Name = ResolveQualifiedName(ident.Name);
                var info = _symbolTable.Lookup(ident.Name);
                return info?.Type;

            case BinaryExpr bin:
                if (ComparisonOperators.Contains(bin.Operator))
                    return new TypeNode { Name = "bool" };
                return GetExpressionType(bin.Left, reportErrors);

            case CallExpr call:
                // Resolve return type for qualified names and function pointers.
                if (call.TargetExpr != null)
                {
                    var qualifiedName = TryGetQualifiedName(call.TargetExpr);
                    var resolvedQualifiedName = qualifiedName;
                    if (!string.IsNullOrEmpty(qualifiedName) &&
                        TryResolveAliasQualifiedName(qualifiedName, out var aliasResolvedQualifiedName))
                    {
                        resolvedQualifiedName = aliasResolvedQualifiedName;
                    }

                    if (!string.IsNullOrEmpty(resolvedQualifiedName) && _symbolTable.TryLookup(resolvedQualifiedName, out var qualifiedInfo))
                    {
                        if (qualifiedInfo!.Kind == SymbolKind.Function)
                            return qualifiedInfo.Type.ReturnType?.Clone();
                        if (qualifiedInfo.Type != null && qualifiedInfo.Type.IsFunction)
                            return qualifiedInfo.Type.ReturnType?.Clone();
                    }

                    var callTargetType = GetExpressionType(call.TargetExpr, reportErrors);
                    if (callTargetType != null && callTargetType.IsFunction)
                    {
                        return callTargetType.ReturnType?.Clone();
                    }
                    return null;
                }

                // EXISTING: Resolve return type for standard named functions
                if (_symbolTable.TryLookup(call.Name, out var funcInfo))
                {
                    if (funcInfo!.Kind == SymbolKind.Function)
                        return funcInfo.Type.ReturnType?.Clone();
                    if (funcInfo.Type != null && funcInfo.Type.IsFunction)
                        return funcInfo.Type.ReturnType?.Clone();
                }
                if (call.NamespacePath.Any())
                {
                    var sourceName = string.Join(".", call.NamespacePath) + "." + call.Name;
                    if (TryResolveAliasQualifiedName(sourceName, out var resolvedCallName))
                    {
                        if (_symbolTable.TryLookup(resolvedCallName, out funcInfo))
                        {
                            if (funcInfo!.Kind == SymbolKind.Function)
                                return funcInfo.Type.ReturnType?.Clone();
                            if (funcInfo.Type != null && funcInfo.Type.IsFunction)
                                return funcInfo.Type.ReturnType?.Clone();
                        }
                    }

                    var fullName = string.Join(".", call.NamespacePath) + "." + call.Name;
                    if (_symbolTable.TryLookup(fullName, out funcInfo))
                    {
                        if (funcInfo!.Kind == SymbolKind.Function)
                            return funcInfo.Type.ReturnType?.Clone();
                        if (funcInfo.Type != null && funcInfo.Type.IsFunction)
                            return funcInfo.Type.ReturnType?.Clone();
                    }
                }
                return null;

            case CastExpr cast:
                return cast.TargetType;

            case IndexExpr idx:
                var targetType = GetExpressionType(idx.Target, reportErrors);
                if (targetType == null)
                    return new TypeNode { Name = "i32" };
                if (targetType.ArraySize != null)
                    return new TypeNode
                    {
                        Name = targetType.Name,
                        NamespacePath = new List<string>(targetType.NamespacePath)
                    };
                if (targetType.IsPointer)
                {
                    int level = targetType.PointerLevel > 0 ? targetType.PointerLevel : 1;
                    if (level > 1)
                    {
                        return new TypeNode
                        {
                            Name = targetType.Name,
                            NamespacePath = new List<string>(targetType.NamespacePath),
                            IsPointer = true,
                            PointerLevel = level - 1
                        };
                    }
                    return new TypeNode
                    {
                        Name = targetType.Name,
                        NamespacePath = new List<string>(targetType.NamespacePath)
                    };
                }
                return new TypeNode { Name = "i32" };

            case FieldExpr field:
                var targetName = TryGetQualifiedName(field.Target);
                var potentialName = string.IsNullOrEmpty(targetName) ? "" : $"{targetName}.{field.Field}";
                
                if (!string.IsNullOrEmpty(potentialName) && _symbolTable.TryLookup(potentialName, out var fieldInfo))
                {
                    if (fieldInfo!.Kind == SymbolKind.Variable || fieldInfo.Kind == SymbolKind.Function)
                    {
                        if (!CheckVisibility(potentialName)) {
                            _errors.Error($"'{potentialName}' is not visible. Did you forget an import?");
                            return new TypeNode { Name = "i32" };
                        }
                        return fieldInfo.Type.Clone();
                    }
                }

                var ft = GetExpressionType(field.Target, reportErrors);
                if (ft == null)
                {
                    if (reportErrors)
                        _errors.Error($"Cannot determine type of target in field access '{field.Field}'");
                    return new TypeNode { Name = "i32" };
                }

                string structName = ft.NamespacePath.Any() 
                    ? string.Join(".", ft.NamespacePath) + "." + ft.Name 
                    : ft.Name;

                if (_numericTypes.Contains(structName))
                {
                    return new TypeNode { Name = structName, IsPointer = ft.IsPointer, PointerLevel = ft.PointerLevel };
                }

                if (_symbolTable.TryLookupStruct(structName, out var structDef))
                {
                    if (!CheckVisibility(structName)) {
                        if (reportErrors)
                            _errors.Error($"Struct '{structName}' is not visible. Did you forget an import?");
                        return new TypeNode { Name = "i32" };
                    }
                    var fieldDef = structDef!.FirstOrDefault(f => f.Name == field.Field);
                    if (!string.IsNullOrEmpty(fieldDef.Name))
                    {
                        return fieldDef.Type.Clone();
                    }
                    if (reportErrors)
                        _errors.Error($"Struct '{structName}' does not have a field named '{field.Field}'");
                }
                else
                {
                    if (reportErrors)
                        _errors.Error(field, $"Type '{structName}' is not a known struct");
                }
                return new TypeNode { Name = "i32" };

            case UnaryExpr un:
                if (un.Operator == "&")
                {
                    var operandType = GetExpressionType(un.Operand, reportErrors);
                    if (operandType != null)
                        return AddressOfType(operandType);
                }
                else if (un.Operator == "!")
                {
                    return new TypeNode { Name = "bool" };
                }
                else if (un.Operator == "-")
                {
                    return GetExpressionType(un.Operand, reportErrors);
                }
                return new TypeNode { Name = "i32" };

            case BuiltinExpr builtin:
                return builtin.Name switch
                {
                    "true" => new TypeNode { Name = "bool" },
                    "false" => new TypeNode { Name = "bool" },
                    "Builtin.IsLinux" => new TypeNode { Name = "bool" },
                    "Builtin.IsWindows" => new TypeNode { Name = "bool" },
                    "Builtin.IsX86_64" => new TypeNode { Name = "bool" },
                    "Builtin.IsAArch64" => new TypeNode { Name = "bool" },
                    _ => new TypeNode { Name = "i32" }
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

    private bool IsNumericType(Expr expr)
    {
        var type = GetExpressionType(expr);
        return type != null && _numericTypes.Contains(type.Name);
    }

    private bool IsBoolType(Expr expr)
    {
        var type = GetExpressionType(expr);
        return type != null
            && !type.IsPointer
            && !type.IsErrorUnion
            && !type.IsFunction
            && type.ArraySize == null
            && type.Name == "bool";
    }

    private bool IsPointerType(Expr expr)
    {
        var type = GetExpressionType(expr);
        return type?.IsPointer == true;
    }

    private bool IsNumericType(TypeNode? type)
    {
        return type != null
            && !type.IsPointer
            && !type.IsErrorUnion
            && !type.IsFunction
            && type.ArraySize == null
            && _numericTypes.Contains(type.Name);
    }

    private static bool IsBoolType(TypeNode? type)
    {
        return type != null
            && !type.IsPointer
            && !type.IsErrorUnion
            && !type.IsFunction
            && type.ArraySize == null
            && type.Name == "bool";
    }

    private static bool IsStringType(TypeNode? type)
    {
        return type != null && !type.IsPointer && !type.IsErrorUnion && type.Name == "string";
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
        var baseName = type.NamespacePath.Any()
            ? string.Join(".", type.NamespacePath) + "." + type.Name
            : type.Name;

        if (type.IsPointer)
        {
            var level = type.PointerLevel > 0 ? type.PointerLevel : 1;
            baseName = new string('*', level) + baseName;
        }

        if (type.ArraySize != null)
            baseName = $"[{type.ArraySize}]{baseName}";

        return baseName;
    }

    private static bool CanDecayArrayToPointer(TypeNode target, TypeNode source)
    {
        if (!target.IsPointer || source.ArraySize == null)
            return false;

        if (source.IsPointer || source.IsErrorUnion || source.IsFunction)
            return false;

        var targetLevel = target.PointerLevel > 0 ? target.PointerLevel : 1;
        if (targetLevel != 1)
            return false;

        return target.Name == source.Name && target.NamespacePath.SequenceEqual(source.NamespacePath);
    }

    private static TypeNode AddressOfType(TypeNode operandType)
    {
        // `&array` is intentionally element-pointer sugar, not pointer-to-array.
        if (operandType.ArraySize != null)
        {
            return new TypeNode
            {
                Name = operandType.Name,
                NamespacePath = new List<string>(operandType.NamespacePath),
                IsPointer = true,
                PointerLevel = 1
            };
        }

        int newLevel = operandType.IsPointer ? (operandType.PointerLevel > 0 ? operandType.PointerLevel + 1 : 2) : 1;
        return new TypeNode
        {
            Name = operandType.Name,
            NamespacePath = new List<string>(operandType.NamespacePath),
            IsPointer = true,
            PointerLevel = newLevel
        };
    }

    private bool IsAssignableTo(TypeNode target, Expr? sourceExpr, TypeNode? source)
    {
        if (source == null)
            return true;

        if (source.IsErrorUnion && !target.IsErrorUnion)
            return false;

        if (target.IsFunction && source.IsFunction)
        {
            if (!SameType(target.ReturnType, source.ReturnType))
                return false;
            if (target.ParamTypes.Count != source.ParamTypes.Count)
                return false;
            for (int i = 0; i < target.ParamTypes.Count; i++)
            {
                if (!SameType(target.ParamTypes[i], source.ParamTypes[i]))
                    return false;
            }
            return true;
        }

        if (SameType(target, source))
            return true;

        if (target.IsPointer && target.Name == "void" && source.IsPointer)
            return true;

        if (!target.IsPointer && !source.IsPointer && _numericTypes.Contains(target.Name) && _numericTypes.Contains(source.Name))
        {
            if (TryGetIntegerLiteralValue(sourceExpr, out var literalValue))
                return NumericLiteralFits(target, literalValue);

            return IsWideningNumericConversion(target, source);
        }

        if (!_numericTypes.Contains(target.Name) && !_numericTypes.Contains(source.Name))
            return target.Name == source.Name && target.NamespacePath.SequenceEqual(source.NamespacePath);

        return false;
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

    private static bool SameType(TypeNode? left, TypeNode? right)
    {
        if (left == null || right == null)
            return left == right;

        if (left.Name != right.Name ||
            left.IsPointer != right.IsPointer ||
            left.PointerLevel != right.PointerLevel ||
            left.ArraySize != right.ArraySize ||
            left.IsErrorUnion != right.IsErrorUnion ||
            left.IsFunction != right.IsFunction ||
            !left.NamespacePath.SequenceEqual(right.NamespacePath))
        {
            return false;
        }

        if (left.IsErrorUnion && !SameType(left.ErrorInnerType, right.ErrorInnerType))
            return false;

        if (left.IsFunction)
        {
            if (!SameType(left.ReturnType, right.ReturnType) || left.ParamTypes.Count != right.ParamTypes.Count)
                return false;

            for (int i = 0; i < left.ParamTypes.Count; i++)
            {
                if (!SameType(left.ParamTypes[i], right.ParamTypes[i]))
                    return false;
            }
        }

        return true;
    }
}
