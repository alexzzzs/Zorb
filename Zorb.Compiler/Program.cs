using System.Diagnostics;
using System.IO;
using System.Reflection;
using Zorb.Compiler.AST;
using Zorb.Compiler.Codegen;
using Zorb.Compiler.Lexer;
using Zorb.Compiler.Parser;
using Zorb.Compiler.Parsing;
using Zorb.Compiler.Semantic;
using Zorb.Compiler.Utils;

class Program
{
    private const string HostLinuxCompileFlags = "-O2 -nostdlib -fno-pie -no-pie -z execstack -fno-builtin";
    private const string RunTimeoutSeconds = "30s";

    private enum CommandMode
    {
        EmitC,
        Build,
        Run
    }

    private sealed class Options
    {
        public CommandMode Mode { get; set; } = CommandMode.EmitC;
        public string InputPath { get; set; } = "";
        public string OutputPath { get; set; } = "out.c";
        public string? KeepCPath { get; set; }
        public bool CheckOnly { get; set; }
        public bool EmitC { get; set; } = true;
        public bool DumpTokens { get; set; }
        public bool NoStdLib { get; set; }
        public bool ShowHelp { get; set; }
        public bool ShowVersion { get; set; }
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

            var compilation = CompileInput(options);
            if (compilation == null)
                return 1;

            if (options.CheckOnly)
            {
                Console.WriteLine($"Check succeeded: {compilation.InputPath}");
                return 0;
            }

            return options.Mode switch
            {
                CommandMode.EmitC => EmitCOutput(compilation.GeneratedCode, options.OutputPath),
                CommandMode.Build => BuildExecutable(compilation.InputPath, compilation.GeneratedCode, options.OutputPath, options.KeepCPath),
                CommandMode.Run => RunExecutable(compilation.InputPath, compilation.GeneratedCode, options.KeepCPath),
                _ => 1
            };
        }
        catch (ZorbCompilerException)
        {
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("--- INTERNAL COMPILER ERROR (BUG) ---");
            Console.WriteLine(ex);
            return 1;
        }
    }

    private static CompiledProgram? CompileInput(Options options)
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
        typeChecker.Check(ast, currentDir);
        if (typeChecker.Errors.Errors.Count > 0)
        {
            Console.Error.WriteLine("Semantic check failed.");
            typeChecker.Errors.ReportAll();
            return null;
        }

        try
        {
            var generator = new CGenerator(currentDir, typeChecker.SymbolTable)
            {
                PreserveStart = options.NoStdLib,
                NoStdLib = options.NoStdLib
            };

            return new CompiledProgram(
                inputPath,
                currentDir,
                ast,
                typeChecker,
                generator.Generate(ast));
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

    private static int BuildExecutable(string inputPath, string cCode, string outputPath, string? keepCPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("Build currently supports Linux hosts only.");
            return 1;
        }

        EnsureToolAvailable("gcc");

        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath) ?? Directory.GetCurrentDirectory());

        var cSourcePath = keepCPath != null
            ? Path.GetFullPath(keepCPath)
            : Path.Combine(Path.GetDirectoryName(fullOutputPath) ?? Directory.GetCurrentDirectory(), Path.GetFileNameWithoutExtension(fullOutputPath) + ".c");

        Directory.CreateDirectory(Path.GetDirectoryName(cSourcePath) ?? Directory.GetCurrentDirectory());
        File.WriteAllText(cSourcePath, cCode);

        var compile = RunProcess(
            "gcc",
            $"{HostLinuxCompileFlags} \"{cSourcePath}\" -o \"{fullOutputPath}\"",
            Path.GetDirectoryName(cSourcePath) ?? Directory.GetCurrentDirectory());

        if (compile.ExitCode != 0)
        {
            Console.Error.WriteLine("Native build failed.");
            if (!string.IsNullOrWhiteSpace(compile.StdErr))
                Console.Error.Write(compile.StdErr);
            if (!string.IsNullOrWhiteSpace(compile.StdOut))
                Console.Error.Write(compile.StdOut);
            return compile.ExitCode;
        }

        Console.WriteLine($"Executable built at {fullOutputPath}");
        Console.WriteLine($"Intermediate C written to {cSourcePath}");
        return 0;
    }

    private static int RunExecutable(string inputPath, string cCode, string? keepCPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("Run currently supports Linux hosts only.");
            return 1;
        }

        EnsureToolAvailable("gcc");
        EnsureToolAvailable("timeout");

        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "zorb-run",
            Path.GetFileNameWithoutExtension(inputPath) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var binaryPath = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(inputPath));
            var cSourcePath = keepCPath != null ? Path.GetFullPath(keepCPath) : Path.Combine(tempDir, "out.c");

            Directory.CreateDirectory(Path.GetDirectoryName(cSourcePath) ?? tempDir);
            File.WriteAllText(cSourcePath, cCode);

            var compile = RunProcess(
                "gcc",
                $"{HostLinuxCompileFlags} \"{cSourcePath}\" -o \"{binaryPath}\"",
                Path.GetDirectoryName(cSourcePath) ?? tempDir);

            if (compile.ExitCode != 0)
            {
                Console.Error.WriteLine("Native build failed.");
                if (!string.IsNullOrWhiteSpace(compile.StdErr))
                    Console.Error.Write(compile.StdErr);
                if (!string.IsNullOrWhiteSpace(compile.StdOut))
                    Console.Error.Write(compile.StdOut);
                return compile.ExitCode;
            }

            var execution = RunProcess("timeout", $"{RunTimeoutSeconds} \"{binaryPath}\"", tempDir);
            if (!string.IsNullOrEmpty(execution.StdOut))
                Console.Write(execution.StdOut);
            if (!string.IsNullOrEmpty(execution.StdErr))
                Console.Error.Write(execution.StdErr);

            return execution.ExitCode;
        }
        finally
        {
            if (keepCPath == null && Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void EnsureToolAvailable(string toolName)
    {
        var check = RunProcess("which", toolName, Directory.GetCurrentDirectory());
        if (check.ExitCode != 0 || string.IsNullOrWhiteSpace(check.StdOut))
            throw new ZorbCompilerException($"Required tool '{toolName}' was not found in PATH.");
    }

    private static ProcessResult RunProcess(string fileName, string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo) ?? throw new Exception($"Failed to start process '{fileName}'.");
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdOut, stdErr);
    }

    private static Options? ParseArgs(string[] args)
    {
        var options = new Options();
        int i = 0;

        if (args.Length > 0)
        {
            if (string.Equals(args[0], "build", StringComparison.Ordinal))
            {
                options.Mode = CommandMode.Build;
                options.NoStdLib = true;
                options.OutputPath = "out";
                i = 1;
            }
            else if (string.Equals(args[0], "run", StringComparison.Ordinal))
            {
                options.Mode = CommandMode.Run;
                options.NoStdLib = true;
                options.OutputPath = "out";
                i = 1;
            }
        }

        for (; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    options.ShowHelp = true;
                    return options;

                case "--version":
                    options.ShowVersion = true;
                    return options;

                case "-nostdlib":
                    options.NoStdLib = true;
                    break;

                case "--check":
                    options.CheckOnly = true;
                    options.EmitC = false;
                    break;

                case "--emit-c":
                    options.EmitC = true;
                    break;

                case "--dump-tokens":
                    options.DumpTokens = true;
                    break;

                case "--keep-c":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Missing value for --keep-c.");
                        PrintUsage();
                        return null;
                    }
                    options.KeepCPath = args[++i];
                    break;

                case "-o":
                case "--output":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine($"Missing value for {arg}.");
                        PrintUsage();
                        return null;
                    }
                    options.OutputPath = args[++i];
                    break;

                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"Unknown option: {arg}");
                        PrintUsage();
                        return null;
                    }

                    if (!string.IsNullOrEmpty(options.InputPath))
                    {
                        Console.Error.WriteLine($"Unexpected extra input path: {arg}");
                        PrintUsage();
                        return null;
                    }

                    options.InputPath = arg;
                    break;
            }
        }

        if (string.IsNullOrEmpty(options.InputPath))
        {
            Console.Error.WriteLine("Missing input file.");
            PrintUsage();
            return null;
        }

        return options;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  Zorb.Compiler <input-file> [options]");
        Console.WriteLine("  Zorb.Compiler build <input-file> [options]");
        Console.WriteLine("  Zorb.Compiler run <input-file> [options]");
        Console.WriteLine("Options:");
        Console.WriteLine("  --check              Run parse and semantic checks only.");
        Console.WriteLine("  --dump-tokens        Print the token stream before parsing.");
        Console.WriteLine("  --emit-c             Emit C output (default unless --check is used).");
        Console.WriteLine("  -o, --output <path>  Write generated C or built binary to the given path.");
        Console.WriteLine("  --keep-c <path>      Keep the generated C file when using build or run.");
        Console.WriteLine("  -nostdlib            Preserve _start and generate no-stdlib output.");
        Console.WriteLine("  -h, --help           Show this help text.");
        Console.WriteLine("  --version            Show the compiler version.");
    }

    private static void DumpTokens(List<Token> tokens)
    {
        foreach (var token in tokens)
        {
            var text = token.Value.Length == 0
                ? token.Type.ToString()
                : $"{token.Type} '{EscapeForDisplay(token.Value)}'";
            Console.WriteLine($"{token.Line}:{token.Column} {text}");
        }
    }

    private static string EscapeForDisplay(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\0", "\\0", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
    }

    private static string GetCompilerVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
            return informationalVersion;

        return assembly.GetName().Version?.ToString() ?? "0.0.0-dev";
    }

    private sealed record CompiledProgram(
        string InputPath,
        string WorkingDirectory,
        List<Node> Ast,
        TypeChecker TypeChecker,
        string GeneratedCode);

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
