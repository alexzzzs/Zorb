using Zorb.Compiler.AST;
using Zorb.Compiler.AST.Statements;
using Zorb.Compiler.Lexer;
using Zorb.Compiler.Utils;

namespace Zorb.Compiler.Parser;

public partial class Parser
{
    public List<Node> ParseProgram()
    {
        var nodes = new List<Node>();

        while (Current.Type != TokenType.Eof)
        {
            try
            {
                if (TrySkipIgnoredTopLevelToken())
                    continue;

                if (TryParseTopLevelNode(out var node))
                {
                    nodes.Add(node);
                }
                else
                {
                    ReportUnexpectedTopLevelToken();
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

    private bool TrySkipIgnoredTopLevelToken()
    {
        if (Current.Type != TokenType.RBrace && Current.Type != TokenType.Semicolon)
            return false;

        Advance();
        return true;
    }

    private bool TryParseTopLevelNode(out Node node)
    {
        if (Current.Type == TokenType.Export)
        {
            Advance();
            node = ParseTopLevelDeclaration(isExported: true);
            return true;
        }

        if (Current.Type == TokenType.Import)
        {
            node = ParseImport();
            return true;
        }

        if (Current.Type == TokenType.Error)
        {
            node = ParseErrorDecl();
            return true;
        }

        if (Current.Type == TokenType.Const)
        {
            node = ParseVarDecl(true);
            return true;
        }

        if (Current.Type == TokenType.Struct)
        {
            node = ParseStruct();
            return true;
        }

        if (Current.Type == TokenType.Enum)
        {
            node = ParseEnum();
            return true;
        }

        if (Current.Type == TokenType.Union)
        {
            node = ParseUnion();
            return true;
        }

        if (Current.Type == TokenType.Fn)
        {
            node = ParseFunction();
            return true;
        }

        if (Current.Type == TokenType.Extern)
        {
            node = ParseExternDeclaration();
            return true;
        }

        if (Current.Type == TokenType.LBracket)
        {
            node = ParseAttributedTopLevelNode();
            return true;
        }

        if (Current.Type == TokenType.Identifier && Peek(1).Type == TokenType.Colon)
        {
            node = ParseVarDecl();
            return true;
        }

        node = null!;
        return false;
    }

    private Node ParseAttributedTopLevelNode()
    {
        var tokenAfterAttributes = PeekDeclarationAfterAttributeLists();
        if (tokenAfterAttributes == TokenType.Fn || tokenAfterAttributes == TokenType.Extern)
            return ParseFunction();

        if (tokenAfterAttributes == TokenType.Struct)
            return ParseStruct();

        var attributes = ParseAttributes();
        var varDecl = (VariableDeclarationNode)ParseVarDecl();
        ApplyVariableAttributes(varDecl, attributes);
        return varDecl;
    }

    private void ReportUnexpectedTopLevelToken()
    {
        ErrorReporter.Error(
            $"Unexpected top-level token {DescribeToken(Current)}. Expected import, error, const, struct, enum, union, fn, extern fn, extern type, an attribute list, or a variable declaration.",
            Current.Line,
            Current.Column,
            _fileName);
    }
}
