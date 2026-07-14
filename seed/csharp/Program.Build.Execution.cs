using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Zorb.Compiler.Codegen;
using Zorb.Compiler.Utils;

partial class Program
{
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
}
