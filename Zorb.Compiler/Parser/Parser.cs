using System;
using System.Collections.Generic;
using Zorb.Compiler.AST;
using Zorb.Compiler.AST.Expressions;
using Zorb.Compiler.AST.Statements;
using Zorb.Compiler.Lexer;
using Zorb.Compiler.Utils;

namespace Zorb.Compiler.Parser;

public partial class Parser
{
    private sealed record AttributeParseResult(List<string> Attributes, Expr? AlignExpr);
    private sealed record FieldAttributeParseResult(List<string> Attributes, Expr? OffsetExpr);

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
        node.Length = Math.Max(1, token.Value.Length);
    }

    private static void StampNode(Node node, Node source)
    {
        node.File = source.File;
        node.Line = source.Line;
        node.Column = source.Column;
        node.Length = Math.Max(1, source.Length);
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

    private void ReportAttributeSeparatorError(string listKind)
    {
        ErrorReporter.Error(
            $"Expected ',' or ']' in {listKind}. Did you forget a comma between attributes? Got {DescribeToken(Current)}.",
            Current.Line,
            Current.Column,
            _fileName);
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

    private (List<string> Path, string Name) ParseDottedDeclName(string missingNameMessage, string missingSegmentMessage)
    {
        var path = new List<string> { Expect(TokenType.Identifier, missingNameMessage).Value };
        while (Match(TokenType.Dot))
            path.Add(Expect(TokenType.Identifier, missingSegmentMessage).Value);

        var name = path[^1];
        path.RemoveAt(path.Count - 1);
        return (path, name);
    }

    private bool MatchContextualKeyword(string keyword)
    {
        if (Current.Type == TokenType.Identifier && Current.Value == keyword)
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
            if (Previous.Type == TokenType.Semicolon)
                return;

            switch (Current.Type)
            {
                case TokenType.Fn:
                case TokenType.Struct:
                case TokenType.Import:
                case TokenType.If:
                case TokenType.While:
                case TokenType.For:
                case TokenType.Switch:
                case TokenType.Return:
                case TokenType.Const:
                case TokenType.Error:
                case TokenType.Export:
                case TokenType.Break:
                case TokenType.Extern:
                case TokenType.Enum:
                case TokenType.Union:
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
                else if (Current.Type == TokenType.Enum)
                    nodes.Add(ParseEnum());
                else if (Current.Type == TokenType.Union)
                    nodes.Add(ParseUnion());
                else if (Current.Type == TokenType.Fn || Current.Type == TokenType.Extern)
                    nodes.Add(ParseFunction());
                else if (Current.Type == TokenType.LBracket)
                {
                    var tokenAfterAttributes = PeekDeclarationAfterAttributeLists();

                    if (tokenAfterAttributes == TokenType.Fn || tokenAfterAttributes == TokenType.Extern)
                    {
                        nodes.Add(ParseFunction());
                    }
                    else if (tokenAfterAttributes == TokenType.Struct)
                    {
                        nodes.Add(ParseStruct());
                    }
                    else
                    {
                        var attributes = ParseAttributes();
                        var varDecl = (VariableDeclarationNode)ParseVarDecl();
                        ApplyVariableAttributes(varDecl, attributes);
                        nodes.Add(varDecl);
                    }
                }
                else if (Current.Type == TokenType.Identifier && Peek(1).Type == TokenType.Colon)
                {
                    nodes.Add(ParseVarDecl());
                }
                else
                {
                    ErrorReporter.Error(
                        $"Unexpected top-level token {DescribeToken(Current)}. Expected import, error, const, struct, enum, union, fn, extern fn, an attribute list, or a variable declaration.",
                        Current.Line,
                        Current.Column,
                        _fileName);
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
}
