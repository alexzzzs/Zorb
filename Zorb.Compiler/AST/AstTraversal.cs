using Zorb.Compiler.AST.Expressions;
using Zorb.Compiler.AST.Statements;

namespace Zorb.Compiler.AST;

public static class AstTraversal
{
    public static IEnumerable<Expr> EnumerateExpressions(IEnumerable<Node> roots)
    {
        foreach (var root in roots)
        {
            foreach (var expression in EnumerateExpressions(root))
                yield return expression;
        }
    }

    public static IEnumerable<Expr> EnumerateExpressions(Node root)
    {
        switch (root)
        {
            case FunctionDecl function:
                foreach (var expression in EnumerateOptionalExpression(function.AlignExpr))
                    yield return expression;
                foreach (var expression in EnumerateExpressions(function.Body))
                    yield return expression;
                yield break;

            case VariableDeclarationNode variableDeclaration:
                foreach (var expression in EnumerateOptionalExpression(variableDeclaration.AlignExpr))
                    yield return expression;
                foreach (var expression in EnumerateOptionalExpression(variableDeclaration.Value))
                    yield return expression;
                yield break;

            case ExpressionStatement expressionStatement:
                foreach (var expression in EnumerateExpressions(expressionStatement.Expression))
                    yield return expression;
                yield break;

            case AssignStmt assign:
                foreach (var expression in EnumerateExpressions(assign.Target))
                    yield return expression;
                foreach (var expression in EnumerateExpressions(assign.Value))
                    yield return expression;
                yield break;

            case ReturnNode returnNode:
                foreach (var expression in EnumerateOptionalExpression(returnNode.Value))
                    yield return expression;
                yield break;

            case IfStmt ifStmt:
                foreach (var expression in EnumerateExpressions(ifStmt.Condition))
                    yield return expression;
                foreach (var expression in EnumerateExpressions(ifStmt.Body))
                    yield return expression;
                foreach (var expression in EnumerateExpressions(ifStmt.ElseBody))
                    yield return expression;
                yield break;

            case WhileStmt whileStmt:
                foreach (var expression in EnumerateExpressions(whileStmt.Condition))
                    yield return expression;
                foreach (var expression in EnumerateExpressions(whileStmt.Body))
                    yield return expression;
                yield break;

            case ForStmt forStmt:
                if (forStmt.Initializer != null)
                {
                    foreach (var expression in EnumerateExpressions(forStmt.Initializer))
                        yield return expression;
                }
                foreach (var expression in EnumerateOptionalExpression(forStmt.Condition))
                    yield return expression;
                if (forStmt.Update != null)
                {
                    foreach (var expression in EnumerateExpressions(forStmt.Update))
                        yield return expression;
                }
                foreach (var expression in EnumerateExpressions(forStmt.Body))
                    yield return expression;
                yield break;

            case SwitchStmt switchStmt:
                foreach (var expression in EnumerateExpressions(switchStmt.Expression))
                    yield return expression;
                foreach (var switchCase in switchStmt.Cases)
                {
                    foreach (var expression in EnumerateExpressions(switchCase.Value))
                        yield return expression;
                    foreach (var expression in EnumerateExpressions(switchCase.Body))
                        yield return expression;
                }
                foreach (var expression in EnumerateExpressions(switchStmt.ElseBody))
                    yield return expression;
                yield break;

            case MatchStmt matchStmt:
                foreach (var expression in EnumerateExpressions(matchStmt.Expression))
                    yield return expression;
                foreach (var matchCase in matchStmt.Cases)
                {
                    foreach (var expression in EnumerateExpressions(matchCase.Pattern))
                        yield return expression;
                    foreach (var expression in EnumerateExpressions(matchCase.Body))
                        yield return expression;
                }
                foreach (var expression in EnumerateExpressions(matchStmt.ElseBody))
                    yield return expression;
                yield break;

            case QualifiedMatchPattern qualifiedPattern:
                foreach (var expression in EnumerateExpressions(qualifiedPattern.Value))
                    yield return expression;
                yield break;

            case UnionMatchPattern unionPattern:
                foreach (var expression in EnumerateExpressions(unionPattern.Variant))
                    yield return expression;
                yield break;

            case StructLiteralField field:
                foreach (var expression in EnumerateExpressions(field.Value))
                    yield return expression;
                yield break;

            case StructNode structNode:
                foreach (var expression in EnumerateOptionalExpression(structNode.AlignExpr))
                    yield return expression;
                foreach (var field in structNode.Fields)
                {
                    foreach (var expression in EnumerateOptionalExpression(field.OffsetExpr))
                        yield return expression;
                }
                yield break;

            case EnumNode enumNode:
                foreach (var member in enumNode.Members)
                {
                    foreach (var expression in EnumerateOptionalExpression(member.Value))
                        yield return expression;
                }
                yield break;

            case AsmStatementNode asmStatement:
                foreach (var operand in asmStatement.Outputs.Concat(asmStatement.Inputs))
                {
                    foreach (var expression in EnumerateExpressions(operand.Expression))
                        yield return expression;
                }
                yield break;
        }
    }

    public static IEnumerable<Expr> EnumerateExpressions(IEnumerable<Statement> statements)
    {
        foreach (var statement in statements)
        {
            foreach (var expression in EnumerateExpressions(statement))
                yield return expression;
        }
    }

    public static IEnumerable<Expr> EnumerateExpressions(Expr root)
    {
        yield return root;

        switch (root)
        {
            case BinaryExpr binary:
                foreach (var expression in EnumerateExpressions(binary.Left))
                    yield return expression;
                foreach (var expression in EnumerateExpressions(binary.Right))
                    yield return expression;
                yield break;

            case CallExpr call:
                if (call.TargetExpr != null)
                {
                    foreach (var expression in EnumerateExpressions(call.TargetExpr))
                        yield return expression;
                }
                foreach (var argument in call.Args)
                {
                    foreach (var expression in EnumerateExpressions(argument))
                        yield return expression;
                }
                yield break;

            case UnaryExpr unary:
                foreach (var expression in EnumerateExpressions(unary.Operand))
                    yield return expression;
                yield break;

            case CastExpr cast:
                foreach (var expression in EnumerateExpressions(cast.Expr))
                    yield return expression;
                yield break;

            case IndexExpr index:
                foreach (var expression in EnumerateExpressions(index.Target))
                    yield return expression;
                foreach (var expression in EnumerateExpressions(index.Index))
                    yield return expression;
                yield break;

            case FieldExpr field:
                foreach (var expression in EnumerateExpressions(field.Target))
                    yield return expression;
                yield break;

            case StructLiteralExpr structLiteral:
                foreach (var field in structLiteral.Fields)
                {
                    foreach (var expression in EnumerateExpressions(field))
                        yield return expression;
                }
                yield break;

            case ArrayLiteralExpr arrayLiteral:
                foreach (var element in arrayLiteral.Elements)
                {
                    foreach (var expression in EnumerateExpressions(element))
                        yield return expression;
                }
                yield break;

            case CatchExpr catchExpr:
                foreach (var expression in EnumerateExpressions(catchExpr.Left))
                    yield return expression;
                foreach (var expression in EnumerateExpressions(catchExpr.CatchBody))
                    yield return expression;
                yield break;
        }
    }

    private static IEnumerable<Expr> EnumerateOptionalExpression(Expr? expression)
    {
        if (expression == null)
            yield break;

        foreach (var descendant in EnumerateExpressions(expression))
            yield return descendant;
    }
}
