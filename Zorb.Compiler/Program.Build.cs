using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Zorb.Compiler.Codegen;
using Zorb.Compiler.Utils;

partial class Program
{
    private const string AArch64LinuxCrossCompilerEnvironmentVariable = "ZORB_AARCH64_LINUX_GCC";
    private const string AArch64LinuxQemuEnvironmentVariable = "ZORB_QEMU_AARCH64";
    private const string AArch64LinuxSysrootEnvironmentVariable = "ZORB_AARCH64_LINUX_SYSROOT";
    private const string DefaultAArch64LinuxSysroot = "/usr/aarch64-linux-gnu";
}
