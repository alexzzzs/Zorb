using Zorb.Compiler.AST.Statements;

namespace Zorb.Compiler.Utils;

public static class TypePredicates
{
    private static readonly HashSet<string> NumericTypes = new()
    {
        "i8", "i16", "i32", "i64", "u8", "u16", "u32", "u64"
    };

    public static bool IsNumericType(TypeNode? type)
    {
        return type != null
            && !type.IsSlice
            && !type.IsPointer
            && !type.IsErrorUnion
            && !type.IsFunction
            && type.ArraySize == null
            && NumericTypes.Contains(type.Name);
    }
}
