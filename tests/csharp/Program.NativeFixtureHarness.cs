using System.Runtime.InteropServices;

internal static partial class Program
{
    private const string NativeFixtureCompilerEnvironmentVariable = "ZORB_NATIVE_FIXTURE_COMPILER";
    private const int NativeFixtureBootstrapTimeoutSeconds = 300;
    private const int NativeFixtureCommandTimeoutSeconds = 60;
    private const int NativeFixtureConcurrentRunCount = 8;

    private sealed class NativeFixtureHarness : IDisposable
    {
        private readonly string compilerPath;
        private readonly string projectRoot;
        private readonly string ownedTempDirectory;
        private int outputIndex;

        private NativeFixtureHarness(string compilerPath, string projectRoot, string ownedTempDirectory)
        {
            this.compilerPath = compilerPath;
            this.projectRoot = projectRoot;
            this.ownedTempDirectory = ownedTempDirectory;
        }

        public static NativeFixtureHarness Create(string projectRoot)
        {
            var configuredCompiler = Environment.GetEnvironmentVariable(NativeFixtureCompilerEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configuredCompiler))
            {
                var compilerPath = Path.GetFullPath(configuredCompiler, projectRoot);
                if (!File.Exists(compilerPath))
                    throw new Exception($"{NativeFixtureCompilerEnvironmentVariable} does not exist: {compilerPath}");
                var outputDirectory = CreateTempDirectory();
                return new NativeFixtureHarness(compilerPath, projectRoot, outputDirectory);
            }

            if (!OperatingSystem.IsLinux())
            {
                throw new Exception(
                    $"Set {NativeFixtureCompilerEnvironmentVariable} to a native Zorb compiler on this platform.");
            }

            var tempDirectory = CreateTempDirectory();
            try
            {
                var compilerPath = Path.Combine(tempDirectory, "zorb");
                var bootstrapScript = Path.Combine(projectRoot, "scripts", "bootstrap-compiler.sh");
                var result = RunProcessWithTimeoutArgs(
                    "bash",
                    [bootstrapScript, compilerPath],
                    projectRoot,
                    TimeSpan.FromSeconds(NativeFixtureBootstrapTimeoutSeconds));
                if (result.ExitCode != 0 || !File.Exists(compilerPath))
                {
                    throw new Exception(
                        $"Unable to bootstrap the native fixture compiler.{Environment.NewLine}" +
                        $"{result.StdErr}{result.StdOut}".Trim());
                }
                return new NativeFixtureHarness(compilerPath, projectRoot, tempDirectory);
            }
            catch
            {
                TryDeleteDirectory(tempDirectory);
                throw;
            }
        }

        public void Validate(string mainPath, FixtureExpectations expectations)
        {
            var expectsSuccess = expectations.ExpectedPhase == FixturePhase.Success &&
                expectations.ExpectedErrors.Count == 0;
            IReadOnlyList<string> arguments;
            string? outputPath = null;
            if (expectsSuccess)
            {
                outputPath = Path.Combine(
                    ownedTempDirectory, $"fixture-{outputIndex++}.ll");
                arguments = ["build", mainPath, "--output-kind", "llvm-ir", "-o", outputPath];
            }
            else
            {
                arguments = ["check", mainPath];
            }
            var result = RunProcessWithTimeoutArgs(
                compilerPath,
                arguments,
                projectRoot,
                TimeSpan.FromSeconds(NativeFixtureCommandTimeoutSeconds));
            if (expectsSuccess && result.ExitCode != 0)
            {
                throw new Exception(
                    $"Native Zorb rejected a successful fixture.{Environment.NewLine}" +
                    $"{result.StdErr}{result.StdOut}".Trim());
            }
            if (expectsSuccess && (outputPath is null || !File.Exists(outputPath) ||
                new FileInfo(outputPath).Length == 0))
            {
                throw new Exception("Native Zorb reported success without emitting LLVM IR.");
            }
            if (!expectsSuccess && (result.ExitCode == 0 ||
                !result.StdErr.Contains("error[", StringComparison.Ordinal)))
            {
                throw new Exception(
                    $"Native Zorb did not reject the fixture with a structured diagnostic.{Environment.NewLine}" +
                    $"{result.StdErr}{result.StdOut}".Trim());
            }
        }

        public void ValidateRunExitCode(string mainPath, int expectedExitCode)
        {
            var result = RunProcessWithTimeoutArgs(
                compilerPath,
                ["run", mainPath],
                projectRoot,
                TimeSpan.FromSeconds(NativeFixtureCommandTimeoutSeconds));
            if (result.ExitCode != expectedExitCode)
            {
                throw new Exception(
                    $"Native Zorb returned {result.ExitCode} for a program that exited with " +
                    $"{expectedExitCode}.{Environment.NewLine}{result.StdErr}{result.StdOut}".Trim());
            }
        }

        public void ValidateConcurrentRuns(string mainPath)
        {
            var runs = Enumerable.Range(0, NativeFixtureConcurrentRunCount)
                .Select(_ => Task.Run(() => RunProcessWithTimeoutArgs(
                    compilerPath,
                    ["run", mainPath],
                    projectRoot,
                    TimeSpan.FromSeconds(NativeFixtureCommandTimeoutSeconds))))
                .ToArray();
            Task.WaitAll(runs);

            var failedRuns = runs
                .Select((run, index) => (index, result: run.Result))
                .Where(run => run.result.ExitCode != 0)
                .ToArray();
            if (failedRuns.Length > 0)
            {
                var details = string.Join(
                    Environment.NewLine,
                    failedRuns.Select(run =>
                        $"run {run.index}: exit {run.result.ExitCode}{Environment.NewLine}" +
                        $"{run.result.StdErr}{run.result.StdOut}".Trim()));
                throw new Exception(
                    $"{failedRuns.Length} concurrent native Zorb run(s) failed.{Environment.NewLine}{details}");
            }
        }

        public void ValidateNativeLinkArgsRequireExecutable(string mainPath)
        {
            var outputPath = Path.Combine(ownedTempDirectory, "invalid-native-link-args.ll");
            var result = RunProcessWithTimeoutArgs(
                compilerPath,
                [
                    "build",
                    mainPath,
                    "--output-kind",
                    "llvm-ir",
                    "-o",
                    outputPath,
                    "--native-link-args",
                    "-lm"
                ],
                projectRoot,
                TimeSpan.FromSeconds(NativeFixtureCommandTimeoutSeconds));
            if (result.ExitCode != 64)
            {
                throw new Exception(
                    $"Native Zorb accepted linker arguments for non-executable output " +
                    $"with exit code {result.ExitCode}.{Environment.NewLine}" +
                    $"{result.StdErr}{result.StdOut}".Trim());
            }
        }

        public void ValidateNamedTargetEmission(string mainPath)
        {
            var nativeLinuxTriple = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "aarch64-unknown-linux-gnu"
                : "x86_64-pc-linux-gnu";
            var nativeWindowsTriple = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "aarch64-pc-windows-msvc"
                : "x86_64-pc-windows-msvc";
            var cases = new (string Name, string Triple)[]
            {
                ("host-linux", nativeLinuxTriple),
                ("freestanding-linux", nativeLinuxTriple),
                ("host-linux-aarch64", "aarch64-unknown-linux-gnu"),
                ("freestanding-linux-aarch64", "aarch64-unknown-linux-gnu"),
                ("bare-metal-x86_64", "x86_64-unknown-none-elf"),
                ("host-windows", nativeWindowsTriple)
            };
            foreach (var targetCase in cases)
            {
                var outputPath = Path.Combine(
                    ownedTempDirectory,
                    $"named-target-{targetCase.Name}.ll");
                var result = RunProcessWithTimeoutArgs(
                    compilerPath,
                    [
                        "build", mainPath, "--target", targetCase.Name,
                        "--output-kind", "llvm-ir", "-o", outputPath
                    ],
                    projectRoot,
                    TimeSpan.FromSeconds(NativeFixtureCommandTimeoutSeconds));
                if (result.ExitCode != 0 || !File.Exists(outputPath))
                {
                    throw new Exception(
                        $"Native Zorb failed named target '{targetCase.Name}'.{Environment.NewLine}" +
                        $"{result.StdErr}{result.StdOut}".Trim());
                }
                var llvmIr = File.ReadAllText(outputPath);
                if (!llvmIr.Contains(
                    $"target triple = \"{targetCase.Triple}\"",
                    StringComparison.Ordinal))
                {
                    throw new Exception(
                        $"Native target '{targetCase.Name}' did not resolve to '{targetCase.Triple}'.");
                }
            }
        }

        public bool CanValidateBareMetalLinking() =>
            OperatingSystem.IsLinux() &&
            RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
            IsBareMetalLinkerAvailable();

        public void ValidateBareMetalLinking(string mainPath)
        {
            var bareMetalLinker = FindBareMetalLinker();
            if (bareMetalLinker == null)
                throw new InvalidOperationException("Bare-metal linking is not available on this host.");

            var outputPath = Path.Combine(ownedTempDirectory, "native-kernel.elf");
            var linkerScriptPath = Path.Combine(ownedTempDirectory, "native-kernel.ld");
            var result = RunProcessWithTimeoutArgs(
                compilerPath,
                [
                    "build", mainPath, "--target", "bare-metal-x86_64",
                    "-o", outputPath, "--emit-linker-script", linkerScriptPath
                ],
                projectRoot,
                TimeSpan.FromSeconds(NativeFixtureCommandTimeoutSeconds),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ZORB_LLD"] = bareMetalLinker
                });
            if (result.ExitCode != 0 || !File.Exists(outputPath) || !File.Exists(linkerScriptPath))
            {
                throw new Exception(
                    $"Native bare-metal linking failed.{Environment.NewLine}" +
                    $"{result.StdErr}{result.StdOut}".Trim());
            }
            var linkerScript = File.ReadAllText(linkerScriptPath);
            if (!linkerScript.Contains("ENTRY(_start)", StringComparison.Ordinal))
                throw new Exception("Native bare-metal linking emitted an unexpected linker script.");
        }

        public void Dispose()
        {
            TryDeleteDirectory(ownedTempDirectory);
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(), $"zorb-native-fixtures-{Environment.ProcessId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
