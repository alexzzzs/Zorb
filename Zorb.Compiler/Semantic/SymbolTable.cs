using System;
using System.Collections.Generic;
using System.Linq;
using Zorb.Compiler.AST.Statements;

namespace Zorb.Compiler.Semantic;

public enum SymbolKind
{
    Variable,
    Function,
    Struct,
    Enum,
    Union,
    ExternType,
    Parameter
}

public class SymbolInfo
{
    public string Name { get; set; } = null!;
    public SymbolKind Kind { get; set; }
    public TypeNode Type { get; set; } = null!;
    public List<Parameter>? Parameters { get; set; }
    public StructNode? StructDefinition { get; set; }
    public EnumNode? EnumDefinition { get; set; }
    public UnionNode? UnionDefinition { get; set; }
    public bool IsConst { get; set; }
}

public class SymbolTable
{
    private readonly Stack<Dictionary<string, SymbolInfo>> _scopes = new();
    private readonly Dictionary<string, SymbolInfo> _globalSymbols = new();

    public SymbolTable()
    {
        _scopes.Push(_globalSymbols);
    }

    public void Clear()
    {
        _scopes.Clear();
        _globalSymbols.Clear();
        _scopes.Push(_globalSymbols);
    }

    public void PushScope()
    {
        _scopes.Push(new Dictionary<string, SymbolInfo>());
    }

    public void PopScope()
    {
        if (_scopes.Count > 1)
            _scopes.Pop();
    }

    public Dictionary<string, SymbolInfo> CurrentScope => _scopes.Peek();

    public void DefineVariable(string name, TypeNode type, bool isConst = false)
    {
        var info = new SymbolInfo
        {
            Name = name,
            Kind = SymbolKind.Variable,
            Type = type,
            IsConst = isConst
        };
        CurrentScope[name] = info;
    }

    public void DefineParameter(string name, TypeNode type)
    {
        var info = new SymbolInfo
        {
            Name = name,
            Kind = SymbolKind.Parameter,
            Type = type
        };
        CurrentScope[name] = info;
    }

    public void DefineFunction(string name, TypeNode returnType, List<Parameter> parameters)
    {
        var info = new SymbolInfo
        {
            Name = name,
            Kind = SymbolKind.Function,
            Type = new TypeNode
            {
                Name = name,
                IsFunction = true,
                ReturnType = returnType.Clone(),
                ParamTypes = parameters.Select(p => p.TypeName.Clone()).ToList()
            },
            Parameters = parameters
        };
        CurrentScope[name] = info;
    }

    public void DefineStruct(string name, StructNode structDefinition)
    {
        var info = new SymbolInfo
        {
            Name = name,
            Kind = SymbolKind.Struct,
            Type = new TypeNode { Name = structDefinition.Name, NamespacePath = new List<string>(structDefinition.NamespacePath) },
            StructDefinition = structDefinition
        };
        CurrentScope[name] = info;
    }

    public void DefineEnum(string name, EnumNode enumDefinition)
    {
        var info = new SymbolInfo
        {
            Name = name,
            Kind = SymbolKind.Enum,
            Type = new TypeNode { Name = enumDefinition.Name, NamespacePath = new List<string>(enumDefinition.NamespacePath) },
            EnumDefinition = enumDefinition
        };
        CurrentScope[name] = info;
    }

    public void DefineUnion(string name, UnionNode unionDefinition)
    {
        var info = new SymbolInfo
        {
            Name = name,
            Kind = SymbolKind.Union,
            Type = new TypeNode { Name = unionDefinition.Name, NamespacePath = new List<string>(unionDefinition.NamespacePath) },
            UnionDefinition = unionDefinition
        };
        CurrentScope[name] = info;
    }

    public void DefineExternType(string name, ExternTypeDecl externType)
    {
        var info = new SymbolInfo
        {
            Name = name,
            Kind = SymbolKind.ExternType,
            Type = new TypeNode { Name = externType.Name, NamespacePath = new List<string>(externType.NamespacePath) }
        };
        CurrentScope[name] = info;
    }

    public SymbolInfo? Lookup(string name)
    {
        foreach (var scope in _scopes)
        {
            if (scope.TryGetValue(name, out var info))
                return info;
        }
        return null;
    }

    public bool TryLookup(string name, out SymbolInfo? info)
    {
        info = Lookup(name);
        return info != null;
    }

    public bool TryLookupCallable(string? name, out SymbolInfo? info)
    {
        info = null;
        return !string.IsNullOrEmpty(name) &&
            TryLookup(name, out info) &&
            info != null &&
            info.IsCallable();
    }

    public bool IsDefined(string name)
    {
        return Lookup(name) != null;
    }

    public List<(string Name, TypeNode Type)>? LookupStruct(string name)
    {
        var info = Lookup(name);
        if (info != null && info.Kind == SymbolKind.Struct && info.StructDefinition != null)
        {
            return info.StructDefinition.Fields
                .Select(field => (field.Name, field.TypeName))
                .ToList();
        }
        return null;
    }

    public bool TryLookupStruct(string name, out List<(string Name, TypeNode Type)>? fields)
    {
        fields = LookupStruct(name);
        return fields != null;
    }

    public StructNode? LookupStructNode(string name)
    {
        var info = Lookup(name);
        if (info != null && info.Kind == SymbolKind.Struct)
            return info.StructDefinition;
        return null;
    }

    public EnumNode? LookupEnumNode(string name)
    {
        var info = Lookup(name);
        if (info != null && info.Kind == SymbolKind.Enum)
            return info.EnumDefinition;
        return null;
    }

    public UnionNode? LookupUnionNode(string name)
    {
        var info = Lookup(name);
        if (info != null && info.Kind == SymbolKind.Union)
            return info.UnionDefinition;
        return null;
    }

    public bool IsExternType(string name)
    {
        var info = Lookup(name);
        return info != null && info.Kind == SymbolKind.ExternType;
    }

    public bool IsLocal(string name)
    {
        if (_scopes.Count <= 1) return false;
        
        foreach (var scope in _scopes.Take(_scopes.Count - 1))
        {
            if (scope.ContainsKey(name)) return true;
        }
        return false;
    }
}
