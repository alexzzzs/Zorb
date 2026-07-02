using Zorb.Compiler.AST;
using Zorb.Compiler.AST.Expressions;
using Zorb.Compiler.AST.Statements;
using Zorb.Compiler.Lexer;
using Zorb.Compiler.Parser;
using Zorb.Compiler.Semantic;

internal static partial class Program
{
    private static void RunTypeCheckerStateResetTests()
    {
        WithTempDirectory("zorb-typechecker-state", tempDir =>
        {
            var firstPath = Path.Combine(tempDir, "first.zorb");
            var secondPath = Path.Combine(tempDir, "second.zorb");

            File.WriteAllText(firstPath, """
const Error_First: i32 = 1

fn first() -> i32 {
    return Error_First
}
""");

            File.WriteAllText(secondPath, """
fn second() -> i32 {
    return missing_symbol
}
""");

            var checker = new TypeChecker();
            var firstParse = ParseFile(firstPath);
            checker.Check(firstParse.EntryNodes, tempDir, firstParse.Files);
            AssertNoErrors(firstParse.Errors);
            AssertNoErrors(checker.Errors.Errors);

            var secondParse = ParseFile(secondPath);
            checker.Check(secondParse.EntryNodes, tempDir, secondParse.Files);
            AssertNoErrors(secondParse.Errors);

            if (!checker.Errors.Errors.Any(error => error.Contains("Use of undeclared identifier 'missing_symbol'.", StringComparison.Ordinal)))
                throw new Exception($"Expected missing symbol diagnostic after reused checker reset.{Environment.NewLine}{string.Join(Environment.NewLine, checker.Errors.Errors)}");

            if (checker.Errors.Errors.Any(error => error.Contains("Error_First", StringComparison.Ordinal)))
                throw new Exception($"TypeChecker leaked diagnostics or symbols from the first check.{Environment.NewLine}{string.Join(Environment.NewLine, checker.Errors.Errors)}");
        });
    }

    private static void RunUnknownTypeCascadeTests()
    {
        WithTempDirectory("zorb-unknown-type-cascade", tempDir =>
        {
            var mainPath = Path.Combine(tempDir, "main.zorb");
            File.WriteAllText(mainPath, """
fn main() -> i64 {
    if missing.field {
        return 1
    }

    return 0
}
""");

            var compilation = CompileFixture(mainPath, tempDir);
            AssertPhase(compilation.Phase, FixturePhase.Semantic, compilation.FailureMessage);

            var errors = compilation.Checker.Errors.Errors;
            if (!errors.Any(error => error.Contains("Use of undeclared identifier 'missing'.", StringComparison.Ordinal)))
                throw new Exception($"Expected undeclared identifier diagnostic.{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");

            if (errors.Any(error => error.Contains("Condition must have type 'bool'", StringComparison.Ordinal)))
                throw new Exception($"Unexpected bool-condition follow-on diagnostic for unknown field target.{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        });
    }

    private static void RunInvalidPostfixCascadeTests()
    {
        WithTempDirectory("zorb-invalid-postfix-cascade", tempDir =>
        {
            var mainPath = Path.Combine(tempDir, "main.zorb");
            var source = """
fn main() -> i64 {
    return , .field(1)[0]
}
""";

            var lexer = new Lexer(source, mainPath);
            var parser = new Parser(lexer.Tokenize(), mainPath);
            var ast = parser.ParseProgram();

            if (parser.ErrorReporter.Errors.Count == 0)
                throw new Exception("Expected parser to report the invalid expression.");

            var checker = new TypeChecker();
            checker.Check(ast, tempDir);

            if (checker.Errors.Errors.Count != 0)
            {
                throw new Exception(
                    $"Expected no semantic follow-on diagnostics for invalid postfix target.{Environment.NewLine}{string.Join(Environment.NewLine, checker.Errors.Errors)}");
            }
        });
    }

    private static void RunBuiltinParserReservedDeclarationTests()
    {
        var compileErrorDecl = new FunctionDecl
        {
            NamespacePath = ["Builtin"],
            Name = "CompileError",
            ReturnType = new TypeNode { Name = "void" }
        };
        var sizeofDecl = new FunctionDecl
        {
            NamespacePath = ["Builtin"],
            Name = "sizeof",
            ReturnType = new TypeNode { Name = "i64" }
        };

        var checker = new TypeChecker();
        checker.Check([compileErrorDecl, sizeofDecl]);

        AssertContains(
            checker.Errors.Errors,
            "Top-level declaration 'Builtin.CompileError' conflicts with a built-in symbol.",
            "error");
        AssertContains(
            checker.Errors.Errors,
            "Top-level declaration 'Builtin.sizeof' conflicts with a built-in symbol.",
            "error");
    }

    private static void RunGenericDefaultArityRecoveryTests()
    {
        var pair = new StructNode
        {
            Name = "Pair",
            TypeParameters = ["T", "U"],
            TypeParameterSpecs =
            [
                new GenericTypeParameter
                {
                    Name = "T",
                    DefaultType = new TypeNode { Name = "i64" }
                },
                new GenericTypeParameter
                {
                    Name = "U"
                }
            ]
        };
        var value = new VariableDeclarationNode
        {
            Name = "value",
            TypeName = new TypeNode { Name = "Pair" }
        };

        var checker = new TypeChecker();
        checker.Check([pair, value]);

        AssertContains(
            checker.Errors.Errors,
            "Struct 'Pair' expects 2 type argument(s), got 0.",
            "generic arity");
    }

    private static void RunGenericFunctionDefaultImportAliasTests()
    {
        WithTempDirectory("zorb-generic-default-import-alias", tempDir =>
        {
            var mainPath = Path.Combine(tempDir, "main.zorb");
            var libPath = Path.Combine(tempDir, "lib.zorb");
            var typesPath = Path.Combine(tempDir, "types.zorb");

            File.WriteAllText(mainPath, """
import "lib.zorb" as factory

fn main() -> i64 {
    return factory.make()
}
""");

            File.WriteAllText(libPath, """
import "types.zorb" as util

export fn make<T = util.Box<i64>>() -> i64 {
    return Builtin.sizeof(T)
}
""");

            File.WriteAllText(typesPath, """
export struct Box<T> {
    value: T,
}
""");

            var compilation = CompileFixture(mainPath, tempDir);
            AssertPhase(compilation.Phase, FixturePhase.Success, compilation.FailureMessage);
            AssertNoErrors(compilation.ParseErrors);
            AssertNoErrors(compilation.Checker.Errors.Errors);
        });
    }

    private static void RunResolvedCallMetadataTests()
    {
        var testProjectRoot = FindAncestorContainingFile(AppContext.BaseDirectory, "Zorb.Compiler.Tests.csproj");
        var fixtureRoot = Path.Combine(testProjectRoot, "fixtures");

        AssertResolvedCallMetadata(
            Path.Combine(fixtureRoot, "import_alias_function_value"),
            expectedQualifiedName: "use",
            expectedTargetQualifiedName: null,
            expectedParamCount: 1);

        AssertResolvedCallMetadata(
            Path.Combine(fixtureRoot, "import_alias_callable_variable_call"),
            expectedQualifiedName: null,
            expectedTargetQualifiedName: "cb",
            expectedParamCount: 1);

        AssertResolvedCallMetadata(
            Path.Combine(fixtureRoot, "import_alias_qualified_call"),
            expectedQualifiedName: null,
            expectedTargetQualifiedName: "module.answer",
            expectedParamCount: 0);
    }

    private static void AssertResolvedCallMetadata(string fixtureDir, string? expectedQualifiedName, string? expectedTargetQualifiedName, int expectedParamCount)
    {
        var mainPath = Path.Combine(fixtureDir, "main.zorb");
        var compilation = CompileFixture(mainPath, fixtureDir);
        AssertPhase(compilation.Phase, FixturePhase.Success, compilation.FailureMessage);
        AssertNoErrors(compilation.ParseErrors);
        AssertNoErrors(compilation.Checker.Errors.Errors);

        var mainFunction = compilation.Ast.OfType<FunctionDecl>().FirstOrDefault(fn => string.Equals(fn.Name, "main", StringComparison.Ordinal))
            ?? throw new Exception($"Expected fixture '{fixtureDir}' to define a main function.");
        var call = FindFirstCallInStatements(mainFunction.Body)
            ?? throw new Exception($"Expected to find a call expression in main() for fixture '{fixtureDir}'.");

        if (!string.Equals(call.ResolvedQualifiedName, expectedQualifiedName, StringComparison.Ordinal))
            throw new Exception($"ResolvedQualifiedName mismatch. Expected '{expectedQualifiedName ?? "<null>"}', got '{call.ResolvedQualifiedName ?? "<null>"}'.");

        if (!string.Equals(call.ResolvedTargetQualifiedName, expectedTargetQualifiedName, StringComparison.Ordinal))
            throw new Exception($"ResolvedTargetQualifiedName mismatch. Expected '{expectedTargetQualifiedName ?? "<null>"}', got '{call.ResolvedTargetQualifiedName ?? "<null>"}'.");

        if (call.ResolvedFunctionType == null || !call.ResolvedFunctionType.IsFunction)
            throw new Exception("ResolvedFunctionType was not cached as a function type.");

        if (call.ResolvedFunctionType.ParamTypes.Count != expectedParamCount)
            throw new Exception($"ResolvedFunctionType cached unexpected parameter count. Expected {expectedParamCount}, got {call.ResolvedFunctionType.ParamTypes.Count}.");

        if (call.ResolvedFunctionType.ReturnType?.Name != "i64")
            throw new Exception("ResolvedFunctionType cached unexpected return type.");
    }

    private static CallExpr? FindFirstCallInNode(Node node)
    {
        switch (node)
        {
            case FunctionDecl fn:
                return FindFirstCallInStatements(fn.Body);
            case VariableDeclarationNode varDecl:
                return varDecl.Value != null ? FindFirstCallInExpr(varDecl.Value) : null;
            case ExpressionStatement exprStmt:
                return FindFirstCallInExpr(exprStmt.Expression);
            case AssignStmt assign:
                return FindFirstCallInExpr(assign.Target) ?? FindFirstCallInExpr(assign.Value);
            case ReturnNode returnNode:
                return returnNode.Value != null ? FindFirstCallInExpr(returnNode.Value) : null;
            case IfStmt ifStmt:
                return FindFirstCallInExpr(ifStmt.Condition) ?? FindFirstCallInStatements(ifStmt.Body) ?? FindFirstCallInStatements(ifStmt.ElseBody);
            case WhileStmt whileStmt:
                return FindFirstCallInExpr(whileStmt.Condition) ?? FindFirstCallInStatements(whileStmt.Body);
            case ForStmt forStmt:
                return (forStmt.Initializer != null ? FindFirstCallInStatement(forStmt.Initializer) : null)
                    ?? (forStmt.Condition != null ? FindFirstCallInExpr(forStmt.Condition) : null)
                    ?? (forStmt.Update != null ? FindFirstCallInStatement(forStmt.Update) : null)
                    ?? FindFirstCallInStatements(forStmt.Body);
            default:
                return null;
        }
    }

    private static CallExpr? FindFirstCallInStatement(Statement statement)
    {
        return FindFirstCallInStatements([statement]);
    }

    private static CallExpr? FindFirstCallInStatements(IEnumerable<Statement> statements)
    {
        foreach (var statement in statements)
        {
            if (FindFirstCallInNode(statement) is CallExpr call)
                return call;
        }

        return null;
    }

    private static CallExpr? FindFirstCallInExpr(Expr expr)
    {
        switch (expr)
        {
            case CallExpr call:
                return call;
            case BinaryExpr binary:
                return FindFirstCallInExpr(binary.Left) ?? FindFirstCallInExpr(binary.Right);
            case UnaryExpr unary:
                return FindFirstCallInExpr(unary.Operand);
            case CastExpr cast:
                return FindFirstCallInExpr(cast.Expr);
            case IndexExpr index:
                return FindFirstCallInExpr(index.Target) ?? FindFirstCallInExpr(index.Index);
            case FieldExpr field:
                return FindFirstCallInExpr(field.Target);
            case StructLiteralExpr structLiteral:
                return FindFirstCallInExprs(structLiteral.Fields.Select(field => field.Value));
            case ArrayLiteralExpr arrayLiteral:
                return FindFirstCallInExprs(arrayLiteral.Elements);
            case CatchExpr catchExpr:
                return FindFirstCallInExpr(catchExpr.Left) ?? FindFirstCallInStatements(catchExpr.CatchBody);
            default:
                return null;
        }
    }

    private static CallExpr? FindFirstCallInExprs(IEnumerable<Expr> expressions)
    {
        foreach (var expr in expressions)
        {
            if (FindFirstCallInExpr(expr) is CallExpr call)
                return call;
        }

        return null;
    }
}
