using System.IO;
using Zorb.Compiler.AST;
using Zorb.Compiler.Codegen;
using Zorb.Compiler.Lexer;
using Zorb.Compiler.Parser;
using Zorb.Compiler.Parsing;
using Zorb.Compiler.Semantic;
using Zorb.Compiler.Utils;

partial class Program
{
    private const int RunTimeoutMilliseconds = 30_000;
    private const string LlvmBackendEnvironmentVariable = "ZORB_LLVM_BACKEND";
    private const string BareMetalLinkerEnvironmentVariable = "ZORB_LLD";
    private const string LlvmBackendExecutableName = "zorb-llvm-backend";
    private const string BareMetalLinkerExecutableName = "ld.lld";
    private const string BareMetalX86_64DefaultLinkerScript = """
OUTPUT_FORMAT(elf64-x86-64)
OUTPUT_ARCH(i386:x86-64)
ENTRY(_start)

SECTIONS
{
    . = 1M;

    .text ALIGN(4K) :
    {
        *(.text .text.*)
    }

    .rodata ALIGN(4K) :
    {
        *(.rodata .rodata.*)
    }

    .data ALIGN(4K) :
    {
        *(.data .data.*)
    }

    .bss ALIGN(4K) :
    {
        *(COMMON)
        *(.bss .bss.*)
    }

    /DISCARD/ :
    {
        *(.comment)
        *(.eh_frame)
        *(.note .note.*)
    }
}
""";

    private enum CommandMode
    {
        EmitLlvmIr,
        Build,
        Run
    }

    private enum CompilationTarget
    {
        HostLinux,
        FreestandingLinux,
        HostLinuxAArch64,
        FreestandingLinuxAArch64,
        BareMetalX86_64,
        HostWindows
    }

    private sealed class Options
    {
        public CommandMode Mode { get; set; } = CommandMode.EmitLlvmIr;
        public CompilationTarget? Target { get; set; }
        public string InputPath { get; set; } = "";
        public string OutputPath { get; set; } = "out.ll";
        public string? LinkerScriptPath { get; set; }
        public string? EmitLinkerScriptPath { get; set; }
        public string NativeFlags { get; set; } = "";
        public bool CheckOnly { get; set; }
        public bool DumpTokens { get; set; }
        public bool LegacyNoStdLib { get; set; }
        public bool ShowHelp { get; set; }
        public bool ShowVersion { get; set; }
        public bool OutputPathExplicitlySet { get; set; }
    }

    static int Main(string[] args)
    {
        try
        {
            var options = ParseArgs(args);
            if (options == null)
                return 1;

            if (options.ShowHelp)
            {
                PrintUsage();
                return 0;
            }

            if (options.ShowVersion)
            {
                Console.WriteLine($"Zorb.Compiler {GetCompilerVersion()}");
                return 0;
            }

            var target = ResolveCompilationTarget(options);
            if (!ValidateTargetSpecificOptions(options, target))
                return 1;
            EnsureTargetSupportedForCurrentHost(options.Mode, target);

            var outputPath = ResolveOutputPath(options, target);
            var compilation = CompileInput(options, target, outputPath);
            if (compilation == null)
                return 1;

            if (options.CheckOnly)
            {
                Console.WriteLine($"Check succeeded: {compilation.InputPath}");
                return 0;
            }

            return options.Mode switch
            {
                CommandMode.EmitLlvmIr => EmitLlvmOutput(compilation.GeneratedCode, outputPath),
                CommandMode.Build => BuildExecutableFromBackend(compilation.GeneratedCode, outputPath, options.LinkerScriptPath, options.EmitLinkerScriptPath, options.NativeFlags, target),
                CommandMode.Run => RunExecutableFromBackend(compilation.InputPath, compilation.GeneratedCode, options.NativeFlags, target),
                _ => 1
            };
        }
        catch (ZorbCompilerException ex)
        {
            if (!string.IsNullOrWhiteSpace(ex.Message))
                Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("--- INTERNAL COMPILER ERROR (BUG) ---");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static CompiledProgram? CompileInput(Options options, CompilationTarget target, string outputPath)
    {
        var inputPath = Path.GetFullPath(options.InputPath);
        var currentDir = Path.GetDirectoryName(inputPath) ?? ".";

        if (options.DumpTokens)
            DumpTokensForFile(inputPath);

        var parseResult = ImportGraphParser.ParseWithImports(inputPath);
        if (parseResult.Errors.Count > 0)
        {
            Console.Error.WriteLine("Parse failed.");
            foreach (var error in parseResult.Errors)
                Console.Error.WriteLine(error);
            return null;
        }

        var ast = parseResult.EntryNodes;

        var typeChecker = new TypeChecker();
        typeChecker.Check(ast, currentDir, parseResult.Files);

        if (typeChecker.Errors.Errors.Count > 0)
        {
            Console.Error.WriteLine("Semantic check failed.");
            typeChecker.Errors.ReportErrors();
            return null;
        }

        try
        {
            string generatedCode;
            var backendNodes = parseResult.Files.Values
                .SelectMany(nodes => nodes)
                .Distinct<Node>(ReferenceEqualityComparer.Instance)
                .ToList();
            generatedCode = new ZigBackendIrWriter(typeChecker).Write(
                backendNodes,
                Path.GetFileNameWithoutExtension(inputPath),
                GetZigBackendTarget(target),
                options.Mode == CommandMode.EmitLlvmIr
                    ? ZigBackendOutputKind.LlvmIr
                    : ZigBackendOutputKind.Object,
                Path.GetFullPath(outputPath),
                addFreestandingEntryShim: target is CompilationTarget.FreestandingLinux or CompilationTarget.FreestandingLinuxAArch64 or CompilationTarget.BareMetalX86_64,
                addHostedEntryShim: target is CompilationTarget.HostLinux or CompilationTarget.HostLinuxAArch64 or CompilationTarget.HostWindows);

            return new CompiledProgram(
                inputPath,
                currentDir,
                ast,
                typeChecker,
                generatedCode);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Code generation failed.");
            Console.Error.WriteLine(ex.Message);
            return null;
        }
    }

    private static void DumpTokensForFile(string inputPath)
    {
        var source = File.ReadAllText(inputPath);
        var lexer = new Lexer(source, inputPath);
        var tokens = lexer.Tokenize();
        DumpTokens(tokens);
    }

    private static int EmitLlvmOutput(string backendIr, string outputPath)
    {
        var backendPath = ResolveZigBackendExecutable();
        var tempDir = CreateTempWorkDir("zorb-llvm-ir", Path.GetFileNameWithoutExtension(outputPath));
        try
        {
            var backendIrPath = Path.Combine(tempDir, "module.json");
            File.WriteAllText(backendIrPath, backendIr);
            var process = RunProcess(backendPath, [backendIrPath], Directory.GetCurrentDirectory());
            if (process.ExitCode != 0)
                return ReportFailedProcess("LLVM backend failed.", process);

            Console.WriteLine($"LLVM IR generated to {Path.GetFullPath(outputPath)}");
            return 0;
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string ResolveZigBackendExecutable()
    {
        return ResolvePackagedTool(
            LlvmBackendEnvironmentVariable,
            LlvmBackendExecutableName,
            Path.Combine("Zorb.LlvmBackend", "zig-out", "bin"),
            "Build Zorb.LlvmBackend with Zig 0.16 or reinstall the compiler package.");
    }

    private sealed record CompiledProgram(
        string InputPath,
        string WorkingDirectory,
        List<Node> Ast,
        TypeChecker TypeChecker,
        string GeneratedCode);

    private sealed record ResolvedLinkerScript(string Path, string Content);

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
