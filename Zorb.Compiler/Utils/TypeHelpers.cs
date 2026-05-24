using Zorb.Compiler.AST.Statements;

namespace Zorb.Compiler.Utils;

public static class TypeHelpers
{
    public static TypeNode AddressOfType(TypeNode operandType)
    {
        // `&array` is intentionally element-pointer sugar, not pointer-to-array.
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

    public static bool SameType(TypeNode? left, TypeNode? right)
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

        if (!left.IsFunction)
            return true;

        if (!SameType(left.ReturnType, right.ReturnType) || left.ParamTypes.Count != right.ParamTypes.Count)
            return false;

        for (int i = 0; i < left.ParamTypes.Count; i++)
        {
            if (!SameType(left.ParamTypes[i], right.ParamTypes[i]))
                return false;
        }

        return true;
    }
}
