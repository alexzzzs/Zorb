partial class Program
{
    private static Options? ParseArgs(string[] args)
    {
        var options = new Options();
        int index = ParseCommandMode(args, options);

        for (; index < args.Length; index++)
        {
            if (!TryParseArgument(args, ref index, options))
                return null;
        }

        return ValidateParsedOptions(options);
    }

    private static int ParseCommandMode(string[] args, Options options)
    {
        if (args.Length == 0)
            return 0;

        if (string.Equals(args[0], "build", StringComparison.Ordinal))
        {
            options.Mode = CommandMode.Build;
            options.OutputPath = "out";
            return 1;
        }

        if (string.Equals(args[0], "run", StringComparison.Ordinal))
        {
            options.Mode = CommandMode.Run;
            options.OutputPath = "out";
            return 1;
        }

        return 0;
    }

    private static bool TryParseArgument(string[] args, ref int index, Options options)
    {
        var arg = args[index];
        switch (arg)
        {
            case "-h":
            case "--help":
                options.ShowHelp = true;
                return true;

            case "--version":
                options.ShowVersion = true;
                return true;

            case "-nostdlib":
                options.LegacyNoStdLib = true;
                return true;

            case "--target":
                return TryParseTargetOption(args, ref index, options);

            case "--check":
                options.CheckOnly = true;
                return true;

            case "--emit-llvm":
                return TryEnableEmitLlvmMode(options);

            case "--dump-tokens":
                options.DumpTokens = true;
                return true;

            case "--linker-script":
                return TryReadOptionValue(args, ref index, "--linker-script", value => options.LinkerScriptPath = value);

            case "--emit-linker-script":
                return TryReadOptionValue(args, ref index, "--emit-linker-script", value => options.EmitLinkerScriptPath = value);

            case "--native-flags":
                return TryReadOptionValue(args, ref index, "--native-flags", value => options.NativeFlags = value);

            case "-o":
            case "--output":
                return TryReadOutputPath(args, ref index, options, arg);

            default:
                return TryParseInputPath(arg, options);
        }
    }

    private static bool TryParseTargetOption(string[] args, ref int index, Options options)
    {
        if (!TryReadOptionValue(args, ref index, "--target", out var targetText))
            return false;

        if (!TryParseCompilationTarget(targetText, out var parsedTarget))
            return FailUsage($"Unknown target: {targetText}");

        options.Target = parsedTarget;
        return true;
    }

    private static bool TryEnableEmitLlvmMode(Options options)
    {
        if (options.Mode is CommandMode.Build or CommandMode.Run)
            return FailUsage("Option --emit-llvm cannot be combined with build or run.");

        options.Mode = CommandMode.EmitLlvmIr;
        if (!options.OutputPathExplicitlySet)
            options.OutputPath = "out.ll";
        return true;
    }

    private static bool TryReadOutputPath(string[] args, ref int index, Options options, string optionName)
    {
        if (!TryReadOptionValue(args, ref index, optionName, out var outputPath))
            return false;

        options.OutputPath = outputPath;
        options.OutputPathExplicitlySet = true;
        return true;
    }

    private static bool TryParseInputPath(string arg, Options options)
    {
        if (arg.StartsWith("-", StringComparison.Ordinal))
            return FailUsage($"Unknown option: {arg}");

        if (!string.IsNullOrEmpty(options.InputPath))
            return FailUsage($"Unexpected extra input path: {arg}");

        options.InputPath = arg;
        return true;
    }

    private static Options? ValidateParsedOptions(Options options)
    {
        if (options.ShowHelp || options.ShowVersion)
            return options;

        if (string.IsNullOrEmpty(options.InputPath))
            return FailUsage("Missing input file.", options);

        if (options.CheckOnly && options.OutputPathExplicitlySet)
            return FailUsage("Option -o/--output is not valid with --check.", options);

        if (options.OutputPathExplicitlySet && options.Mode == CommandMode.Run)
            return FailUsage("Option -o/--output is not valid with run.", options);

        if (options.CheckOnly && options.Mode != CommandMode.EmitLlvmIr)
            return FailUsage("Option --check cannot be combined with build or run.", options);

        return options;
    }

    private static bool TryReadOptionValue(string[] args, ref int index, string optionName, Action<string> assign)
    {
        if (!TryReadOptionValue(args, ref index, optionName, out var value))
            return false;

        assign(value);
        return true;
    }

    private static bool TryReadOptionValue(string[] args, ref int index, string optionName, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            return FailUsage($"Missing value for {optionName}.");
        }

        value = args[++index];
        return true;
    }

    private static bool FailUsage(string message)
    {
        Console.Error.WriteLine(message);
        PrintUsage();
        return false;
    }

    private static Options? FailUsage(string message, Options _)
    {
        return FailUsage(message) ? _ : null;
    }
}
