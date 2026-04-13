namespace Zorb.Compiler.AST.Expressions;
using Zorb.Compiler.AST.Statements;

public abstract class Expr : Node { }

public class CallExpr : Expr
{
    public List<string> NamespacePath { get; set; } = new();
    public string Name { get; set; } = null!;
    public List<Expr> Args { get; set; } = new();
    public Expr? TargetExpr { get; set; }
}

public class NumberExpr : Expr
{
    public long Value { get; set; }
}

public class IdentifierExpr : Expr
{
    public string Name { get; set; } = null!;
}

public class StringExpr : Expr
{
    public string Value { get; set; } = null!;
}

public class BinaryExpr : Expr
{
    public Expr Left { get; set; } = null!;
    public string Operator { get; set; } = null!;
    public Expr Right { get; set; } = null!;
}

public class IndexExpr : Expr
{
    public Expr Target { get; set; } = null!;
    public Expr Index { get; set; } = null!;
}

public class UnaryExpr : Expr
{
    public string Operator { get; set; } = null!;
    public Expr Operand { get; set; } = null!;
}

public class FieldExpr : Expr
{
    public Expr Target { get; set; } = null!;
    public string Field { get; set; } = null!;
}

public class CastExpr : Expr
{
    public TypeNode TargetType { get; set; } = null!;
    public Expr Expr { get; set; } = null!;
}

public class BuiltinExpr : Expr
{
    public string Name { get; set; } = null!;
    public bool Value { get; set; }
}

public class ErrorNamespaceExpr : Expr { }

public class SizeofExpr : Expr
{
    public TypeNode TargetType { get; set; } = null!;
}

public class ErrorExpr : Expr
{
    public string ErrorCode { get; set; } = null!;
}

public class CatchExpr : Expr
{
    public Expr Left { get; set; } = null!;
    public string ErrorVar { get; set; } = null!;
    public List<AST.Statements.Statement> CatchBody { get; set; } = new();
}
