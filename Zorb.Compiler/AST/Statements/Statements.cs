using System.Collections.Generic;
using System.Linq;
using Zorb.Compiler.AST.Expressions;

namespace Zorb.Compiler.AST.Statements;

public abstract class Statement : Node { }

public class TypeNode
{
    public string Name { get; set; } = "i32";
    public List<string> NamespacePath { get; set; } = new();
    public List<TypeNode> TypeArguments { get; set; } = new();
    public bool IsAliasQualifiedReference { get; set; }
    public bool IsVolatile { get; set; }
    public bool IsSlice { get; set; }
    public bool IsPointer { get; set; }
    public int PointerLevel { get; set; }
    public int? ArraySize { get; set; }
    public Expr? ArraySizeExpr { get; set; }
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
            TypeArguments = TypeArguments.Select(t => t.Clone()).ToList(),
            IsAliasQualifiedReference = IsAliasQualifiedReference,
            IsVolatile = IsVolatile,
            IsSlice = IsSlice,
            IsPointer = IsPointer,
            PointerLevel = PointerLevel,
            ArraySize = ArraySize,
            // ArraySizeExpr is intentionally aliased: expression trees are treated as immutable
            // after parse, while semantic checking mutates only the resolved ArraySize value.
            ArraySizeExpr = ArraySizeExpr,
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

public class GenericTypeParameter
{
    public string Name { get; set; } = null!;
    public TypeNode? Constraint { get; set; }
    public TypeNode? DefaultType { get; set; }
}

public class StructField : Node
{
    public string Name { get; set; } = null!;
    public TypeNode TypeName { get; set; } = null!;
    public List<string> Attributes { get; set; } = new();
    public Expr? OffsetExpr { get; set; }
}

public class FunctionDecl : Node
{
    public bool IsExported { get; set; }
    public List<string> NamespacePath { get; set; } = new();
    public string Name { get; set; } = null!;
    public List<string> TypeParameters { get; set; } = new();
    public List<GenericTypeParameter> TypeParameterSpecs { get; set; } = new();
    public List<Parameter> Parameters { get; set; } = new();
    public TypeNode ReturnType { get; set; } = new() { Name = "void" };
    public List<Statement> Body { get; set; } = new();
    public bool IsExtern { get; set; }
    public List<string> Attributes { get; set; } = new();
    public Expr? AlignExpr { get; set; }
}

public class ExternTypeDecl : Node
{
    public bool IsExported { get; set; }
    public List<string> NamespacePath { get; set; } = new();
    public string Name { get; set; } = null!;
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
    public Expr? AlignExpr { get; set; }
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

public class ForStmt : Statement
{
    public Statement? Initializer { get; set; }
    public Expr? Condition { get; set; }
    public Statement? Update { get; set; }
    public List<Statement> Body { get; set; } = new();
}

public class SwitchCase
{
    public Expr Value { get; set; } = null!;
    public List<Statement> Body { get; set; } = new();
}

public class SwitchStmt : Statement
{
    public Expr Expression { get; set; } = null!;
    public List<SwitchCase> Cases { get; set; } = new();
    public List<Statement> ElseBody { get; set; } = new();
}

public abstract class MatchPattern : Node { }

public class QualifiedMatchPattern : MatchPattern
{
    public Expr Value { get; set; } = null!;
}

public class UnionMatchPattern : MatchPattern
{
    public Expr Variant { get; set; } = null!;
    public string? BindingName { get; set; }
    public string? ResolvedUnionName { get; set; }
    public string? VariantName { get; set; }
    public TypeNode? BindingType { get; set; }
}

public class MatchCase
{
    public MatchPattern Pattern { get; set; } = null!;
    public List<Statement> Body { get; set; } = new();
}

public class MatchStmt : Statement
{
    public Expr Expression { get; set; } = null!;
    public List<MatchCase> Cases { get; set; } = new();
    public List<Statement> ElseBody { get; set; } = new();
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
    public List<string> TypeParameters { get; set; } = new();
    public List<GenericTypeParameter> TypeParameterSpecs { get; set; } = new();
    public List<string> Attributes { get; set; } = new();
    public List<StructField> Fields { get; set; } = new();
    public Expr? AlignExpr { get; set; }
}

public class EnumMember : Node
{
    public string Name { get; set; } = null!;
    public Expr? Value { get; set; }
    public long? ResolvedValue { get; set; }
}

public class EnumNode : Node
{
    public bool IsExported { get; set; }
    public List<string> NamespacePath { get; set; } = new();
    public string Name { get; set; } = null!;
    public List<string> TypeParameters { get; set; } = new();
    public List<GenericTypeParameter> TypeParameterSpecs { get; set; } = new();
    public TypeNode UnderlyingType { get; set; } = null!;
    public List<EnumMember> Members { get; set; } = new();
}

public class UnionVariant : Node
{
    public string Name { get; set; } = null!;
    public TypeNode TypeName { get; set; } = null!;
}

public class UnionNode : Node
{
    public bool IsExported { get; set; }
    public List<string> NamespacePath { get; set; } = new();
    public string Name { get; set; } = null!;
    public List<string> TypeParameters { get; set; } = new();
    public List<GenericTypeParameter> TypeParameterSpecs { get; set; } = new();
    public List<UnionVariant> Variants { get; set; } = new();
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
