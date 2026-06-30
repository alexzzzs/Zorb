using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Zorb.Compiler.Codegen;
using Zorb.Compiler.Utils;

partial class Program
{
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
}
