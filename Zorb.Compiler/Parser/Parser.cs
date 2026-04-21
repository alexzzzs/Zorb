using System.Collections.Generic;
using System.IO;
using System.Text;
using Zorb.Compiler.Lexer;
using Zorb.Compiler.AST;
using Zorb.Compiler.AST.Expressions;
using Zorb.Compiler.AST.Statements;
using Zorb.Compiler.Utils;

namespace Zorb.Compiler.Parser;

public class Parser
{
    private readonly List<Token> _tokens;
    private readonly string _fileName;
    private readonly ErrorReporter _errorReporter;
    private int _pos = 0;

    public ErrorReporter ErrorReporter => _errorReporter;

    public Parser(List<Token> tokens, string fileName = "unknown", ErrorReporter? errorReporter = null) 
    {
        _tokens = tokens;
        _fileName = fileName;
        _errorReporter = errorReporter ?? new ErrorReporter();
    }

    private Token Current => _tokens[Math.Min(_pos, _tokens.Count - 1)];
    private Token Previous => _pos > 0 ? _tokens[_pos - 1] : Current;

    private Token Advance()
    {
        var token = Current;
        if (_pos < _tokens.Count - 1)
            _pos++;
        return token;
    }

    private void StampNode(Node node, Token token)
    {
        node.File = _fileName;
        node.Line = token.Line;
        node.Column = token.Column;
        node.Length = token.Value.Length;
    }

    private Token Peek(int offset)
    {
        var index = _pos + offset;
        if (index >= _tokens.Count)
            return _tokens[_tokens.Count - 1];
        return _tokens[index];
    }

    private string DescribeToken(Token token)
    {
        return token.Type switch
        {
            TokenType.Identifier => $"identifier '{token.Value}'",
            TokenType.String => $"string literal \"{token.Value}\"",
            TokenType.Number => $"number literal '{token.Value}'",
            _ => token.Type.ToString()
        };
    }

    private Token Expect(TokenType type, string? customMessage = null)
    {
        if (Current.Type != type)
            ErrorReporter.Error(customMessage ?? $"Expected {type}, got {DescribeToken(Current)}", Current.Line, Current.Column, _fileName);
        return Advance();
    }

    private bool Match(TokenType type)
    {
        if (Current.Type == type)
        {
            Advance();
            return true;
        }
        return false;
    }

private void Synchronize()
{
    if (Current.Type != TokenType.Eof)
        Advance();

    while (Current.Type != TokenType.Eof)
    {
        if (Previous.Type == TokenType.Semicolon) return;

        switch (Current.Type)
        {
            case TokenType.Fn:
            case TokenType.Struct:
            case TokenType.Import:
            case TokenType.If:
            case TokenType.While:
            case TokenType.Return:
            case TokenType.Const:
            case TokenType.Error:
            case TokenType.Export:
            case TokenType.Break:
                return;
        }

        Advance();
    }
}

public List<Node> ParseProgram()
{
    var nodes = new List<Node>();

    while (Current.Type != TokenType.Eof)
    {
        try
        {
            if (Current.Type == TokenType.RBrace || Current.Type == TokenType.Semicolon)
            {
                Advance();
                continue;
            }

            if (Current.Type == TokenType.Export)
            {
                Advance();
                nodes.Add(ParseTopLevelDeclaration(isExported: true));
            }
            else if (Current.Type == TokenType.Import)
                nodes.Add(ParseImport());
            else if (Current.Type == TokenType.Error)
                nodes.Add(ParseErrorDecl());
            else if (Current.Type == TokenType.Const)
                nodes.Add(ParseVarDecl(true));
            else if (Current.Type == TokenType.Struct)
                nodes.Add(ParseStruct());
            else if (Current.Type == TokenType.Fn || Current.Type == TokenType.Extern)
                nodes.Add(ParseFunction());
            else if (Current.Type == TokenType.LBracket)
            {
                int lookahead = 0;
                int bracketDepth = 0;
                while (_pos + lookahead < _tokens.Count)
                {
                    var t = Peek(lookahead).Type;
                    if (t == TokenType.LBracket) bracketDepth++;
                    else if (t == TokenType.RBracket) bracketDepth--;
                
                    lookahead++;
                    if (bracketDepth == 0) break;
                }

                var tokenAfterBracket = Peek(lookahead).Type;
                bool isAttribute = tokenAfterBracket == TokenType.Fn || tokenAfterBracket == TokenType.Extern;
                bool isArray = Peek(1).Type == TokenType.Number;
                
                if (isAttribute)
                {
                    nodes.Add(ParseFunction());
                }
                else
                {
                    var attributes = ParseAttributes();
                    var varDecl = (VariableDeclarationNode)ParseVarDecl();
                    varDecl.Attributes = attributes;
                    nodes.Add(varDecl);
                }
            }
            else if (Current.Type == TokenType.Identifier && Peek(1).Type == TokenType.Colon)
            {
                nodes.Add(ParseVarDecl());
            }
            else
            {
                ErrorReporter.Error($"Unexpected top-level token: {Current.Type}", Current.Line, Current.Column, _fileName);
                Advance();
            }
        }
        catch (ZorbCompilerException)
        {
            Synchronize();
        }
    }

    return nodes;
}

    private Node ParseTopLevelDeclaration(bool isExported)
    {
        if (Current.Type == TokenType.Import)
        {
            ErrorReporter.Error("Cannot export an import declaration.", Current.Line, Current.Column, _fileName);
            return ParseImport();
        }

        if (Current.Type == TokenType.Error)
        {
            var decl = ParseErrorDecl();
            decl.IsExported = true;
            return decl;
        }

        if (Current.Type == TokenType.Const)
        {
            var decl = (VariableDeclarationNode)ParseVarDecl(true);
            decl.IsExported = true;
            return decl;
        }

        if (Current.Type == TokenType.Struct)
        {
            var decl = ParseStruct();
            decl.IsExported = true;
            return decl;
        }

        if (Current.Type == TokenType.Fn || Current.Type == TokenType.Extern)
        {
            var decl = ParseFunction();
            decl.IsExported = true;
            return decl;
        }

        if (Current.Type == TokenType.LBracket)
        {
            int lookahead = 0;
            int bracketDepth = 0;
            while (_pos + lookahead < _tokens.Count)
            {
                var t = Peek(lookahead).Type;
                if (t == TokenType.LBracket) bracketDepth++;
                else if (t == TokenType.RBracket) bracketDepth--;

                lookahead++;
                if (bracketDepth == 0) break;
            }

            var tokenAfterBracket = Peek(lookahead).Type;
            if (tokenAfterBracket == TokenType.Fn || tokenAfterBracket == TokenType.Extern)
            {
                var decl = ParseFunction();
                decl.IsExported = true;
                return decl;
            }

            var attributes = ParseAttributes();
            var varDecl = (VariableDeclarationNode)ParseVarDecl();
            varDecl.Attributes = attributes;
            varDecl.IsExported = true;
            return varDecl;
        }

        if (Current.Type == TokenType.Identifier && Peek(1).Type == TokenType.Colon)
        {
            var decl = (VariableDeclarationNode)ParseVarDecl();
            decl.IsExported = true;
            return decl;
        }

        ErrorReporter.Error($"Expected exportable top-level declaration after 'export', got {DescribeToken(Current)}", Current.Line, Current.Column, _fileName);
        Advance();
        return new VariableDeclarationNode
        {
            Name = "__invalid_export",
            TypeName = new TypeNode { Name = "i32" },
            IsConst = true,
            IsExported = true
        };
    }

    private ImportNode ParseImport()
    {
        Expect(TokenType.Import);

        if (Current.Type == TokenType.Identifier && Current.Value == "c")
        {
            Advance();
            var headerToken = Expect(TokenType.String, "Expected C header string after 'import c'.");
            return new ImportNode { Path = headerToken.Value, Alias = "c" };
        }

        var pathToken = Expect(TokenType.String, "Expected import path string after 'import'.");
        string? alias = null;

        if (Current.Type == TokenType.As)
        {
            Advance();
            alias = Expect(TokenType.Identifier, "Expected alias name after 'as' in import.").Value;
        }

        return new ImportNode { Path = pathToken.Value, Alias = alias };
    }

    private StructNode ParseStruct()
    {
        Expect(TokenType.Struct);

        var path = new List<string>();
        path.Add(Expect(TokenType.Identifier, "Expected struct name after 'struct'.").Value);
        while (Match(TokenType.Dot))
        {
            path.Add(Expect(TokenType.Identifier, "Expected identifier after '.' in struct name.").Value);
        }

        var name = path.Last();
        path.RemoveAt(path.Count - 1);

        Expect(TokenType.LBrace, "Expected '{' to start struct body.");

        var fields = new List<(string, TypeNode)>();
        while (Current.Type != TokenType.RBrace && Current.Type != TokenType.Eof)
        {
            var fieldName = Expect(TokenType.Identifier, "Expected struct field name.").Value;
            Expect(TokenType.Colon, $"Expected ':' after field name '{fieldName}'.");
            var fieldType = ParseType();
            fields.Add((fieldName, fieldType));

            if (Current.Type == TokenType.Comma)
            {
                Advance();
                continue;
            }
        }

        Expect(TokenType.RBrace, "Expected '}' to close struct body.");

        return new StructNode { NamespacePath = path, Name = name, Fields = fields };
    }

    private VariableDeclarationNode ParseErrorDecl()
    {
        var startToken = Expect(TokenType.Error);
        var errorCode = Expect(TokenType.Identifier, "Expected error name after 'error'.").Value;
        Expect(TokenType.Equals, $"Expected '=' after error name '{errorCode}'.");
        var initializer = ParseExpression();

        var declaration = new VariableDeclarationNode
        {
            Name = $"Error_{errorCode}",
            TypeName = new TypeNode { Name = "i32" },
            Value = initializer,
            IsConst = true
        };
        StampNode(declaration, startToken);
        return declaration;
    }

    private TypeNode ParseType()
    {
        int? arraySize = null;
        
        if (Current.Type == TokenType.LBracket && Peek(1).Type == TokenType.Number)
        {
            Advance();
            arraySize = int.Parse(Expect(TokenType.Number, "Expected array size after '[' in type.").Value);
            Expect(TokenType.RBracket, "Expected ']' after array size in type.");
        }

        if (Match(TokenType.Fn))
        {
            Expect(TokenType.LParen, "Expected '(' after 'fn' in function type.");
            var paramsList = new List<TypeNode>();
            if (Current.Type != TokenType.RParen)
            {
                do {
                    paramsList.Add(ParseType());
                } while (Match(TokenType.Comma));
            }
            Expect(TokenType.RParen, "Expected ')' to close function type parameter list.");

            TypeNode retType = new() { Name = "void" };
            if (Match(TokenType.Arrow)) retType = ParseType();

            var fnTypeNode = new TypeNode { IsFunction = true, ParamTypes = paramsList, ReturnType = retType };
            if (arraySize.HasValue)
            {
                fnTypeNode.ArraySize = arraySize;
            }
            return fnTypeNode;
        }

        bool isErrorUnion = Match(TokenType.Bang);

        bool pointer = false;
        int pointerLevel = 0;

        while (Match(TokenType.Star))
        {
            pointer = true;
            pointerLevel++;
        }

        var path = new List<string>();
        path.Add(Expect(TokenType.Identifier, "Expected type name.").Value);
        while (Match(TokenType.Dot))
        {
            path.Add(Expect(TokenType.Identifier, "Expected identifier after '.' in type name.").Value);
        }

        var name = path.Last();
        path.RemoveAt(path.Count - 1);

        if (Current.Type == TokenType.LBracket && Peek(1).Type == TokenType.Number)
        {
            ErrorReporter.Error(
                "Array types must be written as '[N]T', not 'T[N]'. For example, use '[4]u8' instead of 'u8[4]'.",
                Current.Line,
                Current.Column,
                _fileName);
            throw new ZorbCompilerException("Invalid postfix array type syntax.");
        }

        var typeNode = new TypeNode
        {
            Name = name,
            NamespacePath = path,
            IsPointer = pointer,
            PointerLevel = pointerLevel,
            ArraySize = arraySize
        };

        if (isErrorUnion)
        {
            return new TypeNode
            {
                Name = name,
                NamespacePath = new List<string>(path),
                IsPointer = pointer,
                PointerLevel = pointerLevel,
                ArraySize = arraySize,
                IsErrorUnion = true,
                ErrorInnerType = new TypeNode
                {
                    Name = name,
                    NamespacePath = new List<string>(path),
                    IsPointer = pointer,
                    PointerLevel = pointerLevel,
                    ArraySize = arraySize
                }
            };
        }

        return typeNode;
    }

    private FunctionDecl ParseFunction()
    {
        var attributes = new List<string>();
        while (Current.Type == TokenType.LBracket)
        {
            Advance();
            while (Current.Type != TokenType.RBracket && Current.Type != TokenType.Eof)
            {
                if (Current.Type == TokenType.Align)
                {
                    Advance();
                    Expect(TokenType.LParen);
                    var numToken = Expect(TokenType.Number);
                    Expect(TokenType.RParen);
                    attributes.Add($"align({numToken.Value})");
                }
                else if (Current.Type == TokenType.NoInline)
                {
                    Advance();
                    attributes.Add("noinline");
                }
                else if (Current.Type == TokenType.NoClone)
                {
                    Advance();
                    attributes.Add("noclone");
                }
                else
                {
                    ErrorReporter.Error($"Unknown attribute {DescribeToken(Current)} in function attribute list.", Current.Line, Current.Column, _fileName);
                    Advance();
                }

                if (Current.Type == TokenType.Comma)
                {
                    Advance();
                }
                else if (Current.Type != TokenType.RBracket)
                {
                    ErrorReporter.Error($"Expected ',' or ']' in function attribute list, got {DescribeToken(Current)}", Current.Line, Current.Column, _fileName);
                }
            }
            Expect(TokenType.RBracket, "Expected ']' to close function attribute list.");
        }

        bool isExtern = Match(TokenType.Extern);
        Expect(TokenType.Fn, "Expected 'fn' after 'extern'.");

        var path = new List<string>();
        path.Add(Expect(TokenType.Identifier, "Expected function name after 'fn'.").Value);
        while (Match(TokenType.Dot))
        {
            path.Add(Expect(TokenType.Identifier, "Expected identifier after '.' in function name.").Value);
        }

        var name = path.Last();
        path.RemoveAt(path.Count - 1);

        Expect(TokenType.LParen, "Expected '(' after function name.");

        var parameters = ParseParameters();

        Expect(TokenType.RParen, "Expected ')' to close parameter list.");

        TypeNode returnType = new() { Name = "void" };
        if (Current.Type == TokenType.Arrow)
        {
            Advance();
            returnType = ParseType();
        }
        else if (Current.Type == TokenType.Bang)
        {
            Advance();
            var innerType = ParseType();
            returnType = new TypeNode
            {
                Name = innerType.Name,
                NamespacePath = innerType.NamespacePath,
                IsPointer = innerType.IsPointer,
                PointerLevel = innerType.PointerLevel,
                ArraySize = innerType.ArraySize,
                IsErrorUnion = true,
                ErrorInnerType = innerType
            };
        }

        FunctionDecl function = new FunctionDecl { NamespacePath = path, Name = name, Parameters = parameters, ReturnType = returnType, IsExtern = isExtern, Attributes = attributes };

        if (isExtern)
        {
            return function;
        }

        Expect(TokenType.LBrace, "Expected '{' to start function body.");

        var body = new List<Statement>();
        while (Current.Type != TokenType.RBrace && Current.Type != TokenType.Eof)
        {
            body.Add(ParseStatement());
        }

        Expect(TokenType.RBrace, "Expected '}' to close function body.");
        function.Body = body;

        return function;
    }

    private List<Parameter> ParseParameters()
    {
        var parameters = new List<Parameter>();

        if (Current.Type != TokenType.RParen)
        {
            while (true)
            {
                var name = Expect(TokenType.Identifier, "Expected parameter name.").Value;
                Expect(TokenType.Colon, $"Expected ':' after parameter name '{name}'.");
                var typeName = ParseType();

                parameters.Add(new Parameter(name, typeName));

                if (Current.Type == TokenType.Comma)
                {
                    Advance();
                    continue;
                }

                break;
            }
        }

        return parameters;
    }

    private List<string> ParseAttributes()
    {
        var attributes = new List<string>();
        while (Current.Type == TokenType.LBracket)
        {
            if (Peek(1).Type == TokenType.Number)
            {
                return attributes;
            }
            
            Advance();
            while (Current.Type != TokenType.RBracket)
            {
                if (Current.Type == TokenType.Align)
                {
                    Advance();
                    Expect(TokenType.LParen);
                    var numToken = Expect(TokenType.Number);
                    Expect(TokenType.RParen);
                    attributes.Add($"align({numToken.Value})");
                }
                else if (Current.Type == TokenType.NoInline)
                {
                    Advance();
                    attributes.Add("noinline");
                }
                else if (Current.Type == TokenType.NoClone)
                {
                    Advance();
                    attributes.Add("noclone");
                }
                else
                {
                    ErrorReporter.Error($"Unknown attribute {DescribeToken(Current)}.", Current.Line, Current.Column, _fileName);
                    Advance();
                }

                if (Current.Type == TokenType.Comma)
                {
                    Advance();
                }
                else if (Current.Type != TokenType.RBracket)
                {
                    ErrorReporter.Error($"Expected ',' or ']' in attribute list, got {DescribeToken(Current)}", Current.Line, Current.Column, _fileName);
                }
            }
            Expect(TokenType.RBracket, "Expected ']' to close attribute list.");
        }
        return attributes;
    }

    private Statement ParseStatement()
    {
        if (Current.Type == TokenType.If)
            return ParseIf();

        if (Current.Type == TokenType.While)
            return ParseWhile();

        if (Current.Type == TokenType.Return)
            return ParseReturn();

        if (Current.Type == TokenType.Continue)
        {
            var stmt = new ContinueStmt();
            StampNode(stmt, Current);
            Advance();
            return stmt;
        }

        if (Current.Type == TokenType.Break)
        {
            var stmt = new BreakStmt();
            StampNode(stmt, Current);
            Advance();
            return stmt;
        }

        if (Current.Type == TokenType.Identifier && Current.Value == "asm")
            return ParseAsm();

        var attributes = ParseAttributes();

        if (Current.Type == TokenType.Identifier && Peek(1).Type == TokenType.Colon)
        {
            var varDecl = (VariableDeclarationNode)ParseVarDecl(false);
            varDecl.Attributes = attributes;
            return varDecl;
        }

        if (Current.Type == TokenType.Identifier && (Peek(1).Type == TokenType.Equals || Peek(1).Type == TokenType.LBracket))
            return ParseAssignment();

        var expr = ParseExpression();

        if (Current.Type == TokenType.Equals)
        {
            Advance();
            var value = ParseExpression();
            return new AssignStmt { Target = expr, Value = value };
        }

        return new ExpressionStatement { Expression = expr };
    }

    private Statement ParseReturn()
    {
        Expect(TokenType.Return);
        
        if (Current.Type == TokenType.Semicolon || Current.Type == TokenType.RBrace)
        {
            return new ReturnNode { Value = null };
        }
        
        var value = ParseExpression();
        return new ReturnNode { Value = value };
    }

    private Statement ParseIf()
    {
        Expect(TokenType.If);

        var condition = ParseExpression();

        Expect(TokenType.LBrace, "Expected '{' to start if body.");

        var body = new List<Statement>();

        while (Current.Type != TokenType.RBrace && Current.Type != TokenType.Eof)
        {
            body.Add(ParseStatement());
        }

        Expect(TokenType.RBrace, "Expected '}' to close if body.");

        var elseBody = new List<Statement>();
        if (Match(TokenType.Else))
        {
            if (Peek(1).Type == TokenType.LBrace)
            {
                Expect(TokenType.LBrace, "Expected '{' to start else body.");
                while (Current.Type != TokenType.RBrace && Current.Type != TokenType.Eof)
                {
                    elseBody.Add(ParseStatement());
                }
                Expect(TokenType.RBrace, "Expected '}' to close else body.");
            }
            else if (Current.Type == TokenType.If)
            {
                elseBody.Add(ParseIf());
            }
            else
            {
                Expect(TokenType.LBrace, "Expected '{' to start else body.");
                while (Current.Type != TokenType.RBrace && Current.Type != TokenType.Eof)
                {
                    elseBody.Add(ParseStatement());
                }
                Expect(TokenType.RBrace, "Expected '}' to close else body.");
            }
        }

        return new IfStmt { Condition = condition, Body = body, ElseBody = elseBody };
    }

    private Statement ParseWhile()
    {
        Expect(TokenType.While);

        var condition = ParseExpression();

        Expect(TokenType.LBrace, "Expected '{' to start while body.");

        var body = new List<Statement>();
        while (Current.Type != TokenType.RBrace && Current.Type != TokenType.Eof)
        {
            body.Add(ParseStatement());
        }

        Expect(TokenType.RBrace, "Expected '}' to close while body.");

        return new WhileStmt { Condition = condition, Body = body };
    }

    private Statement ParseAssignment()
    {
        var target = ParsePostfix();
        Expect(TokenType.Equals);
        var value = ParseExpression();
        return new AssignStmt { Target = target, Value = value };
    }

    private List<AsmOperand> ParseAsmConstraints()
    {
        var constraints = new List<AsmOperand>();
        while (Current.Type == TokenType.String)
        {
            var constraint = Expect(TokenType.String).Value;
            Expect(TokenType.LParen);
            var expr = ParseExpression();
            Expect(TokenType.RParen);

            constraints.Add(new AsmOperand
            {
                Constraint = constraint,
                Expression = expr
            });

            if (Current.Type == TokenType.Comma)
            {
                Advance();
            }
            else
            {
                break;
            }
        }
        return constraints;
    }

    private Statement ParseAsm()
    {
        Expect(TokenType.Identifier);
        Expect(TokenType.LBrace, "Expected '{' to start asm block.");

        var node = new AsmStatementNode();
        
        while (Current.Type == TokenType.String)
        {
            node.Code.Add(Expect(TokenType.String).Value);
        }

        if (Current.Type == TokenType.Colon)
        {
            Advance();
            node.Outputs = ParseAsmConstraints();
        }

        if (Current.Type == TokenType.Colon)
        {
            Advance();
            node.Inputs = ParseAsmConstraints();
        }

        if (Current.Type == TokenType.Colon)
        {
            Advance();
            while (Current.Type == TokenType.String)
            {
                node.Clobbers.Add(Expect(TokenType.String).Value);
                if (Current.Type == TokenType.Comma)
                {
                    Advance();
                }
                else
                {
                    break;
                }
            }
        }

        Expect(TokenType.RBrace, "Expected '}' to close asm block.");
        return node;
    }

    private Statement ParseVarDecl(bool isConst = false)
    {
        if (Current.Type == TokenType.Const)
        {
            Advance();
            isConst = true;
        }

        var name = Expect(TokenType.Identifier, isConst ? "Expected constant name after 'const'." : "Expected variable name.").Value;

        while (Match(TokenType.Dot))
        {
            name += "." + Expect(TokenType.Identifier, "Expected identifier after '.' in variable name.").Value;
        }

        Expect(TokenType.Colon, $"Expected ':' after declaration name '{name}'.");
        var typeName = ParseType();

        Expr? initializer = null;
        if (Current.Type == TokenType.Equals)
        {
            Advance();
            initializer = ParseExpression();
        }

        return new VariableDeclarationNode { Name = name, TypeName = typeName, Value = initializer, IsConst = isConst };
    }

    private Expr ParseExpression(int parentPrecedence = 0)
    {
        var left = ParsePostfix();

        while (true)
        {
            if (Current.Type == TokenType.Catch)
            {
                Advance();
                Expect(TokenType.Pipe, "Expected '|' after 'catch'.");
                var errorVar = Expect(TokenType.Identifier, "Expected catch error variable name.").Value;
                Expect(TokenType.Pipe, "Expected closing '|' after catch error variable.");
                Expect(TokenType.LBrace, "Expected '{' to start catch body.");
                var catchBody = new List<Statement>();
                while (Current.Type != TokenType.RBrace && Current.Type != TokenType.Eof)
                {
                    catchBody.Add(ParseStatement());
                }
                Expect(TokenType.RBrace, "Expected '}' to close catch body.");
                left = new CatchExpr { Left = left, ErrorVar = errorVar, CatchBody = catchBody };
                continue;
            }

            var prec = GetPrecedence(Current.Type);
            if (prec == 0 || prec <= parentPrecedence)
                break;

            var opToken = Current;
            Advance();
            var right = ParseExpression(prec);
            left = new BinaryExpr { Left = left, Operator = TokenToOperator(opToken.Type), Right = right };
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
                Advance();
                var index = ParseExpression();
                Expect(TokenType.RBracket, "Expected ']' after index expression.");
                expr = new IndexExpr { Target = expr, Index = index };
            }
            else if (Current.Type == TokenType.Dot)
            {
                Advance();
                var field = Expect(TokenType.Identifier, "Expected identifier after '.'.").Value;
                if (expr is ErrorNamespaceExpr)
                {
                    expr = new ErrorExpr { ErrorCode = field };
                }
                else
                {
                    expr = new FieldExpr { Target = expr, Field = field };
                }
            }
            else if (Current.Type == TokenType.LParen)
            {
                var callStartToken = Current;
                Advance();
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
                var totalSpan = (lastToken.Column + lastToken.Length) - callStartToken.Column;

                if (expr is IdentifierExpr id)
                    expr = new CallExpr { NamespacePath = new List<string>(), Name = id.Name, Args = args };
                else
                    expr = new CallExpr { Name = "", Args = args, TargetExpr = expr };
                
                expr.File = _fileName;
                expr.Line = callStartToken.Line;
                expr.Column = callStartToken.Column;
                expr.Length = totalSpan;
            }
            else
            {
                break;
            }
        }

        return expr;
    }

    private bool IsStructLiteralStart()
    {
        if (Current.Type != TokenType.Identifier)
            return false;

        var offset = 1;
        while (Peek(offset).Type == TokenType.Dot && Peek(offset + 1).Type == TokenType.Identifier)
            offset += 2;

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
            var parenDepth = 0;
            var bracketDepth = 0;
            var braceDepth = 0;
            var sawValueToken = false;

            while (true)
            {
                var tokenType = Peek(offset).Type;
                if (tokenType == TokenType.Eof)
                    return false;

                if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                {
                    if (tokenType == TokenType.Equals
                        || tokenType == TokenType.Semicolon
                        || tokenType == TokenType.Fn
                        || tokenType == TokenType.If
                        || tokenType == TokenType.While
                        || tokenType == TokenType.Return
                        || tokenType == TokenType.Const
                        || tokenType == TokenType.Break
                        || tokenType == TokenType.Continue)
                    {
                        return false;
                    }

                    if (tokenType == TokenType.Comma || tokenType == TokenType.RBrace)
                    {
                        if (!sawValueToken)
                            return false;
                        break;
                    }
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
                }

                offset++;
            }

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
                NamespacePath = path
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
        if (typeName.ArraySize == null)
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
            return null!;
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
            var startToken = Advance();
            var expr = new BuiltinExpr { Name = "true", Value = true };
            StampNode(expr, startToken);
            return expr;
        }
        if (Current.Type == TokenType.False)
        {
            var startToken = Advance();
            var expr = new BuiltinExpr { Name = "false", Value = false };
            StampNode(expr, startToken);
            return expr;
        }

        if (Current.Type == TokenType.Number)
        {
            var startToken = Current;
            long val;
            if (Current.Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                val = Convert.ToInt64(Current.Value.Substring(2), 16);
            }
            else
            {
                val = long.Parse(Current.Value);
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

        ErrorReporter.Error($"Unexpected token in expression: {DescribeToken(Current)}", Current.Line, Current.Column, _fileName);
        Advance();
        return null!;
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
