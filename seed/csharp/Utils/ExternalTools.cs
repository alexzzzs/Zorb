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
        return GetWindowsCompileAndLinkArgumentList(compiler, cSourcePath, [], outputPath);
    }

    public static IReadOnlyList<string> GetWindowsCompileAndLinkArgumentList(
        string compiler,
        string cSourcePath,
        IReadOnlyList<string> additionalInputs,
        string outputPath)
    {
        var linkerSelectionArguments = compiler == "clang-cl"
            ? new[] { "-fuse-ld=lld" }
            : Array.Empty<string>();

        return compiler switch
        {
            "clang-cl" or "cl" => [
                "/nologo",
                .. linkerSelectionArguments,
                "/TC",
                "/O2",
                "/MD",
                cSourcePath,
                .. additionalInputs,
                $"/Fe:{outputPath}",
                "/link",
                "/subsystem:console",
                "kernel32.lib",
                "ws2_32.lib"
            ],
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
            if (FindAvailableTool(toolName) != null)
                return toolName;
        }

        if (toolNames.Length == 1)
            throw new ZorbCompilerException($"Required tool '{toolNames[0]}' was not found in PATH.");

        throw new ZorbCompilerException($"Required tools were not found in PATH. Install one of: {string.Join(", ", toolNames)}.");
    }

    public static bool IsToolAvailable(string toolName)
    {
        return FindAvailableTool(toolName) != null;
    }

    public static string? FindAvailableTool(string toolName)
    {
        var locator = OperatingSystem.IsWindows() ? "where" : "which";
        var check = RunProcess(locator, [toolName], Directory.GetCurrentDirectory());
        if (check.ExitCode != 0)
            return null;

        return check.StdOut
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }

    public static string? FindAvailableToolByPrefix(string toolNamePrefix)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var executableSuffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        string? bestMatch = null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Directory.Exists(directory))
                continue;

            IEnumerable<string> candidates;
            try
            {
                candidates = Directory.EnumerateFiles(directory);
            }
            catch
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                var fileName = Path.GetFileName(candidate);
                if (!fileName.StartsWith(toolNamePrefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    continue;

                if (OperatingSystem.IsWindows() &&
                    !fileName.EndsWith(executableSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (bestMatch == null || comparer.Compare(fileName, Path.GetFileName(bestMatch)) > 0)
                    bestMatch = candidate;
            }
        }

        return bestMatch;
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
