using System.Buffers.Binary;
using System.Text.Json;
using Cn.Pcln.Terracotta.Contracts;

namespace Cn.Pcln.Terracotta.Infrastructure;

public static class IpcFraming
{
    public static async ValueTask WriteAsync(
        Stream stream,
        IpcEnvelope envelope,
        JsonSerializerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(options);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(envelope, options);
        if (payload.Length is 0 or > ProtocolVersion.MaximumFrameBytes)
            throw new HelperProtocolException("IPC frame size is outside the allowed range.");

        byte[] header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)payload.Length));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<IpcEnvelope> ReadAsync(
        Stream stream,
        JsonSerializerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        byte[] header = new byte[sizeof(uint)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > ProtocolVersion.MaximumFrameBytes)
            throw new HelperProtocolException("IPC peer sent an invalid frame size.");

        byte[] payload = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<IpcEnvelope>(payload, options)
                ?? throw new HelperProtocolException("IPC peer sent an empty envelope.");
        }
        catch (JsonException exception)
        {
            throw new HelperProtocolException("IPC peer sent malformed JSON.", exception);
        }
    }
}
