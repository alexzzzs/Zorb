using Zorb.Compiler.AST;
using Zorb.Compiler.AST.Expressions;
using Zorb.Compiler.AST.Statements;
using Zorb.Compiler.Utils;

namespace Zorb.Compiler.Codegen;

public sealed partial class ZigBackendIrWriter
{
    private static void AddEntryShim(
        List<FunctionDecl> functions,
        ZigBackendTarget target,
        bool addFreestandingEntryShim,
        bool addHostedEntryShim)
    {
        var main = functions.FirstOrDefault(function =>
            function.NamespacePath.Count == 0 && function.Name == "main");
        var start = functions.FirstOrDefault(function =>
            function.NamespacePath.Count == 0 && function.Name == "_start");

        if (addFreestandingEntryShim && start == null && main != null)
        {
            var exitSyscallNumber = GetFreestandingExitSyscallNumber(target);
            var mainType = FunctionType(main);
            var callMain = new CallExpr
            {
                Name = "main",
                ResolvedTargetQualifiedName = "main",
                ResolvedFunctionType = mainType
            };
            Expr exitCode = main.ReturnType.Name == "void"
                ? new NumberExpr { Value = 0 }
                : callMain;
            var exitNumber = new NumberExpr { Value = exitSyscallNumber };
            functions.Add(new FunctionDecl
            {
                Name = "_start",
                ReturnType = new TypeNode { Name = "void" },
                Body = new List<Statement>
                {
                    main.ReturnType.Name == "void"
                        ? new ExpressionStatement { Expression = callMain }
                        : new ExpressionStatement
                        {
                            Expression = new CallExpr
                            {
                                Name = "syscall",
                                Args = new List<Expr> { exitNumber, exitCode },
                                ResolvedTargetQualifiedName = "syscall",
                                ResolvedFunctionType = SyscallType()
                            }
                        },
                    main.ReturnType.Name == "void"
                        ? new ExpressionStatement
                        {
                            Expression = new CallExpr
                            {
                                Name = "syscall",
                                Args = new List<Expr> { exitNumber, exitCode },
                                ResolvedTargetQualifiedName = "syscall",
                                ResolvedFunctionType = SyscallType()
                            }
                        }
                        : new ReturnNode()
                }
            });
            return;
        }

        if (addHostedEntryShim && main == null && start != null)
        {
            var renamedStart = new FunctionDecl
            {
                File = start.File,
                Line = start.Line,
                Column = start.Column,
                Length = start.Length,
                IsExported = start.IsExported,
                NamespacePath = new List<string>(start.NamespacePath),
                Name = "__zorb_user_start",
                Parameters = start.Parameters,
                ReturnType = start.ReturnType,
                Body = start.Body,
                IsExtern = start.IsExtern,
                Attributes = start.Attributes,
                AlignExpr = start.AlignExpr
            };
            functions[functions.IndexOf(start)] = renamedStart;
            functions.Add(new FunctionDecl
            {
                Name = "main",
                ReturnType = new TypeNode { Name = "i32" },
                Body = new List<Statement>
                {
                    new ExpressionStatement
                    {
                        Expression = new CallExpr
                        {
                            Name = "__zorb_user_start",
                            ResolvedTargetQualifiedName = "__zorb_user_start",
                            ResolvedFunctionType = FunctionType(renamedStart)
                        }
                    },
                    new ReturnNode { Value = new NumberExpr { Value = 0 } }
                }
            });
        }
    }

    private static TypeNode FunctionType(FunctionDecl function)
    {
        return new TypeNode
        {
            Name = "fn",
            IsFunction = true,
            ParamTypes = function.Parameters.Select(parameter => parameter.TypeName.Clone()).ToList(),
            ReturnType = function.ReturnType.Clone()
        };
    }

    private static TypeNode SyscallType()
    {
        return new TypeNode
        {
            Name = "fn",
            IsFunction = true,
            ParamTypes = Enumerable.Repeat(new TypeNode { Name = "i64" }, 7).ToList(),
            ReturnType = new TypeNode { Name = "i64" }
        };
    }

    private static long GetFreestandingExitSyscallNumber(ZigBackendTarget target)
    {
        return target.Triple switch
        {
            "x86_64-pc-linux-gnu" => 60,
            "aarch64-unknown-linux-gnu" => 93,
            _ => throw new ZorbCompilerException(
                $"Freestanding entry shim does not support target triple '{target.Triple}'.")
        };
    }
}
