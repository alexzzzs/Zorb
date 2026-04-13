namespace Zorb.Compiler.Lexer;

public class Token
{
    public TokenType Type { get; }
    public string Value { get; }
    public int Line { get; }
    public int Column { get; }
    public int Length => Value.Length;

    public Token(TokenType type, string value = "", int line = 1, int column = 1)
    {
        Type = type;
        Value = value;
        Line = line;
        Column = column;
    }
}