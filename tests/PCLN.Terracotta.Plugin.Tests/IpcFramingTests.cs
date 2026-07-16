using System.Buffers.Binary;
using System.Text.Json;
using Cn.Pcln.Terracotta.Contracts;
using Cn.Pcln.Terracotta.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Cn.Pcln.Terracotta.Plugin.Tests;

[TestClass]
public sealed class IpcFramingTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task WriteAsyncUsesFourByteLittleEndianLengthPrefix()
    {
        IpcEnvelope envelope = IpcEnvelope.Create(HelperMessageTypes.RoomStatus, new { });
        await using MemoryStream stream = new();

        await IpcFraming.WriteAsync(stream, envelope, JsonOptions);

        byte[] bytes = stream.ToArray();
        int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, sizeof(uint))));
        Assert.AreEqual(bytes.Length - sizeof(uint), length);
        IpcEnvelope? decoded = JsonSerializer.Deserialize<IpcEnvelope>(bytes.AsSpan(sizeof(uint)), JsonOptions);
        Assert.IsNotNull(decoded);
        Assert.AreEqual(envelope.Id, decoded.Id);
    }

    [TestMethod]
    public async Task ReadAsyncRoundTripsEnvelope()
    {
        IpcEnvelope envelope = IpcEnvelope.Create(HelperMessageTypes.RoomStatus, new { });
        await using MemoryStream stream = new();
        await IpcFraming.WriteAsync(stream, envelope, JsonOptions);
        stream.Position = 0;

        IpcEnvelope decoded = await IpcFraming.ReadAsync(stream, JsonOptions);

        Assert.AreEqual(envelope.Protocol, decoded.Protocol);
        Assert.AreEqual(envelope.Id, decoded.Id);
        Assert.AreEqual(envelope.Type, decoded.Type);
    }

    [TestMethod]
    [DataRow(0u)]
    [DataRow((uint)ProtocolVersion.MaximumFrameBytes + 1u)]
    public async Task ReadAsyncRejectsInvalidFrameLength(uint length)
    {
        await using MemoryStream stream = new();
        byte[] header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        await stream.WriteAsync(header);
        stream.Position = 0;

        await Assert.ThrowsExactlyAsync<HelperProtocolException>(async () =>
            await IpcFraming.ReadAsync(stream, JsonOptions));
    }
}
