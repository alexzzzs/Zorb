using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Zorb.Compiler.AST;
using Zorb.Compiler.Codegen;
using Zorb.Compiler.Lexer;
using Zorb.Compiler.Parser;
using Zorb.Compiler.Parsing;
using Zorb.Compiler.Semantic;
using Zorb.Compiler.Utils;

class Program
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
            typeChecker.Errors.ReportAll();
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

    private static int BuildExecutable(string inputPath, string cCode, string outputPath, string? keepCPath, string? linkerScriptPath, string? emitLinkerScriptPath, CompilationTarget target)
    {
        if (target == CompilationTarget.HostLinux || target == CompilationTarget.FreestandingLinux)
        {
            return BuildExecutableOnLinux(cCode, outputPath, keepCPath, UsesNoStdLib(target));
        }

        if (target == CompilationTarget.BareMetalX86_64)
        {
            return BuildBareMetalImageOnLinux(cCode, outputPath, keepCPath, linkerScriptPath, emitLinkerScriptPath);
        }

        if (target == CompilationTarget.HostWindows)
        {
            return BuildExecutableOnWindows(cCode, outputPath, keepCPath);
        }

        Console.Error.WriteLine($"Build does not support target '{FormatTarget(target)}'.");
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

    private static int BuildBareMetalImageOnLinux(string cCode, string outputPath, string? keepCPath, string? linkerScriptPath, string? emitLinkerScriptPath)
    {
        EnsureToolAvailable("gcc");
        EnsureToolAvailable("ld");

        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath) ?? Directory.GetCurrentDirectory());

        var cSourcePath = keepCPath != null
            ? Path.GetFullPath(keepCPath)
            : Path.Combine(Path.GetDirectoryName(fullOutputPath) ?? Directory.GetCurrentDirectory(), Path.GetFileNameWithoutExtension(fullOutputPath) + ".c");

        Directory.CreateDirectory(Path.GetDirectoryName(cSourcePath) ?? Directory.GetCurrentDirectory());
        File.WriteAllText(cSourcePath, cCode);

        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "zorb-bare-metal-build",
            Path.GetFileNameWithoutExtension(fullOutputPath) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var objectPath = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(fullOutputPath) + ".o");
            var linkerScript = ResolveBareMetalLinkerScript(linkerScriptPath, tempDir);

            if (emitLinkerScriptPath != null)
                EmitBareMetalLinkerScript(linkerScript.Content, emitLinkerScriptPath);

            var compile = RunProcess(
                "gcc",
                $"{BareMetalX86_64ObjectCompileFlags} \"{cSourcePath}\" -o \"{objectPath}\"",
                Path.GetDirectoryName(cSourcePath) ?? Directory.GetCurrentDirectory());

            if (compile.ExitCode != 0)
            {
                Console.Error.WriteLine("Bare-metal object build failed.");
                if (!string.IsNullOrWhiteSpace(compile.StdErr))
                    Console.Error.Write(compile.StdErr);
                if (!string.IsNullOrWhiteSpace(compile.StdOut))
                    Console.Error.Write(compile.StdOut);
                return compile.ExitCode;
            }

            var link = RunProcess(
                "ld",
                $"-m elf_x86_64 -T \"{linkerScript.Path}\" -z max-page-size=0x1000 -o \"{fullOutputPath}\" \"{objectPath}\"",
                tempDir);

            if (link.ExitCode != 0)
            {
                Console.Error.WriteLine("Bare-metal link failed.");
                if (!string.IsNullOrWhiteSpace(link.StdErr))
                    Console.Error.Write(link.StdErr);
                if (!string.IsNullOrWhiteSpace(link.StdOut))
                    Console.Error.Write(link.StdOut);
                return link.ExitCode;
            }

            Console.WriteLine($"Bare-metal kernel image built at {fullOutputPath}");
            Console.WriteLine($"Intermediate C written to {cSourcePath}");
            Console.WriteLine(linkerScriptPath != null
                ? $"Linker script: {Path.GetFullPath(linkerScriptPath)}"
                : "Linker script: bundled bare-metal-x86_64 default");
            if (emitLinkerScriptPath != null)
                Console.WriteLine($"Emitted linker script to {Path.GetFullPath(emitLinkerScriptPath)}");
            return 0;
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static ResolvedLinkerScript ResolveBareMetalLinkerScript(string? linkerScriptPath, string tempDir)
    {
        if (linkerScriptPath != null)
        {
            var fullLinkerScriptPath = Path.GetFullPath(linkerScriptPath);
            if (!File.Exists(fullLinkerScriptPath))
                throw new ZorbCompilerException($"Linker script '{fullLinkerScriptPath}' does not exist.");
            return new ResolvedLinkerScript(fullLinkerScriptPath, File.ReadAllText(fullLinkerScriptPath));
        }

        var defaultLinkerScriptPath = Path.Combine(tempDir, "bare-metal-x86_64.ld");
        File.WriteAllText(defaultLinkerScriptPath, BareMetalX86_64DefaultLinkerScript);
        return new ResolvedLinkerScript(defaultLinkerScriptPath, BareMetalX86_64DefaultLinkerScript);
    }

    private static void EmitBareMetalLinkerScript(string linkerScriptContent, string emitLinkerScriptPath)
    {
        var fullEmitPath = Path.GetFullPath(emitLinkerScriptPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullEmitPath) ?? Directory.GetCurrentDirectory());
        File.WriteAllText(fullEmitPath, linkerScriptContent);
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

    private static int RunExecutable(string inputPath, string cCode, string? keepCPath, CompilationTarget target)
    {
        if (target == CompilationTarget.HostLinux || target == CompilationTarget.FreestandingLinux)
        {
            return RunExecutableOnLinux(inputPath, cCode, keepCPath, UsesNoStdLib(target));
        }

        if (target == CompilationTarget.BareMetalX86_64)
        {
            Console.Error.WriteLine($"Run does not support target '{FormatTarget(target)}'. Build the object file and link it with your kernel or bootloader toolchain.");
            return 1;
        }

        if (target == CompilationTarget.HostWindows)
        {
            return RunExecutableOnWindows(inputPath, cCode, keepCPath);
        }

        Console.Error.WriteLine($"Run does not support target '{FormatTarget(target)}'.");
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

                Console.Error.WriteLine($"Process '{fileName}' timed out after {timeoutMilliseconds.Value / 1000} seconds.");
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

    private static CompilationTarget ResolveCompilationTarget(Options options)
    {
        if (options.Target.HasValue)
        {
            if (options.LegacyNoStdLib && options.Target.Value != CompilationTarget.FreestandingLinux)
                throw new ZorbCompilerException($"Cannot combine -nostdlib with --target {FormatTarget(options.Target.Value)}. Use --target freestanding-linux or omit -nostdlib.");

            return options.Target.Value;
        }

        if (options.LegacyNoStdLib)
            return CompilationTarget.FreestandingLinux;

        return options.Mode switch
        {
            CommandMode.Build or CommandMode.Run when OperatingSystem.IsLinux() => CompilationTarget.FreestandingLinux,
            CommandMode.Build or CommandMode.Run when OperatingSystem.IsWindows() => CompilationTarget.HostWindows,
            CommandMode.Build or CommandMode.Run => throw new ZorbCompilerException("Build and run currently support Linux and Windows hosts only."),
            _ => CompilationTarget.HostLinux
        };
    }

    private static void EnsureTargetSupportedForCurrentHost(CommandMode mode, CompilationTarget target)
    {
        if (mode != CommandMode.Build && mode != CommandMode.Run)
            return;

        if ((target == CompilationTarget.HostLinux || target == CompilationTarget.FreestandingLinux) && !OperatingSystem.IsLinux())
            throw new ZorbCompilerException($"Target '{FormatTarget(target)}' currently requires a Linux host for build and run.");

        if (target == CompilationTarget.BareMetalX86_64)
        {
            if (mode == CommandMode.Run)
                return;

            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new ZorbCompilerException("Target 'bare-metal-x86_64' currently requires a Linux x86_64 host for build.");
            return;
        }

        if (target == CompilationTarget.HostWindows && !OperatingSystem.IsWindows())
            throw new ZorbCompilerException("Target 'host-windows' currently requires a Windows host for build and run.");
    }

    private static bool UsesNoStdLib(CompilationTarget target)
    {
        return target == CompilationTarget.FreestandingLinux;
    }

    private static bool PreservesStart(CompilationTarget target)
    {
        return target == CompilationTarget.FreestandingLinux || target == CompilationTarget.BareMetalX86_64;
    }

    private static string ResolveOutputPath(Options options, CompilationTarget target)
    {
        if (options.OutputPathExplicitlySet || options.Mode == CommandMode.EmitC)
            return options.OutputPath;

        if ((options.Mode == CommandMode.Build || options.Mode == CommandMode.Run) && target == CompilationTarget.HostWindows)
            return "out.exe";

        if (options.Mode == CommandMode.Build && target == CompilationTarget.BareMetalX86_64)
            return "out.elf";

        return options.OutputPath;
    }

    private static bool ValidateTargetSpecificOptions(Options options, CompilationTarget target)
    {
        if (options.LinkerScriptPath == null && options.EmitLinkerScriptPath == null)
            return true;

        if (options.Mode != CommandMode.Build || target != CompilationTarget.BareMetalX86_64)
        {
            if (options.LinkerScriptPath != null)
                Console.Error.WriteLine("Option --linker-script is only valid with build --target bare-metal-x86_64.");
            else
                Console.Error.WriteLine("Option --emit-linker-script is only valid with build --target bare-metal-x86_64.");
            return false;
        }

        return true;
    }

    private static void ConfigureGeneratorForTarget(CGenerator generator, CompilationTarget target)
    {
        switch (target)
        {
            case CompilationTarget.HostLinux:
            case CompilationTarget.FreestandingLinux:
                generator.BuiltinIsLinux = true;
                generator.BuiltinIsWindows = false;
                generator.BuiltinIsBareMetal = false;
                generator.BuiltinIsX86_64 = null;
                generator.BuiltinIsAArch64 = null;
                generator.EmitLinuxSyscallWrapper = true;
                break;

            case CompilationTarget.BareMetalX86_64:
                generator.BuiltinIsLinux = false;
                generator.BuiltinIsWindows = false;
                generator.BuiltinIsBareMetal = true;
                generator.BuiltinIsX86_64 = true;
                generator.BuiltinIsAArch64 = false;
                generator.EmitLinuxSyscallWrapper = false;
                break;

            case CompilationTarget.HostWindows:
                generator.BuiltinIsLinux = false;
                generator.BuiltinIsWindows = true;
                generator.BuiltinIsBareMetal = false;
                generator.BuiltinIsX86_64 = null;
                generator.BuiltinIsAArch64 = null;
                // Keep the syscall compatibility macro available so dead Linux-only
                // branches in shared stdlib code still parse on Windows hosts.
                generator.EmitLinuxSyscallWrapper = true;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }
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
                options.OutputPath = "out";
                i = 1;
            }
            else if (string.Equals(args[0], "run", StringComparison.Ordinal))
            {
                options.Mode = CommandMode.Run;
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
                    options.LegacyNoStdLib = true;
                    break;

                case "--target":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Missing value for --target.");
                        PrintUsage();
                        return null;
                    }

                    var targetText = args[++i];
                    if (!TryParseCompilationTarget(targetText, out var parsedTarget))
                    {
                        Console.Error.WriteLine($"Unknown target: {targetText}");
                        PrintUsage();
                        return null;
                    }

                    options.Target = parsedTarget;
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

                case "--linker-script":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Missing value for --linker-script.");
                        PrintUsage();
                        return null;
                    }
                    options.LinkerScriptPath = args[++i];
                    break;

                case "--emit-linker-script":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Missing value for --emit-linker-script.");
                        PrintUsage();
                        return null;
                    }
                    options.EmitLinkerScriptPath = args[++i];
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

        if (options.KeepCPath != null && options.Mode == CommandMode.EmitC)
        {
            Console.Error.WriteLine("Option --keep-c is only valid with build or run.");
            PrintUsage();
            return null;
        }

        if (options.CheckOnly && options.OutputPathExplicitlySet)
        {
            Console.Error.WriteLine("Option -o/--output is not valid with --check.");
            PrintUsage();
            return null;
        }

        if (options.OutputPathExplicitlySet && options.Mode == CommandMode.Run)
        {
            Console.Error.WriteLine("Option -o/--output is not valid with run.");
            PrintUsage();
            return null;
        }

        if (options.CheckOnly && options.Mode != CommandMode.EmitC)
        {
            Console.Error.WriteLine("Option --check cannot be combined with build or run.");
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
        Console.WriteLine("  --linker-script <p>  Use a custom linker script for build --target bare-metal-x86_64.");
        Console.WriteLine("  --emit-linker-script <p>");
        Console.WriteLine("                      Write the linker script used by build --target bare-metal-x86_64 to the given path.");
        Console.WriteLine("  --target <name>      Select the compilation target.");
        Console.WriteLine("                      Supported targets: host-linux, freestanding-linux, bare-metal-x86_64, host-windows.");
        Console.WriteLine("                      Build/run default to freestanding-linux on Linux and host-windows on Windows.");
        Console.WriteLine("                      bare-metal-x86_64 build links a kernel ELF with a bundled linker script unless overridden.");
        Console.WriteLine("  -nostdlib            Legacy shorthand for --target freestanding-linux.");
        Console.WriteLine("  -h, --help           Show this help text.");
        Console.WriteLine("  --version            Show the compiler version.");
    }

    private static bool TryParseCompilationTarget(string text, out CompilationTarget target)
    {
        switch (text)
        {
            case "host-linux":
                target = CompilationTarget.HostLinux;
                return true;
            case "freestanding-linux":
                target = CompilationTarget.FreestandingLinux;
                return true;
            case "bare-metal-x86_64":
                target = CompilationTarget.BareMetalX86_64;
                return true;
            case "host-windows":
                target = CompilationTarget.HostWindows;
                return true;
            default:
                target = default;
                return false;
        }
    }

    private static string FormatTarget(CompilationTarget target)
    {
        return target switch
        {
            CompilationTarget.HostLinux => "host-linux",
            CompilationTarget.FreestandingLinux => "freestanding-linux",
            CompilationTarget.BareMetalX86_64 => "bare-metal-x86_64",
            CompilationTarget.HostWindows => "host-windows",
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
        };
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

    private sealed record ResolvedLinkerScript(string Path, string Content);

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
