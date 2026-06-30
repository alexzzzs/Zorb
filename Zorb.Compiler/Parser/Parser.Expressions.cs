using System;
using System.Collections.Generic;
using Zorb.Compiler.AST;
using Zorb.Compiler.AST.Expressions;
using Zorb.Compiler.AST.Statements;
using Zorb.Compiler.Lexer;

namespace Zorb.Compiler.Parser;

public partial class Parser
{
    private Expr ParseExpression(int parentPrecedence = 0)
    {
        var left = ParsePostfix();

        while (true)
        {
            if (Current.Type == TokenType.Catch)
            {
                var catchToken = Advance();
                Expect(TokenType.Pipe, "Expected '|' after 'catch'.");
                var errorVar = Expect(TokenType.Identifier, "Expected catch error variable name.").Value;
                Expect(TokenType.Pipe, "Expected closing '|' after catch error variable.");
                Expect(TokenType.LBrace, "Expected '{' to start catch body.");
                var catchBody = new List<Statement>();
                while (Current.Type != TokenType.RBrace && Current.Type != TokenType.Eof)
                    catchBody.Add(ParseStatement());
                Expect(TokenType.RBrace, "Expected '}' to close catch body.");
                var catchExpr = new CatchExpr { Left = left, ErrorVar = errorVar, CatchBody = catchBody };
                StampNode(catchExpr, catchToken);
                left = catchExpr;
                continue;
            }

            var prec = GetPrecedence(Current.Type);
            if (prec == 0 || prec <= parentPrecedence)
                break;

            var opToken = Current;
            Advance();
            var right = ParseExpression(prec);
            var binary = new BinaryExpr { Left = left, Operator = TokenToOperator(opToken.Type), Right = right };
            StampNode(binary, opToken);
            left = binary;
        }

        return left;
    }

    private Expr ParsePostfix()
    {
        var expr = ParsePrimary();

        while (true)
        {
            if (Current.Type == TokenType.LBracket)
            {
                if (Peek(1).Type == TokenType.Align)
                    break;
                var bracketToken = Advance();
                var index = ParseExpression();
                Expect(TokenType.RBracket, "Expected ']' after index expression.");
                var indexExpr = new IndexExpr { Target = expr, Index = index };
                StampNode(indexExpr, bracketToken);
                expr = indexExpr;
            }
            else if (Current.Type == TokenType.Dot)
            {
                Advance();
                var fieldToken = Expect(TokenType.Identifier, "Expected identifier after '.'.");
                var field = fieldToken.Value;
                if (expr is ErrorNamespaceExpr)
                {
                    var errorExpr = new ErrorExpr { ErrorCode = field };
                    StampNode(errorExpr, fieldToken);
                    expr = errorExpr;
                }
                else
                {
                    var fieldExpr = new FieldExpr { Target = expr, Field = field };
                    StampNode(fieldExpr, fieldToken);
                    expr = fieldExpr;
                }
            }
            else if (Current.Type == TokenType.LParen)
            {
                var calleeLocation = expr;
                expr = ParseCallExpr(expr, calleeLocation);
            }
            else if (Current.Type == TokenType.Less && IsGenericCallStart())
            {
                var calleeLocation = expr;
                var typeArguments = ParseTypeArgumentList();
                expr = ParseCallExpr(expr, calleeLocation, typeArguments);
            }
            else if (Current.Type == TokenType.Less && IsGenericFunctionValueStart(expr))
            {
                ApplyGenericFunctionValueTypeArguments(expr, ParseTypeArgumentList());
            }
            else
            {
                break;
            }
        }

        return expr;
    }

    private CallExpr ParseCallExpr(Expr callee, Node calleeLocation, List<TypeNode>? typeArguments = null)
    {
        Expect(
            TokenType.LParen,
            typeArguments == null
                ? "Expected '(' to start function call."
                : "Expected '(' after generic function type arguments.");

        var args = new List<Expr>();
        if (Current.Type != TokenType.RParen)
        {
            do
            {
                args.Add(ParseExpression());
                if (Current.Type == TokenType.Comma)
                    Advance();
                else
                    break;
            } while (true);
        }
        Expect(TokenType.RParen, "Missing closing ')' in function call");

        var lastToken = Previous;
        var callExpr = callee is IdentifierExpr id
            ? new CallExpr
            {
                NamespacePath = new List<string>(),
                Name = id.Name,
                TypeArguments = typeArguments ?? new List<TypeNode>(),
                Args = args
            }
            : new CallExpr
            {
                Name = "",
                TypeArguments = typeArguments ?? new List<TypeNode>(),
                Args = args,
                TargetExpr = callee
            };

        StampNode(callExpr, calleeLocation);
        var totalSpan = calleeLocation.Line == lastToken.Line
            ? (lastToken.Column + lastToken.Length) - calleeLocation.Column
            : Math.Max(callExpr.Length, lastToken.Length);
        callExpr.Length = Math.Max(callExpr.Length, totalSpan);
        return callExpr;
    }

    private bool IsGenericCallStart()
    {
        var offset = 0;
        if (!TrySkipTypeArgumentList(ref offset))
            return false;
        return Peek(offset).Type == TokenType.LParen;
    }

    private bool IsGenericFunctionValueStart(Expr expr)
    {
        if (expr is not IdentifierExpr and not FieldExpr)
            return false;

        var offset = 0;
        if (!TrySkipTypeArgumentList(ref offset))
            return false;

        return Peek(offset).Type is TokenType.Comma
            or TokenType.RParen
            or TokenType.RBracket
            or TokenType.RBrace
            or TokenType.Eof;
    }

    private static void ApplyGenericFunctionValueTypeArguments(Expr expr, List<TypeNode> typeArguments)
    {
        switch (expr)
        {
            case IdentifierExpr identifier:
                identifier.TypeArguments = typeArguments;
                break;

            case FieldExpr field:
                field.TypeArguments = typeArguments;
                break;
        }
    }

    private bool IsStructLiteralStart()
    {
        if (Current.Type != TokenType.Identifier)
            return false;

        var offset = 1;
        while (Peek(offset).Type == TokenType.Dot && Peek(offset + 1).Type == TokenType.Identifier)
            offset += 2;
        if (!TrySkipTypeArgumentList(ref offset))
            return false;

        if (Peek(offset).Type != TokenType.LBrace)
            return false;

        offset++;
        if (Peek(offset).Type == TokenType.RBrace)
            return true;

        while (true)
        {
            if (Peek(offset).Type != TokenType.Identifier)
                return false;

            offset++;
            if (Peek(offset).Type != TokenType.Colon)
                return false;

            offset++;
            if (!TrySkipStructLiteralFieldValue(ref offset))
                return false;

            if (Peek(offset).Type == TokenType.Comma)
            {
                offset++;
                if (Peek(offset).Type == TokenType.RBrace)
                    return true;
                continue;
            }

            return Peek(offset).Type == TokenType.RBrace;
        }
    }

    private bool TrySkipTypeArgumentList(ref int offset)
    {
        if (Peek(offset).Type != TokenType.Less)
            return true;

        var depth = 0;
        while (true)
        {
            var tokenType = Peek(offset).Type;
            if (tokenType == TokenType.Eof)
                return false;

            if (tokenType == TokenType.Less)
                depth++;
            else if (tokenType == TokenType.Greater)
            {
                depth--;
                if (depth == 0)
                {
                    offset++;
                    return true;
                }
            }
            else if (tokenType == TokenType.RShift)
            {
                depth -= 2;
                if (depth <= 0)
                {
                    offset++;
                    return depth == 0;
                }
            }

            offset++;
        }
    }

    private bool TrySkipStructLiteralFieldValue(ref int offset)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var sawValueToken = false;

        while (true)
        {
            var tokenType = Peek(offset).Type;
            if (tokenType == TokenType.Eof)
                return false;

            if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 &&
                (tokenType == TokenType.Comma || tokenType == TokenType.RBrace))
            {
                return sawValueToken;
            }

            sawValueToken = true;
            switch (tokenType)
            {
                case TokenType.LParen:
                    parenDepth++;
                    break;
                case TokenType.RParen:
                    if (parenDepth == 0)
                        return false;
                    parenDepth--;
                    break;
                case TokenType.LBracket:
                    bracketDepth++;
                    break;
                case TokenType.RBracket:
                    if (bracketDepth == 0)
                        return false;
                    bracketDepth--;
                    break;
                case TokenType.LBrace:
                    braceDepth++;
                    break;
                case TokenType.RBrace:
                    if (braceDepth == 0)
                        return false;
                    braceDepth--;
                    break;
                case TokenType.Semicolon:
                case TokenType.Equals:
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        return false;
                    break;
            }

            offset++;
        }
    }

    private Expr ParseStructLiteral()
    {
        var startToken = Current;
        var path = new List<string>
        {
            Expect(TokenType.Identifier, "Expected struct type name.").Value
        };

        while (Match(TokenType.Dot))
            path.Add(Expect(TokenType.Identifier, "Expected identifier after '.' in struct literal type.").Value);

        var name = path[^1];
        path.RemoveAt(path.Count - 1);
        var typeArguments = ParseTypeArgumentList();

        Expect(TokenType.LBrace, "Expected '{' to start struct literal.");
        var fields = new List<StructLiteralField>();
        while (Current.Type != TokenType.RBrace && Current.Type != TokenType.Eof)
        {
            var fieldNameToken = Expect(TokenType.Identifier, "Expected struct field name in literal.");
            Expect(TokenType.Colon, $"Expected ':' after struct literal field '{fieldNameToken.Value}'.");
            var field = new StructLiteralField
            {
                Name = fieldNameToken.Value,
                Value = ParseExpression()
            };
            StampNode(field, fieldNameToken);
            fields.Add(field);

            if (Current.Type == TokenType.Comma)
            {
                Advance();
                if (Current.Type == TokenType.RBrace)
                    break;
            }
            else
            {
                break;
            }
        }

        Expect(TokenType.RBrace, "Expected '}' to close struct literal.");
        var expr = new StructLiteralExpr
        {
            TypeName = new TypeNode
            {
                Name = name,
                NamespacePath = path,
                TypeArguments = typeArguments
            },
            Fields = fields
        };
        StampNode(expr, startToken);
        return expr;
    }

    private Expr ParseArrayLiteral()
    {
        var startToken = Current;
        var typeName = ParseType();
        if (typeName.ArraySize == null && typeName.ArraySizeExpr == null)
            ErrorReporter.Error("Array literals must use an array type like '[4]u8'.", startToken.Line, startToken.Column, _fileName);

        Expect(TokenType.LBrace, "Expected '{' to start array literal.");
        var elements = new List<Expr>();
        while (Current.Type != TokenType.RBrace && Current.Type != TokenType.Eof)
        {
            elements.Add(ParseExpression());
            if (Current.Type == TokenType.Comma)
            {
                Advance();
                if (Current.Type == TokenType.RBrace)
                    break;
            }
            else
            {
                break;
            }
        }

        Expect(TokenType.RBrace, "Expected '}' to close array literal.");
        var expr = new ArrayLiteralExpr
        {
            TypeName = typeName,
            Elements = elements
        };
        StampNode(expr, startToken);
        return expr;
    }

    private Expr ParsePrimary()
    {
        if (Current.Type == TokenType.Error)
        {
            var startToken = Advance();
            var errorNamespace = new ErrorNamespaceExpr();
            StampNode(errorNamespace, startToken);
            return errorNamespace;
        }

        if (Current.Type == TokenType.Builtin)
        {
            var startToken = Advance();
            Expect(TokenType.Dot, "Expected '.' after 'Builtin'.");
            var builtinName = Expect(TokenType.Identifier, "Expected builtin name after 'Builtin.'.").Value;

            if (builtinName == "IsLinux")
            {
                var expr = new BuiltinExpr { Name = "Builtin.IsLinux" };
                StampNode(expr, startToken);
                return expr;
            }
            if (builtinName == "IsWindows")
            {
                var expr = new BuiltinExpr { Name = "Builtin.IsWindows" };
                StampNode(expr, startToken);
                return expr;
            }
            if (builtinName == "IsBareMetal")
            {
                var expr = new BuiltinExpr { Name = "Builtin.IsBareMetal" };
                StampNode(expr, startToken);
                return expr;
            }
            if (builtinName == "IsX86_64")
            {
                var expr = new BuiltinExpr { Name = "Builtin.IsX86_64" };
                StampNode(expr, startToken);
                return expr;
            }
            if (builtinName == "IsAArch64")
            {
                var expr = new BuiltinExpr { Name = "Builtin.IsAArch64" };
                StampNode(expr, startToken);
                return expr;
            }
            if (builtinName == "CompileError")
            {
                Expect(TokenType.LParen, "Expected '(' after 'Builtin.CompileError'.");
                var messageExpr = ParseExpression();
                Expect(TokenType.RParen, "Expected ')' to close Builtin.CompileError argument list.");

                if (messageExpr is not StringExpr message)
                {
                    ErrorReporter.Error("Builtin.CompileError expects a string literal message.", startToken.Line, startToken.Column, _fileName);
                    return CreateInvalidExpr(startToken);
                }

                var expr = new BuiltinExpr { Name = "Builtin.CompileError", Message = message.Value };
                StampNode(expr, startToken);
                return expr;
            }
            if (string.Equals(builtinName, "sizeof", StringComparison.Ordinal))
            {
                Expect(TokenType.LParen, "Expected '(' after 'Builtin.sizeof'.");
                var targetType = ParseType();
                Expect(TokenType.RParen, "Expected ')' to close Builtin.sizeof argument list.");
                var expr = new SizeofExpr { TargetType = targetType };
                StampNode(expr, startToken);
                return expr;
            }

            ErrorReporter.Error($"Unknown builtin: {builtinName}", startToken.Line, startToken.Column, _fileName);
            return CreateInvalidExpr(startToken);
        }

        if (Current.Type == TokenType.Cast)
        {
            var startToken = Advance();
            Expect(TokenType.LParen, "Expected '(' after 'cast'.");
            var targetType = ParseType();
            Expect(TokenType.Comma, "Expected ',' between cast target type and expression.");
            var expr = ParseExpression();
            Expect(TokenType.RParen, "Expected ')' to close cast expression.");
            var castExpr = new CastExpr { TargetType = targetType, Expr = expr };
            StampNode(castExpr, startToken);
            return castExpr;
        }

        if (Current.Type == TokenType.Amp || Current.Type == TokenType.Minus || Current.Type == TokenType.Bang)
        {
            var startToken = Current;
            var op = Current.Type switch
            {
                TokenType.Amp => "&",
                TokenType.Minus => "-",
                TokenType.Bang => "!",
                _ => throw new InvalidOperationException("Unexpected unary operator token.")
            };
            Advance();
            var operand = ParsePostfix();
            var unaryExpr = new UnaryExpr { Operator = op, Operand = operand };
            StampNode(unaryExpr, startToken);
            return unaryExpr;
        }

        if (Current.Type == TokenType.True)
        {
            var startTrue = Advance();
            var expr = new BuiltinExpr { Name = "true", Value = true };
            StampNode(expr, startTrue);
            return expr;
        }
        if (Current.Type == TokenType.False)
        {
            var startFalse = Advance();
            var expr = new BuiltinExpr { Name = "false", Value = false };
            StampNode(expr, startFalse);
            return expr;
        }

        if (Current.Type == TokenType.Number)
        {
            var startToken = Current;
            long val = 0;
            try
            {
                if (Current.Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    val = Convert.ToInt64(Current.Value.Substring(2), 16);
                }
                else
                {
                    val = long.Parse(Current.Value);
                }
            }
            catch (FormatException)
            {
                ErrorReporter.Error("Malformed numeric literal.", startToken.Line, startToken.Column, _fileName);
            }
            catch (OverflowException)
            {
                ErrorReporter.Error("Numeric literal is too large for i64.", startToken.Line, startToken.Column, _fileName);
            }
            Advance();
            var numExpr = new NumberExpr { Value = val };
            StampNode(numExpr, startToken);
            return numExpr;
        }

        if (Current.Type == TokenType.String)
        {
            var startToken = Current;
            var val = Current.Value;
            Advance();
            var strExpr = new StringExpr { Value = val };
            StampNode(strExpr, startToken);
            return strExpr;
        }

        if (Current.Type == TokenType.LBracket)
            return ParseArrayLiteral();

        if (IsStructLiteralStart())
            return ParseStructLiteral();

        if (Current.Type == TokenType.Identifier)
        {
            if (IsStaticTypeReferenceStart())
                return ParseStaticTypeReference();

            var startToken = Current;
            var name = Current.Value;
            Advance();

            var identExpr = new IdentifierExpr { Name = name };
            StampNode(identExpr, startToken);
            return identExpr;
        }

        if (Current.Type == TokenType.LParen)
        {
            Advance();
            var expr = ParseExpression();
            Expect(TokenType.RParen, "Expected ')' to close parenthesized expression.");
            return expr;
        }

        var unexpectedToken = Current;
        ErrorReporter.Error(
            $"Unexpected token in expression: {DescribeToken(Current)}. Expected a literal, identifier, builtin, cast, unary operator, array literal, struct literal, or '('.",
            Current.Line,
            Current.Column,
            _fileName);
        Advance();
        return CreateInvalidExpr(unexpectedToken);
    }

    private Expr CreateInvalidExpr(Token token)
    {
        var expr = new InvalidExpr();
        StampNode(expr, token);
        return expr;
    }

    private bool IsStaticTypeReferenceStart()
    {
        if (Current.Type != TokenType.Identifier)
            return false;

        var offset = 1;
        while (Peek(offset).Type == TokenType.Dot && Peek(offset + 1).Type == TokenType.Identifier)
            offset += 2;

        if (Peek(offset).Type != TokenType.Less)
            return false;

        if (!TrySkipTypeArgumentList(ref offset))
            return false;

        return Peek(offset).Type == TokenType.Dot;
    }

    private Expr ParseStaticTypeReference()
    {
        var startToken = Current;
        var path = new List<string>
        {
            Expect(TokenType.Identifier, "Expected type name.").Value
        };

        while (Match(TokenType.Dot))
            path.Add(Expect(TokenType.Identifier, "Expected identifier after '.' in type reference.").Value);

        var name = path[^1];
        path.RemoveAt(path.Count - 1);
        var typeArguments = ParseTypeArgumentList();

        var typeReference = new TypeReferenceExpr
        {
            TypeName = new TypeNode
            {
                Name = name,
                NamespacePath = path,
                TypeArguments = typeArguments
            }
        };
        StampNode(typeReference, startToken);
        return typeReference;
    }

    private int GetPrecedence(TokenType type) => type switch
    {
        TokenType.Star or TokenType.Slash or TokenType.Percent => 9,
        TokenType.Plus or TokenType.Minus => 8,
        TokenType.LShift or TokenType.RShift => 7,
        TokenType.Amp => 6,
        TokenType.Caret => 5,
        TokenType.Pipe => 4,
        TokenType.Greater or TokenType.Less
        or TokenType.GreaterEqual or TokenType.LessEqual
        or TokenType.EqualEqual or TokenType.BangEqual => 3,
        TokenType.AndAnd => 2,
        TokenType.OrOr => 1,
        _ => 0
    };

    private string TokenToOperator(TokenType type) => type switch
    {
        TokenType.Plus => "+",
        TokenType.Minus => "-",
        TokenType.Star => "*",
        TokenType.Slash => "/",
        TokenType.Percent => "%",
        TokenType.Amp => "&",
        TokenType.AndAnd => "&&",
        TokenType.Pipe => "|",
        TokenType.OrOr => "||",
        TokenType.Caret => "^",
        TokenType.LShift => "<<",
        TokenType.RShift => ">>",
        TokenType.Greater => ">",
        TokenType.Less => "<",
        TokenType.GreaterEqual => ">=",
        TokenType.LessEqual => "<=",
        TokenType.EqualEqual => "==",
        TokenType.BangEqual => "!=",
        _ => "?"
    };
}
