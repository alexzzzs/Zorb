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
    private readonly HashSet<string> _catchErrorVars = new();
    private readonly Stack<string?> _continueTargets = new();
    private readonly HashSet<string> _generatedResultTypes = new();
    private readonly HashSet<string> _generatedSliceTypes = new();
    private readonly StringBuilder _dynamicResultStructs = new();
    private readonly StringBuilder _dynamicSliceStructs = new();
    private List<Node> _allNodes = new();
    private IReadOnlyDictionary<string, IReadOnlyList<Node>>? _parsedFilesByPath;
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

    private static bool IsNumericType(TypeNode? type) => TypePredicates.IsNumericType(type);

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

    private const string RuntimeFailureHelpers = @"
#if __zorb_builtin_is_windows
extern void ExitProcess(uint32_t);
#endif

static void __zorb_slice_oob(void) {
#if __zorb_builtin_is_windows
    ExitProcess(1);
#elif __zorb_builtin_is_bare_metal
    while (1) {
#if defined(__x86_64__) || defined(_M_X64)
        __asm__ volatile (""cli; hlt"" ::: ""memory"");
#else
        __asm__ volatile ("""" ::: ""memory"");
#endif
    }
#elif defined(__x86_64__)
    syscall(60, 1);
#elif defined(__aarch64__)
    syscall(93, 1);
#else
    __builtin_trap();
#endif
}

";

    public CGenerator() : this(".", new SymbolTable()) {}

    public CGenerator(string currentDir) : this(currentDir, new SymbolTable()) {}

    public CGenerator(string currentDir, SymbolTable symbolTable)
    {
        _currentDir = currentDir;
        _symbolTable = symbolTable;
    }

    public string Generate(List<Node> nodes, IReadOnlyDictionary<string, IReadOnlyList<Node>>? parsedFilesByPath = null)
    {
        var allNodes = new List<Node>();
        var processed = new HashSet<string>();
        var emittedItems = new HashSet<string>();

        ResetGenerationState(allNodes, parsedFilesByPath);
        _allNodes = allNodes;

        CollectNodes(nodes, allNodes, processed, emittedItems, _currentDir);

        // PASS 1: Generate variables and functions first. 
        // This ensures MapType is called and all dynamic structs (like Result_u8_ptr) are generated.
        var varsSb = new StringBuilder();
        var emittedVars = new HashSet<string>();
        var variables = allNodes.OfType<VariableDeclarationNode>().ToList();
        foreach (var v in variables)
        {
            if (emittedVars.Contains(v.Name)) continue;
            emittedVars.Add(v.Name);
            
            var stmt = GenerateStatement(v);
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
        sb.AppendLine(RuntimeFailureHelpers);

        PreGenerateSliceTypes(allNodes);

        // Slice structs may appear as fields inside user structs, so they must be emitted first.
        sb.Append(_dynamicSliceStructs.ToString());

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

        var emittedEnums = new HashSet<string>();
        var enums = allNodes.OfType<EnumNode>().ToList();
        foreach (var enumNode in enums)
        {
            var cName = FlattenName(enumNode.NamespacePath, enumNode.Name, null);
            if (emittedEnums.Contains(cName))
                continue;
            emittedEnums.Add(cName);
            sb.Append(GenerateEnum(enumNode));
        }

        var emittedUnions = new HashSet<string>();
        var unions = allNodes.OfType<UnionNode>().ToList();
        foreach (var unionNode in unions)
        {
            var cName = FlattenName(unionNode.NamespacePath, unionNode.Name, null);
            if (emittedUnions.Contains(cName))
                continue;
            emittedUnions.Add(cName);
            sb.Append(GenerateUnion(unionNode));
        }

        // Emit result structs after user structs because result values can contain user struct values.
        sb.Append(_dynamicResultStructs.ToString());

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

        // Emit freestanding entry shim before user functions so linker entry is stable.
        sb.Append(GenerateFreestandingMainShim());

        // Emit functions
        sb.Append(funcsSb.ToString());

        _parsedFilesByPath = null;
        return sb.ToString();
    }

    private void ResetGenerationState(List<Node> allNodes, IReadOnlyDictionary<string, IReadOnlyList<Node>>? parsedFilesByPath)
    {
        _structs.Clear();
        _includes.Clear();
        _includes.Add("stdint.h");
        _cHeaders.Clear();
        _localVars.Clear();
        _dynamicResultStructs.Clear();
        _dynamicSliceStructs.Clear();
        _generatedResultTypes.Clear();
        _generatedSliceTypes.Clear();
        _allNodes = allNodes;
        _parsedFilesByPath = parsedFilesByPath;
        _tempCounter = 0;
        _insideFunctionBody = false;
        _continueTargets.Clear();
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
                if (_parsedFilesByPath == null)
                    throw new ZorbCompilerException("Parsed import graph is not available.");

                if (!_parsedFilesByPath.TryGetValue(fullPath, out var subNodes))
                    throw new ZorbCompilerException($"Parsed import graph is missing '{fullPath}'.");
                
                CollectNodes(subNodes.ToList(), result, processed, emittedItems, dir);
            }
            else
            {
                result.Add(node);
            }
        }
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
        if (PreserveStart && fn.NamespacePath.Count == 0 && rawName == "_start" && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            attrs.Add("force_align_arg_pointer");
        var attrStr = attrs.Count > 0 ? $"__attribute__(({string.Join(", ", attrs)})) " : "";

        if (fn.IsExtern)
        {
            if (fn.Attributes.Contains("c_header"))
                return "";
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

    private string GenerateEnum(EnumNode enumNode)
    {
        var sb = new StringBuilder();
        var enumName = FlattenName(enumNode.NamespacePath, enumNode.Name, null);
        sb.AppendLine($"typedef {MapType(enumNode.UnderlyingType)} {enumName};");
        foreach (var member in enumNode.Members)
        {
            var memberName = $"{enumName}_{member.Name}";
            var memberValue = member.ResolvedValue ?? 0;
            sb.AppendLine($"static const {enumName} {memberName} = ({enumName}){memberValue};");
        }
        sb.AppendLine();
        return sb.ToString();
    }

    private string GenerateUnion(UnionNode unionNode)
    {
        var sb = new StringBuilder();
        var unionName = FlattenName(unionNode.NamespacePath, unionNode.Name, null);
        var tagName = GetUnionTagCName(unionNode);
        sb.AppendLine($"typedef int32_t {tagName};");
        for (int i = 0; i < unionNode.Variants.Count; i++)
            sb.AppendLine($"static const {tagName} {tagName}_{unionNode.Variants[i].Name} = ({tagName}){i};");
        sb.AppendLine($"struct {unionName} {{");
        sb.AppendLine($"    {tagName} tag;");
        sb.AppendLine("    union {");
        foreach (var variant in unionNode.Variants)
        {
            var cType = variant.TypeName.IsFunction ? MapType(variant.TypeName, variant.Name) : MapType(variant.TypeName);
            if (variant.TypeName.ArraySize != null)
                sb.AppendLine($"        {cType} {variant.Name}[{variant.TypeName.ArraySize}];");
            else if (variant.TypeName.IsFunction)
                sb.AppendLine($"        {cType};");
            else
                sb.AppendLine($"        {cType} {variant.Name};");
        }
        sb.AppendLine("    } data;");
        sb.AppendLine("};");
        sb.AppendLine();
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
        var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
        var enumDefinition = _symbolTable.LookupEnumNode(fullName);
        if (enumDefinition != null)
        {
            baseType = MapType(enumDefinition.UnderlyingType);
        }
        else if (_symbolTable.IsExternType(fullName))
        {
            baseType = FlattenName(type.NamespacePath, type.Name);
        }
        else
        {
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
        }

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
                _ => _symbolTable.IsExternType(QualifiedNames.GetFullName(innerType.NamespacePath, innerType.Name))
                    ? FlattenName(innerType.NamespacePath, innerType.Name)
                    : "struct " + FlattenName(innerType.NamespacePath, innerType.Name)
            };

        if (!innerType.IsSlice && innerType.IsPointer && innerType.Name != "string")
        {
            var pointerLevel = innerType.PointerLevel > 0 ? innerType.PointerLevel : 1;
            cInnerType += new string('*', pointerLevel);
        }

        _includes.Add("stdint.h");
        var structCode = $"struct {resultName} {{\n    {cInnerType} value;\n    int32_t error;\n}};\n";
        
        _dynamicResultStructs.AppendLine(structCode);
        
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
        var pointerType = TypeHelpers.AddressOfType(elementType);
        var structCode = $"struct {sliceName} {{\n    {MapType(pointerType, "ptr")};\n    int64_t len;\n}};\n";

        _dynamicSliceStructs.AppendLine(structCode);
    }

    private void PreGenerateSliceTypes(List<Node> nodes)
    {
        foreach (var structNode in nodes.OfType<StructNode>())
        {
            foreach (var field in structNode.Fields)
                PreGenerateSliceType(field.TypeName);
        }

        foreach (var unionNode in nodes.OfType<UnionNode>())
        {
            foreach (var variant in unionNode.Variants)
                PreGenerateSliceType(variant.TypeName);
        }

        foreach (var function in nodes.OfType<FunctionDecl>())
        {
            PreGenerateSliceType(function.ReturnType);
            foreach (var parameter in function.Parameters)
                PreGenerateSliceType(parameter.TypeName);
        }

        foreach (var variable in nodes.OfType<VariableDeclarationNode>())
            PreGenerateSliceType(variable.TypeName);
    }

    private void PreGenerateSliceType(TypeNode type)
    {
        if (type.IsSlice)
            GenerateSliceStruct(type, GetSliceStructName(type));

        if (type.IsErrorUnion && type.ErrorInnerType != null)
            PreGenerateSliceType(type.ErrorInnerType);

        if (type.IsFunction)
        {
            if (type.ReturnType != null)
                PreGenerateSliceType(type.ReturnType);
            foreach (var parameterType in type.ParamTypes)
                PreGenerateSliceType(parameterType);
        }
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

    private bool HasFreestandingMainShim()
    {
        if (!PreserveStart || !NoStdLib)
            return false;

        var hasUserStart = _allNodes.OfType<FunctionDecl>().Any(fn => fn.NamespacePath.Count == 0 && fn.Name == "_start");
        if (hasUserStart)
            return false;

        return _allNodes.OfType<FunctionDecl>().Any(fn => fn.NamespacePath.Count == 0 && fn.Name == "main");
    }

    private FunctionDecl? GetFreestandingMainFunction()
    {
        if (!HasFreestandingMainShim())
            return null;

        return _allNodes.OfType<FunctionDecl>().FirstOrDefault(fn => fn.NamespacePath.Count == 0 && fn.Name == "main");
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

    private string GenerateFreestandingMainShim()
    {
        var mainFunction = GetFreestandingMainFunction();
        if (mainFunction == null)
            return "";

        var mainName = GetFunctionCName(mainFunction.NamespacePath, mainFunction.Name, null);
        var returnsVoid = mainFunction.ReturnType.Name == "void" &&
            mainFunction.ReturnType.NamespacePath.Count == 0 &&
            !mainFunction.ReturnType.IsPointer &&
            mainFunction.ReturnType.ArraySize == null &&
            !mainFunction.ReturnType.IsSlice &&
            !mainFunction.ReturnType.IsErrorUnion;

        var sb = new StringBuilder();
        sb.AppendLine("__attribute__((force_align_arg_pointer, noreturn)) void _start(void) {");
        if (returnsVoid)
        {
            sb.AppendLine($"    {mainName}();");
            sb.AppendLine("    int64_t code = 0;");
        }
        else
        {
            sb.AppendLine($"    int64_t code = (int64_t){mainName}();");
        }
        sb.AppendLine("#if defined(__x86_64__)");
        sb.AppendLine("    syscall(60, code);");
        sb.AppendLine("#elif defined(__aarch64__)");
        sb.AppendLine("    syscall(93, code);");
        sb.AppendLine("#else");
        sb.AppendLine("    __builtin_trap();");
        sb.AppendLine("#endif");
        sb.AppendLine("    while (1) { }");
        sb.AppendLine("}");
        sb.AppendLine();
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
            if (es.Expression is BuiltinExpr { Name: "Builtin.CompileError" } compileErrorExpr)
            {
                return $"#error \"{EscapeCString(compileErrorExpr.Message ?? string.Empty)}\"";
            }

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
        if (stmt is MatchStmt matchStmt)
        {
            return GenerateMatchStatement(matchStmt);
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
                } else if (ret.Value is IdentifierExpr identifier && _catchErrorVars.Contains(identifier.Name)) {
                    var returnCode = $"return (struct {resultName}){{ .value = 0, .error = {valueCode} }};";
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

    private string GenerateMatchStatement(MatchStmt matchStmt)
    {
        var matchType = GetExprType(matchStmt.Expression)
            ?? throw new Exception("Match expressions require a known type during code generation.");
        var generatedMatchExpression = GenerateExpressionWithPrelude(matchStmt.Expression);
        var matchValueTemp = NewTemp("match_value");
        var matchMatchedTemp = NewTemp("match_matched");
        var savedLocals = CloneLocalVars();
        var sb = new StringBuilder();

        try
        {
            sb.AppendLine("{");
            AppendIndentedBlock(sb, generatedMatchExpression.Prelude, "    ");
            sb.AppendLine($"    {MapType(matchType)} {matchValueTemp} = {generatedMatchExpression.Code};");
            sb.AppendLine($"    int32_t {matchMatchedTemp} = 0;");
            _localVars[matchValueTemp] = matchType;

            if (IsUnionType(matchType))
                GenerateUnionMatchCases(sb, matchStmt, matchType, matchValueTemp, matchMatchedTemp);
            else
                GenerateEnumMatchCases(sb, matchStmt, matchType, matchValueTemp, matchMatchedTemp);

            if (matchStmt.ElseBody.Count > 0)
            {
                sb.AppendLine($"    if (!{matchMatchedTemp}) {{");
                AppendIndentedGeneratedBlock(sb, matchStmt.ElseBody, "        ");
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

    private void GenerateEnumMatchCases(StringBuilder sb, MatchStmt matchStmt, TypeNode matchType, string matchValueTemp, string matchMatchedTemp)
    {
        foreach (var matchCase in matchStmt.Cases)
        {
            Expr caseValue;
            if (matchCase.Pattern is QualifiedMatchPattern qualifiedPattern)
            {
                caseValue = qualifiedPattern.Value;
            }
            else
            {
                throw new InvalidOperationException($"Unexpected match pattern '{matchCase.Pattern.GetType().Name}' in enum match over '{FormatType(matchType)}'.");
            }

            var generatedCaseValue = CoerceExpressionToTargetType(matchType, caseValue, GenerateExpressionWithPrelude(caseValue));
            sb.AppendLine($"    if (!{matchMatchedTemp}) {{");
            AppendIndentedBlock(sb, generatedCaseValue.Prelude, "        ");
            sb.AppendLine($"        if ({matchValueTemp} == {generatedCaseValue.Code}) {{");
            sb.AppendLine($"            {matchMatchedTemp} = 1;");
            AppendIndentedGeneratedBlock(sb, matchCase.Body, "            ");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
        }
    }

    private void GenerateUnionMatchCases(StringBuilder sb, MatchStmt matchStmt, TypeNode matchType, string matchValueTemp, string matchMatchedTemp)
    {
        var unionDefinition = GetUnionDefinition(matchType)
            ?? throw new Exception($"Unknown union type '{FormatType(matchType)}'.");

        foreach (var matchCase in matchStmt.Cases)
        {
            string variantName;
            string? bindingName = null;
            TypeNode? bindingType = null;
            if (matchCase.Pattern is QualifiedMatchPattern qualifiedPattern)
            {
                var qualifiedName = QualifiedNames.TryGetQualifiedName(qualifiedPattern.Value)
                    ?? throw new InvalidOperationException($"Unexpected match pattern '{matchCase.Pattern.GetType().Name}' in union match over '{FormatType(matchType)}'.");
                var lastDot = qualifiedName.LastIndexOf('.');
                if (lastDot < 0)
                    throw new InvalidOperationException($"Unexpected match pattern '{matchCase.Pattern.GetType().Name}' in union match over '{FormatType(matchType)}'.");
                variantName = qualifiedName[(lastDot + 1)..];
            }
            else if (matchCase.Pattern is UnionMatchPattern unionPattern && !string.IsNullOrEmpty(unionPattern.VariantName))
            {
                variantName = unionPattern.VariantName;
                bindingName = unionPattern.BindingName;
                bindingType = unionPattern.BindingType?.Clone();
            }
            else
            {
                throw new InvalidOperationException($"Unexpected match pattern '{matchCase.Pattern.GetType().Name}' in union match over '{FormatType(matchType)}'.");
            }

            var tagCode = GetUnionTagMemberCode(unionDefinition, variantName);
            sb.AppendLine($"    if (!{matchMatchedTemp}) {{");
            sb.AppendLine($"        if ({matchValueTemp}.tag == {tagCode}) {{");
            sb.AppendLine($"            {matchMatchedTemp} = 1;");

            if (!string.IsNullOrEmpty(bindingName))
            {
                var bindingValue = new FieldExpr
                {
                    Target = new IdentifierExpr { Name = matchValueTemp },
                    Field = variantName
                };
                var bindingDecl = new VariableDeclarationNode
                {
                    Name = bindingName!,
                    TypeName = bindingType ?? GetUnionVariantType(matchType, variantName) ?? throw new Exception($"Unknown union variant '{variantName}'."),
                    Value = bindingValue
                };
                AppendIndentedBlock(sb, GenerateStatement(bindingDecl), "            ");
            }

            AppendIndentedGeneratedBlock(sb, matchCase.Body, "            ");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
        }
    }

    private void AppendArrayCopyStatements(StringBuilder sb, string targetCode, TypeNode arrayType, string sourceCode)
    {
        if (arrayType.ArraySize is not int arrayLength)
            throw new Exception("Array copy lowering requires a fixed-size array type.");

        var elementType = arrayType.Clone();
        elementType.ArraySize = null;

        var elementPointerType = TypeHelpers.AddressOfType(elementType);
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

        if (expr is UnaryExpr unary && unary.Operator == "!")
        {
            if (TryGetPlatformPreprocessorCondition(unary.Operand, out var operandCondition))
            {
                condition = $"!({operandCondition})";
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
        return GetCallFunctionType(call)?.ParamTypes.Select(type => type.Clone()).ToList()
            ?? new List<TypeNode>();
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
                var resolvedFunctionType = GetCallFunctionType(call);
                var parameterTypes = resolvedFunctionType?.ParamTypes.Select(type => type.Clone()).ToList()
                    ?? new List<TypeNode>();
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
                    if (TryGetDirectFunctionSymbol(call.ResolvedTargetQualifiedName, out _) &&
                        !string.IsNullOrEmpty(call.ResolvedTargetQualifiedName))
                    {
                        var (resolvedNamespacePath, resolvedName) = QualifiedNames.SplitQualifiedName(call.ResolvedTargetQualifiedName);
                        var resolvedTarget = GetFunctionCName(resolvedNamespacePath, resolvedName, null);
                        return new GeneratedExpression(callPrelude.ToString(), $"{resolvedTarget}({args})", GetExprType(expr));
                    }

                    var generatedTarget = GenerateExpressionWithPrelude(call.TargetExpr);
                    callPrelude.Append(generatedTarget.Prelude);
                    var target = generatedTarget.Code.Replace(".", "_");
                    return new GeneratedExpression(callPrelude.ToString(), $"{target}({args})", GetExprType(expr));
                }

                var cCallName = call.Name;
                if (TryGetDirectFunctionSymbol(call.ResolvedQualifiedName, out var resolvedFunctionSymbol))
                {
                    var resolvedFunctionName = resolvedFunctionSymbol!.Name;
                    var (resolvedNamespacePath, resolvedName) = QualifiedNames.SplitQualifiedName(resolvedFunctionName);
                    cCallName = GetFunctionCName(resolvedNamespacePath, resolvedName, null);
                }
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
                if (IsUnionType(structLiteral.TypeName))
                {
                    var unionDefinition = GetUnionDefinition(structLiteral.TypeName)
                        ?? throw new Exception($"Unknown union type '{FormatType(structLiteral.TypeName)}'.");
                    if (structLiteral.Fields.Count != 1)
                        throw new Exception($"Union literal '{FormatType(structLiteral.TypeName)}' must initialize exactly one variant.");

                    var variantField = structLiteral.Fields[0];
                    var variantType = GetUnionVariantType(structLiteral.TypeName, variantField.Name)
                        ?? throw new Exception($"Unknown union variant '{variantField.Name}' in '{FormatType(structLiteral.TypeName)}'.");
                    var generatedVariant = CoerceExpressionToTargetType(variantType, variantField.Value, GenerateExpressionWithPrelude(variantField.Value));
                    var tagCode = GetUnionTagMemberCode(unionDefinition, variantField.Name);

                    if (string.IsNullOrEmpty(generatedVariant.Prelude) && (variantType.ArraySize == null || variantField.Value is ArrayLiteralExpr))
                    {
                        return new GeneratedExpression(
                            "",
                            $"({MapType(structLiteral.TypeName)}){{ .tag = {tagCode}, .data = {{ .{variantField.Name} = {generatedVariant.Code} }} }}",
                            GetExprType(expr));
                    }

                    var unionTemp = NewTemp("union_literal");
                    var unionPrelude = new StringBuilder();
                    unionPrelude.AppendLine($"{MapType(structLiteral.TypeName)} {unionTemp};");
                    unionPrelude.AppendLine($"{unionTemp}.tag = {tagCode};");
                    unionPrelude.Append(generatedVariant.Prelude);
                    if (variantType.ArraySize is int variantArrayLength)
                    {
                        for (int i = 0; i < variantArrayLength; i++)
                            unionPrelude.AppendLine($"{unionTemp}.data.{variantField.Name}[{i}] = {generatedVariant.Code}[{i}];");
                    }
                    else
                    {
                        unionPrelude.AppendLine($"{unionTemp}.data.{variantField.Name} = {generatedVariant.Code};");
                    }

                    return new GeneratedExpression(unionPrelude.ToString(), unionTemp, GetExprType(expr));
                }

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
                if (indexedTargetType != null && indexedTargetType.IsSlice)
                {
                    var sliceTemp = NewTemp("slice");
                    var indexTemp = NewTemp("index");
                    var sliceType = indexedTargetType.Clone();
                    var indexPrelude = new StringBuilder();
                    indexPrelude.Append(generatedTargetExpr.Prelude);
                    indexPrelude.Append(generatedIndex.Prelude);
                    indexPrelude.AppendLine($"{MapType(sliceType)} {sliceTemp} = {generatedTargetExpr.Code};");
                    indexPrelude.AppendLine($"int64_t {indexTemp} = (int64_t)({generatedIndex.Code});");
                    indexPrelude.AppendLine($"if ({indexTemp} < 0 || {indexTemp} >= {sliceTemp}.len) __zorb_slice_oob();");
                    return new GeneratedExpression(
                        indexPrelude.ToString(),
                        $"{sliceTemp}.ptr[{indexTemp}]",
                        GetExprType(expr));
                }

                var targetCode = indexedTargetType != null && indexedTargetType.IsSlice
                    ? $"{generatedTargetExpr.Code}.ptr"
                    : generatedTargetExpr.Code;
                return new GeneratedExpression(
                    generatedTargetExpr.Prelude + generatedIndex.Prelude,
                    $"{targetCode}[{generatedIndex.Code}]",
                    GetExprType(expr));
            case FieldExpr field:
                var qualifiedName = field.ResolvedQualifiedName;
                if (!string.IsNullOrEmpty(qualifiedName) &&
                    _symbolTable.TryLookup(qualifiedName, out var varInfo))
                {
                    if (varInfo!.Kind == SymbolKind.Variable)
                        return new GeneratedExpression("", qualifiedName.Replace(".", "_"), GetExprType(expr));

                    if (varInfo.Kind == SymbolKind.Function)
                    {
                        var (resolvedNamespacePath, resolvedName) = QualifiedNames.SplitQualifiedName(qualifiedName);
                        return new GeneratedExpression("", GetFunctionCName(resolvedNamespacePath, resolvedName, null), GetExprType(expr));
                    }
                }

                var generatedFieldTarget = GenerateExpressionWithPrelude(field.Target);
                var fieldTargetCode = generatedFieldTarget.Code;
                var targetType = GetExprType(field.Target);
                if (targetType != null && targetType.IsSlice)
                    return new GeneratedExpression(generatedFieldTarget.Prelude, $"{fieldTargetCode}.{field.Field}", GetExprType(expr));
                var unionFieldTargetType = targetType?.IsPointer == true ? GetPointeeType(targetType) : targetType;
                if (unionFieldTargetType != null && IsUnionType(unionFieldTargetType))
                {
                    var unionAccess = field.Field == "tag"
                        ? (targetType?.IsPointer == true ? $"{fieldTargetCode}->tag" : $"{fieldTargetCode}.tag")
                        : (targetType?.IsPointer == true ? $"{fieldTargetCode}->data.{field.Field}" : $"{fieldTargetCode}.data.{field.Field}");
                    return new GeneratedExpression(generatedFieldTarget.Prelude, unionAccess, GetExprType(expr));
                }
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
                var savedCatchErrorVars = new HashSet<string>(_catchErrorVars);
                _localVars[catchExpr.ErrorVar] = new TypeNode { Name = "i32" };
                _catchErrorVars.Add(catchExpr.ErrorVar);
                sb.AppendLine($"    int32_t {catchExpr.ErrorVar} = {errorTemp};");
                foreach (var catchStmt in catchExpr.CatchBody)
                    sb.AppendLine($"    {GenerateStatement(catchStmt)}");
                RestoreLocalVars(savedLocals);
                RestoreCatchErrorVars(savedCatchErrorVars);

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
                var qualifiedName = field.ResolvedQualifiedName;
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
                        return TypeHelpers.AddressOfType(GetSliceElementType(targetType));
                    return null;
                }
                return GetStructFieldType(targetType, field.Field);
            case BinaryExpr bin:
                if (bin.Operator is "==" or "!=" or ">" or "<" or ">=" or "<=" or "&&" or "||")
                    return new TypeNode { Name = "bool" };

                var leftType = GetExprType(bin.Left);
                var rightType = GetExprType(bin.Right);
                if (leftType != null && rightType != null)
                {
                    if (bin.Operator == "+" && IsNumericType(leftType) && rightType.IsPointer)
                        return rightType.Clone();

                    if ((bin.Operator == "+" || bin.Operator == "-") && leftType.IsPointer && IsNumericType(rightType))
                        return leftType.Clone();
                }

                return leftType;
            case CallExpr call:
                return GetCallFunctionType(call)?.ReturnType?.Clone();
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

                    return TypeHelpers.AddressOfType(operandType);
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

        return TypeHelpers.SameType(targetElement, sourceElement);
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
                return TypeHelpers.AddressOfType(GetSliceElementType(structType));
            return null;
        }

        var fullName = QualifiedNames.GetFullName(structType.NamespacePath, structType.Name);

        if (_symbolTable.LookupUnionNode(fullName) is UnionNode unionDefinition)
        {
            if (fieldName == "tag")
                return GetUnionTagType(unionDefinition);

            var unionVariant = unionDefinition.Variants.FirstOrDefault(variant => variant.Name == fieldName);
            if (unionVariant != null)
                return unionVariant.TypeName.Clone();
        }

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

    private bool IsUnionType(TypeNode type)
    {
        var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
        return _symbolTable.LookupUnionNode(fullName) != null;
    }

    private UnionNode? GetUnionDefinition(TypeNode type)
    {
        var fullName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);
        return _symbolTable.LookupUnionNode(fullName);
    }

    private static TypeNode? GetPointeeType(TypeNode? type)
    {
        if (type == null || !type.IsPointer)
            return type;

        var pointee = type.Clone();
        if (pointee.PointerLevel > 1)
            pointee.PointerLevel--;
        else
        {
            pointee.IsPointer = false;
            pointee.PointerLevel = 0;
        }
        return pointee;
    }

    private TypeNode? GetUnionVariantType(TypeNode unionType, string variantName)
    {
        var unionDefinition = GetUnionDefinition(unionType);
        return unionDefinition?.Variants.FirstOrDefault(variant => variant.Name == variantName)?.TypeName.Clone();
    }

    private static TypeNode GetUnionTagType(UnionNode unionNode)
    {
        return new TypeNode
        {
            Name = "Tag",
            NamespacePath = unionNode.NamespacePath.Concat(new[] { unionNode.Name }).ToList()
        };
    }

    private string GetUnionTagCName(UnionNode unionNode)
    {
        return FlattenName(unionNode.NamespacePath.Concat(new[] { unionNode.Name }).ToList(), "Tag", null);
    }

    private string GetUnionTagMemberCode(UnionNode unionNode, string variantName)
    {
        var tagName = GetUnionTagCName(unionNode);
        return $"{tagName}_{variantName}";
    }

    private static string FormatType(TypeNode type)
    {
        var baseName = QualifiedNames.GetFullName(type.NamespacePath, type.Name);

        if (type.IsPointer)
            baseName = new string('*', Math.Max(type.PointerLevel, 1)) + baseName;
        if (type.ArraySize != null)
            baseName = $"[{type.ArraySize}]{baseName}";
        if (type.IsSlice)
            baseName = $"[]{baseName}";

        return baseName;
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

    private void RestoreCatchErrorVars(HashSet<string> saved)
    {
        _catchErrorVars.Clear();
        foreach (var name in saved)
            _catchErrorVars.Add(name);
    }

    private TypeNode? GetCallFunctionType(CallExpr call)
    {
        if (call.ResolvedFunctionType != null)
            return call.ResolvedFunctionType.Clone();

        if (call.TargetExpr != null)
            return GetExprType(call.TargetExpr) is TypeNode targetType && targetType.IsFunction
                ? targetType.Clone()
                : null;

        if (_symbolTable.TryLookupCallable(call.ResolvedQualifiedName, out var resolvedSymbol))
            return resolvedSymbol!.GetCallableFunctionType();

        var fullName = QualifiedNames.GetFullName(call.NamespacePath, call.Name);
        if (_symbolTable.TryLookupCallable(fullName, out var qualifiedSymbol))
            return qualifiedSymbol!.GetCallableFunctionType();

        if (_symbolTable.TryLookupCallable(call.Name, out var bareSymbol))
            return bareSymbol!.GetCallableFunctionType();

        return null;
    }

    private bool TryGetDirectFunctionSymbol(string? name, out SymbolInfo? symbolInfo)
    {
        symbolInfo = null;
        return _symbolTable.TryLookupCallable(name, out symbolInfo) &&
            symbolInfo!.Kind == SymbolKind.Function;
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
