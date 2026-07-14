using Zorb.Compiler.AST.Statements;

namespace Zorb.Compiler.Semantic;

internal static class SymbolInfoExtensions
{
    public static bool IsCallable(this SymbolInfo symbolInfo)
    {
        return symbolInfo.Kind == SymbolKind.Function ||
               symbolInfo.Type.IsFunction;
    }

    public static TypeNode GetCallableFunctionType(this SymbolInfo symbolInfo)
    {
        return symbolInfo.Type.Clone();
    }

    public static List<Parameter> GetCallableParameters(this SymbolInfo symbolInfo)
    {
        return symbolInfo.Kind == SymbolKind.Function
            ? symbolInfo.Parameters ?? new List<Parameter>()
            : symbolInfo.Type.ParamTypes.Select(type => new Parameter("", type)).ToList();
    }

    public static TypeNode? GetCallableReturnType(this SymbolInfo symbolInfo)
    {
        return symbolInfo.Type.ReturnType?.Clone();
    }
}
