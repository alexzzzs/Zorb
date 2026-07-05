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
    private bool TryEvaluateConstIntExpr(Expr expr, out long value, out string? error)
    {
        switch (expr)
        {
            case NumberExpr number:
                return TryEvaluateNumberConstExpr(number, out value, out error);

            case IdentifierExpr identifier:
                return TryEvaluateIdentifierConstExpr(identifier, out value, out error);

            case FieldExpr field:
                return TryEvaluateFieldConstExpr(field, out value, out error);

            case UnaryExpr { Operator: "-", Operand: var operand }:
                return TryEvaluateUnaryNegationConstExpr(operand, out value, out error);

            case BinaryExpr binary when NumericOperators.Contains(binary.Operator):
                return TryEvaluateBinaryConstExpr(binary, out value, out error);

            default:
                value = 0;
                error = null;
                return false;
        }
    }

    private static bool TryEvaluateNumberConstExpr(NumberExpr number, out long value, out string? error)
    {
        value = number.Value;
        error = null;
        return true;
    }

    private bool TryEvaluateIdentifierConstExpr(IdentifierExpr identifier, out long value, out string? error)
    {
        identifier.Name = ResolveQualifiedName(identifier.Name);
        if (TryLookupConstValue(identifier.Name, out value))
        {
            error = null;
            return true;
        }

        value = 0;
        error = null;
        return false;
    }

    private bool TryEvaluateFieldConstExpr(FieldExpr field, out long value, out string? error)
    {
        if (TryResolveStaticEnumMember(field, out _, out value))
        {
            error = null;
            return true;
        }

        if (TryResolveFieldConstQualifiedName(field, out var resolvedQualifiedName) &&
            TryLookupConstValue(resolvedQualifiedName, out value))
        {
            error = null;
            return true;
        }

        value = 0;
        error = null;
        return false;
    }

    private bool TryResolveFieldConstQualifiedName(FieldExpr field, out string resolvedQualifiedName)
    {
        if (TryGetQualifiedName(field) is not string qualifiedName)
        {
            resolvedQualifiedName = "";
            return false;
        }

        resolvedQualifiedName = TryResolveAliasQualifiedName(qualifiedName, out var aliasResolvedQualifiedName)
            ? aliasResolvedQualifiedName
            : qualifiedName;
        return true;
    }

    private bool TryEvaluateUnaryNegationConstExpr(Expr operand, out long value, out string? error)
    {
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
    }

    private bool TryEvaluateBinaryConstExpr(BinaryExpr binary, out long value, out string? error)
    {
        if (!TryEvaluateConstBinaryOperands(binary, out var left, out var right, out error))
        {
            value = 0;
            return false;
        }

        return TryComputeConstBinaryValue(binary.Operator, left, right, out value, out error);
    }

    private bool TryEvaluateConstBinaryOperands(BinaryExpr binary, out long left, out long right, out string? error)
    {
        if (!TryEvaluateConstIntExpr(binary.Left, out left, out error))
        {
            right = 0;
            return false;
        }

        if (!TryEvaluateConstIntExpr(binary.Right, out right, out error))
            return false;

        return true;
    }

    private static bool TryComputeConstBinaryValue(string op, long left, long right, out long value, out string? error)
    {
        try
        {
            value = op switch
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
