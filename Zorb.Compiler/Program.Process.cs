using Zorb.Compiler.Utils;

partial class Program
{
    private static string GetLinuxCompileFlags(bool noStdLib)
    {
        return noStdLib ? HostLinuxFreestandingCompileFlags : HostLinuxHostedCompileFlags;
    }

    private static IReadOnlyList<string> GetWindowsCompileArgumentList(string compiler, string cSourcePath, string outputPath)
    {
        return ExternalTools.GetWindowsCompileArgumentList(compiler, cSourcePath, outputPath);
    }

    private static string NormalizeWindowsExecutablePath(string outputPath)
    {
        return ExternalTools.NormalizeWindowsExecutablePath(outputPath);
    }

    private static string EnsureToolAvailable(params string[] toolNames)
    {
        return ExternalTools.EnsureToolAvailable(toolNames);
    }

    private static bool IsToolAvailable(string toolName)
    {
        return ExternalTools.IsToolAvailable(toolName);
    }

    private static ProcessResult RunProcess(string fileName, string arguments, string workingDirectory)
    {
        return FromCommandResult(ExternalTools.RunProcess(fileName, arguments, workingDirectory));
    }

    private static ProcessResult RunProcess(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        return FromCommandResult(ExternalTools.RunProcess(fileName, arguments, workingDirectory));
    }

    private static ProcessResult RunProcessWithTimeout(string fileName, string arguments, string workingDirectory, int timeoutMilliseconds)
    {
        return FromCommandResult(ExternalTools.RunProcessWithTimeout(fileName, arguments, workingDirectory, timeoutMilliseconds));
    }

    private static ProcessResult RunProcessWithTimeout(string fileName, IEnumerable<string> arguments, string workingDirectory, int timeoutMilliseconds)
    {
        return FromCommandResult(ExternalTools.RunProcessWithTimeout(fileName, arguments, workingDirectory, timeoutMilliseconds));
    }

    private static ProcessResult FromCommandResult(CommandResult result)
    {
        return new ProcessResult(result.ExitCode, result.StdOut, result.StdErr);
    }
}
