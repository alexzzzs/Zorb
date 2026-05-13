using System.Collections.Generic;
using Zorb.Compiler.AST;
using Zorb.Compiler.AST.Expressions;
using Zorb.Compiler.AST.Statements;
using Zorb.Compiler.Lexer;

namespace Zorb.Compiler.Parser;

public partial class Parser
{
    private Statement ParseStatement()
    {
        if (Current.Type == TokenType.If)
            return ParseIf();

        if (Current.Type == TokenType.While)
            return ParseWhile();

        if (Current.Type == TokenType.For)
            return ParseFor();

        if (Current.Type == TokenType.Switch)
            return ParseSwitch();

        if (Current.Type == TokenType.Match)
            return ParseMatch();

        if (Current.Type == TokenType.Return)
            return ParseReturn();

        if (Current.Type == TokenType.Continue)
        {
            var stmt = new ContinueStmt();
            StampNode(stmt, Current);
            Advance();
            return stmt;
        }

        if (Current.Type == TokenType.Break)
        {
            var stmt = new BreakStmt();
            StampNode(stmt, Current);
            Advance();
            return stmt;
        }

        if (Current.Type == TokenType.Identifier && Current.Value == "asm")
            return ParseAsm();

        var attributes = ParseAttributes();

        if (Current.Type == TokenType.Const)
        {
            var varDecl = (VariableDeclarationNode)ParseVarDecl(false);
            ApplyVariableAttributes(varDecl, attributes);
            return varDecl;
        }

        if (Current.Type == TokenType.Identifier && Peek(1).Type == TokenType.Colon)
        {
            var varDecl = (VariableDeclarationNode)ParseVarDecl(false);
            ApplyVariableAttributes(varDecl, attributes);
            return varDecl;
        }

        if (Current.Type == TokenType.Identifier && (Peek(1).Type == TokenType.Equals || Peek(1).Type == TokenType.LBracket))
            return ParseAssignment();

        var expr = ParseExpression();

        if (Current.Type == TokenType.Equals)
        {
            Advance();
            var value = ParseExpression();
            var stmt = new AssignStmt { Target = expr, Value = value };
            StampNode(stmt, expr);
            return stmt;
        }

        var exprStmt = new ExpressionStatement { Expression = expr };
        StampNode(exprStmt, expr);
        return exprStmt;
    }

    private Statement ParseReturn()
    {
        var startToken = Expect(TokenType.Return);

        if (Current.Type == TokenType.Semicolon || Current.Type == TokenType.RBrace)
        {
            var emptyReturn = new ReturnNode { Value = null };
            StampNode(emptyReturn, startToken);
            return emptyReturn;
        }

        var value = ParseExpression();
        var node = new ReturnNode { Value = value };
        StampNode(node, startToken);
        return node;
    }

    private Statement ParseIf()
    {
        var startToken = Expect(TokenType.If);

        var condition = ParseExpression();

        Expect(TokenType.LBrace, "Expected '{' to start if body.");

        var body = new List<Statement>();

        while (Current.Type != TokenType.RBrace && Current.Type != TokenType.Eof)
            body.Add(ParseStatement());

        Expect(TokenType.RBrace, "Expected '}' to close if body.");

        var elseBody = new List<Statement>();
        if (Match(TokenType.Else))
        {
            if (Peek(1).Type == TokenType.LBrace)
            {
                Expect(TokenType.LBrace, "Expected '{' to start else body.");
                while (Current.Type != TokenType.RBrace && Current.Type != TokenType.Eof)
                    elseBody.Add(ParseStatement());
                Expect(TokenType.RBrace, "Expected '}' to close else body.");
            }
            else if (Current.Type == TokenType.If)
            {
                elseBody.Add(ParseIf());
            }
            else
            {
                Expect(TokenType.LBrace, "Expected '{' to start else body.");
                while (Current.Type != TokenType.RBrace && Current.Type != TokenType.Eof)
                    elseBody.Add(ParseStatement());
                Expect(TokenType.RBrace, "Expected '}' to close else body.");
            }
        }

        var stmt = new IfStmt { Condition = condition, Body = body, ElseBody = elseBody };
        StampNode(stmt, startToken);
        return stmt;
    }

    private Statement ParseWhile()
    {
        var startToken = Expect(TokenType.While);

        var condition = ParseExpression();

        var stmt = new WhileStmt
        {
            Condition = condition,
            Body = ParseStatementBlock("Expected '{' to start while body.", "Expected '}' to close while body.")
        };
        StampNode(stmt, startToken);
        return stmt;
    }

    private Statement ParseFor()
    {
        var startToken = Expect(TokenType.For);

        Statement? initializer = null;
        if (Current.Type != TokenType.Semicolon)
            initializer = ParseForClauseStatement();
        Expect(TokenType.Semicolon, "Expected ';' after for-loop initializer.");

        Expr? condition = null;
        if (Current.Type != TokenType.Semicolon)
            condition = ParseExpression();
        Expect(TokenType.Semicolon, "Expected ';' after for-loop condition.");

        Statement? update = null;
        if (Current.Type != TokenType.LBrace)
            update = ParseForClauseStatement();

        var stmt = new ForStmt
        {
            Initializer = initializer,
            Condition = condition,
            Update = update,
            Body = ParseStatementBlock("Expected '{' to start for-loop body.", "Expected '}' to close for-loop body.")
        };
        StampNode(stmt, startToken);
        return stmt;
    }

    private Statement ParseSwitch()
    {
        var startToken = Expect(TokenType.Switch);
        var expression = ParseExpression();
        Expect(TokenType.LBrace, "Expected '{' to start switch body.");

        var cases = new List<SwitchCase>();
        var elseBody = new List<Statement>();
        var sawElse = false;

        while (Current.Type != TokenType.RBrace && Current.Type != TokenType.Eof)
        {
            if (Match(TokenType.Case))
            {
                if (sawElse)
                {
                    ErrorReporter.Error("Switch case branches cannot appear after 'else'.", Current.Line, Current.Column, _fileName);
                    ParseExpression();
                    ParseStatementBlock("Expected '{' to start case body.", "Expected '}' to close case body.");
                    continue;
                }

                var caseValue = ParseExpression();
                var caseBody = ParseStatementBlock("Expected '{' to start case body.", "Expected '}' to close case body.");
                cases.Add(new SwitchCase { Value = caseValue, Body = caseBody });
                continue;
            }

            if (Match(TokenType.Else))
            {
                if (sawElse)
                {
                    ErrorReporter.Error("Switch statements may contain only one 'else' branch.", Current.Line, Current.Column, _fileName);
                    continue;
                }

                elseBody = ParseStatementBlock("Expected '{' to start switch else body.", "Expected '}' to close switch else body.");
                sawElse = true;
                continue;
            }

            ErrorReporter.Error(
                $"Expected 'case value {{ ... }}' or 'else {{ ... }}' in switch body, got {DescribeToken(Current)}.",
                Current.Line,
                Current.Column,
                _fileName);
            Advance();
        }

        Expect(TokenType.RBrace, "Expected '}' to close switch body.");

        var stmt = new SwitchStmt
        {
            Expression = expression,
            Cases = cases,
            ElseBody = elseBody
        };
        StampNode(stmt, startToken);
        return stmt;
    }

    private Statement ParseMatch()
    {
        var startToken = Expect(TokenType.Match);
        var expression = ParseExpression();
        Expect(TokenType.LBrace, "Expected '{' to start match body.");

        var cases = new List<MatchCase>();
        var elseBody = new List<Statement>();
        var sawElse = false;

        while (Current.Type != TokenType.RBrace && Current.Type != TokenType.Eof)
        {
            if (Match(TokenType.Case))
            {
                if (sawElse)
                {
                    ErrorReporter.Error("Match case branches cannot appear after 'else'.", Current.Line, Current.Column, _fileName);
                    ParseMatchPattern();
                    ParseStatementBlock("Expected '{' to start case body.", "Expected '}' to close case body.");
                    continue;
                }

                var pattern = ParseMatchPattern();
                var caseBody = ParseStatementBlock("Expected '{' to start case body.", "Expected '}' to close case body.");
                cases.Add(new MatchCase { Pattern = pattern, Body = caseBody });
                continue;
            }

            if (Match(TokenType.Else))
            {
                if (sawElse)
                {
                    ErrorReporter.Error("Match statements may contain only one 'else' branch.", Current.Line, Current.Column, _fileName);
                    continue;
                }

                elseBody = ParseStatementBlock("Expected '{' to start match else body.", "Expected '}' to close match else body.");
                sawElse = true;
                continue;
            }

            ErrorReporter.Error(
                $"Expected 'case Pattern {{ ... }}' or 'else {{ ... }}' in match body, got {DescribeToken(Current)}.",
                Current.Line,
                Current.Column,
                _fileName);
            Advance();
        }

        Expect(TokenType.RBrace, "Expected '}' to close match body.");

        var stmt = new MatchStmt
        {
            Expression = expression,
            Cases = cases,
            ElseBody = elseBody
        };
        StampNode(stmt, startToken);
        return stmt;
    }

    private MatchPattern ParseMatchPattern()
    {
        var patternExpr = ParseQualifiedReferenceExpression(
            "Expected match pattern name.",
            "Expected identifier after '.'.");

        if (Current.Type == TokenType.LParen)
        {
            Advance();
            var bindingToken = Expect(TokenType.Identifier, "Expected payload binding name inside match pattern.");
            Expect(TokenType.RParen, "Expected ')' to close payload binding.");
            var pattern = new UnionMatchPattern
            {
                Variant = patternExpr,
                BindingName = bindingToken.Value
            };
            StampNode(pattern, patternExpr);
            return pattern;
        }

        if (patternExpr is FieldExpr)
        {
            var pattern = new UnionMatchPattern
            {
                Variant = patternExpr,
                BindingName = null
            };
            StampNode(pattern, patternExpr);
            return pattern;
        }

        var enumPattern = new EnumMatchPattern { Value = patternExpr };
        StampNode(enumPattern, patternExpr);
        return enumPattern;
    }

    private Expr ParseQualifiedReferenceExpression(string missingNameMessage, string missingSegmentMessage)
    {
        Expr expr = new IdentifierExpr { Name = Expect(TokenType.Identifier, missingNameMessage).Value };
        StampNode(expr, Previous);
        while (Match(TokenType.Dot))
        {
            var fieldToken = Expect(TokenType.Identifier, missingSegmentMessage);
            var field = new FieldExpr
            {
                Target = expr,
                Field = fieldToken.Value
            };
            StampNode(field, fieldToken);
            expr = field;
        }

        return expr;
    }

    private Statement ParseAssignment()
    {
        var target = ParsePostfix();
        Expect(TokenType.Equals);
        var value = ParseExpression();
        var stmt = new AssignStmt { Target = target, Value = value };
        StampNode(stmt, target);
        return stmt;
    }

    private Statement ParseForClauseStatement()
    {
        var attributes = ParseAttributes();

        if (Current.Type == TokenType.Const)
        {
            var varDecl = (VariableDeclarationNode)ParseVarDecl(false);
            ApplyVariableAttributes(varDecl, attributes);
            return varDecl;
        }

        if (Current.Type == TokenType.Identifier && Peek(1).Type == TokenType.Colon)
        {
            var varDecl = (VariableDeclarationNode)ParseVarDecl(false);
            ApplyVariableAttributes(varDecl, attributes);
            return varDecl;
        }

        if (attributes.Attributes.Count > 0 || attributes.AlignExpr != null)
            ErrorReporter.Error("Attributes in for-loop clauses are only supported on variable declarations.", Current.Line, Current.Column, _fileName);

        if (Current.Type == TokenType.Identifier && (Peek(1).Type == TokenType.Equals || Peek(1).Type == TokenType.LBracket))
            return ParseAssignment();

        var expr = ParseExpression();

        if (Current.Type == TokenType.Equals)
        {
            Advance();
            var value = ParseExpression();
            var stmt = new AssignStmt { Target = expr, Value = value };
            StampNode(stmt, expr);
            return stmt;
        }

        var exprStmt = new ExpressionStatement { Expression = expr };
        StampNode(exprStmt, expr);
        return exprStmt;
    }

    private List<Statement> ParseStatementBlock(string openMessage, string closeMessage)
    {
        Expect(TokenType.LBrace, openMessage);

        var body = new List<Statement>();
        while (Current.Type != TokenType.RBrace && Current.Type != TokenType.Eof)
            body.Add(ParseStatement());

        Expect(TokenType.RBrace, closeMessage);
        return body;
    }

    private List<AsmOperand> ParseAsmConstraints()
    {
        var constraints = new List<AsmOperand>();
        while (Current.Type == TokenType.String)
        {
            var constraint = Expect(TokenType.String).Value;
            Expect(TokenType.LParen);
            var expr = ParseExpression();
            Expect(TokenType.RParen);

            constraints.Add(new AsmOperand
            {
                Constraint = constraint,
                Expression = expr
            });

            if (Current.Type == TokenType.Comma)
            {
                Advance();
            }
            else
            {
                break;
            }
        }
        return constraints;
    }

    private Statement ParseAsm()
    {
        var startToken = Expect(TokenType.Identifier);
        Expect(TokenType.LBrace, "Expected '{' to start asm block.");

        var node = new AsmStatementNode();
        StampNode(node, startToken);

        while (Current.Type == TokenType.String)
            node.Code.Add(Expect(TokenType.String).Value);

        if (Current.Type == TokenType.Colon)
        {
            Advance();
            node.Outputs = ParseAsmConstraints();
        }

        if (Current.Type == TokenType.Colon)
        {
            Advance();
            node.Inputs = ParseAsmConstraints();
        }

        if (Current.Type == TokenType.Colon)
        {
            Advance();
            while (Current.Type == TokenType.String)
            {
                node.Clobbers.Add(Expect(TokenType.String).Value);
                if (Current.Type == TokenType.Comma)
                {
                    Advance();
                }
                else
                {
                    break;
                }
            }
        }

        Expect(TokenType.RBrace, "Expected '}' to close asm block.");
        return node;
    }

    private Statement ParseVarDecl(bool isConst = false)
    {
        var startToken = Current;
        if (Current.Type == TokenType.Const)
        {
            startToken = Advance();
            isConst = true;
        }

        var nameToken = Expect(TokenType.Identifier, isConst ? "Expected constant name after 'const'." : "Expected variable name.");
        if (!isConst)
            startToken = nameToken;
        var name = nameToken.Value;

        while (Match(TokenType.Dot))
            name += "." + Expect(TokenType.Identifier, "Expected identifier after '.' in variable name.").Value;

        Expect(TokenType.Colon, $"Expected ':' after declaration name '{name}'.");
        var typeName = ParseType();

        Expr? initializer = null;
        if (Current.Type == TokenType.Equals)
        {
            Advance();
            initializer = ParseExpression();
        }

        var node = new VariableDeclarationNode { Name = name, TypeName = typeName, Value = initializer, IsConst = isConst };
        StampNode(node, startToken);
        return node;
    }
}
