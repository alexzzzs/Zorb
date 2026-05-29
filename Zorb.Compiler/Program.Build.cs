using System.IO;
using System.Runtime.InteropServices;
using Zorb.Compiler.Codegen;
using Zorb.Compiler.Utils;

partial class Program
{
    private static string CreateTempWorkDir(string prefix, string name)
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            prefix,
            name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static string ResolveCSourcePath(string outputOrTempDir, string? keepCPath, string fallbackName)
    {
        var cSourcePath = keepCPath != null
            ? Path.GetFullPath(keepCPath)
            : Path.Combine(outputOrTempDir, fallbackName);
        Directory.CreateDirectory(Path.GetDirectoryName(cSourcePath) ?? outputOrTempDir);
        return cSourcePath;
    }

    private static void WriteCSource(string cSourcePath, string cCode)
    {
        File.WriteAllText(cSourcePath, cCode);
    }

    private static int ReportFailedProcess(string banner, ProcessResult process)
    {
        Console.Error.WriteLine(banner);
        if (!string.IsNullOrWhiteSpace(process.StdErr))
            Console.Error.Write(process.StdErr);
        if (!string.IsNullOrWhiteSpace(process.StdOut))
            Console.Error.Write(process.StdOut);
        return process.ExitCode;
    }

    private static int BuildExecutable(string inputPath, string cCode, string outputPath, string? keepCPath, string? linkerScriptPath, string? emitLinkerScriptPath, string nativeFlags, CompilationTarget target)
    {
        if (target == CompilationTarget.HostLinux || target == CompilationTarget.FreestandingLinux)
            return BuildExecutableOnLinux(cCode, outputPath, keepCPath, UsesNoStdLib(target), nativeFlags);

        if (target == CompilationTarget.BareMetalX86_64)
            return BuildBareMetalImageOnLinux(cCode, outputPath, keepCPath, linkerScriptPath, emitLinkerScriptPath);

        if (target == CompilationTarget.HostWindows)
            return BuildExecutableOnWindows(cCode, outputPath, keepCPath);

        Console.Error.WriteLine($"Build does not support target '{FormatTarget(target)}'.");
        return 1;
    }

    private static int BuildExecutableOnLinux(string cCode, string outputPath, string? keepCPath, bool noStdLib, string nativeFlags)
    {
        EnsureToolAvailable("gcc");

        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath) ?? Directory.GetCurrentDirectory());

        var cSourcePath = ResolveCSourcePath(
            Path.GetDirectoryName(fullOutputPath) ?? Directory.GetCurrentDirectory(),
            keepCPath,
            Path.GetFileNameWithoutExtension(fullOutputPath) + ".c");
        WriteCSource(cSourcePath, cCode);

        var compile = RunProcess(
            "gcc",
            $"{GetLinuxCompileFlags(noStdLib)} \"{cSourcePath}\" -o \"{fullOutputPath}\" {nativeFlags}",
            Path.GetDirectoryName(cSourcePath) ?? Directory.GetCurrentDirectory());

        if (compile.ExitCode != 0)
            return ReportFailedProcess("Native build failed.", compile);

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

        var cSourcePath = ResolveCSourcePath(
            Path.GetDirectoryName(fullOutputPath) ?? Directory.GetCurrentDirectory(),
            keepCPath,
            Path.GetFileNameWithoutExtension(fullOutputPath) + ".c");
        WriteCSource(cSourcePath, cCode);

        var tempDir = CreateTempWorkDir("zorb-bare-metal-build", Path.GetFileNameWithoutExtension(fullOutputPath));

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
                return ReportFailedProcess("Bare-metal object build failed.", compile);

            var link = RunProcess(
                "ld",
                $"-m elf_x86_64 -T \"{linkerScript.Path}\" -z max-page-size=0x1000 -o \"{fullOutputPath}\" \"{objectPath}\"",
                tempDir);

            if (link.ExitCode != 0)
                return ReportFailedProcess("Bare-metal link failed.", link);

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

        var cSourcePath = ResolveCSourcePath(
            Path.GetDirectoryName(fullOutputPath) ?? Directory.GetCurrentDirectory(),
            keepCPath,
            Path.GetFileNameWithoutExtension(fullOutputPath) + ".c");
        WriteCSource(cSourcePath, cCode);

        var compile = RunProcess(
            compiler,
            GetWindowsCompileArguments(compiler, cSourcePath, fullOutputPath),
            Path.GetDirectoryName(cSourcePath) ?? Directory.GetCurrentDirectory());

        if (compile.ExitCode != 0)
            return ReportFailedProcess("Native build failed.", compile);

        Console.WriteLine($"Executable built at {fullOutputPath}");
        Console.WriteLine($"Intermediate C written to {cSourcePath}");
        Console.WriteLine($"Windows toolchain: {compiler}");
        return 0;
    }

    private static int RunExecutable(string inputPath, string cCode, string? keepCPath, string nativeFlags, CompilationTarget target)
    {
        if (target == CompilationTarget.HostLinux || target == CompilationTarget.FreestandingLinux)
            return RunExecutableOnLinux(inputPath, cCode, keepCPath, UsesNoStdLib(target), nativeFlags);

        if (target == CompilationTarget.BareMetalX86_64)
        {
            Console.Error.WriteLine($"Run does not support target '{FormatTarget(target)}'. Build the object file and link it with your kernel or bootloader toolchain.");
            return 1;
        }

        if (target == CompilationTarget.HostWindows)
            return RunExecutableOnWindows(inputPath, cCode, keepCPath);

        Console.Error.WriteLine($"Run does not support target '{FormatTarget(target)}'.");
        return 1;
    }

    private static int RunExecutableOnLinux(string inputPath, string cCode, string? keepCPath, bool noStdLib, string nativeFlags)
    {
        EnsureToolAvailable("gcc");

        var tempDir = CreateTempWorkDir("zorb-run", Path.GetFileNameWithoutExtension(inputPath));

        try
        {
            var binaryPath = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(inputPath));
            var cSourcePath = ResolveCSourcePath(tempDir, keepCPath, "out.c");
            WriteCSource(cSourcePath, cCode);

            var compile = RunProcess(
                "gcc",
                $"{GetLinuxCompileFlags(noStdLib)} \"{cSourcePath}\" -o \"{binaryPath}\" {nativeFlags}",
                Path.GetDirectoryName(cSourcePath) ?? tempDir);

            if (compile.ExitCode != 0)
                return ReportFailedProcess("Native build failed.", compile);

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

        var tempDir = CreateTempWorkDir("zorb-run", Path.GetFileNameWithoutExtension(inputPath));

        try
        {
            var binaryFileName = Path.GetFileNameWithoutExtension(inputPath) + ".exe";
            var binaryPath = Path.Combine(tempDir, binaryFileName);
            var cSourcePath = ResolveCSourcePath(tempDir, keepCPath, "out.c");
            WriteCSource(cSourcePath, cCode);

            var compile = RunProcess(
                compiler,
                GetWindowsCompileArguments(compiler, cSourcePath, binaryPath),
                Path.GetDirectoryName(cSourcePath) ?? tempDir);

            if (compile.ExitCode != 0)
                return ReportFailedProcess("Native build failed.", compile);

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
            CommandMode.Build or CommandMode.Run => throw new ZorbCompilerException($"Build and run currently support Linux and Windows hosts only. Current host: {DescribeCurrentHost()}."),
            _ => CompilationTarget.HostLinux
        };
    }

    private static void EnsureTargetSupportedForCurrentHost(CommandMode mode, CompilationTarget target)
    {
        if (mode != CommandMode.Build && mode != CommandMode.Run)
            return;

        if (target == CompilationTarget.HostLinux || target == CompilationTarget.FreestandingLinux)
        {
            if (!OperatingSystem.IsLinux())
                throw new ZorbCompilerException($"Target '{FormatTarget(target)}' currently requires a Linux host for build and run. Current host: {DescribeCurrentHost()}.");

            if (!IsSupportedHostedArchitecture())
                throw new ZorbCompilerException($"Target '{FormatTarget(target)}' currently requires an x86_64 or aarch64 Linux host. Current host: {DescribeCurrentHost()}.");
        }

        if (target == CompilationTarget.BareMetalX86_64)
        {
            if (mode == CommandMode.Run)
                return;

            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new ZorbCompilerException($"Target 'bare-metal-x86_64' currently requires a Linux x86_64 host for build. Current host: {DescribeCurrentHost()}.");
            return;
        }

        if (target == CompilationTarget.HostWindows)
        {
            if (!OperatingSystem.IsWindows())
                throw new ZorbCompilerException($"Target 'host-windows' currently requires a Windows host for build and run. Current host: {DescribeCurrentHost()}.");

            if (!IsSupportedHostedArchitecture())
                throw new ZorbCompilerException($"Target 'host-windows' currently requires an x86_64 or aarch64 Windows host. Current host: {DescribeCurrentHost()}.");
        }
    }

    private static bool IsSupportedHostedArchitecture()
    {
        return RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;
    }

    private static string DescribeCurrentHost()
    {
        string os =
            OperatingSystem.IsLinux() ? "linux" :
            OperatingSystem.IsWindows() ? "windows" :
            OperatingSystem.IsMacOS() ? "macos" :
            "unknown";

        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };

        return $"{os} {arch}";
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
                generator.EmitLinuxSyscallWrapper = true;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }
    }
}
