partial class Program
{
    private static bool TryParseCompilationTarget(string text, out CompilationTarget target)
    {
        switch (text)
        {
            case "host-linux":
                target = CompilationTarget.HostLinux;
                return true;
            case "freestanding-linux":
                target = CompilationTarget.FreestandingLinux;
                return true;
            case "host-linux-aarch64":
                target = CompilationTarget.HostLinuxAArch64;
                return true;
            case "freestanding-linux-aarch64":
                target = CompilationTarget.FreestandingLinuxAArch64;
                return true;
            case "bare-metal-x86_64":
                target = CompilationTarget.BareMetalX86_64;
                return true;
            case "host-windows":
                target = CompilationTarget.HostWindows;
                return true;
            default:
                target = default;
                return false;
        }
    }

    private static string FormatTarget(CompilationTarget target)
    {
        return target switch
        {
            CompilationTarget.HostLinux => "host-linux",
            CompilationTarget.FreestandingLinux => "freestanding-linux",
            CompilationTarget.HostLinuxAArch64 => "host-linux-aarch64",
            CompilationTarget.FreestandingLinuxAArch64 => "freestanding-linux-aarch64",
            CompilationTarget.BareMetalX86_64 => "bare-metal-x86_64",
            CompilationTarget.HostWindows => "host-windows",
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
        };
    }
}
