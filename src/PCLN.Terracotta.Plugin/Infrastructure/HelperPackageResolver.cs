namespace Cn.Pcln.Terracotta.Infrastructure;

public static class HelperPackageResolver
{
    public static string GetRelativePath(string rid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);
        return $"runtimes/{rid}/native/{RuntimePlatformResolver.HelperFileName(rid)}";
    }

    public static string Resolve(string entryAssemblyPath, string rid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryAssemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);
        string assemblyDirectory = Path.GetDirectoryName(Path.GetFullPath(entryAssemblyPath))
            ?? throw new ArgumentException("Entry assembly has no parent directory.", nameof(entryAssemblyPath));
        string packageRoot = Path.GetFullPath(Path.Combine(assemblyDirectory, "..", ".."));
        string helper = Path.GetFullPath(Path.Combine(
            packageRoot,
            "runtimes",
            rid,
            "native",
            RuntimePlatformResolver.HelperFileName(rid)));
        string expectedRoot = packageRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!helper.StartsWith(expectedRoot, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Resolved Helper path escaped the plugin package.");
        }

        return helper;
    }
}
