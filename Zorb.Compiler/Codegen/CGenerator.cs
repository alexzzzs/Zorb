using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using Zorb.Compiler.AST;
using Zorb.Compiler.AST.Expressions;
using Zorb.Compiler.AST.Statements;
using Zorb.Compiler.Lexer;
using Zorb.Compiler.Semantic;
using Zorb.Compiler.Utils;

namespace Zorb.Compiler.Codegen;

public class CGenerator
{
    private sealed record GeneratedExpression(string Prelude, string Code, TypeNode? Type);

    private readonly List<StructNode> _structs = new();
    private readonly HashSet<string> _includes = new() { "stdint.h" };
    private readonly HashSet<string> _cHeaders = new();
    private readonly string _currentDir;
    private readonly SymbolTable _symbolTable;
    private readonly Dictionary<string, TypeNode> _localVars = new();
    private readonly HashSet<string> _generatedResultTypes = new();
    private readonly StringBuilder _dynamicStructs = new();
    private List<Node> _allNodes = new();
    private int _tempCounter;
    private bool _insideFunctionBody;
    public bool PreserveStart { get; set; }
    public bool NoStdLib { get; set; }

    private static string GetErrorSymbolName(string errorCode)
    {
        return $"Error_{errorCode}";
    }

    private static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    private const string LinuxSyscallWrapper = @"
#if defined(__x86_64__)
static int64_t __zorb_syscall(int64_t n, int64_t a1, int64_t a2, int64_t a3, int64_t a4, int64_t a5, int64_t a6) {
    int64_t ret;
    register int64_t r10 __asm__(""r10"") = a4;
    register int64_t r8  __asm__(""r8"")  = a5;
    register int64_t r9  __asm__(""r9"")  = a6;
    __asm__ volatile (
        ""syscall""
        : ""=a""(ret)
        : ""a""(n), ""D""(a1), ""S""(a2), ""d""(a3), ""r""(r10), ""r""(r8), ""r""(r9)
        : ""rcx"", ""r11"", ""memory""
    );
    return ret;
}
#elif defined(__aarch64__)
static int64_t __zorb_syscall(int64_t n, int64_t a1, int64_t a2, int64_t a3, int64_t a4, int64_t a5, int64_t a6) {
    register int64_t x8 __asm__(""x8"") = n;
    register int64_t x0 __asm__(""x0"") = a1;
    register int64_t x1 __asm__(""x1"") = a2;
    register int64_t x2 __asm__(""x2"") = a3;
    register int64_t x3 __asm__(""x3"") = a4;
    register int64_t x4 __asm__(""x4"") = a5;
    register int64_t x5 __asm__(""x5"") = a6;
    __asm__ volatile (
        ""svc #0""
        : ""+r""(x0)
        : ""r""(x8), ""r""(x1), ""r""(x2), ""r""(x3), ""r""(x4), ""r""(x5)
        : ""memory""
    );
    return x0;
}
#else
static int64_t __zorb_syscall(int64_t n, int64_t a1, int64_t a2, int64_t a3, int64_t a4, int64_t a5, int64_t a6) {
    (void)n; (void)a1; (void)a2; (void)a3; (void)a4; (void)a5; (void)a6;
    return -38;
}
#endif

#define SYSCALL_GET_8TH(_1,_2,_3,_4,_5,_6,_7,NAME,...) NAME
#define syscall(...) SYSCALL_GET_8TH(__VA_ARGS__, __syscall7, __syscall6, __syscall5, __syscall4, __syscall3, __syscall2, __syscall1)(__VA_ARGS__)

#define __syscall1(n) __zorb_syscall((int64_t)n, 0, 0, 0, 0, 0, 0)
#define __syscall2(n, a) __zorb_syscall((int64_t)n, (int64_t)a, 0, 0, 0, 0, 0)
#define __syscall3(n, a, b) __zorb_syscall((int64_t)n, (int64_t)a, (int64_t)b, 0, 0, 0, 0)
#define __syscall4(n, a, b, c) __zorb_syscall((int64_t)n, (int64_t)a, (int64_t)b, (int64_t)c, 0, 0, 0)
#define __syscall5(n, a, b, c, d) __zorb_syscall((int64_t)n, (int64_t)a, (int64_t)b, (int64_t)c, (int64_t)d, 0, 0)
#define __syscall6(n, a, b, c, d, e) __zorb_syscall((int64_t)n, (int64_t)a, (int64_t)b, (int64_t)c, (int64_t)d, (int64_t)e, 0)
#define __syscall7(n, a, b, c, d, e, f) __zorb_syscall((int64_t)n, (int64_t)a, (int64_t)b, (int64_t)c, (int64_t)d, (int64_t)e, (int64_t)f)

";

    public CGenerator() : this(".", new SymbolTable()) {}

    public CGenerator(string currentDir) : this(currentDir, new SymbolTable()) {}

    public CGenerator(string currentDir, SymbolTable symbolTable)
    {
        _currentDir = currentDir;
        _symbolTable = symbolTable;
    }

    public string Generate(List<Node> nodes)
    {
        var allNodes = new List<Node>();
        var processed = new HashSet<string>();
        var emittedItems = new HashSet<string>();
        
        _dynamicStructs.Clear();
        _generatedResultTypes.Clear();
        _allNodes = allNodes;
        _tempCounter = 0;
        _insideFunctionBody = false;
        
        CollectNodes(nodes, allNodes, processed, emittedItems, _currentDir);

        // PASS 1: Generate variables and functions first. 
        // This ensures MapType is called and all dynamic structs (like Result_u8_ptr) are generated.
        var varsSb = new StringBuilder();
        var emittedConstants = new HashSet<string>();
        var emittedVars = new HashSet<string>();
        var variables = allNodes.OfType<VariableDeclarationNode>().ToList();
        foreach (var v in variables)
        {
            if (emittedVars.Contains(v.Name)) continue;
            emittedVars.Add(v.Name);
            
            var stmt = GenerateStatement(v);
            if (v.IsConst && v.TypeName.Name == "i32" && v.Value is NumberExpr)
            {
                var constKey = v.Name;
                if (emittedConstants.Contains(constKey))
                    continue;
                emittedConstants.Add(constKey);
            }
            varsSb.AppendLine(stmt);
        }

        var funcsSb = new StringBuilder();
        var functions = allNodes.OfType<FunctionDecl>().ToList();
        foreach (var fn in functions)
        {
            var cName = GetFunctionCName(fn.NamespacePath, fn.Name, null);
            if (emittedItems.Contains(cName))
                continue;
            emittedItems.Add(cName);
            funcsSb.AppendLine(GenerateFunction(fn, null));
        }

        // PASS 2: Assemble the final file
        var sb = new StringBuilder();

        // Standard headers
        sb.AppendLine("#include <stdint.h>");
        
        sb.AppendLine("#if defined(__linux__)");
        sb.AppendLine("    #define __zorb_builtin_is_linux 1");
        sb.AppendLine("#else");
        sb.AppendLine("    #define __zorb_builtin_is_linux 0");
        sb.AppendLine("#endif");
        sb.AppendLine("#if defined(_WIN32)");
        sb.AppendLine("    #define __zorb_builtin_is_windows 1");
        sb.AppendLine("#else");
        sb.AppendLine("    #define __zorb_builtin_is_windows 0");
        sb.AppendLine("#endif");
        sb.AppendLine("#if defined(__x86_64__) || defined(_M_X64)");
        sb.AppendLine("    #define __zorb_builtin_is_x86_64 1");
        sb.AppendLine("#else");
        sb.AppendLine("    #define __zorb_builtin_is_x86_64 0");
        sb.AppendLine("#endif");
        sb.AppendLine("#if defined(__aarch64__) || defined(_M_ARM64)");
        sb.AppendLine("    #define __zorb_builtin_is_aarch64 1");
        sb.AppendLine("#else");
        sb.AppendLine("    #define __zorb_builtin_is_aarch64 0");
        sb.AppendLine("#endif");
        
        // User C headers
        foreach (var header in _includes)
        {
            if (header != "stdint.h")
                sb.AppendLine($"#include <{header}>");
        }
        foreach (var header in _cHeaders)
        {
            if (header != "windows.h")
                sb.AppendLine($"#include <{header}>");
        }
        sb.AppendLine();

        // Syscall shim always exists so non-Linux branches still parse on other hosts.
        sb.AppendLine(LinuxSyscallWrapper);

        // Emit AST structs first (must be before prototypes)
        var emittedStructs = new HashSet<string>();
        var structs = allNodes.OfType<StructNode>().ToList();
        foreach (var s in structs)
        {
            var cName = FlattenName(s.NamespacePath, s.Name, null);
            if (emittedStructs.Contains(cName))
                continue;
            emittedStructs.Add(cName);
            sb.Append(GenerateStruct(s, null));
        }

        // EMIT DYNAMIC STRUCTS (Result unions)
        sb.Append(_dynamicStructs.ToString());

        // Generate function prototypes (structs are already defined)
        var prototypesSb = new StringBuilder();
        var emittedPrototypes = new HashSet<string>();
        foreach (var fn in functions)
        {
            if (fn.IsExtern) continue;
            var lowersToHostedMain = ShouldLowerToHostedMain(fn.NamespacePath, fn.Name);
            var cReturnType = lowersToHostedMain ? "int" : MapType(fn.ReturnType);
            var cName = GetFunctionCName(fn.NamespacePath, fn.Name, null);
            if (emittedPrototypes.Contains(cName)) continue;
            emittedPrototypes.Add(cName);
            var parameters = string.Join(", ", fn.Parameters.Select(p => MapType(p.TypeName, p.Name)));
            prototypesSb.AppendLine($"{cReturnType} {cName}({parameters});");
        }
        sb.Append(prototypesSb.ToString());
        sb.AppendLine();

        // Emit constants/variables
        sb.Append(varsSb.ToString());

        // Emit functions
        sb.Append(funcsSb.ToString());

        return sb.ToString();
    }

    private void CollectNodes(List<Node> nodes, List<Node> result, HashSet<string> processed, HashSet<string> emittedItems, string currentDir)
    {
        foreach (var node in nodes)
        {
            if (node is ImportNode import)
            {
                if (import.Alias == "c")
                {
                    _cHeaders.Add(import.Path);
                    continue;
                }

                var fullPath = Path.IsPathRooted(import.Path) 
                    ? import.Path 
                    : Path.Combine(currentDir, import.Path);
                fullPath = Path.GetFullPath(fullPath);

                if (processed.Contains(fullPath))
                    continue;
                processed.Add(fullPath);

                var dir = Path.GetDirectoryName(fullPath) ?? ".";
                var subNodes = ParseFile(fullPath, dir, processed);
                
                CollectNodes(subNodes, result, processed, emittedItems, dir);
            }
            else
            {
                result.Add(node);
            }
        }
    }

    private List<Node> ParseFile(string path, string dir, HashSet<string> processed)
    {
        if (!File.Exists(path))
            throw new System.Exception($"Import file not found: {path}");

        var source = File.ReadAllText(path);
        List<Token> tokens;
        try
        {
            var lexer = new Zorb.Compiler.Lexer.Lexer(source, path);
            tokens = lexer.Tokenize();
        }
        catch (LexerException ex)
        {
            throw new ZorbCompilerException($"{ex.File}:{ex.Line}:{ex.Column}: error: {ex.Message}");
        }
        var errorReporter = new ErrorReporter();
        var parser = new Zorb.Compiler.Parser.Parser(tokens, path, errorReporter);
        var nodes = parser.ParseProgram();
        errorReporter.ThrowIfErrors();
        return nodes;
    }

    private string GenerateStruct(StructNode s, string? prefix)
    {
        var sb = new StringBuilder();
        var cName = FlattenName(s.NamespacePath, s.Name, prefix);
        sb.AppendLine($"struct {cName} {{");
        foreach (var f in s.Fields)
        {
            var cType = f.Type.IsFunction ? MapType(f.Type, f.Name) : MapType(f.Type);
            if (f.Type.ArraySize != null)
                sb.AppendLine($"    {cType} {f.Name}[{f.Type.ArraySize}];");
            else if (f.Type.IsFunction)
                sb.AppendLine($"    {cType};");
            else
                sb.AppendLine($"    {cType} {f.Name};");
        }
        sb.AppendLine("};");
        sb.AppendLine();
        return sb.ToString();
    }

    private string GenerateFunction(FunctionDecl fn, string? prefix)
    {
        _localVars.Clear();
        foreach (var p in fn.Parameters) _localVars[p.Name] = p.TypeName;

        if (fn.ReturnType.IsErrorUnion)
        {
            _localVars["__return_type"] = fn.ReturnType;
        }

        var rawName = fn.Name;
        var sb = new StringBuilder();
        var lowersToHostedMain = ShouldLowerToHostedMain(fn.NamespacePath, rawName);
        var cReturnType = lowersToHostedMain ? "int" : MapType(fn.ReturnType);
        var parameters = string.Join(", ", fn.Parameters.Select(p => MapType(p.TypeName, p.Name)));
        var cName = GetFunctionCName(fn.NamespacePath, rawName, prefix);

        var attrs = new List<string>();
        foreach (var attr in fn.Attributes)
        {
            if (attr == "noinline")
                attrs.Add("noinline");
            else if (attr == "noclone" && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                attrs.Add("noclone");
            else if (attr.StartsWith("align(") && attr.EndsWith(")"))
                attrs.Add($"aligned({attr.Substring(6, attr.Length - 7)})");
        }
        var attrStr = attrs.Count > 0 ? $"__attribute__(({string.Join(", ", attrs)})) " : "";

        if (fn.IsExtern)
        {
            sb.AppendLine($"{attrStr}extern {cReturnType} {cName}({parameters});");
            return sb.ToString();
        }

        sb.AppendLine($"{attrStr}{cReturnType} {cName}({parameters}) {{");
        _insideFunctionBody = true;
        foreach (var stmt in fn.Body)
            sb.AppendLine($"    {GenerateStatement(stmt)}");
        if (lowersToHostedMain && fn.ReturnType.Name == "void" && !fn.ReturnType.IsPointer && !fn.ReturnType.IsErrorUnion)
            sb.AppendLine("    return 0;");
        _insideFunctionBody = false;
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string MapType(TypeNode type, string name = "")
    {
        if (type.IsErrorUnion)
        {
            var innerType = type.ErrorInnerType ?? type;
            var resultName = "Result_" + GetResultTypeName(innerType);
            GenerateResultStruct(innerType, resultName);
            if (string.IsNullOrEmpty(name)) return "struct " + resultName;
            return $"struct {resultName} {name}";
        }

        if (type.IsFunction)
        {
            var ret = MapType(type.ReturnType ?? new TypeNode { Name = "void" });
            var args = string.Join(", ", type.ParamTypes.Select(t => MapType(t)));
            if (string.IsNullOrEmpty(name))
                return $"{ret} (*)( {args} )";
            return $"{ret} (*{name})({args})";
        }

        string baseType;
        baseType = type.Name switch
        {
            "i8" => "int8_t",
            "i16" => "int16_t",
            "i32" => "int32_t",
            "i64" => "int64_t",
            "u8" => "uint8_t",
            "u16" => "uint16_t",
            "u32" => "uint32_t",
            "u64" => "uint64_t",
            "string" => "char*",
            "void" => "void",
            "char" => "char",
            // If not a primitive, it's a struct - flatten name and prefix with 'struct'
            _ => "struct " + FlattenName(type.NamespacePath, type.Name)
        };

        if (type.IsPointer && type.Name != "string")
        {
            var pointerLevel = type.PointerLevel > 0 ? type.PointerLevel : 1;
            baseType += new string('*', pointerLevel);
        }

        if (string.IsNullOrEmpty(name)) return baseType;
        return $"{baseType} {name}";
    }

    private string GetResultTypeName(TypeNode innerType)
    {
        var baseName = innerType.Name switch
        {
            "i8" => "i8",
            "i16" => "i16",
            "i32" => "i32",
            "i64" => "i64",
            "u8" => "u8",
            "u16" => "u16",
            "u32" => "u32",
            "u64" => "u64",
            "string" => "ptr",
            "void" => "void",
            "char" => "char",
            _ => FlattenName(innerType.NamespacePath, innerType.Name)
        };
        if (innerType.IsPointer && innerType.Name != "string")
        {
            var pointerLevel = innerType.PointerLevel > 0 ? innerType.PointerLevel : 1;
            for (int i = 0; i < pointerLevel; i++)
                baseName += "_ptr";
        }
        return baseName;
    }

    private void GenerateResultStruct(TypeNode innerType, string resultName)
    {
        if (_generatedResultTypes.Contains(resultName))
            return;
        _generatedResultTypes.Add(resultName);

        var cInnerType = innerType.Name switch
        {
            "i8" => "int8_t",
            "i16" => "int16_t",
            "i32" => "int32_t",
            "i64" => "int64_t",
            "u8" => "uint8_t",
            "u16" => "uint16_t",
            "u32" => "uint32_t",
            "u64" => "uint64_t",
            "string" => "char*",
            "void" => "int8_t",
            "char" => "char",
            _ => "struct " + FlattenName(innerType.NamespacePath, innerType.Name)
        };

        if (innerType.IsPointer && innerType.Name != "string")
        {
            var pointerLevel = innerType.PointerLevel > 0 ? innerType.PointerLevel : 1;
            cInnerType += new string('*', pointerLevel);
        }

        _includes.Add("stdint.h");
        var structCode = $"struct {resultName} {{\n    {cInnerType} value;\n    int32_t error;\n}};\n";
        
        _dynamicStructs.AppendLine(structCode);
        
        _structs.Insert(0, new StructNode { Name = resultName, Fields = new List<(string, TypeNode)> { ("value", innerType), ("error", new TypeNode { Name = "i32" }) } });
    }

    private string FlattenName(List<string> path, string name, string? prefix = null)
    {
        var parts = new List<string>();
        if (prefix != null)
            parts.Add(prefix);
        parts.AddRange(path);
        parts.Add(name);
        return string.Join("_", parts);
    }

    private bool ShouldLowerToHostedMain(List<string> namespacePath, string name)
    {
        return !PreserveStart && namespacePath.Count == 0 && name == "_start";
    }

    private string GetFunctionCName(List<string> namespacePath, string name, string? prefix = null)
    {
        if (ShouldLowerToHostedMain(namespacePath, name))
            return "main";

        return FlattenName(namespacePath, name, prefix);
    }

    private string GenerateStatement(Statement stmt)
    {
        if (stmt is ExpressionStatement es)
        {
            var generated = GenerateExpressionWithPrelude(es.Expression);
            if (string.IsNullOrEmpty(generated.Prelude))
                return generated.Code + ";";

            var sb = new StringBuilder();
            sb.Append(generated.Prelude);
            sb.Append(generated.Code);
            sb.Append(';');
            return sb.ToString();
        }
        if (stmt is VariableDeclarationNode vd)
        {
            var safeName = vd.Name.Replace(".", "_");

            _localVars[vd.Name] = vd.TypeName; // Track the variable type

            var isFuncPtr = vd.TypeName.IsFunction;
            var alignment = "";
            foreach (var attr in vd.Attributes)
            {
                if (attr.StartsWith("align(") && attr.EndsWith(")"))
                {
                    var alignNum = attr.Substring(6, attr.Length - 7);
                    alignment = $" __attribute__((aligned(" + alignNum + ")))";
                }
            }
            if (vd.TypeName.ArraySize != null)
            {
                var cTypeArray = isFuncPtr ? MapType(vd.TypeName, safeName) : MapType(vd.TypeName);
                if (vd.Value != null)
                    return $"{cTypeArray} {safeName}[{vd.TypeName.ArraySize}] = {GenerateExpression(vd.Value)}{alignment};";
                return $"{cTypeArray} {safeName}[{vd.TypeName.ArraySize}]{alignment};";
            }
            if (vd.Value is StringExpr)
            {
                var baseType = vd.TypeName.Name == "string" ? "char" : "uint8_t";
                return $"{baseType} {safeName}[] = {GenerateExpression(vd.Value)}{alignment};";
            }
            var cType = isFuncPtr ? MapType(vd.TypeName, safeName) : MapType(vd.TypeName);
            var constKeyword = vd.IsConst ? "const " : "";
            if (vd.Value != null)
            {
                var generated = GenerateExpressionWithPrelude(vd.Value);
                var valueCode = generated.Code;
                var valueType = generated.Type ?? GetExprType(vd.Value);
                if (valueType != null && valueType.IsErrorUnion && !vd.TypeName.IsErrorUnion)
                {
                    var innerType = valueType.ErrorInnerType ?? valueType;
                    var resultName = "Result_" + GetResultTypeName(innerType);
                    GenerateResultStruct(innerType, resultName);
                    valueCode = $"({valueCode}).value";
                }
                var declaration = isFuncPtr
                    ? $"{constKeyword}{cType} = {valueCode}{alignment};"
                    : $"{constKeyword}{cType} {safeName} = {valueCode}{alignment};";

                if (string.IsNullOrEmpty(generated.Prelude))
                    return declaration;

                var sb = new StringBuilder();
                sb.Append(generated.Prelude);
                sb.Append(declaration);
                if (isFuncPtr)
                    return sb.ToString();
                return sb.ToString();
            }
            if (isFuncPtr)
                return $"{constKeyword}{cType}{alignment};";
            return $"{constKeyword}{cType} {safeName}{alignment};";
        }
        if (stmt is IfStmt ifs)
        {
            if (TryGetPlatformPreprocessorCondition(ifs.Condition, out var preprocessorCondition))
            {
                var conditionalSb = new StringBuilder();
                conditionalSb.AppendLine($"#if {preprocessorCondition}");
                AppendIndentedGeneratedBlock(conditionalSb, ifs.Body, "    ");
                if (ifs.ElseBody.Count > 0)
                {
                    conditionalSb.AppendLine("#else");
                    AppendIndentedGeneratedBlock(conditionalSb, ifs.ElseBody, "    ");
                }
                conditionalSb.Append("#endif");
                return conditionalSb.ToString();
            }

            var generatedCondition = GenerateExpressionWithPrelude(ifs.Condition);
            var cond = generatedCondition.Code;
            var sb = new StringBuilder();
            sb.Append(generatedCondition.Prelude);
            sb.AppendLine($"if ({cond}) {{");
            AppendIndentedGeneratedBlock(sb, ifs.Body, "        ");
            sb.Append("    }");
            if (ifs.ElseBody.Count > 0)
            {
                sb.AppendLine(" else {");
                AppendIndentedGeneratedBlock(sb, ifs.ElseBody, "        ");
                sb.Append("    }");
            }
            return sb.ToString();
        }
        if (stmt is WhileStmt ws)
        {
            var sb = new StringBuilder();
            var generatedCondition = GenerateExpressionWithPrelude(ws.Condition);
            if (string.IsNullOrEmpty(generatedCondition.Prelude))
            {
                sb.AppendLine($"while ({generatedCondition.Code}) {{");
            }
            else
            {
                sb.AppendLine("while (1) {");
                AppendIndentedBlock(sb, generatedCondition.Prelude, "        ");
                sb.AppendLine($"        if (!({generatedCondition.Code})) break;");
            }
            AppendIndentedGeneratedBlock(sb, ws.Body, "        ");
            sb.Append("    }");
            return sb.ToString();
        }
        if (stmt is ContinueStmt)
            return "continue;";
        if (stmt is BreakStmt)
            return "break;";
        if (stmt is AssignStmt assign)
        {
            var targetType = GetExprType(assign.Target);
            var valueExpr = assign.Value;
            var generated = GenerateExpressionWithPrelude(valueExpr);
            var valueType = generated.Type ?? GetExprType(valueExpr);
            var valueCode = generated.Code;
            
            if (valueType != null && valueType.IsErrorUnion && (targetType == null || !targetType.IsErrorUnion))
            {
                var innerType = valueType.ErrorInnerType ?? valueType;
                var resultName = "Result_" + GetResultTypeName(innerType);
                GenerateResultStruct(innerType, resultName);
                valueCode = $"({valueCode}).value";
            }

            var assignment = $"{GenerateExpression(assign.Target)} = {valueCode};";
            if (string.IsNullOrEmpty(generated.Prelude))
                return assignment;

            return generated.Prelude + assignment;
        }
        if (stmt is ReturnNode ret)
        {
            if (ret.Value == null) return "return;";

            var generated = GenerateExpressionWithPrelude((Expr)ret.Value);
            var valueCode = generated.Code;

            var fnReturnType = _localVars.TryGetValue("__return_type", out var rt) ? rt : null;
            if (fnReturnType != null && fnReturnType.IsErrorUnion)
            {
                var innerType = fnReturnType.ErrorInnerType ?? fnReturnType;
                var resultName = "Result_" + GetResultTypeName(innerType);
                GenerateResultStruct(innerType, resultName);

                if (ret.Value is ErrorExpr errExpr) {
                    var returnCode = $"return (struct {resultName}){{ .value = 0, .error = {GetErrorSymbolName(errExpr.ErrorCode)} }};";
                    return string.IsNullOrEmpty(generated.Prelude) ? returnCode : generated.Prelude + returnCode;
                } else {
                    var returnCode = $"return (struct {resultName}){{ .value = {valueCode}, .error = 0 }};";
                    return string.IsNullOrEmpty(generated.Prelude) ? returnCode : generated.Prelude + returnCode;
                }
            }
            var plainReturn = $"return {valueCode};";
            return string.IsNullOrEmpty(generated.Prelude) ? plainReturn : generated.Prelude + plainReturn;
        }
        if (stmt is AsmStatementNode asm)
        {
            var quotedCode = string.Join("", asm.Code.Select(c => $"\"{c}\\n\""));
            var outputs = GenerateAsmOperands(asm.Outputs);
            var inputs = GenerateAsmOperands(asm.Inputs);
            var clobbers = string.Join(", ", asm.Clobbers.Select(c => $"\"{c}\""));
            return $"__asm__ volatile ({quotedCode} : {outputs} : {inputs} : {clobbers});";
        }
        throw new System.Exception("Unknown statement type");
    }

    private string GenerateStatementBlock(List<Statement> statements)
    {
        if (statements.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        var savedLocals = CloneLocalVars();
        foreach (var statement in statements)
        {
            var generated = GenerateStatement(statement);
            if (!string.IsNullOrEmpty(generated))
                sb.AppendLine(generated);
        }
        RestoreLocalVars(savedLocals);
        return sb.ToString().TrimEnd();
    }

    private void AppendIndentedGeneratedBlock(StringBuilder sb, List<Statement> statements, string indent)
    {
        var generated = GenerateStatementBlock(statements);
        if (!string.IsNullOrEmpty(generated))
            AppendIndentedBlock(sb, generated, indent);
    }

    private bool TryGetPlatformPreprocessorCondition(Expr expr, out string condition)
    {
        if (expr is BuiltinExpr builtin)
        {
            switch (builtin.Name)
            {
                case "Builtin.IsLinux":
                    condition = "defined(__linux__)";
                    return true;
                case "Builtin.IsWindows":
                    condition = "defined(_WIN32)";
                    return true;
                case "Builtin.IsX86_64":
                    condition = "defined(__x86_64__) || defined(_M_X64)";
                    return true;
                case "Builtin.IsAArch64":
                    condition = "defined(__aarch64__) || defined(_M_ARM64)";
                    return true;
            }
        }

        condition = string.Empty;
        return false;
    }

    private string GenerateAsmOperands(List<AsmOperand> operands)
    {
        return string.Join(", ", operands.Select(operand =>
            $"\"{operand.Constraint}\"({GenerateExpression(operand.Expression)})"));
    }

    private string GenerateExpression(Expr expr)
    {
        var generated = GenerateExpressionWithPrelude(expr);
        if (!string.IsNullOrEmpty(generated.Prelude))
        {
            if (!_insideFunctionBody)
                throw new Exception("Catch expressions requiring control-flow lowering are not supported in global initializers");

            return WrapStatementExpression(generated);
        }
        return generated.Code;
    }

    private string WrapStatementExpression(GeneratedExpression generated)
    {
        var sb = new StringBuilder();
        sb.AppendLine("({");
        AppendIndentedBlock(sb, generated.Prelude, "    ");
        sb.Append("    ");
        sb.Append(generated.Code);
        sb.AppendLine(";");
        sb.Append("})");
        return sb.ToString();
    }

    private GeneratedExpression GenerateExpressionWithPrelude(Expr expr)
    {
        switch (expr)
        {
            case CallExpr call:
                var callPrelude = new StringBuilder();
                var generatedArgs = new List<string>();
                foreach (var arg in call.Args)
                {
                    var generatedArg = GenerateExpressionWithPrelude(arg);
                    callPrelude.Append(generatedArg.Prelude);
                    generatedArgs.Add(generatedArg.Code);
                }
                var args = string.Join(", ", generatedArgs);

                if (call.TargetExpr != null)
                {
                    var generatedTarget = GenerateExpressionWithPrelude(call.TargetExpr);
                    callPrelude.Append(generatedTarget.Prelude);
                    var target = generatedTarget.Code.Replace(".", "_");
                    return new GeneratedExpression(callPrelude.ToString(), $"{target}({args})", GetExprType(expr));
                }

                var cCallName = GetFunctionCName(call.NamespacePath, call.Name, null);
                return new GeneratedExpression(callPrelude.ToString(), $"{cCallName}({args})", GetExprType(expr));
            case NumberExpr num:
                return new GeneratedExpression("", num.Value.ToString(), GetExprType(expr));
            case StringExpr str:
                return new GeneratedExpression("", $"\"{EscapeCString(str.Value)}\"", GetExprType(expr));
            case IdentifierExpr ident:
                if (_symbolTable.TryLookup(ident.Name, out var identSym) && identSym!.Kind == SymbolKind.Function)
                    return new GeneratedExpression("", GetFunctionCName(new List<string>(), ident.Name, null), GetExprType(expr));

                return new GeneratedExpression("", ident.Name, GetExprType(expr));
            case BinaryExpr bin:
                var generatedLeft = GenerateExpressionWithPrelude(bin.Left);
                var generatedRight = GenerateExpressionWithPrelude(bin.Right);
                return new GeneratedExpression(
                    generatedLeft.Prelude + generatedRight.Prelude,
                    $"({generatedLeft.Code} {bin.Operator} {generatedRight.Code})",
                    GetExprType(expr));
            case IndexExpr idx:
                var generatedTargetExpr = GenerateExpressionWithPrelude(idx.Target);
                var generatedIndex = GenerateExpressionWithPrelude(idx.Index);
                return new GeneratedExpression(
                    generatedTargetExpr.Prelude + generatedIndex.Prelude,
                    $"{generatedTargetExpr.Code}[{generatedIndex.Code}]",
                    GetExprType(expr));
            case FieldExpr field:
                string GetFullName(Expr e)
                {
                    if (e is IdentifierExpr id) return id.Name;
                    if (e is FieldExpr f) return GetFullName(f.Target) + "." + f.Field;
                    return "";
                }
                var potentialName = GetFullName(field.Target) + "." + field.Field;
                if (!string.IsNullOrEmpty(potentialName) && _symbolTable.TryLookup(potentialName, out var varInfo) && varInfo!.Kind == SymbolKind.Variable)
                {
                    return new GeneratedExpression("", potentialName.Replace(".", "_"), GetExprType(expr));
                }

                var generatedFieldTarget = GenerateExpressionWithPrelude(field.Target);
                var targetCode = generatedFieldTarget.Code;
                var targetType = GetExprType(field.Target);
                bool isPointerTarget = false;

                // FIX: Error Unions are always structs in C, never pointers, even if their inner type is a pointer.
                if (targetType != null && targetType.IsErrorUnion)
                {
                    isPointerTarget = false;
                }
                else if (targetType != null && targetType.IsPointer && targetType.Name != "string")
                {
                    isPointerTarget = true;
                }
                else if (field.Target is IdentifierExpr idTarget)
                {
                    var varType = GetVarType(idTarget.Name);
                    if (varType != null && varType.IsPointer && varType.Name != "string")
                    {
                        isPointerTarget = true;
                    }
                }
                if (isPointerTarget)
                {
                    return new GeneratedExpression(generatedFieldTarget.Prelude, $"{targetCode}->{field.Field}", GetExprType(expr));
                }
                return new GeneratedExpression(generatedFieldTarget.Prelude, $"{targetCode}.{field.Field}", GetExprType(expr));
            case CastExpr cast:
                var generatedCastExpr = GenerateExpressionWithPrelude(cast.Expr);
                var exprCode = generatedCastExpr.Code;
                var sourceType = GetExprType(cast.Expr);

                if (sourceType != null && sourceType.IsErrorUnion && !cast.TargetType.IsErrorUnion)
                {
                    exprCode = $"({exprCode}).value";
                }
                return new GeneratedExpression(generatedCastExpr.Prelude, $"({MapType(cast.TargetType)}){exprCode}", GetExprType(expr));
            case UnaryExpr un:
                if (un.Operator == "&")
                {
                    var operandType = GetExprType(un.Operand);
                    if (operandType != null && operandType.ArraySize != null)
                    {
                        return GenerateExpressionWithPrelude(un.Operand);
                    }
                }
                var generatedOperand = GenerateExpressionWithPrelude(un.Operand);
                return new GeneratedExpression(generatedOperand.Prelude, $"{un.Operator}{generatedOperand.Code}", GetExprType(expr));
            case BuiltinExpr builtin:
                return new GeneratedExpression("", builtin.Name switch
                {
                    "Builtin.IsLinux" => "__zorb_builtin_is_linux",
                    "Builtin.IsWindows" => "__zorb_builtin_is_windows",
                    "Builtin.IsX86_64" => "__zorb_builtin_is_x86_64",
                    "Builtin.IsAArch64" => "__zorb_builtin_is_aarch64",
                    "true" => "1",
                    "false" => "0",
                    _ => builtin.Value ? "1" : "0"
                }, GetExprType(expr));
            case ErrorNamespaceExpr:
                throw new Exception("Expected '.Name' after 'error' in expression");
            case SizeofExpr sizeofExpr:
                return new GeneratedExpression("", $"((int64_t)sizeof({MapTypeForSizeof(sizeofExpr.TargetType)}))", GetExprType(expr));
            case ErrorExpr err:
                return new GeneratedExpression("", $"({MapType(new TypeNode { Name = "i32" })}){GetErrorSymbolName(err.ErrorCode)}", GetExprType(expr));
            case CatchExpr catchExpr:
                var generatedLeftExpr = GenerateExpressionWithPrelude(catchExpr.Left);
                var leftType = GetExprType(catchExpr.Left);
                if (leftType == null || !leftType.IsErrorUnion)
                    throw new Exception("Catch expressions require an error-union operand");

                var successType = leftType.ErrorInnerType ?? leftType;
                var resultName = "Result_" + GetResultTypeName(successType);
                GenerateResultStruct(successType, resultName);

                var resultTemp = NewTemp("catch_result");
                var errorTemp = NewTemp("catch_err");
                var sb = new StringBuilder();
                sb.Append(generatedLeftExpr.Prelude);
                sb.AppendLine($"struct {resultName} {resultTemp} = {generatedLeftExpr.Code};");
                sb.AppendLine($"int32_t {errorTemp} = {resultTemp}.error;");
                sb.AppendLine($"if ({errorTemp} != 0) {{");

                var savedLocals = CloneLocalVars();
                _localVars[catchExpr.ErrorVar] = new TypeNode { Name = "i32" };
                sb.AppendLine($"    int32_t {catchExpr.ErrorVar} = {errorTemp};");
                foreach (var catchStmt in catchExpr.CatchBody)
                    sb.AppendLine($"    {GenerateStatement(catchStmt)}");
                RestoreLocalVars(savedLocals);

                sb.AppendLine("}");
                return new GeneratedExpression(sb.ToString(), $"{resultTemp}.value", successType.Clone());
            default:
                throw new System.Exception("Unknown expression type");
        }
    }

    private TypeNode? GetExprType(Expr expr)
    {
        switch (expr)
        {
            case NumberExpr _:
                return new TypeNode { Name = "i64" };
            case StringExpr _:
                return new TypeNode { Name = "string" };
            case ErrorNamespaceExpr:
                return null;
            case IdentifierExpr ident:
                return GetVarType(ident.Name);
            case FieldExpr field:
                var qualifiedName = TryGetQualifiedName(expr);
                if (!string.IsNullOrEmpty(qualifiedName) &&
                    _symbolTable.TryLookup(qualifiedName, out var fieldInfo) &&
                    (fieldInfo!.Kind == SymbolKind.Variable || fieldInfo.Kind == SymbolKind.Function))
                {
                    return fieldInfo.Type.Clone();
                }

                var targetType = GetExprType(field.Target);
                if (targetType == null) return null;
                return GetStructFieldType(targetType, field.Field);
            case BinaryExpr bin:
                if (bin.Operator is "==" or "!=" or ">" or "<" or ">=" or "<=")
                    return new TypeNode { Name = "bool" };
                return GetExprType(bin.Left);
            case CallExpr call:
                if (call.TargetExpr != null)
                {
                    var targetName = TryGetQualifiedName(call.TargetExpr);
                    if (!string.IsNullOrEmpty(targetName) &&
                        _symbolTable.TryLookup(targetName, out var targetSym) &&
                        targetSym!.Kind == SymbolKind.Function)
                    {
                        return targetSym.Type.ReturnType?.Clone();
                    }

                    var targetExprType = GetExprType(call.TargetExpr);
                    if (targetExprType != null && targetExprType.IsFunction)
                        return targetExprType.ReturnType?.Clone();
                    return null;
                }

                var fullName = call.NamespacePath.Any()
                    ? string.Join(".", call.NamespacePath) + "." + call.Name
                    : call.Name;

                if (_symbolTable.TryLookup(fullName, out var sym) && sym!.Kind == SymbolKind.Function)
                    return sym.Type.ReturnType?.Clone();
                
                // Fallback: scan AST for function declaration if not in symbol table
                foreach (var node in _allNodes)
                {
                    if (node is FunctionDecl fn && fn.Name == call.Name)
                    {
                        if (call.NamespacePath.Count > 0 && fn.NamespacePath.SequenceEqual(call.NamespacePath))
                            return fn.ReturnType.Clone();
                        else if (call.NamespacePath.Count == 0)
                            return fn.ReturnType.Clone();
                    }
                }
                return null;
            case CastExpr cast:
                return cast.TargetType.Clone();
            case CatchExpr catchExpr:
                var catchLeftType = GetExprType(catchExpr.Left);
                if (catchLeftType == null || !catchLeftType.IsErrorUnion)
                    return null;
                return (catchLeftType.ErrorInnerType ?? catchLeftType).Clone();
            case UnaryExpr un:
                if (un.Operator == "&")
                {
                    var operandType = GetExprType(un.Operand);
                    if (operandType == null)
                        return null;

                    return AddressOfType(operandType);
                }
                if (un.Operator == "!")
                    return new TypeNode { Name = "bool" };
                return GetExprType(un.Operand);
            default:
                return null;
        }
    }

    private string NewTemp(string prefix)
    {
        var name = $"__zorb_{prefix}_{_tempCounter}";
        _tempCounter++;
        return name;
    }

    private static TypeNode AddressOfType(TypeNode operandType)
    {
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

        var result = operandType.Clone();
        result.IsPointer = true;
        result.PointerLevel = operandType.IsPointer
            ? Math.Max(operandType.PointerLevel, 1) + 1
            : 1;
        result.ArraySize = null;
        return result;
    }

    private string MapTypeForSizeof(TypeNode type)
    {
        if (type.IsFunction)
            return MapType(type);

        var mapped = MapType(type);
        if (type.ArraySize != null)
            return $"{mapped}[{type.ArraySize}]";
        return mapped;
    }

    private static string EscapeCString(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\0':
                    sb.Append("\\0");
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        return sb.ToString();
    }

    private static void AppendIndentedBlock(StringBuilder sb, string block, string indent)
    {
        var normalized = block.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        foreach (var line in lines)
        {
            if (line.Length == 0)
                continue;
            sb.Append(indent);
            sb.AppendLine(line);
        }
    }

    private TypeNode? GetVarType(string name)
    {
        // Check local variables and parameters first
        if (_localVars.TryGetValue(name, out var type)) return type.Clone();
        
        // Check symbol table for global variables
        if (_symbolTable.TryLookup(name, out var globalSym) && globalSym!.Kind == SymbolKind.Variable)
            return globalSym.Type.Clone();
        
        // Fallback to checking if it's a global struct name
        foreach (var s in _structs)
        {
            if (s.Name == name) return new TypeNode { Name = s.Name, NamespacePath = s.NamespacePath };
        }
        return null;
    }

    private TypeNode? GetStructFieldType(TypeNode structType, string fieldName)
    {
        foreach (var s in _structs)
        {
            // Match both name and namespace path
            if (s.Name == structType.Name && s.NamespacePath.SequenceEqual(structType.NamespacePath))
            {
                foreach (var f in s.Fields)
                {
                    if (f.Name == fieldName) return f.Type.Clone();
                }
            }
        }
        return null;
    }

    private Dictionary<string, TypeNode> CloneLocalVars()
    {
        return _localVars.ToDictionary(entry => entry.Key, entry => entry.Value.Clone());
    }

    private void RestoreLocalVars(Dictionary<string, TypeNode> saved)
    {
        _localVars.Clear();
        foreach (var entry in saved)
            _localVars[entry.Key] = entry.Value;
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
}
