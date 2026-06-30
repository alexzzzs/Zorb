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
    private void RegisterTopLevelDeclarations(
        IReadOnlyList<Node> nodes,
        string currentDir,
        Dictionary<string, Node> declaredInThisFile)
    {
        foreach (var node in nodes)
        {
            RegisterTopLevelDeclaration(node, currentDir, declaredInThisFile);
        }
    }
    private void RegisterTopLevelDeclaration(
        Node node,
        string currentDir,
        Dictionary<string, Node> declaredInThisFile)
    {
        if (node is ImportNode importNode)
        {
            ProcessImport(importNode, currentDir);
            return;
        }

        if (node is FunctionDecl functionDecl)
        {
            RegisterFunctionDeclaration(functionDecl, declaredInThisFile);
            return;
        }

        if (node is StructNode structNode)
        {
            RegisterStructDeclaration(structNode, declaredInThisFile);
            return;
        }

        if (node is EnumNode enumNode)
        {
            RegisterEnumDeclaration(enumNode, declaredInThisFile);
            return;
        }

        if (node is UnionNode unionNode)
        {
            RegisterUnionDeclaration(unionNode, declaredInThisFile);
            return;
        }

        if (node is ExternTypeDecl externType)
        {
            RegisterExternTypeDeclaration(externType, declaredInThisFile);
            return;
        }

        if (node is VariableDeclarationNode variableDeclaration)
            RegisterVariableDeclaration(variableDeclaration, declaredInThisFile);
    }
    private void RegisterFunctionDeclaration(FunctionDecl functionDecl, Dictionary<string, Node> declaredInThisFile)
    {
        NormalizeTypeReferenceInPlace(functionDecl.ReturnType);
        foreach (var param in functionDecl.Parameters)
            NormalizeTypeReferenceInPlace(param.TypeName);
        ResolveTypeParameterReferences(functionDecl.TypeParameterSpecs, functionDecl);

        var fullName = QualifiedNames.GetFullName(functionDecl.NamespacePath, functionDecl.Name);
        ReportDuplicateTopLevelDeclaration(functionDecl, fullName, declaredInThisFile);
        ReportBuiltinDeclarationConflict(functionDecl, fullName);
        MakeVisible(fullName);
        if (!_symbolTable.IsDefined(fullName))
        {
            _symbolTable.DefineFunction(
                fullName,
                functionDecl.ReturnType,
                functionDecl.Parameters,
                functionDecl.TypeParameters,
                functionDecl.TypeParameterSpecs);
        }
    }
    private void RegisterStructDeclaration(StructNode structNode, Dictionary<string, Node> declaredInThisFile)
    {
        var fullName = QualifiedNames.GetFullName(structNode.NamespacePath, structNode.Name);
        ReportDuplicateTopLevelDeclaration(structNode, fullName, declaredInThisFile);
        MakeVisible(fullName);
        if (!_symbolTable.IsDefined(fullName))
            _symbolTable.DefineStruct(fullName, structNode);

        foreach (var field in structNode.Fields)
            NormalizeTypeReferenceInPlace(field.TypeName);
    }
    private void RegisterEnumDeclaration(EnumNode enumNode, Dictionary<string, Node> declaredInThisFile)
    {
        NormalizeTypeReferenceInPlace(enumNode.UnderlyingType);
        var fullName = QualifiedNames.GetFullName(enumNode.NamespacePath, enumNode.Name);
        ReportDuplicateTopLevelDeclaration(enumNode, fullName, declaredInThisFile);
        MakeVisible(fullName);
        if (!_symbolTable.IsDefined(fullName))
            _symbolTable.DefineEnum(fullName, enumNode);

        RegisterEnumMembers(enumNode);
    }
    private void RegisterUnionDeclaration(UnionNode unionNode, Dictionary<string, Node> declaredInThisFile)
    {
        var fullName = QualifiedNames.GetFullName(unionNode.NamespacePath, unionNode.Name);
        ReportDuplicateTopLevelDeclaration(unionNode, fullName, declaredInThisFile);
        MakeVisible(fullName);
        if (!_symbolTable.IsDefined(fullName))
            _symbolTable.DefineUnion(fullName, unionNode);

        RegisterUnionTagEnum(unionNode);
    }
    private void RegisterExternTypeDeclaration(ExternTypeDecl externType, Dictionary<string, Node> declaredInThisFile)
    {
        var fullName = QualifiedNames.GetFullName(externType.NamespacePath, externType.Name);
        ReportDuplicateTopLevelDeclaration(externType, fullName, declaredInThisFile);
        MakeVisible(fullName);
        if (!_symbolTable.IsDefined(fullName))
            _symbolTable.DefineExternType(fullName, externType);
    }
    private void RegisterVariableDeclaration(VariableDeclarationNode variableDeclaration, Dictionary<string, Node> declaredInThisFile)
    {
        NormalizeTypeReferenceInPlace(variableDeclaration.TypeName);
        ValidateVariableAttributes(variableDeclaration, isGlobal: true);
        RegisterErrorDeclaration(variableDeclaration);
        ReportDuplicateTopLevelDeclaration(variableDeclaration, variableDeclaration.Name, declaredInThisFile);
        ReportBuiltinDeclarationConflict(variableDeclaration, variableDeclaration.Name);
        MakeVisible(variableDeclaration.Name);
        if (!_symbolTable.IsDefined(variableDeclaration.Name))
            _symbolTable.DefineVariable(variableDeclaration.Name, variableDeclaration.TypeName, variableDeclaration.IsConst);

        TryRecordConstValue(variableDeclaration);
    }
    private void ReportDuplicateTopLevelDeclaration(
        Node node,
        string name,
        Dictionary<string, Node> declaredInThisFile)
    {
        if (!declaredInThisFile.TryAdd(name, node))
            _errors.Error(node, $"Duplicate top-level declaration '{name}'.{FormatPreviousDeclarationSuffix(declaredInThisFile[name])}");
    }
    private void ReportBuiltinDeclarationConflict(Node node, string name)
    {
        if (IsBuiltinDeclarationName(name))
            _errors.Error(node, $"Top-level declaration '{name}' conflicts with a built-in symbol.");
    }
    private void ValidateTopLevelDeclarationsAndBodies(IReadOnlyList<Node> nodes)
    {
        foreach (var node in nodes)
        {
            ValidateTopLevelDeclaration(node);
        }
    }
    private void ValidateTopLevelDeclaration(Node node)
    {
        if (node is VariableDeclarationNode variableDeclaration)
        {
            ValidateVariableDeclaration(variableDeclaration);
            return;
        }

        if (node is StructNode structNode)
        {
            ValidateStructDeclaration(structNode);
            return;
        }

        if (node is EnumNode enumNode)
        {
            ValidateEnumNode(enumNode);
            return;
        }

        if (node is UnionNode unionNode)
        {
            ValidateUnionNode(unionNode);
            return;
        }

        if (node is FunctionDecl functionDecl)
            ValidateFunctionDeclaration(functionDecl);
    }
    private void ValidateVariableDeclaration(VariableDeclarationNode variableDeclaration)
    {
        ResolveAlignmentAttribute(
            variableDeclaration.Attributes,
            variableDeclaration.AlignExpr,
            variableDeclaration,
            $"Variable '{variableDeclaration.Name}' Alignment");
        ValidateTypeReference(variableDeclaration.TypeName, variableDeclaration);
        CheckVariableInitializer(variableDeclaration);
    }
    private void ValidateStructDeclaration(StructNode structNode)
    {
        ResolveStructAttributes(structNode);
        ValidateTypeParameterDeclarations(structNode.TypeParameterSpecs, structNode, $"Struct '{QualifiedNames.GetFullName(structNode.NamespacePath, structNode.Name)}'");
        PushTypeParameterScope(structNode.TypeParameters);
        try
        {
            foreach (var field in structNode.Fields)
                ValidateTypeReference(field.TypeName, field);
        }
        finally
        {
            PopTypeParameterScope();
        }

        var fullName = QualifiedNames.GetFullName(structNode.NamespacePath, structNode.Name);
        ValidateStructAttributes(structNode, fullName);
    }
    private void ValidateEnumNode(EnumNode enumNode)
    {
        ValidateTypeParameterDeclarations(enumNode.TypeParameterSpecs, enumNode, $"Enum '{QualifiedNames.GetFullName(enumNode.NamespacePath, enumNode.Name)}'");
        PushTypeParameterScope(enumNode.TypeParameters);
        try
        {
            ValidateEnumDeclaration(enumNode);
        }
        finally
        {
            PopTypeParameterScope();
        }
    }
    private void ValidateUnionNode(UnionNode unionNode)
    {
        ValidateTypeParameterDeclarations(unionNode.TypeParameterSpecs, unionNode, $"Union '{QualifiedNames.GetFullName(unionNode.NamespacePath, unionNode.Name)}'");
        PushTypeParameterScope(unionNode.TypeParameters);
        try
        {
            ValidateUnionDeclaration(unionNode);
        }
        finally
        {
            PopTypeParameterScope();
        }
    }
    private void ValidateFunctionDeclaration(FunctionDecl functionDecl)
    {
        ResolveAlignmentAttribute(functionDecl.Attributes, functionDecl.AlignExpr, functionDecl, "Function Alignment");
        if (functionDecl.IsExtern && functionDecl.TypeParameters.Count > 0)
            _errors.Error(functionDecl, $"Extern function '{QualifiedNames.GetFullName(functionDecl.NamespacePath, functionDecl.Name)}' cannot declare type parameters.");
        ValidateTypeParameterDeclarations(functionDecl.TypeParameterSpecs, functionDecl, $"Function '{QualifiedNames.GetFullName(functionDecl.NamespacePath, functionDecl.Name)}'");

        PushTypeParameterScope(functionDecl.TypeParameters);
        try
        {
            ValidateFunctionSignature(functionDecl);
        }
        finally
        {
            PopTypeParameterScope();
        }

        if (!functionDecl.IsExtern)
            ValidateFunctionBody(functionDecl);
    }
    private void ValidateFunctionSignature(FunctionDecl functionDecl)
    {
        ValidateTypeReference(functionDecl.ReturnType, functionDecl);
        foreach (var param in functionDecl.Parameters)
            ValidateTypeReference(param.TypeName, functionDecl);
    }
    private void ValidateFunctionBody(FunctionDecl functionDecl)
    {
        _currentFunction = functionDecl;
        PushTypeParameterScope(functionDecl.TypeParameters);
        PushScopedState();
        try
        {
            RegisterFunctionParameters(functionDecl);

            var flow = CheckBlock(functionDecl.Body, pushScope: false);
            if (FunctionRequiresReturn(functionDecl) && flow != FlowOutcome.Returns)
                _errors.Error(functionDecl, $"Function '{functionDecl.Name}' may exit without returning a value");
        }
        finally
        {
            PopScopedState();
            PopTypeParameterScope();
            _currentFunction = null;
        }
    }
    private void RegisterFunctionParameters(FunctionDecl functionDecl)
    {
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
    }
    private void ProcessImport(ImportNode importNode, string currentDir)
    {
        if (importNode.Alias == "c")
            return;

        var fullPath = ResolveImportFullPath(importNode, currentDir);
        if (TryApplyCachedImport(importNode, fullPath))
            return;

        if (_processedImports.Contains(fullPath))
            return;

        if (!File.Exists(fullPath))
        {
            _processedImports.Add(fullPath);
            _errors.Error(importNode, $"Import file not found: '{importNode.Path}' resolved to '{fullPath}'.");
            return;
        }

        var dir = Path.GetDirectoryName(fullPath) ?? ".";
        if (!TryLoadImportedNodes(fullPath, out var importedNodes))
            return;

        var exports = CollectExportedSymbols(importedNodes);
        _fileExports[fullPath] = exports;
        _processedImports.Add(fullPath);
        ApplyImportExports(importNode, exports);
        CheckImportedNodes(importedNodes, dir);
    }
    private string ResolveImportFullPath(ImportNode importNode, string currentDir)
    {
        return Path.GetFullPath(Path.IsPathRooted(importNode.Path)
            ? importNode.Path
            : Path.Combine(currentDir, importNode.Path));
    }
    private bool TryApplyCachedImport(ImportNode importNode, string fullPath)
    {
        if (!_fileExports.TryGetValue(fullPath, out var cachedExports))
            return false;

        ApplyImportExports(importNode, cachedExports);
        return true;
    }
    private bool TryLoadImportedNodes(string fullPath, out IReadOnlyList<Node> importedNodes)
    {
        if (_parsedFilesByPath != null && _parsedFilesByPath.TryGetValue(fullPath, out var preParsedNodes))
        {
            importedNodes = preParsedNodes;
            return true;
        }

        string source;
        try
        {
            source = File.ReadAllText(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            _errors.Error($"Failed to read import '{fullPath}': {ex.Message}", 0, 0, fullPath);
            importedNodes = Array.Empty<Node>();
            return false;
        }

        List<Token> tokens;
        try
        {
            var lexer = new Zorb.Compiler.Lexer.Lexer(source, fullPath);
            tokens = lexer.Tokenize();
        }
        catch (LexerException ex)
        {
            _errors.Error(ex.Message, ex.Line, ex.Column, ex.File);
            importedNodes = Array.Empty<Node>();
            return false;
        }

        var parser = new Zorb.Compiler.Parser.Parser(tokens, fullPath, _errors);
        importedNodes = parser.ParseProgram();
        return true;
    }
    private List<string> CollectExportedSymbols(IReadOnlyList<Node> importedNodes)
    {
        var exports = new List<string>();
        foreach (var node in importedNodes)
            CollectExportedSymbols(node, exports);

        return exports;
    }
    private void CollectExportedSymbols(Node node, List<string> exports)
    {
        if (node is FunctionDecl functionDecl && functionDecl.IsExported)
        {
            exports.Add(QualifiedNames.GetFullName(functionDecl.NamespacePath, functionDecl.Name));
            return;
        }

        if (node is StructNode structNode && structNode.IsExported)
        {
            exports.Add(QualifiedNames.GetFullName(structNode.NamespacePath, structNode.Name));
            return;
        }

        if (node is EnumNode enumNode && enumNode.IsExported)
        {
            var enumName = QualifiedNames.GetFullName(enumNode.NamespacePath, enumNode.Name);
            exports.Add(enumName);
            exports.AddRange(enumNode.Members.Select(member => $"{enumName}.{member.Name}"));
            return;
        }

        if (node is UnionNode unionNode && unionNode.IsExported)
        {
            var unionName = QualifiedNames.GetFullName(unionNode.NamespacePath, unionNode.Name);
            var tagEnumName = GetUnionTagEnumFullName(unionNode);
            exports.Add(unionName);
            exports.Add(tagEnumName);
            exports.AddRange(unionNode.Variants.Select(variant => $"{tagEnumName}.{variant.Name}"));
            return;
        }

        if (node is VariableDeclarationNode variableDeclaration && variableDeclaration.IsExported)
            exports.Add(variableDeclaration.Name);
    }
    private void ApplyImportExports(ImportNode importNode, IEnumerable<string> exports)
    {
        if (!string.IsNullOrEmpty(importNode.Alias))
        {
            RegisterImportAlias(importNode.Alias, exports);
            return;
        }

        foreach (var symbol in exports)
            MakeVisible(symbol);
    }
    private void CheckImportedNodes(IReadOnlyList<Node> importedNodes, string dir)
    {
        var previousDir = _currentDir;
        _currentDir = dir;
        _fileScopes.Push(new HashSet<string>());
        _importAliasScopes.Push(new Dictionary<string, HashSet<string>>(StringComparer.Ordinal));
        try
        {
            MakeBuiltinsVisibleInImportedScope();
            CheckNodes(importedNodes, dir);
        }
        finally
        {
            _importAliasScopes.Pop();
            _fileScopes.Pop();
            _currentDir = previousDir;
        }
    }
    private void MakeBuiltinsVisibleInImportedScope()
    {
        MakeVisible("syscall");
        MakeVisible("Builtin.IsLinux");
        MakeVisible("Builtin.IsWindows");
        MakeVisible("Builtin.IsBareMetal");
        MakeVisible("Builtin.IsX86_64");
        MakeVisible("Builtin.IsAArch64");
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
    private static bool IsBuiltinDeclarationName(string name)
    {
        return name is "syscall"
            or "Builtin.IsLinux"
            or "Builtin.IsWindows"
            or "Builtin.IsBareMetal"
            or "Builtin.IsX86_64"
            or "Builtin.IsAArch64"
            or "Builtin.CompileError"
            or "Builtin.sizeof";
    }
    private static bool IsInvalidPostfixTarget(Expr expr)
    {
        return expr switch
        {
            InvalidExpr => true,
            CallExpr { TargetExpr: Expr target } => IsInvalidPostfixTarget(target),
            FieldExpr field => IsInvalidPostfixTarget(field.Target),
            IndexExpr index => IsInvalidPostfixTarget(index.Target),
            _ => false
        };
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
}
