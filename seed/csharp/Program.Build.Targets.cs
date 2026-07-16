using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Zorb.Compiler.Codegen;
using Zorb.Compiler.Utils;

partial class Program
{
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
