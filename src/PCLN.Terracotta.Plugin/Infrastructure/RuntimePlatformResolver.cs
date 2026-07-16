using System.Runtime.InteropServices;

namespace Cn.Pcln.Terracotta.Infrastructure;

public static class RuntimePlatformResolver
{
    public static string ResolveCurrentRid() => Resolve(
        OperatingSystem.IsWindows(),
        OperatingSystem.IsLinux(),
        OperatingSystem.IsMacOS(),
        RuntimeInformation.ProcessArchitecture);

    public static string Resolve(bool windows, bool linux, bool macos, Architecture architecture)
    {
        string operatingSystem = windows ? "win" : linux ? "linux" : macos ? "osx" :
            throw new PlatformNotSupportedException("Terracotta supports Windows, Linux, and macOS only.");
        string machine = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException($"Terracotta does not support {architecture}.")
        };
        return operatingSystem + '-' + machine;
    }

    public static string HelperFileName(string rid) =>
        rid.StartsWith("win-", StringComparison.Ordinal) ? "terracotta-helper.exe" : "terracotta-helper";
}
