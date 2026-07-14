namespace Zorb.Compiler.AST;

public abstract class Node 
{ 
    public string File { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public int Length { get; set; }
}