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
    private const string HostLinuxFreestandingCompileFlags = "-O2 -nostdlib -fno-pie -no-pie -z execstack -fno-builtin";
    private const string HostLinuxHostedCompileFlags = "-O2";
    private const int RunTimeoutMilliseconds = 30_000;

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
        public bool NoStdLibExplicitlySet { get; set; }
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

            var effectiveNoStdLib = ResolveNoStdLib(options);
            var outputPath = ResolveOutputPath(options);
            var compilation = CompileInput(options, effectiveNoStdLib);
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
                CommandMode.Build => BuildExecutable(compilation.InputPath, compilation.GeneratedCode, outputPath, options.KeepCPath, effectiveNoStdLib),
                CommandMode.Run => RunExecutable(compilation.InputPath, compilation.GeneratedCode, options.KeepCPath, effectiveNoStdLib),
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

    private static CompiledProgram? CompileInput(Options options, bool effectiveNoStdLib)
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
                PreserveStart = effectiveNoStdLib,
                NoStdLib = effectiveNoStdLib
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

    private static int BuildExecutable(string inputPath, string cCode, string outputPath, string? keepCPath, bool noStdLib)
    {
        if (OperatingSystem.IsLinux())
        {
            return BuildExecutableOnLinux(cCode, outputPath, keepCPath, noStdLib);
        }

        if (OperatingSystem.IsWindows())
        {
            if (noStdLib)
            {
                Console.Error.WriteLine("Windows build currently supports hosted output only. Omit -nostdlib.");
                return 1;
            }

            return BuildExecutableOnWindows(cCode, outputPath, keepCPath);
        }

        Console.Error.WriteLine("Build currently supports Linux and Windows hosts only.");
        return 1;
    }

    private static int BuildExecutableOnLinux(string cCode, string outputPath, string? keepCPath, bool noStdLib)
    {
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
            $"{GetLinuxCompileFlags(noStdLib)} \"{cSourcePath}\" -o \"{fullOutputPath}\"",
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

    private static int BuildExecutableOnWindows(string cCode, string outputPath, string? keepCPath)
    {
        var compiler = EnsureToolAvailable("clang-cl", "cl");
        var fullOutputPath = NormalizeWindowsExecutablePath(Path.GetFullPath(outputPath));
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath) ?? Directory.GetCurrentDirectory());

        var cSourcePath = keepCPath != null
            ? Path.GetFullPath(keepCPath)
            : Path.Combine(Path.GetDirectoryName(fullOutputPath) ?? Directory.GetCurrentDirectory(), Path.GetFileNameWithoutExtension(fullOutputPath) + ".c");

        Directory.CreateDirectory(Path.GetDirectoryName(cSourcePath) ?? Directory.GetCurrentDirectory());
        File.WriteAllText(cSourcePath, cCode);

        var compile = RunProcess(
            compiler,
            GetWindowsCompileArguments(compiler, cSourcePath, fullOutputPath),
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
        Console.WriteLine($"Windows toolchain: {compiler}");
        return 0;
    }

    private static int RunExecutable(string inputPath, string cCode, string? keepCPath, bool noStdLib)
    {
        if (OperatingSystem.IsLinux())
        {
            return RunExecutableOnLinux(inputPath, cCode, keepCPath, noStdLib);
        }

        if (OperatingSystem.IsWindows())
        {
            if (noStdLib)
            {
                Console.Error.WriteLine("Windows run currently supports hosted output only. Omit -nostdlib.");
                return 1;
            }

            return RunExecutableOnWindows(inputPath, cCode, keepCPath);
        }

        Console.Error.WriteLine("Run currently supports Linux and Windows hosts only.");
        return 1;
    }

    private static int RunExecutableOnLinux(string inputPath, string cCode, string? keepCPath, bool noStdLib)
    {
        EnsureToolAvailable("gcc");

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
                $"{GetLinuxCompileFlags(noStdLib)} \"{cSourcePath}\" -o \"{binaryPath}\"",
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

            var execution = RunProcessWithTimeout(binaryPath, "", tempDir, RunTimeoutMilliseconds);
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

    private static int RunExecutableOnWindows(string inputPath, string cCode, string? keepCPath)
    {
        var compiler = EnsureToolAvailable("clang-cl", "cl");

        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "zorb-run",
            Path.GetFileNameWithoutExtension(inputPath) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var binaryFileName = Path.GetFileNameWithoutExtension(inputPath) + ".exe";
            var binaryPath = Path.Combine(tempDir, binaryFileName);
            var cSourcePath = keepCPath != null ? Path.GetFullPath(keepCPath) : Path.Combine(tempDir, "out.c");

            Directory.CreateDirectory(Path.GetDirectoryName(cSourcePath) ?? tempDir);
            File.WriteAllText(cSourcePath, cCode);

            var compile = RunProcess(
                compiler,
                GetWindowsCompileArguments(compiler, cSourcePath, binaryPath),
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

            var execution = RunProcessWithTimeout(binaryPath, "", tempDir, RunTimeoutMilliseconds);
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

    private static string GetLinuxCompileFlags(bool noStdLib)
    {
        return noStdLib ? HostLinuxFreestandingCompileFlags : HostLinuxHostedCompileFlags;
    }

    private static string GetWindowsCompileArguments(string compiler, string cSourcePath, string outputPath)
    {
        return compiler switch
        {
            "clang-cl" => $"/nologo /TC /O2 \"{cSourcePath}\" /Fe:\"{outputPath}\" /link kernel32.lib",
            "cl" => $"/nologo /TC /O2 \"{cSourcePath}\" /Fe:\"{outputPath}\" /link kernel32.lib",
            _ => throw new ZorbCompilerException($"Unsupported Windows compiler '{compiler}'.")
        };
    }

    private static string NormalizeWindowsExecutablePath(string outputPath)
    {
        return outputPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? outputPath
            : outputPath + ".exe";
    }

    private static string EnsureToolAvailable(params string[] toolNames)
    {
        foreach (var toolName in toolNames)
        {
            if (IsToolAvailable(toolName))
                return toolName;
        }

        if (toolNames.Length == 1)
            throw new ZorbCompilerException($"Required tool '{toolNames[0]}' was not found in PATH.");

        throw new ZorbCompilerException($"Required tools were not found in PATH. Install one of: {string.Join(", ", toolNames)}.");
    }

    private static bool IsToolAvailable(string toolName)
    {
        var locator = OperatingSystem.IsWindows() ? "where" : "which";
        var check = RunProcess(locator, toolName, Directory.GetCurrentDirectory());
        return check.ExitCode == 0 && !string.IsNullOrWhiteSpace(check.StdOut);
    }

    private static ProcessResult RunProcess(string fileName, string arguments, string workingDirectory)
    {
        return RunProcessCore(fileName, arguments, workingDirectory, timeoutMilliseconds: null);
    }

    private static ProcessResult RunProcessWithTimeout(string fileName, string arguments, string workingDirectory, int timeoutMilliseconds)
    {
        return RunProcessCore(fileName, arguments, workingDirectory, timeoutMilliseconds);
    }

    private static ProcessResult RunProcessCore(string fileName, string arguments, string workingDirectory, int? timeoutMilliseconds)
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
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        if (timeoutMilliseconds.HasValue)
        {
            if (!process.WaitForExit(timeoutMilliseconds.Value))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                throw new ZorbCompilerException($"Process '{fileName}' timed out after {timeoutMilliseconds.Value / 1000} seconds.");
            }
        }
        else
        {
            process.WaitForExit();
        }

        var stdOut = stdOutTask.GetAwaiter().GetResult();
        var stdErr = stdErrTask.GetAwaiter().GetResult();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdOut, stdErr);
    }

    private static bool ResolveNoStdLib(Options options)
    {
        if (options.NoStdLibExplicitlySet)
            return options.NoStdLib;

        return options.Mode switch
        {
            CommandMode.Build or CommandMode.Run => OperatingSystem.IsLinux(),
            _ => options.NoStdLib
        };
    }

    private static string ResolveOutputPath(Options options)
    {
        if (options.OutputPathExplicitlySet || options.Mode == CommandMode.EmitC)
            return options.OutputPath;

        if ((options.Mode == CommandMode.Build || options.Mode == CommandMode.Run) && OperatingSystem.IsWindows())
            return "out.exe";

        return options.OutputPath;
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
                    options.NoStdLibExplicitlySet = true;
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
                    options.OutputPathExplicitlySet = true;
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
        Console.WriteLine("                      Build/run default to freestanding output on Linux and hosted output on Windows.");
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
