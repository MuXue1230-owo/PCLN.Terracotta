using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cn.Pcln.Terracotta.Contracts;

public sealed record IpcEnvelope
{
    [JsonConstructor]
    public IpcEnvelope(int protocol, string id, string type, JsonElement payload)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(protocol);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        Protocol = protocol;
        Id = id;
        Type = type;
        Payload = payload.Clone();
    }

    public int Protocol { get; }

    public string Id { get; }

    public string Type { get; }

    public JsonElement Payload { get; }

    public static IpcEnvelope Create<TPayload>(
        string type,
        TPayload payload,
        string? id = null,
        JsonSerializerOptions? options = null) =>
        new(
            ProtocolVersion.Current,
            id ?? Guid.NewGuid().ToString("N"),
            type,
            JsonSerializer.SerializeToElement(payload, options));

    public TPayload ReadPayload<TPayload>(JsonSerializerOptions? options = null) =>
        Payload.Deserialize<TPayload>(options)
        ?? throw new JsonException($"IPC payload '{Type}' deserialized to null.");
}
