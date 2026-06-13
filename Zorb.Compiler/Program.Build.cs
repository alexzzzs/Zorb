using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Zorb.Compiler.Codegen;
using Zorb.Compiler.Utils;

partial class Program
{
    private const string AArch64LinuxCrossCompilerEnvironmentVariable = "ZORB_AARCH64_LINUX_GCC";
    private const string AArch64LinuxQemuEnvironmentVariable = "ZORB_QEMU_AARCH64";
    private const string AArch64LinuxSysrootEnvironmentVariable = "ZORB_AARCH64_LINUX_SYSROOT";
    private const string DefaultAArch64LinuxSysroot = "/usr/aarch64-linux-gnu";

    private static int BuildExecutableFromBackend(
        string backendIr,
        string outputPath,
        string? linkerScriptPath,
        string? emitLinkerScriptPath,
        string nativeFlags,
        CompilationTarget target,
        bool reportSuccess = true)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath) ?? Directory.GetCurrentDirectory());
        var tempDir = CreateTempWorkDir("zorb-llvm-build", Path.GetFileNameWithoutExtension(fullOutputPath));
        try
        {
            var objectPath = Path.Combine(tempDir, "module.o");
            var emitResult = EmitBackendObject(backendIr, objectPath);
            if (emitResult != 0)
                return emitResult;

            if (target == CompilationTarget.BareMetalX86_64)
            {
                var linker = ResolveBareMetalLinker();
                var linkerScript = ResolveBareMetalLinkerScript(linkerScriptPath, tempDir);
                if (emitLinkerScriptPath != null)
                    EmitBareMetalLinkerScript(linkerScript.Content, emitLinkerScriptPath);
                var link = RunProcess(
                    linker,
                    ["-m", "elf_x86_64", "-T", linkerScript.Path, "-z", "max-page-size=0x1000", "-o", fullOutputPath, objectPath],
                    tempDir);
                if (link.ExitCode != 0)
                    return ReportFailedProcess("Bare-metal link failed.", link);
            }
            else if (IsLinuxTarget(target))
            {
                var compiler = ResolveLinuxCompiler(target);
                var args = new List<string>();
                if (IsFreestandingLinuxTarget(target))
                    args.AddRange(["-nostdlib", "-fno-pie", "-no-pie", "-z", "execstack", "-fno-builtin"]);
                else
                    args.Add("-no-pie");
                args.Add(objectPath);
                args.Add("-o");
                args.Add(fullOutputPath);
                args.AddRange(ExternalTools.SplitCommandLine(nativeFlags));
                var link = RunProcess(compiler, args, tempDir);
                if (link.ExitCode != 0)
                    return ReportFailedProcess("Native link failed.", link);
            }
            else
            {
                var compiler = EnsureToolAvailable("clang-cl", "cl");
                var linkStubPath = Path.Combine(tempDir, "zorb_link_stub.c");
                File.WriteAllText(linkStubPath, "int __zorb_link_stub = 0;\n");
                var link = RunProcess(
                    compiler,
                    ExternalTools.GetWindowsCompileAndLinkArgumentList(
                        compiler,
                        linkStubPath,
                        [objectPath],
                        fullOutputPath)
                        .Concat(ExternalTools.SplitCommandLine(nativeFlags))
                        .ToArray(),
                    tempDir);
                if (link.ExitCode != 0)
                    return ReportFailedProcess("Native link failed.", link);
            }

            if (reportSuccess && target == CompilationTarget.BareMetalX86_64)
            {
                Console.WriteLine($"Bare-metal kernel image built at {fullOutputPath}");
                Console.WriteLine(linkerScriptPath != null
                    ? $"Linker script: {Path.GetFullPath(linkerScriptPath)}"
                    : "Linker script: bundled bare-metal-x86_64 default");
                if (emitLinkerScriptPath != null)
                    Console.WriteLine($"Emitted linker script to {Path.GetFullPath(emitLinkerScriptPath)}");
            }
            else if (reportSuccess)
            {
                Console.WriteLine($"Executable built at {fullOutputPath}");
            }
            return 0;
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static int RunExecutableFromBackend(
        string inputPath,
        string backendIr,
        string nativeFlags,
        CompilationTarget target)
    {
        if (target == CompilationTarget.BareMetalX86_64)
        {
            Console.Error.WriteLine($"Run does not support target '{FormatTarget(target)}'.");
            return 1;
        }

        var tempDir = CreateTempWorkDir("zorb-llvm-run", Path.GetFileNameWithoutExtension(inputPath));
        try
        {
            var binaryPath = Path.Combine(
                tempDir,
                OperatingSystem.IsWindows() ? "program.exe" : "program");
            var buildResult = BuildExecutableFromBackend(
                backendIr,
                binaryPath,
                null,
                null,
                nativeFlags,
                target,
                reportSuccess: false);
            if (buildResult != 0)
                return buildResult;
            var execution = RunBuiltBinaryForTarget(binaryPath, tempDir, target);
            if (!string.IsNullOrEmpty(execution.StdOut))
                Console.Write(execution.StdOut);
            if (!string.IsNullOrEmpty(execution.StdErr))
                Console.Error.Write(execution.StdErr);
            return execution.ExitCode;
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static int EmitBackendObject(string backendIr, string objectPath)
    {
        var root = JsonNode.Parse(backendIr)?.AsObject()
            ?? throw new ZorbCompilerException("The Zig backend IR document is invalid.");
        root["output_kind"] = "object";
        root["output_path"] = Path.GetFullPath(objectPath);
        var tempDir = CreateTempWorkDir("zorb-llvm-object", Path.GetFileNameWithoutExtension(objectPath));
        try
        {
            var irPath = Path.Combine(tempDir, "module.json");
            File.WriteAllText(irPath, root.ToJsonString());
            var process = RunProcess(
                ResolveZigBackendExecutable(),
                [irPath],
                Directory.GetCurrentDirectory());
            return process.ExitCode == 0
                ? 0
                : ReportFailedProcess("LLVM object emission failed.", process);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string ResolveBareMetalLinker()
    {
        var configuredPath = Environment.GetEnvironmentVariable(BareMetalLinkerEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath))
                return Path.GetFullPath(configuredPath);
            throw new ZorbCompilerException(
                $"{BareMetalLinkerEnvironmentVariable} points to a missing file: {Path.GetFullPath(configuredPath)}");
        }

        var packagedTool = ResolvePackagedToolIfPresent(BareMetalLinkerExecutableName, null);
        if (packagedTool != null)
            return packagedTool;

        var pathCandidate = ExternalTools.FindAvailableTool(BareMetalLinkerExecutableName);
        if (pathCandidate != null)
            return pathCandidate;

        var versionedPathCandidate = ExternalTools.FindAvailableToolByPrefix(BareMetalLinkerExecutableName + "-");
        if (versionedPathCandidate != null)
            return versionedPathCandidate;

        throw new ZorbCompilerException(
            $"Unable to find {BareMetalLinkerExecutableName}. Set {BareMetalLinkerEnvironmentVariable} to its path. Install LLVM LLD 20 or reinstall the compiler package.");
    }

    private static string? ResolvePackagedToolIfPresent(
        string executableName,
        string? repositoryRelativeDirectory)
    {
        var platformExecutableName = OperatingSystem.IsWindows()
            ? executableName + ".exe"
            : executableName;
        var packagedCandidate = Path.Combine(AppContext.BaseDirectory, platformExecutableName);
        if (File.Exists(packagedCandidate))
            return packagedCandidate;

        if (repositoryRelativeDirectory != null)
        {
            var searchDirectory = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (searchDirectory != null)
            {
                var candidate = Path.Combine(
                    searchDirectory.FullName,
                    repositoryRelativeDirectory,
                    platformExecutableName);
                if (File.Exists(candidate))
                    return candidate;
                searchDirectory = searchDirectory.Parent;
            }
        }

        return null;
    }

    private static string ResolvePackagedTool(
        string environmentVariable,
        string executableName,
        string? repositoryRelativeDirectory,
        string failureHint)
    {
        var configuredPath = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath))
                return Path.GetFullPath(configuredPath);
            throw new ZorbCompilerException(
                $"{environmentVariable} points to a missing file: {Path.GetFullPath(configuredPath)}");
        }

        var packagedCandidate = ResolvePackagedToolIfPresent(executableName, repositoryRelativeDirectory);
        if (packagedCandidate != null)
            return packagedCandidate;

        var platformExecutableName = OperatingSystem.IsWindows()
            ? executableName + ".exe"
            : executableName;
        var pathCandidate = ExternalTools.FindAvailableTool(platformExecutableName);
        if (pathCandidate != null)
            return pathCandidate;

        throw new ZorbCompilerException(
            $"Unable to find {platformExecutableName}. Set {environmentVariable} to its path. {failureHint}");
    }

    private static string CreateTempWorkDir(string prefix, string name)
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            prefix,
            name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return tempDir;
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

        if (IsLinuxTarget(target))
        {
            if (!OperatingSystem.IsLinux())
                throw new ZorbCompilerException($"Target '{FormatTarget(target)}' currently requires a Linux host for build and run. Current host: {DescribeCurrentHost()}.");

            if (!IsSupportedLinuxHostForTarget(target))
                throw new ZorbCompilerException($"Target '{FormatTarget(target)}' currently requires a compatible Linux host and toolchain. Current host: {DescribeCurrentHost()}.");
        }

        if (target == CompilationTarget.BareMetalX86_64)
        {
            if (mode == CommandMode.Run)
                return;
            if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) ||
                RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                throw new ZorbCompilerException(
                    $"Target 'bare-metal-x86_64' currently requires a Linux or Windows x86_64 host for build. Current host: {DescribeCurrentHost()}.");
            }
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

    private static ZigBackendTarget GetZigBackendTarget(CompilationTarget target)
    {
        return target switch
        {
            CompilationTarget.HostLinux or CompilationTarget.FreestandingLinux
                when RuntimeInformation.ProcessArchitecture == Architecture.X64
                => new ZigBackendTarget("x86_64-pc-linux-gnu"),
            CompilationTarget.HostLinux or CompilationTarget.FreestandingLinux
                when RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                => new ZigBackendTarget("aarch64-unknown-linux-gnu"),
            CompilationTarget.HostLinuxAArch64 or CompilationTarget.FreestandingLinuxAArch64
                => new ZigBackendTarget("aarch64-unknown-linux-gnu"),
            CompilationTarget.BareMetalX86_64
                => new ZigBackendTarget("x86_64-unknown-none-elf"),
            CompilationTarget.HostWindows
                when RuntimeInformation.ProcessArchitecture == Architecture.X64
                => new ZigBackendTarget("x86_64-pc-windows-msvc"),
            CompilationTarget.HostWindows
                when RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                => new ZigBackendTarget("aarch64-pc-windows-msvc"),
            _ => throw new ZorbCompilerException(
                $"The Zig LLVM backend does not support target '{FormatTarget(target)}' on {DescribeCurrentHost()}.")
        };
    }

    private static bool IsLinuxTarget(CompilationTarget target)
    {
        return target is
            CompilationTarget.HostLinux or
            CompilationTarget.FreestandingLinux or
            CompilationTarget.HostLinuxAArch64 or
            CompilationTarget.FreestandingLinuxAArch64;
    }

    private static bool IsFreestandingLinuxTarget(CompilationTarget target)
    {
        return target is CompilationTarget.FreestandingLinux or CompilationTarget.FreestandingLinuxAArch64;
    }

    private static bool IsAArch64LinuxTarget(CompilationTarget target)
    {
        return target is CompilationTarget.HostLinuxAArch64 or CompilationTarget.FreestandingLinuxAArch64;
    }

    private static bool IsSupportedLinuxHostForTarget(CompilationTarget target)
    {
        if (!IsSupportedHostedArchitecture())
            return false;

        if (!IsAArch64LinuxTarget(target))
            return true;

        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return true;

        return FindAArch64LinuxCrossCompiler() != null;
    }

    private static string ResolveLinuxCompiler(CompilationTarget target)
    {
        if (IsAArch64LinuxTarget(target) && RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            var compiler = FindAArch64LinuxCrossCompiler();
            if (compiler != null)
                return compiler;

            throw new ZorbCompilerException(
                $"Target '{FormatTarget(target)}' requires an AArch64 Linux cross-compiler on x86_64 hosts. Install 'aarch64-linux-gnu-gcc' or set {AArch64LinuxCrossCompilerEnvironmentVariable}.");
        }

        return EnsureToolAvailable("gcc");
    }

    private static string? FindAArch64LinuxCrossCompiler()
    {
        var configuredPath = Environment.GetEnvironmentVariable(AArch64LinuxCrossCompilerEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath))
                return Path.GetFullPath(configuredPath);

            throw new ZorbCompilerException(
                $"{AArch64LinuxCrossCompilerEnvironmentVariable} points to a missing file: {Path.GetFullPath(configuredPath)}");
        }

        return ExternalTools.FindAvailableTool("aarch64-linux-gnu-gcc");
    }

    private static ProcessResult RunBuiltBinaryForTarget(string binaryPath, string workingDirectory, CompilationTarget target)
    {
        if (!IsAArch64LinuxTarget(target) || RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            return RunProcessWithTimeout(
                binaryPath,
                Array.Empty<string>(),
                workingDirectory,
                RunTimeoutMilliseconds);
        }

        var qemu = ResolveAArch64Qemu();
        var args = new List<string>();
        var sysroot = ResolveAArch64LinuxSysroot();
        if (sysroot != null)
        {
            args.Add("-L");
            args.Add(sysroot);
        }

        args.Add(binaryPath);
        return RunProcessWithTimeout(
            qemu,
            args.ToArray(),
            workingDirectory,
            RunTimeoutMilliseconds);
    }

    private static string ResolveAArch64Qemu()
    {
        var configuredPath = Environment.GetEnvironmentVariable(AArch64LinuxQemuEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath))
                return Path.GetFullPath(configuredPath);

            throw new ZorbCompilerException(
                $"{AArch64LinuxQemuEnvironmentVariable} points to a missing file: {Path.GetFullPath(configuredPath)}");
        }

        var qemu = ExternalTools.FindAvailableTool("qemu-aarch64")
            ?? ExternalTools.FindAvailableTool("qemu-aarch64-static");
        if (qemu != null)
            return qemu;

        throw new ZorbCompilerException(
            $"Target execution for AArch64 Linux requires qemu-aarch64. Install 'qemu-user' or set {AArch64LinuxQemuEnvironmentVariable}.");
    }

    private static string? ResolveAArch64LinuxSysroot()
    {
        var configuredPath = Environment.GetEnvironmentVariable(AArch64LinuxSysrootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (Directory.Exists(configuredPath))
                return Path.GetFullPath(configuredPath);

            throw new ZorbCompilerException(
                $"{AArch64LinuxSysrootEnvironmentVariable} points to a missing directory: {Path.GetFullPath(configuredPath)}");
        }

        return Directory.Exists(DefaultAArch64LinuxSysroot)
            ? DefaultAArch64LinuxSysroot
            : null;
    }

    private static string ResolveOutputPath(Options options, CompilationTarget target)
    {
        if (options.OutputPathExplicitlySet || options.Mode == CommandMode.EmitLlvmIr)
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

}
