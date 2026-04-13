using System.Collections.Generic;
using System.Linq;
using Zorb.Compiler.AST.Expressions;

namespace Zorb.Compiler.AST.Statements;

public abstract class Statement : Node { }

public class TypeNode
{
    public string Name { get; set; } = "i32";
    public List<string> NamespacePath { get; set; } = new();
    public bool IsPointer { get; set; }
    public int PointerLevel { get; set; }
    public int? ArraySize { get; set; }
    public bool IsErrorUnion { get; set; }
    public TypeNode? ErrorInnerType { get; set; }

    public bool IsFunction { get; set; }
    public List<TypeNode> ParamTypes { get; set; } = new();
    public TypeNode? ReturnType { get; set; }

    public TypeNode Clone()
    {
        return new TypeNode
        {
            Name = Name,
            NamespacePath = new List<string>(NamespacePath),
            IsPointer = IsPointer,
            PointerLevel = PointerLevel,
            ArraySize = ArraySize,
            IsErrorUnion = IsErrorUnion,
            ErrorInnerType = ErrorInnerType?.Clone(),
            IsFunction = IsFunction,
            ParamTypes = ParamTypes.Select(t => t.Clone()).ToList(),
            ReturnType = ReturnType?.Clone()
        };
    }
}

public class Parameter
{
    public string Name { get; set; } = null!;
    public TypeNode TypeName { get; set; } = null!;

    public Parameter(string name, TypeNode typeName)
    {
        Name = name;
        TypeName = typeName;
    }

    public Parameter() { }
}

public class FunctionDecl : Node
{
    public bool IsExported { get; set; }
    public List<string> NamespacePath { get; set; } = new();
    public string Name { get; set; } = null!;
    public List<Parameter> Parameters { get; set; } = new();
    public TypeNode ReturnType { get; set; } = new() { Name = "void" };
    public List<Statement> Body { get; set; } = new();
    public bool IsExtern { get; set; }
    public List<string> Attributes { get; set; } = new();
}

public class BlockNode : Node
{
    public List<Node> Statements { get; set; } = new();
}

public class ReturnNode : Statement
{
    public Expr? Value { get; set; }
}

public class VariableDeclarationNode : Statement
{
    public bool IsExported { get; set; }
    public string Name { get; set; } = null!;
    public TypeNode TypeName { get; set; } = null!;
    public Expr? Value { get; set; }
    public List<string> Attributes { get; set; } = new();
    public bool IsConst { get; set; }
}

public class ExpressionStatement : Statement
{
    public Expr Expression { get; set; } = null!;
}

public class IfStmt : Statement
{
    public Expr Condition { get; set; } = null!;
    public List<Statement> Body { get; set; } = new();
    public List<Statement> ElseBody { get; set; } = new();
}

public class WhileStmt : Statement
{
    public Expr Condition { get; set; } = null!;
    public List<Statement> Body { get; set; } = new();
}

public class ContinueStmt : Statement { }

public class BreakStmt : Statement { }

public class AssignStmt : Statement
{
    public Expr Target { get; set; } = null!;
    public Expr Value { get; set; } = null!;
}

public class StructNode : Node
{
    public bool IsExported { get; set; }
    public List<string> NamespacePath { get; set; } = new();
    public string Name { get; set; } = null!;
    public List<(string Name, TypeNode Type)> Fields { get; set; } = new();
}

public class ImportNode : Statement
{
    public string Path { get; set; } = null!;
    public string? Alias { get; set; }
}

public class CHeaderNode : Node
{
    public string Header { get; set; } = null!;
}

public class AsmOperand
{
    public string Constraint { get; set; } = "";
    public Expr Expression { get; set; } = null!;
}

public class AsmStatementNode : Statement
{
    public List<string> Code { get; set; } = new();
    public List<AsmOperand> Outputs { get; set; } = new();
    public List<AsmOperand> Inputs { get; set; } = new();
    public List<string> Clobbers { get; set; } = new();
}
