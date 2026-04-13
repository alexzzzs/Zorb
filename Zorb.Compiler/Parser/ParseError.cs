namespace Zorb.Compiler.Parser;

public class ParseError : System.Exception
{
    public int Line { get; }
    public int Column { get; }

    public ParseError(string message, int line, int column) : base(message)
    {
        Line = line;
        Column = column;
    }
}