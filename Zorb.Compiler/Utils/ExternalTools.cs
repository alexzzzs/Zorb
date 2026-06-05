using System.Diagnostics;

namespace Zorb.Compiler.Utils;

public readonly record struct CommandResult(int ExitCode, string StdOut, string StdErr);

public static class ExternalTools
{
    public static string GetWindowsCompileArguments(string compiler, string cSourcePath, string outputPath)
    {
        return string.Join(" ", GetWindowsCompileArgumentList(compiler, cSourcePath, outputPath).Select(QuoteArgument));
    }

    public static IReadOnlyList<string> GetWindowsCompileArgumentList(string compiler, string cSourcePath, string outputPath)
    {
        return compiler switch
        {
            "clang-cl" or "cl" => ["/nologo", "/TC", "/O2", cSourcePath, $"/Fe:{outputPath}", "/link", "kernel32.lib"],
            _ => throw new ZorbCompilerException($"Unsupported Windows compiler '{compiler}'.")
        };
    }

    public static string NormalizeWindowsExecutablePath(string outputPath)
    {
        return outputPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? outputPath
            : outputPath + ".exe";
    }

    public static string EnsureToolAvailable(params string[] toolNames)
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

    public static bool IsToolAvailable(string toolName)
    {
        var locator = OperatingSystem.IsWindows() ? "where" : "which";
        var check = RunProcess(locator, [toolName], Directory.GetCurrentDirectory());
        return check.ExitCode == 0 && !string.IsNullOrWhiteSpace(check.StdOut);
    }

    public static CommandResult RunProcess(string fileName, string arguments, string workingDirectory)
    {
        return RunProcessCore(fileName, arguments, workingDirectory, timeoutMilliseconds: null);
    }

    public static CommandResult RunProcess(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        return RunProcessCore(fileName, arguments, workingDirectory, timeoutMilliseconds: null);
    }

    public static CommandResult RunProcessWithTimeout(string fileName, string arguments, string workingDirectory, int timeoutMilliseconds)
    {
        return RunProcessCore(fileName, arguments, workingDirectory, timeoutMilliseconds);
    }

    public static CommandResult RunProcessWithTimeout(string fileName, IEnumerable<string> arguments, string workingDirectory, int timeoutMilliseconds)
    {
        return RunProcessCore(fileName, arguments, workingDirectory, timeoutMilliseconds);
    }

    public static CommandResult RunProcessWithTimeout(string fileName, string arguments, string workingDirectory, TimeSpan timeout)
    {
        return RunProcessCore(fileName, arguments, workingDirectory, (int)timeout.TotalMilliseconds);
    }

    public static CommandResult RunProcessWithTimeout(string fileName, IEnumerable<string> arguments, string workingDirectory, TimeSpan timeout)
    {
        return RunProcessCore(fileName, arguments, workingDirectory, (int)timeout.TotalMilliseconds);
    }

    private static CommandResult RunProcessCore(string fileName, IEnumerable<string> arguments, string workingDirectory, int? timeoutMilliseconds)
    {
        var startInfo = CreateProcessStartInfo(fileName, workingDirectory);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return RunProcessCore(startInfo, timeoutMilliseconds);
    }

    private static CommandResult RunProcessCore(string fileName, string arguments, string workingDirectory, int? timeoutMilliseconds)
    {
        var startInfo = CreateProcessStartInfo(fileName, workingDirectory);
        startInfo.Arguments = arguments;

        return RunProcessCore(startInfo, timeoutMilliseconds);
    }

    private static ProcessStartInfo CreateProcessStartInfo(string fileName, string workingDirectory)
    {
        return new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
    }

    private static CommandResult RunProcessCore(ProcessStartInfo startInfo, int? timeoutMilliseconds)
    {
        using var process = Process.Start(startInfo) ?? throw new ZorbCompilerException($"Failed to start process '{startInfo.FileName}'.");
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
                    // Intentionally ignored: the process may have already exited,
                    // and a kill failure should not mask the timeout diagnostic.
                }

                throw new ZorbCompilerException($"Process '{startInfo.FileName}' timed out after {timeoutMilliseconds.Value / 1000} seconds.");
            }
        }
        else
        {
            process.WaitForExit();
        }

        var stdOut = stdOutTask.GetAwaiter().GetResult();
        var stdErr = stdErrTask.GetAwaiter().GetResult();
        process.WaitForExit();
        return new CommandResult(process.ExitCode, stdOut, stdErr);
    }

    public static IReadOnlyList<string> SplitCommandLine(string arguments)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var ch in arguments)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length == 0 || argument.Any(char.IsWhiteSpace) || argument.Contains('"', StringComparison.Ordinal))
            return "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

        return argument;
    }
}
