namespace Zorb.Compiler.Utils;

public class LexerException : Exception
{
    public string File { get; }
    public int Line { get; }
    public int Column { get; }

    public LexerException(string message, int line, int column, string file = "unknown")
        : base(message)
    {
        File = file;
        Line = line;
        Column = column;
    }
}
