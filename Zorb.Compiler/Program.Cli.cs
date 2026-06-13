using System.Reflection;
using Zorb.Compiler.Lexer;

partial class Program
{
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
                    break;

                case "--emit-llvm":
                    if (options.Mode is CommandMode.Build or CommandMode.Run)
                    {
                        Console.Error.WriteLine("Option --emit-llvm cannot be combined with build or run.");
                        PrintUsage();
                        return null;
                    }
                    options.Mode = CommandMode.EmitLlvmIr;
                    if (!options.OutputPathExplicitlySet)
                        options.OutputPath = "out.ll";
                    break;

                case "--dump-tokens":
                    options.DumpTokens = true;
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

                case "--native-flags":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Missing value for --native-flags.");
                        PrintUsage();
                        return null;
                    }
                    options.NativeFlags = args[++i];
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

        if (options.CheckOnly && options.Mode != CommandMode.EmitLlvmIr)
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
        Console.WriteLine("  --emit-llvm          Emit verified LLVM IR through the Zig backend (default behavior).");
        Console.WriteLine("  -o, --output <path>  Write generated LLVM IR or a built binary to the given path.");
        Console.WriteLine("  --native-flags <s>   Append raw native compiler/linker flags for hosted build/run.");
        Console.WriteLine("  --linker-script <p>  Use a custom linker script for build --target bare-metal-x86_64.");
        Console.WriteLine("  --emit-linker-script <p>");
        Console.WriteLine("                      Write the linker script used by build --target bare-metal-x86_64 to the given path.");
        Console.WriteLine("  --target <name>      Select the compilation target.");
        Console.WriteLine("                      Supported targets: host-linux, freestanding-linux, host-linux-aarch64, freestanding-linux-aarch64, bare-metal-x86_64, host-windows.");
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
            case "host-linux-aarch64":
                target = CompilationTarget.HostLinuxAArch64;
                return true;
            case "freestanding-linux-aarch64":
                target = CompilationTarget.FreestandingLinuxAArch64;
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
            CompilationTarget.HostLinuxAArch64 => "host-linux-aarch64",
            CompilationTarget.FreestandingLinuxAArch64 => "freestanding-linux-aarch64",
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
}
