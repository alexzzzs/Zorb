using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using Zorb.Compiler.AST;
using Zorb.Compiler.AST.Expressions;
using Zorb.Compiler.AST.Statements;
using Zorb.Compiler.Layouts;
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
    private readonly Stack<string?> _continueTargets = new();
    private readonly HashSet<string> _generatedResultTypes = new();
    private readonly HashSet<string> _generatedSliceTypes = new();
    private readonly StringBuilder _dynamicStructs = new();
    private List<Node> _allNodes = new();
    private int _tempCounter;
    private bool _insideFunctionBody;
    public bool PreserveStart { get; set; }
    public bool NoStdLib { get; set; }
    public bool? BuiltinIsLinux { get; set; }
    public bool? BuiltinIsWindows { get; set; }
    public bool BuiltinIsBareMetal { get; set; }
    public bool? BuiltinIsX86_64 { get; set; }
    public bool? BuiltinIsAArch64 { get; set; }
    public bool EmitLinuxSyscallWrapper { get; set; } = true;

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
        _generatedSliceTypes.Clear();
        _allNodes = allNodes;
        _tempCounter = 0;
        _insideFunctionBody = false;
        _continueTargets.Clear();
        
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
        if (HasHostedStartShim())
            funcsSb.AppendLine(GenerateHostedMainShim());

        // PASS 2: Assemble the final file
        var sb = new StringBuilder();

        // Standard headers
        sb.AppendLine("#include <stdint.h>");

        AppendBuiltinDefine(sb, "__zorb_builtin_is_linux", BuiltinIsLinux, "defined(__linux__)");
        AppendBuiltinDefine(sb, "__zorb_builtin_is_windows", BuiltinIsWindows, "defined(_WIN32)");
        AppendBuiltinDefine(sb, "__zorb_builtin_is_bare_metal", BuiltinIsBareMetal ? "1" : "0");
        AppendBuiltinDefine(sb, "__zorb_builtin_is_x86_64", BuiltinIsX86_64, "defined(__x86_64__) || defined(_M_X64)");
        AppendBuiltinDefine(sb, "__zorb_builtin_is_aarch64", BuiltinIsAArch64, "defined(__aarch64__) || defined(_M_ARM64)");
        
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

        if (EmitLinuxSyscallWrapper)
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
            var cReturnType = MapType(fn.ReturnType);
            var cName = GetFunctionCName(fn.NamespacePath, fn.Name, null);
            if (emittedPrototypes.Contains(cName)) continue;
            emittedPrototypes.Add(cName);
            var parameters = string.Join(", ", fn.Parameters.Select(p => MapType(p.TypeName, p.Name)));
            prototypesSb.AppendLine($"{cReturnType} {cName}({parameters});");
        }
        if (HasHostedStartShim())
            prototypesSb.AppendLine("int main();");
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
        var needsComputedLayout = StructLayout.HasPackedAttribute(s) || StructLayout.GetAlignment(s.Attributes) is > 0;
        if (!needsComputedLayout)
        {
            sb.AppendLine($"struct {cName} {{");
            foreach (var field in s.Fields)
            {
                var cType = field.TypeName.IsFunction ? MapType(field.TypeName, field.Name) : MapType(field.TypeName);
                if (field.TypeName.ArraySize != null)
                    sb.AppendLine($"    {cType} {field.Name}[{field.TypeName.ArraySize}];");
                else if (field.TypeName.IsFunction)
                    sb.AppendLine($"    {cType};");
                else
                    sb.AppendLine($"    {cType} {field.Name};");
            }
            sb.AppendLine("};");
            sb.AppendLine();
            return sb.ToString();
        }

        var attrs = new List<string>();
        if (StructLayout.HasPackedAttribute(s))
            attrs.Add("packed");
        var alignment = StructLayout.GetAlignment(s.Attributes);
        if (alignment is > 0)
            attrs.Add($"aligned({alignment.Value})");
        var attrStr = attrs.Count > 0 ? $" __attribute__(({string.Join(", ", attrs)}))" : "";

        if (!StructLayout.TryCompute(s, name => _symbolTable.LookupStructNode(name), out var layout, out var error))
            throw new InvalidOperationException(error ?? $"Unable to compute layout for struct '{cName}'.");

        sb.AppendLine($"struct{attrStr} {cName} {{");
        var currentOffset = 0;
        var padIndex = 0;
        foreach (var layoutField in layout!.Fields)
        {
            if (layout.IsExplicit && layoutField.Offset > currentOffset)
            {
                var gap = layoutField.Offset - currentOffset;
                sb.AppendLine($"    uint8_t __zorb_pad_{padIndex++}[{gap}];");
                currentOffset = layoutField.Offset;
            }

            var field = layoutField.Field;
            var cType = field.TypeName.IsFunction ? MapType(field.TypeName, field.Name) : MapType(field.TypeName);
            if (field.TypeName.ArraySize != null)
                sb.AppendLine($"    {cType} {field.Name}[{field.TypeName.ArraySize}];");
            else if (field.TypeName.IsFunction)
                sb.AppendLine($"    {cType};");
            else
                sb.AppendLine($"    {cType} {field.Name};");

            currentOffset = layoutField.Offset + layoutField.Size;
        }
        sb.AppendLine("};");
        if (layout.IsExplicit)
        {
            _includes.Add("stddef.h");
            foreach (var layoutField in layout.Fields)
                sb.AppendLine($"_Static_assert(offsetof(struct {cName}, {layoutField.Field.Name}) == {layoutField.Offset}, \"offset mismatch for {cName}.{layoutField.Field.Name}\");");
        }
        sb.AppendLine();
        return sb.ToString();
    }

    private string GenerateFunction(FunctionDecl fn, string? prefix)
    {
        _localVars.Clear();
        foreach (var p in fn.Parameters) _localVars[p.Name] = p.TypeName;

        if (fn.ReturnType.Name != "void" || fn.ReturnType.IsPointer || fn.ReturnType.IsSlice || fn.ReturnType.ArraySize != null || fn.ReturnType.IsErrorUnion || fn.ReturnType.IsFunction)
        {
            _localVars["__return_type"] = fn.ReturnType;
        }

        var rawName = fn.Name;
        var sb = new StringBuilder();
        var cReturnType = MapType(fn.ReturnType);
        var parameters = string.Join(", ", fn.Parameters.Select(p => MapType(p.TypeName, p.Name)));
        var cName = GetFunctionCName(fn.NamespacePath, rawName, prefix);

        var attrs = new List<string>();
        foreach (var attr in fn.Attributes)
        {
            if (attr == "noinline")
                attrs.Add("noinline");
            else if (attr == "noclone" && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                attrs.Add("noclone");
            else if (attr == "abi(sysv)")
                attrs.Add("sysv_abi");
            else if (attr == "abi(ms)")
                attrs.Add("ms_abi");
            else if (attr.StartsWith("section:", StringComparison.Ordinal))
                attrs.Add($"section(\"{EscapeCString(attr.Substring(8))}\")");
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
        _insideFunctionBody = false;
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string GetVariableAttributeSuffix(List<string> attributes)
    {
        var attrs = new List<string>();
        foreach (var attr in attributes)
        {
            if (attr.StartsWith("align(") && attr.EndsWith(")"))
                attrs.Add($"aligned({attr.Substring(6, attr.Length - 7)})");
            else if (attr.StartsWith("section:", StringComparison.Ordinal))
                attrs.Add($"section(\"{EscapeCString(attr.Substring(8))}\")");
        }

        return attrs.Count > 0 ? $" __attribute__(({string.Join(", ", attrs)}))" : "";
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

        if (type.IsSlice)
        {
            var sliceName = GetSliceStructName(type);
            GenerateSliceStruct(type, sliceName);
            if (string.IsNullOrEmpty(name)) return "struct " + sliceName;
            return $"struct {sliceName} {name}";
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
            "bool" => "int32_t",
            "string" => "char*",
            "void" => "void",
            "char" => "char",
            // If not a primitive, it's a struct - flatten name and prefix with 'struct'
            _ => "struct " + FlattenName(type.NamespacePath, type.Name)
        };

        if (type.IsVolatile)
            baseType = "volatile " + baseType;

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
        if (innerType.IsSlice)
        {
            var elementType = GetSliceElementType(innerType);
            return "slice_" + GetResultTypeName(elementType);
        }

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
            "bool" => "bool",
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

        var cInnerType = innerType.IsSlice
            ? MapType(innerType)
            : innerType.Name switch
            {
                "i8" => "int8_t",
                "i16" => "int16_t",
                "i32" => "int32_t",
                "i64" => "int64_t",
                "u8" => "uint8_t",
                "u16" => "uint16_t",
                "u32" => "uint32_t",
                "u64" => "uint64_t",
                "bool" => "int32_t",
                "string" => "char*",
                "void" => "int8_t",
                "char" => "char",
                _ => "struct " + FlattenName(innerType.NamespacePath, innerType.Name)
            };

        if (!innerType.IsSlice && innerType.IsPointer && innerType.Name != "string")
        {
            var pointerLevel = innerType.PointerLevel > 0 ? innerType.PointerLevel : 1;
            cInnerType += new string('*', pointerLevel);
        }

        _includes.Add("stdint.h");
        var structCode = $"struct {resultName} {{\n    {cInnerType} value;\n    int32_t error;\n}};\n";
        
        _dynamicStructs.AppendLine(structCode);
        
        _structs.Insert(0, new StructNode
        {
            Name = resultName,
            Fields = new List<StructField>
            {
                new() { Name = "value", TypeName = innerType },
                new() { Name = "error", TypeName = new TypeNode { Name = "i32" } }
            }
        });
    }

    private void GenerateSliceStruct(TypeNode sliceType, string sliceName)
    {
        if (_generatedSliceTypes.Contains(sliceName))
            return;
        _generatedSliceTypes.Add(sliceName);

        var elementType = GetSliceElementType(sliceType);
        var pointerType = AddressOfType(elementType);
        var structCode = $"struct {sliceName} {{\n    {MapType(pointerType, "ptr")};\n    int64_t len;\n}};\n";

        _dynamicStructs.AppendLine(structCode);
    }

    private string GetSliceStructName(TypeNode sliceType)
    {
        return "Slice_" + GetResultTypeName(GetSliceElementType(sliceType));
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

    private bool HasHostedStartShim()
    {
        if (PreserveStart)
            return false;

        return _allNodes.OfType<FunctionDecl>().Any(fn => fn.NamespacePath.Count == 0 && fn.Name == "_start");
    }

    private FunctionDecl? GetHostedStartFunction()
    {
        if (!HasHostedStartShim())
            return null;

        return _allNodes.OfType<FunctionDecl>().FirstOrDefault(fn => fn.NamespacePath.Count == 0 && fn.Name == "_start");
    }

    private bool IsHostedStartFunction(List<string> namespacePath, string name)
    {
        return HasHostedStartShim() && namespacePath.Count == 0 && name == "_start";
    }

    private bool ShouldRenameHostedMain(List<string> namespacePath, string name)
    {
        return HasHostedStartShim() && namespacePath.Count == 0 && name == "main";
    }

    private string GenerateHostedMainShim()
    {
        var startFunction = GetHostedStartFunction();
        if (startFunction == null)
            return "";

        var startName = GetFunctionCName(startFunction.NamespacePath, startFunction.Name, null);
        var sb = new StringBuilder();
        sb.AppendLine("int main() {");
        sb.AppendLine($"    {startName}();");
        sb.AppendLine("    return 0;");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string GetFunctionCName(List<string> namespacePath, string name, string? prefix = null)
    {
        if (IsHostedStartFunction(namespacePath, name))
            return "__zorb_user_start";

        if (ShouldRenameHostedMain(namespacePath, name))
            return "__zorb_user_main";

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

            var declarationType = vd.TypeName.Clone();
            if (vd.Attributes.Contains("volatile"))
                declarationType.IsVolatile = true;

            var isFuncPtr = vd.TypeName.IsFunction;
            var attributeSuffix = GetVariableAttributeSuffix(vd.Attributes);
            if (declarationType.ArraySize != null)
            {
                var cTypeArray = isFuncPtr ? MapType(declarationType, safeName) : MapType(declarationType);
                if (vd.Value is ArrayLiteralExpr arrayLiteral)
                {
                    var generatedElements = arrayLiteral.Elements.Select(GenerateExpressionWithPrelude).ToList();
                    if (generatedElements.All(generatedElement => string.IsNullOrEmpty(generatedElement.Prelude)))
                    {
                        var initializer = string.Join(", ", generatedElements.Select(generatedElement => generatedElement.Code));
                        return $"{cTypeArray} {safeName}[{declarationType.ArraySize}]{attributeSuffix} = {{ {initializer} }};";
                    }

                    var sb = new StringBuilder();
                    sb.AppendLine($"{cTypeArray} {safeName}[{declarationType.ArraySize}]{attributeSuffix};");
                    for (int i = 0; i < generatedElements.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(generatedElements[i].Prelude))
                            sb.Append(generatedElements[i].Prelude);
                        sb.AppendLine($"{safeName}[{i}] = {generatedElements[i].Code};");
                    }
                    return sb.ToString().TrimEnd();
                }

                if (vd.Value != null)
                {
                    if (!_insideFunctionBody)
                        return $"{cTypeArray} {safeName}[{declarationType.ArraySize}]{attributeSuffix} = {GenerateExpression(vd.Value)};";

                    var generated = GenerateExpressionWithPrelude(vd.Value);
                    var sb = new StringBuilder();
                    sb.Append(generated.Prelude);
                    sb.AppendLine($"{cTypeArray} {safeName}[{declarationType.ArraySize}]{attributeSuffix};");
                    AppendArrayCopyStatements(sb, safeName, declarationType, generated.Code);
                    return sb.ToString().TrimEnd();
                }
                return $"{cTypeArray} {safeName}[{declarationType.ArraySize}]{attributeSuffix};";
            }
            if (vd.Value is StringExpr)
            {
                var elementType = declarationType.Name == "string"
                    ? new TypeNode { Name = "char", IsVolatile = declarationType.IsVolatile }
                    : new TypeNode { Name = "u8", IsVolatile = declarationType.IsVolatile };
                return $"{MapType(elementType)} {safeName}[]{attributeSuffix} = {GenerateExpression(vd.Value)};";
            }
            var cType = isFuncPtr ? MapType(declarationType, safeName) : MapType(declarationType);
            var constKeyword = vd.IsConst ? "const " : "";
            if (vd.Value != null)
            {
                var generated = CoerceExpressionToTargetType(vd.TypeName, vd.Value, GenerateExpressionWithPrelude(vd.Value));
                var valueCode = generated.Code;
                var declaration = isFuncPtr
                    ? $"{constKeyword}{cType}{attributeSuffix} = {valueCode};"
                    : $"{constKeyword}{cType} {safeName}{attributeSuffix} = {valueCode};";

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
                return $"{constKeyword}{cType}{attributeSuffix};";
            return $"{constKeyword}{cType} {safeName}{attributeSuffix};";
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
            var cond = FormatConditionExpression(generatedCondition.Code);
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
                sb.AppendLine($"while ({FormatConditionExpression(generatedCondition.Code)}) {{");
            }
            else
            {
                sb.AppendLine("while (1) {");
                AppendIndentedBlock(sb, generatedCondition.Prelude, "        ");
                sb.AppendLine($"        if (!({FormatConditionExpression(generatedCondition.Code)})) break;");
            }
            _continueTargets.Push(null);
            try
            {
                AppendIndentedGeneratedBlock(sb, ws.Body, "        ");
            }
            finally
            {
                _continueTargets.Pop();
            }
            sb.Append("    }");
            return sb.ToString();
        }
        if (stmt is ForStmt forStmt)
        {
            var sb = new StringBuilder();
            var savedLocals = CloneLocalVars();
            try
            {
                sb.AppendLine("{");

                if (forStmt.Initializer != null)
                {
                    var generatedInitializer = GenerateStatement(forStmt.Initializer);
                    if (!string.IsNullOrEmpty(generatedInitializer))
                        AppendIndentedBlock(sb, generatedInitializer, "    ");
                }

                var continueLabel = NewTemp("for_continue");
                sb.AppendLine("    while (1) {");

                if (forStmt.Condition != null)
                {
                    var generatedCondition = GenerateExpressionWithPrelude(forStmt.Condition);
                    AppendIndentedBlock(sb, generatedCondition.Prelude, "        ");
                    sb.AppendLine($"        if (!({FormatConditionExpression(generatedCondition.Code)})) break;");
                }

                _continueTargets.Push(continueLabel);
                try
                {
                    AppendIndentedGeneratedBlock(sb, forStmt.Body, "        ");
                }
                finally
                {
                    _continueTargets.Pop();
                }

                sb.AppendLine($"    {continueLabel}: ;");
                if (forStmt.Update != null)
                {
                    var generatedUpdate = GenerateStatement(forStmt.Update);
                    if (!string.IsNullOrEmpty(generatedUpdate))
                        AppendIndentedBlock(sb, generatedUpdate, "        ");
                }

                sb.AppendLine("    }");
                sb.Append("}");
            }
            finally
            {
                RestoreLocalVars(savedLocals);
            }
            return sb.ToString();
        }
        if (stmt is SwitchStmt switchStmt)
        {
            var switchType = GetExprType(switchStmt.Expression)
                ?? throw new Exception("Switch expressions require a known type during code generation.");
            var generatedSwitchExpression = GenerateExpressionWithPrelude(switchStmt.Expression);
            var switchValueTemp = NewTemp("switch_value");
            var switchMatchedTemp = NewTemp("switch_matched");
            var savedLocals = CloneLocalVars();
            var sb = new StringBuilder();

            try
            {
                sb.AppendLine("{");
                AppendIndentedBlock(sb, generatedSwitchExpression.Prelude, "    ");
                sb.AppendLine($"    {MapType(switchType)} {switchValueTemp} = {generatedSwitchExpression.Code};");
                sb.AppendLine($"    int32_t {switchMatchedTemp} = 0;");

                foreach (var switchCase in switchStmt.Cases)
                {
                    var generatedCaseValue = CoerceExpressionToTargetType(switchType, switchCase.Value, GenerateExpressionWithPrelude(switchCase.Value));
                    sb.AppendLine($"    if (!{switchMatchedTemp}) {{");
                    AppendIndentedBlock(sb, generatedCaseValue.Prelude, "        ");
                    sb.AppendLine($"        if ({switchValueTemp} == {generatedCaseValue.Code}) {{");
                    sb.AppendLine($"            {switchMatchedTemp} = 1;");
                    AppendIndentedGeneratedBlock(sb, switchCase.Body, "            ");
                    sb.AppendLine("        }");
                    sb.AppendLine("    }");
                }

                if (switchStmt.ElseBody.Count > 0)
                {
                    sb.AppendLine($"    if (!{switchMatchedTemp}) {{");
                    AppendIndentedGeneratedBlock(sb, switchStmt.ElseBody, "        ");
                    sb.AppendLine("    }");
                }

                sb.Append("}");
            }
            finally
            {
                RestoreLocalVars(savedLocals);
            }

            return sb.ToString();
        }
        if (stmt is ContinueStmt)
        {
            if (_continueTargets.Count > 0 && _continueTargets.Peek() is string continueTarget)
                return $"goto {continueTarget};";
            return "continue;";
        }
        if (stmt is BreakStmt)
            return "break;";
        if (stmt is AssignStmt assign)
        {
            var generatedTarget = GenerateExpressionWithPrelude(assign.Target);
            var targetType = GetExprType(assign.Target);
            var valueExpr = assign.Value;
            var generatedValue = CoerceExpressionToTargetType(targetType, valueExpr, GenerateExpressionWithPrelude(valueExpr));
            var valueCode = generatedValue.Code;

            if (targetType?.ArraySize is not null)
            {
                var sb = new StringBuilder();
                sb.Append(generatedTarget.Prelude);
                sb.Append(generatedValue.Prelude);
                AppendArrayCopyStatements(sb, generatedTarget.Code, targetType, valueCode);
                return sb.ToString().TrimEnd();
            }

            var assignment = $"{generatedTarget.Code} = {valueCode};";
            if (string.IsNullOrEmpty(generatedTarget.Prelude) && string.IsNullOrEmpty(generatedValue.Prelude))
                return assignment;

            return generatedTarget.Prelude + generatedValue.Prelude + assignment;
        }
        if (stmt is ReturnNode ret)
        {
            if (ret.Value == null)
                return "return;";

            var fnReturnType = _localVars.TryGetValue("__return_type", out var rt) ? rt : null;
            var generated = CoerceExpressionToTargetType(fnReturnType, (Expr)ret.Value, GenerateExpressionWithPrelude((Expr)ret.Value));
            var valueCode = generated.Code;
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

    private void AppendArrayCopyStatements(StringBuilder sb, string targetCode, TypeNode arrayType, string sourceCode)
    {
        if (arrayType.ArraySize is not int arrayLength)
            throw new Exception("Array copy lowering requires a fixed-size array type.");

        var elementType = arrayType.Clone();
        elementType.ArraySize = null;

        var elementPointerType = AddressOfType(elementType);
        var restrictedElementPointerType = $"{MapType(elementPointerType)} restrict";
        var targetTemp = NewTemp("array_copy_target");
        var sourceTemp = NewTemp("array_copy_source");
        var indexTemp = NewTemp("array_copy_index");

        sb.AppendLine($"{restrictedElementPointerType} {targetTemp} = {targetCode};");
        sb.AppendLine($"{restrictedElementPointerType} {sourceTemp} = {sourceCode};");
        sb.AppendLine($"for (int {indexTemp} = 0; {indexTemp} < {arrayLength}; {indexTemp}++) {{");
        sb.AppendLine($"    {targetTemp}[{indexTemp}] = {sourceTemp}[{indexTemp}];");
        sb.Append("}");
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
                    condition = "__zorb_builtin_is_linux";
                    return true;
                case "Builtin.IsWindows":
                    condition = "__zorb_builtin_is_windows";
                    return true;
                case "Builtin.IsBareMetal":
                    condition = "__zorb_builtin_is_bare_metal";
                    return true;
                case "Builtin.IsX86_64":
                    condition = "__zorb_builtin_is_x86_64";
                    return true;
                case "Builtin.IsAArch64":
                    condition = "__zorb_builtin_is_aarch64";
                    return true;
            }
        }

        if (expr is BinaryExpr binary && (binary.Operator == "&&" || binary.Operator == "||"))
        {
            if (TryGetPlatformPreprocessorCondition(binary.Left, out var leftCondition)
                && TryGetPlatformPreprocessorCondition(binary.Right, out var rightCondition))
            {
                condition = binary.Operator == "&&"
                    ? $"({leftCondition}) && ({rightCondition})"
                    : $"({leftCondition}) || ({rightCondition})";
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

    private List<TypeNode> GetCallParameterTypes(CallExpr call)
    {
        if (call.TargetExpr != null)
        {
            if (!string.IsNullOrEmpty(call.ResolvedTargetQualifiedName) &&
                _symbolTable.TryLookup(call.ResolvedTargetQualifiedName, out var resolvedInfo))
            {
                return resolvedInfo!.Kind == SymbolKind.Function
                    ? resolvedInfo.Parameters!.Select(parameter => parameter.TypeName.Clone()).ToList()
                    : resolvedInfo.Type.ParamTypes.Select(type => type.Clone()).ToList();
            }

            var qualifiedName = TryGetQualifiedName(call.TargetExpr);
            if (!string.IsNullOrEmpty(qualifiedName) &&
                _symbolTable.TryLookup(qualifiedName, out var qualifiedInfo))
            {
                return qualifiedInfo!.Kind == SymbolKind.Function
                    ? qualifiedInfo.Parameters!.Select(parameter => parameter.TypeName.Clone()).ToList()
                    : qualifiedInfo.Type.ParamTypes.Select(type => type.Clone()).ToList();
            }

            var targetType = GetExprType(call.TargetExpr);
            if (targetType != null && targetType.IsFunction)
                return targetType.ParamTypes.Select(type => type.Clone()).ToList();

            return new List<TypeNode>();
        }

        var fullName = call.NamespacePath.Any()
            ? string.Join(".", call.NamespacePath) + "." + call.Name
            : call.Name;

        if (_symbolTable.TryLookup(fullName, out var info))
        {
            return info!.Kind == SymbolKind.Function
                ? info.Parameters!.Select(parameter => parameter.TypeName.Clone()).ToList()
                : info.Type.ParamTypes.Select(type => type.Clone()).ToList();
        }

        if (_symbolTable.TryLookup(call.Name, out var bareInfo))
        {
            return bareInfo!.Kind == SymbolKind.Function
                ? bareInfo.Parameters!.Select(parameter => parameter.TypeName.Clone()).ToList()
                : bareInfo.Type.ParamTypes.Select(type => type.Clone()).ToList();
        }

        return new List<TypeNode>();
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
                var parameterTypes = GetCallParameterTypes(call);
                for (int i = 0; i < call.Args.Count; i++)
                {
                    var arg = call.Args[i];
                    var parameterType = i < parameterTypes.Count ? parameterTypes[i] : null;
                    var generatedArg = CoerceExpressionToTargetType(parameterType, arg, GenerateExpressionWithPrelude(arg));
                    callPrelude.Append(generatedArg.Prelude);
                    generatedArgs.Add(generatedArg.Code);
                }
                var args = string.Join(", ", generatedArgs);

                if (call.TargetExpr != null)
                {
                    if (!string.IsNullOrEmpty(call.ResolvedTargetQualifiedName))
                    {
                        var resolvedParts = call.ResolvedTargetQualifiedName.Split('.');
                        var resolvedName = resolvedParts[^1];
                        var resolvedNamespacePath = resolvedParts.Length > 1
                            ? resolvedParts[..^1].ToList()
                            : new List<string>();
                        var resolvedTarget = GetFunctionCName(resolvedNamespacePath, resolvedName, null);
                        return new GeneratedExpression(callPrelude.ToString(), $"{resolvedTarget}({args})", GetExprType(expr));
                    }

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
                if (bin.Operator is "&&" or "||")
                {
                    var generatedLogicalLeft = GenerateExpressionWithPrelude(bin.Left);
                    var generatedLogicalRight = GenerateExpressionWithPrelude(bin.Right);

                    if (string.IsNullOrEmpty(generatedLogicalLeft.Prelude) && string.IsNullOrEmpty(generatedLogicalRight.Prelude))
                    {
                        return new GeneratedExpression(
                            "",
                            $"({generatedLogicalLeft.Code} {bin.Operator} {generatedLogicalRight.Code})",
                            new TypeNode { Name = "bool" });
                    }

                    var logicalResult = NewTemp("logical");
                    var logicalPrelude = new StringBuilder();
                    logicalPrelude.Append(generatedLogicalLeft.Prelude);
                    logicalPrelude.AppendLine($"int32_t {logicalResult} = !!({generatedLogicalLeft.Code});");

                    if (bin.Operator == "&&")
                    {
                        logicalPrelude.AppendLine($"if ({logicalResult}) {{");
                        AppendIndentedBlock(logicalPrelude, generatedLogicalRight.Prelude, "    ");
                        logicalPrelude.AppendLine($"    {logicalResult} = !!({generatedLogicalRight.Code});");
                        logicalPrelude.AppendLine("}");
                    }
                    else
                    {
                        logicalPrelude.AppendLine($"if (!({logicalResult})) {{");
                        AppendIndentedBlock(logicalPrelude, generatedLogicalRight.Prelude, "    ");
                        logicalPrelude.AppendLine($"    {logicalResult} = !!({generatedLogicalRight.Code});");
                        logicalPrelude.AppendLine("}");
                    }

                    return new GeneratedExpression(logicalPrelude.ToString(), logicalResult, new TypeNode { Name = "bool" });
                }

                var generatedLeft = GenerateExpressionWithPrelude(bin.Left);
                var generatedRight = GenerateExpressionWithPrelude(bin.Right);
                return new GeneratedExpression(
                    generatedLeft.Prelude + generatedRight.Prelude,
                    $"({generatedLeft.Code} {bin.Operator} {generatedRight.Code})",
                    GetExprType(expr));
            case StructLiteralExpr structLiteral:
                var generatedFields = structLiteral.Fields
                    .Select(field =>
                    {
                        var fieldType = GetStructFieldType(structLiteral.TypeName, field.Name);
                        return (Field: field, FieldType: fieldType, Generated: CoerceExpressionToTargetType(fieldType, field.Value, GenerateExpressionWithPrelude(field.Value)));
                    })
                    .ToList();

                if (generatedFields.All(field => CanInlineStructLiteralField(field.Field, field.FieldType, field.Generated)))
                {
                    var fieldInitializers = string.Join(", ", generatedFields.Select(field => GenerateInlineStructLiteralFieldInitializer(field.Field, field.FieldType, field.Generated)));
                    return new GeneratedExpression(
                        "",
                        $"({MapType(structLiteral.TypeName)}){{ {fieldInitializers} }}",
                        GetExprType(expr));
                }

                var structTemp = NewTemp("struct_literal");
                var structPrelude = new StringBuilder();
                structPrelude.AppendLine($"{MapType(structLiteral.TypeName)} {structTemp};");
                foreach (var field in generatedFields)
                {
                    structPrelude.Append(field.Generated.Prelude);
                    if (field.FieldType?.ArraySize is int fieldLength)
                    {
                        for (int i = 0; i < fieldLength; i++)
                            structPrelude.AppendLine($"{structTemp}.{field.Field.Name}[{i}] = {field.Generated.Code}[{i}];");
                        continue;
                    }

                    structPrelude.AppendLine($"{structTemp}.{field.Field.Name} = {field.Generated.Code};");
                }
                return new GeneratedExpression(structPrelude.ToString(), structTemp, GetExprType(expr));
            case ArrayLiteralExpr arrayLiteral:
                var generatedElements = arrayLiteral.Elements.Select(GenerateExpressionWithPrelude).ToList();
                var arrayType = MapTypeForSizeof(arrayLiteral.TypeName);
                if (generatedElements.All(generatedElement => string.IsNullOrEmpty(generatedElement.Prelude)))
                {
                    var elementCodes = string.Join(", ", generatedElements.Select(generatedElement => generatedElement.Code));
                    return new GeneratedExpression(
                        "",
                        $"({arrayType}){{ {elementCodes} }}",
                        GetExprType(expr));
                }

                var arrayElementType = arrayLiteral.TypeName.Clone();
                arrayElementType.ArraySize = null;
                var arrayTemp = NewTemp("array_literal");
                var arrayPrelude = new StringBuilder();
                arrayPrelude.AppendLine($"{MapType(arrayElementType)} {arrayTemp}[{arrayLiteral.TypeName.ArraySize}];");
                for (int i = 0; i < generatedElements.Count; i++)
                {
                    arrayPrelude.Append(generatedElements[i].Prelude);
                    arrayPrelude.AppendLine($"{arrayTemp}[{i}] = {generatedElements[i].Code};");
                }
                return new GeneratedExpression(arrayPrelude.ToString(), arrayTemp, GetExprType(expr));
            case IndexExpr idx:
                var generatedTargetExpr = GenerateExpressionWithPrelude(idx.Target);
                var generatedIndex = GenerateExpressionWithPrelude(idx.Index);
                var indexedTargetType = GetExprType(idx.Target);
                var targetCode = indexedTargetType != null && indexedTargetType.IsSlice
                    ? $"{generatedTargetExpr.Code}.ptr"
                    : generatedTargetExpr.Code;
                return new GeneratedExpression(
                    generatedTargetExpr.Prelude + generatedIndex.Prelude,
                    $"{targetCode}[{generatedIndex.Code}]",
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
                var fieldTargetCode = generatedFieldTarget.Code;
                var targetType = GetExprType(field.Target);
                if (targetType != null && targetType.IsSlice)
                    return new GeneratedExpression(generatedFieldTarget.Prelude, $"{fieldTargetCode}.{field.Field}", GetExprType(expr));
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
                    return new GeneratedExpression(generatedFieldTarget.Prelude, $"{fieldTargetCode}->{field.Field}", GetExprType(expr));
                }
                return new GeneratedExpression(generatedFieldTarget.Prelude, $"{fieldTargetCode}.{field.Field}", GetExprType(expr));
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
                    "Builtin.IsBareMetal" => "__zorb_builtin_is_bare_metal",
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
            case IndexExpr idx:
                var indexedTargetType = GetExprType(idx.Target);
                if (indexedTargetType == null)
                    return null;
                if (indexedTargetType.IsSlice)
                    return GetSliceElementType(indexedTargetType);
                if (indexedTargetType.ArraySize != null)
                {
                    var elementType = indexedTargetType.Clone();
                    elementType.ArraySize = null;
                    return elementType;
                }
                if (indexedTargetType.IsPointer)
                {
                    var level = indexedTargetType.PointerLevel > 0 ? indexedTargetType.PointerLevel : 1;
                    if (level > 1)
                    {
                        var pointerElementType = indexedTargetType.Clone();
                        pointerElementType.PointerLevel = level - 1;
                        return pointerElementType;
                    }

                    var pointeeType = indexedTargetType.Clone();
                    pointeeType.IsPointer = false;
                    pointeeType.PointerLevel = 0;
                    return pointeeType;
                }
                return null;
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
                if (targetType.IsSlice)
                {
                    if (field.Field == "len")
                        return new TypeNode { Name = "i64" };
                    if (field.Field == "ptr")
                        return AddressOfType(GetSliceElementType(targetType));
                    return null;
                }
                return GetStructFieldType(targetType, field.Field);
            case BinaryExpr bin:
                if (bin.Operator is "==" or "!=" or ">" or "<" or ">=" or "<=" or "&&" or "||")
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
            case StructLiteralExpr structLiteral:
                return structLiteral.TypeName.Clone();
            case ArrayLiteralExpr arrayLiteral:
                return arrayLiteral.TypeName.Clone();
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
                IsVolatile = operandType.IsVolatile,
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

    private static TypeNode GetSliceElementType(TypeNode sliceType)
    {
        var elementType = sliceType.Clone();
        elementType.IsSlice = false;
        elementType.ArraySize = null;
        return elementType;
    }

    private static bool CanCoerceArrayToSlice(TypeNode targetType, TypeNode sourceType)
    {
        if (!targetType.IsSlice || sourceType.IsSlice || sourceType.ArraySize == null)
            return false;

        if (sourceType.IsErrorUnion || sourceType.IsFunction)
            return false;

        var targetElement = GetSliceElementType(targetType);
        var sourceElement = sourceType.Clone();
        sourceElement.ArraySize = null;

        return SameType(targetElement, sourceElement);
    }

    private GeneratedExpression CoerceExpressionToTargetType(TypeNode? targetType, Expr sourceExpr, GeneratedExpression generated)
    {
        var sourceType = generated.Type ?? GetExprType(sourceExpr);
        var code = generated.Code;

        if (sourceType != null && sourceType.IsErrorUnion && (targetType == null || !targetType.IsErrorUnion))
        {
            var innerType = sourceType.ErrorInnerType ?? sourceType;
            var resultName = "Result_" + GetResultTypeName(innerType);
            GenerateResultStruct(innerType, resultName);
            code = $"({code}).value";
            sourceType = innerType.Clone();
        }

        if (targetType != null && sourceType != null && CanCoerceArrayToSlice(targetType, sourceType))
        {
            if (sourceType.ArraySize is not int arrayLength)
                throw new Exception("Array-to-slice coercion requires a fixed-size array source.");

            var sliceName = GetSliceStructName(targetType);
            GenerateSliceStruct(targetType, sliceName);
            code = $"(struct {sliceName}){{ .ptr = {generated.Code}, .len = {arrayLength} }}";
            return new GeneratedExpression(generated.Prelude, code, targetType.Clone());
        }

        return new GeneratedExpression(generated.Prelude, code, sourceType);
    }

    private static bool SameType(TypeNode? left, TypeNode? right)
    {
        if (left == null || right == null)
            return left == right;

        if (left.Name != right.Name ||
            left.IsVolatile != right.IsVolatile ||
            left.IsSlice != right.IsSlice ||
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

    private static string FormatConditionExpression(string code)
    {
        if (code.Length >= 2 && code[0] == '(' && code[^1] == ')')
            return code[1..^1];
        return code;
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
        if (structType.IsSlice)
        {
            if (fieldName == "len")
                return new TypeNode { Name = "i64" };
            if (fieldName == "ptr")
                return AddressOfType(GetSliceElementType(structType));
            return null;
        }

        var fullName = structType.NamespacePath.Any()
            ? string.Join(".", structType.NamespacePath) + "." + structType.Name
            : structType.Name;

        if (_symbolTable.TryLookupStruct(fullName, out var fields))
        {
            foreach (var field in fields!)
            {
                if (field.Name == fieldName)
                    return field.Type.Clone();
            }
        }

        foreach (var s in _structs)
        {
            // Match both name and namespace path
            if (s.Name == structType.Name && s.NamespacePath.SequenceEqual(structType.NamespacePath))
            {
                foreach (var f in s.Fields)
                {
                    if (f.Name == fieldName) return f.TypeName.Clone();
                }
            }
        }
        return null;
    }

    private static bool CanInlineStructLiteralField(StructLiteralField field, TypeNode? fieldType, GeneratedExpression generated)
    {
        if (!string.IsNullOrEmpty(generated.Prelude))
            return false;

        if (fieldType?.ArraySize == null)
            return true;

        return field.Value is ArrayLiteralExpr;
    }

    private string GenerateInlineStructLiteralFieldInitializer(StructLiteralField field, TypeNode? fieldType, GeneratedExpression generated)
    {
        if (fieldType?.ArraySize == null)
            return $".{field.Name} = {generated.Code}";

        var arrayLiteral = (ArrayLiteralExpr)field.Value;
        var generatedElements = arrayLiteral.Elements.Select(GenerateExpressionWithPrelude).ToList();
        var elementCodes = string.Join(", ", generatedElements.Select(element => element.Code));
        return $".{field.Name} = {{ {elementCodes} }}";
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

    private static void AppendBuiltinDefine(StringBuilder sb, string macroName, string value)
    {
        sb.AppendLine($"#define {macroName} {value}");
    }

    private static void AppendBuiltinDefine(StringBuilder sb, string macroName, bool? fixedValue, string condition)
    {
        if (fixedValue.HasValue)
        {
            AppendBuiltinDefine(sb, macroName, fixedValue.Value ? "1" : "0");
            return;
        }

        sb.AppendLine($"#if {condition}");
        sb.AppendLine($"    #define {macroName} 1");
        sb.AppendLine("#else");
        sb.AppendLine($"    #define {macroName} 0");
        sb.AppendLine("#endif");
    }
}
