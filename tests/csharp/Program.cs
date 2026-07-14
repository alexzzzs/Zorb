using System.Runtime.InteropServices;

internal static partial class Program
{
    private const string RunAArch64TestsEnvironmentVariable = "ZORB_RUN_AARCH64_TESTS";
    private const string AArch64CrossCompilerEnvironmentVariable = "ZORB_AARCH64_LINUX_GCC";
    private const string AArch64QemuEnvironmentVariable = "ZORB_QEMU_AARCH64";
    private const string AArch64SysrootEnvironmentVariable = "ZORB_AARCH64_LINUX_SYSROOT";
    private const string DefaultAArch64LinuxSysroot = "/usr/aarch64-linux-gnu";
    private const string FrontendParityOnlyEnvironmentVariable = "ZORB_FRONTEND_PARITY_ONLY";

    private static int Main()
    {
        var projectRoot = GetProjectRoot();
        var fixtureRoot = GetFixtureRoot();
        var fixtureDirs = Directory.GetDirectories(fixtureRoot).OrderBy(path => path, StringComparer.Ordinal).ToList();
        var failures = new List<string>();

        if (string.Equals(Environment.GetEnvironmentVariable(FrontendParityOnlyEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            RunNamedTest(failures, "fixture_parity_classification", RunFixtureParityClassificationTests);
            RunNamedTest(failures, "self_check_bootstrap", () => RunSelfCheckBootstrapTests(fixtureRoot));
            RunNamedTest(failures, "frontend_differential", () => RunFrontendDifferentialTests(fixtureRoot));
            if (failures.Count == 0)
                return 0;

            foreach (var failure in failures)
                Console.Error.WriteLine(failure);
            return 1;
        }

        foreach (var fixtureDir in fixtureDirs)
            RunNamedTest(failures, Path.GetFileName(fixtureDir), () => RunFixture(fixtureDir));

        RunNamedTest(failures, "cli_workflow", () => RunCliWorkflowTests(fixtureRoot));
        RunNamedTest(failures, "fixture_parity_classification", RunFixtureParityClassificationTests);
        RunNamedTest(failures, "frontend_differential", () => RunFrontendDifferentialTests(fixtureRoot));
        RunNamedTest(failures, "aarch64_linux_targets", () => RunAArch64LinuxCrossTargetTests(fixtureRoot));
        RunNamedTest(failures, "cli_bare_metal", () => RunBareMetalCliBuildTests(fixtureRoot));
        RunNamedTest(failures, "cli_args", () => RunCliArgumentValidationTests(fixtureRoot));
        RunNamedTest(failures, "self_check_bootstrap", () => RunSelfCheckBootstrapTests(fixtureRoot));
        RunNamedTest(failures, "semantic_output", () => RunSemanticDiagnosticOutputTests(fixtureRoot));
        RunNamedTest(failures, "type_checker_state_reset", RunTypeCheckerStateResetTests);
        RunNamedTest(failures, "llvm_writer_state_reset", () => RunLlvmWriterStateResetTests(fixtureRoot));
        RunNamedTest(failures, "llvm_backend_output_modes", () => RunLlvmBackendOutputModeTests(fixtureRoot));
        RunNamedTest(failures, "llvm_backend_regressions", () => RunLlvmBackendRegressionTests(fixtureRoot));
        RunNamedTest(failures, "unknown_type_cascade", RunUnknownTypeCascadeTests);
        RunNamedTest(failures, "invalid_postfix_cascade", RunInvalidPostfixCascadeTests);
        RunNamedTest(failures, "builtin_parser_reserved_declarations", RunBuiltinParserReservedDeclarationTests);
        RunNamedTest(failures, "generic_default_arity_recovery", RunGenericDefaultArityRecoveryTests);
        RunNamedTest(failures, "generic_function_default_import_alias", RunGenericFunctionDefaultImportAliasTests);
        RunNamedTest(failures, "generic_function_value_deferred_inference", RunGenericFunctionValueDeferredInferenceTests);
        RunNamedTest(failures, "generic_function_value_factory", RunGenericFunctionValueFactoryTests);
        RunNamedTest(failures, "resolved_call_metadata", RunResolvedCallMetadataTests);

        var examplesRoot = Path.Combine(projectRoot, "examples");
        var examplePaths = Directory.EnumerateFiles(examplesRoot, "*.zorb", SearchOption.AllDirectories)
            .Where(path =>
            {
                var fileName = Path.GetFileName(path);
                if (string.Equals(fileName, "main.zorb", StringComparison.Ordinal))
                    return true;

                var directory = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(directory))
                    return true;

                var siblingMainPath = Path.Combine(directory, "main.zorb");
                return !File.Exists(siblingMainPath);
            })
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        foreach (var examplePath in examplePaths)
            RunNamedTest(failures, Path.GetRelativePath(projectRoot, examplePath), () => RunExampleCompilationTest(examplePath));

        if (failures.Count > 0)
        {
            Console.Error.WriteLine();
            foreach (var failure in failures)
                Console.Error.WriteLine(failure);
            return 1;
        }

        return 0;
    }

    private static void RunNamedTest(List<string> failures, string testName, Action action)
    {
        try
        {
            action();
            Console.WriteLine($"PASS {testName}");
        }
        catch (Exception ex)
        {
            failures.Add($"{testName}: {ex.Message}");
            Console.WriteLine($"FAIL {testName}");
        }
    }
}
