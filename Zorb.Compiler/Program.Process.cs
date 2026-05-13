using System.Diagnostics;

partial class Program
{
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
            _ => throw new Zorb.Compiler.Utils.ZorbCompilerException($"Unsupported Windows compiler '{compiler}'.")
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
            throw new Zorb.Compiler.Utils.ZorbCompilerException($"Required tool '{toolNames[0]}' was not found in PATH.");

        throw new Zorb.Compiler.Utils.ZorbCompilerException($"Required tools were not found in PATH. Install one of: {string.Join(", ", toolNames)}.");
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
                    // Intentionally ignored: the process may have already exited,
                    // and a kill failure should not mask the timeout diagnostic.
                }

                throw new Zorb.Compiler.Utils.ZorbCompilerException($"Process '{fileName}' timed out after {timeoutMilliseconds.Value / 1000} seconds.");
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
}
