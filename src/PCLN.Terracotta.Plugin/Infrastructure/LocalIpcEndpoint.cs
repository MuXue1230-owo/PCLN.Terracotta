using System.Security.Cryptography;

namespace Cn.Pcln.Terracotta.Infrastructure;

public sealed class LocalIpcEndpoint : IAsyncDisposable
{
    private LocalIpcEndpoint(string address, string? ownedDirectory)
    {
        Address = address;
        OwnedDirectory = ownedDirectory;
    }

    public string Address { get; }

    private string? OwnedDirectory { get; }

    public static LocalIpcEndpoint Create(string pluginTemporaryDirectory)
    {
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        if (OperatingSystem.IsWindows())
            return new LocalIpcEndpoint($@"\\.\pipe\pcln-terracotta-{nonce}", null);

        string root = Path.Combine(Path.GetFullPath(pluginTemporaryDirectory), "ipc", nonce[..12]);
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new LocalIpcEndpoint(Path.Combine(root, $"terracotta-{nonce}.sock"), root);
    }

    public ValueTask DisposeAsync()
    {
        if (OwnedDirectory is not null && Directory.Exists(OwnedDirectory))
        {
            string socket = Address;
            if (File.Exists(socket))
                File.Delete(socket);
            Directory.Delete(OwnedDirectory, false);
        }

        return ValueTask.CompletedTask;
    }
}
