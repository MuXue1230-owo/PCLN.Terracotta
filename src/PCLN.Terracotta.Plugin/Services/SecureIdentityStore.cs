using System.Security.Cryptography;
using PCL.N.Plugin;

namespace Cn.Pcln.Terracotta.Services;

public sealed class SecureIdentityStore(IPluginSecureStorage? storage)
{
    private const int IdentityBytes = 32;
    private static readonly PluginSecretKey PrivateKey = new("identity.private-key");

    public async ValueTask<byte[]> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        if (storage is null)
            throw new SecureIdentityException("安全存储服务不可用，无法初始化陶瓦身份。");

        PluginSecretReadResult read;
        try
        {
            read = await storage.ReadAsync(PrivateKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SecureIdentityException("无法读取陶瓦安全身份。", exception);
        }

        if (read.Status == PluginSecureStorageStatus.Success)
        {
            if (read.Value is { Length: IdentityBytes } value)
                return value;
            CryptographicOperations.ZeroMemory(read.Value ?? []);
            throw new SecureIdentityException("安全存储中的陶瓦身份数据已损坏。");
        }

        if (read.Status != PluginSecureStorageStatus.NotFound)
            throw new SecureIdentityException("无法读取陶瓦安全身份。");

        byte[] created = RandomNumberGenerator.GetBytes(IdentityBytes);
        PluginSecretOperationResult write;
        try
        {
            write = await storage.WriteAsync(PrivateKey, created, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CryptographicOperations.ZeroMemory(created);
            throw new SecureIdentityException("无法将陶瓦身份写入安全存储。", exception);
        }

        if (write.Status != PluginSecureStorageStatus.Success)
        {
            CryptographicOperations.ZeroMemory(created);
            throw new SecureIdentityException("无法将陶瓦身份写入安全存储。");
        }

        return created;
    }
}

public sealed class SecureIdentityException : InvalidOperationException
{
    public SecureIdentityException(string message)
        : base(message)
    {
    }

    public SecureIdentityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
