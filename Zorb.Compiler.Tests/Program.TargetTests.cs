using System.Runtime.InteropServices;
using System.Text;

internal static partial class Program
{
    private static void RunAArch64LinuxCrossTargetTests(string fixtureRoot)
    {
        if (!OperatingSystem.IsLinux())
            return;

        var shouldRun = string.Equals(
            Environment.GetEnvironmentVariable(RunAArch64TestsEnvironmentVariable),
            "1",
            StringComparison.Ordinal);

        var linuxCompiler = FindAArch64LinuxCompiler();
        var qemu = FindAArch64Qemu();
        var isNativeAArch64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        var canBuild = isNativeAArch64 || linuxCompiler != null;
        var canRun = isNativeAArch64 || qemu != null;

        if (!shouldRun && (!canBuild || !canRun))
            return;

        if (!canBuild)
        {
            throw new Exception(
                $"AArch64 Linux target tests require either an aarch64 host or an AArch64 cross-compiler. Install 'aarch64-linux-gnu-gcc' or set {AArch64CrossCompilerEnvironmentVariable}.");
        }

        if (!canRun)
        {
            throw new Exception(
                $"AArch64 Linux target tests require either an aarch64 host or qemu-aarch64. Install 'qemu-user' or set {AArch64QemuEnvironmentVariable}.");
        }

        EnsureToolAvailable("timeout");

        var testProjectRoot = FindAncestorContainingFile(AppContext.BaseDirectory, "Zorb.Compiler.Tests.csproj");
        var projectRoot = Directory.GetParent(testProjectRoot)?.FullName
            ?? throw new Exception($"Unable to determine repository root from '{testProjectRoot}'.");
        var compilerInvocation = GetCompilerInvocation(projectRoot);

        RunAArch64EmissionVerification(compilerInvocation, projectRoot, fixtureRoot);
        RunAArch64RuntimeExpectationTests(fixtureRoot);

        WithTempDirectory("zorb-aarch64-cli-tests", tempDir =>
        {
            foreach (var workflowCase in GetAArch64CliWorkflowCases())
                RunCliWorkflowFixture(compilerInvocation, projectRoot, fixtureRoot, tempDir, workflowCase);
        });
    }

    private static CliWorkflowCase[] GetAArch64CliWorkflowCases()
    {
        return
        [
            new CliWorkflowCase("runtime_hello_world", "freestanding-linux-aarch64"),
            new CliWorkflowCase("runtime_string_escapes", "freestanding-linux-aarch64"),
            new CliWorkflowCase("runtime_condition_catch", "freestanding-linux-aarch64"),
            new CliWorkflowCase("runtime_task_yield_without_fiber", "freestanding-linux-aarch64"),
            new CliWorkflowCase("runtime_host_platform_branch", "host-linux-aarch64"),
            new CliWorkflowCase("runtime_stdlib_support_checks", "host-linux-aarch64")
        ];
    }

    private static AArch64RuntimeCase[] GetAArch64RuntimeCases()
    {
        return
        [
            new AArch64RuntimeCase("runtime_hello_world", "freestanding-linux-aarch64"),
            new AArch64RuntimeCase("runtime_string_escapes", "freestanding-linux-aarch64"),
            new AArch64RuntimeCase("runtime_condition_catch", "freestanding-linux-aarch64"),
            new AArch64RuntimeCase("runtime_numeric_match", "freestanding-linux-aarch64"),
            new AArch64RuntimeCase("runtime_bool_match", "freestanding-linux-aarch64"),
            new AArch64RuntimeCase("runtime_generic_enum", "freestanding-linux-aarch64"),
            new AArch64RuntimeCase("runtime_generic_union", "freestanding-linux-aarch64"),
            new AArch64RuntimeCase("runtime_generic_inference_and_coercions", "freestanding-linux-aarch64"),
            new AArch64RuntimeCase("runtime_tagged_union", "freestanding-linux-aarch64"),
            new AArch64RuntimeCase("runtime_union_match_binding", "freestanding-linux-aarch64"),
            new AArch64RuntimeCase("runtime_task_yield_without_fiber", "freestanding-linux-aarch64"),
            new AArch64RuntimeCase("runtime_task_many_yields", "freestanding-linux-aarch64"),
            new AArch64RuntimeCase("runtime_task_round_robin", "freestanding-linux-aarch64"),
            new AArch64RuntimeCase("runtime_async_reinit_safe", "freestanding-linux-aarch64"),
            new AArch64RuntimeCase("runtime_host_platform_branch", "host-linux-aarch64"),
            new AArch64RuntimeCase("runtime_host_platform_catch", "host-linux-aarch64"),
            new AArch64RuntimeCase("runtime_host_import_alias", "host-linux-aarch64"),
            new AArch64RuntimeCase("runtime_host_stderr_write", "host-linux-aarch64"),
            new AArch64RuntimeCase("runtime_host_nonzero_exit", "host-linux-aarch64"),
            new AArch64RuntimeCase("runtime_stdlib_cross_platform", "host-linux-aarch64"),
            new AArch64RuntimeCase("runtime_stdlib_support_checks", "host-linux-aarch64")
        ];
    }

    private static void RunAArch64RuntimeExpectationTests(string fixtureRoot)
    {
        foreach (var runtimeCase in GetAArch64RuntimeCases())
        {
            var fixtureDir = Path.Combine(fixtureRoot, runtimeCase.FixtureName);
            var mainPath = Path.Combine(fixtureDir, "main.zorb");
            var runtimeExpectation = ReadRuntimeExpectationForTarget(fixtureDir, runtimeCase.TargetName);
            RunLlvmRuntimeExpectation(fixtureDir, mainPath, runtimeExpectation);
        }
    }

    private static void RunAArch64EmissionVerification(CompilerInvocation compilerInvocation, string projectRoot, string fixtureRoot)
    {
        var emissionCases = new[]
        {
            new AArch64EmissionCase("runtime_hello_world", "freestanding-linux-aarch64", ["svc #0"]),
            new AArch64EmissionCase("stdlib_linux_arch_syscall_codegen", "freestanding-linux-aarch64", ["svc #0"]),
            new AArch64EmissionCase("stdlib_task_aarch64_codegen", "freestanding-linux-aarch64", ["svc #0"]),
            new AArch64EmissionCase("runtime_host_platform_branch", "host-linux-aarch64", [])
        };

        WithTempDirectory("zorb-aarch64-emission", tempDir =>
        {
            foreach (var emissionCase in emissionCases)
            {
                var fixtureDir = Path.Combine(fixtureRoot, emissionCase.FixtureName);
                var mainPath = Path.Combine(fixtureDir, "main.zorb");
                var llvmPath = Path.Combine(tempDir, emissionCase.FixtureName + ".ll");
                var binaryPath = Path.Combine(tempDir, emissionCase.FixtureName);

                var emission = RunProcessWithTimeoutArgs(
                    compilerInvocation.FileName,
                    BuildCommandArguments(compilerInvocation, "--emit-llvm", mainPath, "--target", emissionCase.TargetName, "-o", llvmPath),
                    projectRoot,
                    TimeSpan.FromSeconds(30));
                if (emission.ExitCode != 0)
                {
                    throw new Exception(
                        $"AArch64 LLVM emission failed for fixture '{emissionCase.FixtureName}' with exit code {emission.ExitCode}.{Environment.NewLine}{emission.StdErr}{emission.StdOut}".Trim());
                }

                if (!File.Exists(llvmPath) || new FileInfo(llvmPath).Length == 0)
                    throw new Exception($"AArch64 LLVM emission did not produce output for fixture '{emissionCase.FixtureName}'.");

                var llvmIr = File.ReadAllText(llvmPath, Encoding.UTF8);
                AssertTextContains(llvmIr, "target triple = \"aarch64-unknown-linux-gnu\"");
                foreach (var expectedLlvmText in emissionCase.ExpectedLlvmIrSubstrings)
                    AssertTextContains(llvmIr, expectedLlvmText);

                var build = RunProcessWithTimeoutArgs(
                    compilerInvocation.FileName,
                    BuildCommandArguments(compilerInvocation, "build", mainPath, "--target", emissionCase.TargetName, "-o", binaryPath),
                    projectRoot,
                    TimeSpan.FromSeconds(30));
                if (build.ExitCode != 0)
                {
                    throw new Exception(
                        $"AArch64 build failed for fixture '{emissionCase.FixtureName}' with exit code {build.ExitCode}.{Environment.NewLine}{build.StdErr}{build.StdOut}".Trim());
                }

                if (!File.Exists(binaryPath) || new FileInfo(binaryPath).Length == 0)
                    throw new Exception($"AArch64 build did not produce output for fixture '{emissionCase.FixtureName}'.");
            }
        });
    }
}
