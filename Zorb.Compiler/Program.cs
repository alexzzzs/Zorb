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
    private const string BareMetalX86_64ObjectCompileFlags = "-O2 -ffreestanding -fno-pie -no-pie -fno-builtin -fno-stack-protector -m64 -mno-red-zone -c";
    private const string HostLinuxFreestandingCompileFlags = "-O2 -nostdlib -fno-pie -no-pie -z execstack -fno-builtin";
    private const string HostLinuxHostedCompileFlags = "-O2";
    private const int RunTimeoutMilliseconds = 30_000;
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
        EmitC,
        Build,
        Run
    }

    private enum CompilationTarget
    {
        HostLinux,
        FreestandingLinux,
        BareMetalX86_64,
        HostWindows
    }

    private sealed class Options
    {
        public CommandMode Mode { get; set; } = CommandMode.EmitC;
        public CompilationTarget? Target { get; set; }
        public string InputPath { get; set; } = "";
        public string OutputPath { get; set; } = "out.c";
        public string? KeepCPath { get; set; }
        public string? LinkerScriptPath { get; set; }
        public string? EmitLinkerScriptPath { get; set; }
        public bool CheckOnly { get; set; }
        public bool EmitC { get; set; } = true;
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
            var compilation = CompileInput(options, target);
            if (compilation == null)
                return 1;

            if (options.CheckOnly)
            {
                Console.WriteLine($"Check succeeded: {compilation.InputPath}");
                return 0;
            }

            return options.Mode switch
            {
                CommandMode.EmitC => EmitCOutput(compilation.GeneratedCode, outputPath),
                CommandMode.Build => BuildExecutable(compilation.InputPath, compilation.GeneratedCode, outputPath, options.KeepCPath, options.LinkerScriptPath, options.EmitLinkerScriptPath, target),
                CommandMode.Run => RunExecutable(compilation.InputPath, compilation.GeneratedCode, options.KeepCPath, target),
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
            Console.WriteLine("--- INTERNAL COMPILER ERROR (BUG) ---");
            Console.WriteLine(ex);
            return 1;
        }
    }

    private static CompiledProgram? CompileInput(Options options, CompilationTarget target)
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
            var generator = new CGenerator(currentDir, typeChecker.SymbolTable)
            {
                PreserveStart = PreservesStart(target),
                NoStdLib = UsesNoStdLib(target)
            };
            ConfigureGeneratorForTarget(generator, target);

            return new CompiledProgram(
                inputPath,
                currentDir,
                ast,
                typeChecker,
                generator.Generate(ast, parseResult.Files));
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

    private static int EmitCOutput(string cCode, string outputPath)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        File.WriteAllText(fullOutputPath, cCode);
        Console.WriteLine($"C code generated to {fullOutputPath}");
        return 0;
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
