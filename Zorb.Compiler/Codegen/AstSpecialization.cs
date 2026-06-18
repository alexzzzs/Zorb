using Zorb.Compiler.AST;
using Zorb.Compiler.AST.Expressions;
using Zorb.Compiler.AST.Statements;

namespace Zorb.Compiler.Codegen;

internal static class AstSpecialization
{
    internal static Dictionary<string, TypeNode> BuildTypeSubstitutions(
        IReadOnlyList<string> parameters,
        IReadOnlyList<TypeNode> arguments)
    {
        var substitutions = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        for (var index = 0; index < parameters.Count; index++)
            substitutions[parameters[index]] = arguments[index];
        return substitutions;
    }

    internal static TypeNode SubstituteTypeParameters(
        TypeNode type,
        IReadOnlyDictionary<string, TypeNode> substitutions)
    {
        if (type.TypeArguments.Count == 0 &&
            !type.IsPointer &&
            !type.IsSlice &&
            type.ArraySize == null &&
            !type.IsErrorUnion &&
            !type.IsFunction &&
            substitutions.TryGetValue(type.Name, out var substituted))
        {
            return substituted.Clone();
        }

        var clone = type.Clone();
        if (substitutions.TryGetValue(type.Name, out substituted))
        {
            clone.Name = substituted.Name;
            clone.NamespacePath = new List<string>(substituted.NamespacePath);
            clone.TypeArguments = substituted.TypeArguments
                .Select(argument => argument.Clone())
                .ToList();
            clone.IsAliasQualifiedReference = substituted.IsAliasQualifiedReference;
        }

        clone.TypeArguments = clone.TypeArguments
            .Select(argument => SubstituteTypeParameters(argument, substitutions))
            .ToList();
        if (clone.ErrorInnerType != null)
            clone.ErrorInnerType = SubstituteTypeParameters(clone.ErrorInnerType, substitutions);
        if (clone.IsFunction)
        {
            clone.ParamTypes = clone.ParamTypes
                .Select(parameter => SubstituteTypeParameters(parameter, substitutions))
                .ToList();
            if (clone.ReturnType != null)
                clone.ReturnType = SubstituteTypeParameters(clone.ReturnType, substitutions);
        }

        return clone;
    }

    internal static FunctionDecl InstantiateFunction(
        FunctionDecl function,
        IReadOnlyDictionary<string, TypeNode> substitutions)
    {
        return new FunctionDecl
        {
            File = function.File,
            Line = function.Line,
            Column = function.Column,
            Length = function.Length,
            IsExported = function.IsExported,
            NamespacePath = new List<string>(function.NamespacePath),
            Name = function.Name,
            Parameters = function.Parameters
                .Select(parameter => new Parameter(
                    parameter.Name,
                    SubstituteTypeParameters(parameter.TypeName, substitutions)))
                .ToList(),
            ReturnType = SubstituteTypeParameters(function.ReturnType, substitutions),
            Body = function.Body
                .Select(statement => CloneStatement(statement, substitutions))
                .ToList(),
            IsExtern = function.IsExtern,
            Attributes = new List<string>(function.Attributes),
            AlignExpr = function.AlignExpr
        };
    }

    private static Statement CloneStatement(
        Statement statement,
        IReadOnlyDictionary<string, TypeNode> substitutions)
    {
        switch (statement)
        {
            case VariableDeclarationNode variableDeclaration:
                return CopyNode(variableDeclaration, new VariableDeclarationNode
                {
                    IsExported = variableDeclaration.IsExported,
                    Name = variableDeclaration.Name,
                    TypeName = SubstituteTypeParameters(variableDeclaration.TypeName, substitutions),
                    Value = variableDeclaration.Value != null ? CloneExpr(variableDeclaration.Value, substitutions) : null,
                    Attributes = new List<string>(variableDeclaration.Attributes),
                    IsConst = variableDeclaration.IsConst,
                    AlignExpr = variableDeclaration.AlignExpr
                });
            case ExpressionStatement expressionStatement:
                return CopyNode(expressionStatement, new ExpressionStatement
                {
                    Expression = CloneExpr(expressionStatement.Expression, substitutions)
                });
            case AssignStmt assign:
                return CopyNode(assign, new AssignStmt
                {
                    Target = CloneExpr(assign.Target, substitutions),
                    Value = CloneExpr(assign.Value, substitutions)
                });
            case ReturnNode returnNode:
                return CopyNode(returnNode, new ReturnNode
                {
                    Value = returnNode.Value != null ? CloneExpr(returnNode.Value, substitutions) : null
                });
            case IfStmt ifStatement:
                return CopyNode(ifStatement, new IfStmt
                {
                    Condition = CloneExpr(ifStatement.Condition, substitutions),
                    Body = ifStatement.Body.Select(statement => CloneStatement(statement, substitutions)).ToList(),
                    ElseBody = ifStatement.ElseBody.Select(statement => CloneStatement(statement, substitutions)).ToList()
                });
            case WhileStmt whileStatement:
                return CopyNode(whileStatement, new WhileStmt
                {
                    Condition = CloneExpr(whileStatement.Condition, substitutions),
                    Body = whileStatement.Body.Select(statement => CloneStatement(statement, substitutions)).ToList()
                });
            case ForStmt forStatement:
                return CopyNode(forStatement, new ForStmt
                {
                    Initializer = forStatement.Initializer != null ? CloneStatement(forStatement.Initializer, substitutions) : null,
                    Condition = forStatement.Condition != null ? CloneExpr(forStatement.Condition, substitutions) : null,
                    Update = forStatement.Update != null ? CloneStatement(forStatement.Update, substitutions) : null,
                    Body = forStatement.Body.Select(statement => CloneStatement(statement, substitutions)).ToList()
                });
            case ContinueStmt continueStatement:
                return CopyNode(continueStatement, new ContinueStmt());
            case BreakStmt breakStatement:
                return CopyNode(breakStatement, new BreakStmt());
            case SwitchStmt switchStatement:
                return CopyNode(switchStatement, new SwitchStmt
                {
                    Expression = CloneExpr(switchStatement.Expression, substitutions),
                    Cases = switchStatement.Cases.Select(@case => new SwitchCase
                    {
                        Value = CloneExpr(@case.Value, substitutions),
                        Body = @case.Body.Select(statement => CloneStatement(statement, substitutions)).ToList()
                    }).ToList(),
                    ElseBody = switchStatement.ElseBody.Select(statement => CloneStatement(statement, substitutions)).ToList()
                });
            case MatchStmt matchStatement:
                return CopyNode(matchStatement, new MatchStmt
                {
                    Expression = CloneExpr(matchStatement.Expression, substitutions),
                    Cases = matchStatement.Cases.Select(@case => new MatchCase
                    {
                        Pattern = @case.Pattern,
                        Body = @case.Body.Select(statement => CloneStatement(statement, substitutions)).ToList()
                    }).ToList(),
                    ElseBody = matchStatement.ElseBody.Select(statement => CloneStatement(statement, substitutions)).ToList()
                });
            case AsmStatementNode asmStatement:
                return CopyNode(asmStatement, new AsmStatementNode
                {
                    Code = new List<string>(asmStatement.Code),
                    Outputs = asmStatement.Outputs.Select(operand => new AsmOperand
                    {
                        Constraint = operand.Constraint,
                        Expression = CloneExpr(operand.Expression, substitutions)
                    }).ToList(),
                    Inputs = asmStatement.Inputs.Select(operand => new AsmOperand
                    {
                        Constraint = operand.Constraint,
                        Expression = CloneExpr(operand.Expression, substitutions)
                    }).ToList(),
                    Clobbers = new List<string>(asmStatement.Clobbers)
                });
            default:
                return statement;
        }
    }

    private static Expr CloneExpr(
        Expr expression,
        IReadOnlyDictionary<string, TypeNode> substitutions)
    {
        switch (expression)
        {
            case NumberExpr number:
                return CopyNode(number, new NumberExpr { Value = number.Value });
            case StringExpr text:
                return CopyNode(text, new StringExpr { Value = text.Value });
            case IdentifierExpr identifier:
                return CopyNode(identifier, new IdentifierExpr { Name = identifier.Name });
            case TypeReferenceExpr typeReference:
                return CopyNode(typeReference, new TypeReferenceExpr
                {
                    TypeName = SubstituteTypeParameters(typeReference.TypeName, substitutions)
                });
            case BinaryExpr binary:
                return CopyNode(binary, new BinaryExpr
                {
                    Left = CloneExpr(binary.Left, substitutions),
                    Operator = binary.Operator,
                    Right = CloneExpr(binary.Right, substitutions)
                });
            case UnaryExpr unary:
                return CopyNode(unary, new UnaryExpr
                {
                    Operator = unary.Operator,
                    Operand = CloneExpr(unary.Operand, substitutions)
                });
            case CallExpr call:
                return CopyNode(call, new CallExpr
                {
                    NamespacePath = new List<string>(call.NamespacePath),
                    Name = call.Name,
                    TypeArguments = call.TypeArguments
                        .Select(argument => SubstituteTypeParameters(argument, substitutions))
                        .ToList(),
                    Args = call.Args.Select(argument => CloneExpr(argument, substitutions)).ToList(),
                    TargetExpr = call.TargetExpr != null ? CloneExpr(call.TargetExpr, substitutions) : null,
                    ResolvedQualifiedName = call.ResolvedQualifiedName,
                    ResolvedTargetQualifiedName = call.ResolvedTargetQualifiedName,
                    ResolvedFunctionType = call.ResolvedFunctionType?.Clone()
                });
            case FieldExpr field:
                return CopyNode(field, new FieldExpr
                {
                    Target = CloneExpr(field.Target, substitutions),
                    Field = field.Field,
                    ResolvedQualifiedName = field.ResolvedQualifiedName
                });
            case IndexExpr index:
                return CopyNode(index, new IndexExpr
                {
                    Target = CloneExpr(index.Target, substitutions),
                    Index = CloneExpr(index.Index, substitutions)
                });
            case CastExpr cast:
                return CopyNode(cast, new CastExpr
                {
                    TargetType = SubstituteTypeParameters(cast.TargetType, substitutions),
                    Expr = CloneExpr(cast.Expr, substitutions)
                });
            case SizeofExpr sizeofExpression:
                return CopyNode(sizeofExpression, new SizeofExpr
                {
                    TargetType = SubstituteTypeParameters(sizeofExpression.TargetType, substitutions)
                });
            case StructLiteralExpr structLiteral:
                return CopyNode(structLiteral, new StructLiteralExpr
                {
                    TypeName = SubstituteTypeParameters(structLiteral.TypeName, substitutions),
                    Fields = structLiteral.Fields.Select(field => CopyNode(field, new StructLiteralField
                    {
                        Name = field.Name,
                        Value = CloneExpr(field.Value, substitutions)
                    })).ToList()
                });
            case ArrayLiteralExpr arrayLiteral:
                return CopyNode(arrayLiteral, new ArrayLiteralExpr
                {
                    TypeName = SubstituteTypeParameters(arrayLiteral.TypeName, substitutions),
                    Elements = arrayLiteral.Elements.Select(element => CloneExpr(element, substitutions)).ToList()
                });
            case CatchExpr catchExpression:
                return CopyNode(catchExpression, new CatchExpr
                {
                    Left = CloneExpr(catchExpression.Left, substitutions),
                    ErrorVar = catchExpression.ErrorVar,
                    CatchBody = catchExpression.CatchBody.Select(statement => CloneStatement(statement, substitutions)).ToList()
                });
            default:
                return expression;
        }
    }

    private static T CopyNode<T>(Node source, T target)
        where T : Node
    {
        target.File = source.File;
        target.Line = source.Line;
        target.Column = source.Column;
        target.Length = source.Length;
        return target;
    }
}
