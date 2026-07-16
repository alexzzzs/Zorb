using System.Reflection;
using Zorb.Compiler.Lexer;

partial class Program
{
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
