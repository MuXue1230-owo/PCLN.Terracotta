namespace Cn.Pcln.Terracotta.Contracts;

public static class HelperMessageTypes
{
    public const string Hello = "hello";
    public const string HelloAccepted = "hello.accepted";
    public const string IdentityInitialize = "identity.initialize";
    public const string RoomCreate = "room.create";
    public const string RoomJoin = "room.join";
    public const string RoomLeave = "room.leave";
    public const string RoomStatus = "room.status";
    public const string RoomStatusResult = "room.status.result";
    public const string RoomSetLanAddress = "room.set-lan-address";
    public const string RoomCreated = "room.created";
    public const string RoomJoined = "room.joined";
    public const string RoomLeft = "room.left";
    public const string RoomFailed = "room.failed";
    public const string RoomStateChanged = "room.state-changed";
    public const string PeerJoined = "peer.joined";
    public const string PeerLeft = "peer.left";
    public const string PeerUpdated = "peer.updated";
    public const string NetworkDiagnose = "network.diagnose";
    public const string NetworkSetConfig = "network.set-config";
    public const string NetworkUpdated = "network.updated";
    public const string DiagnosticUpdated = "diagnostic.updated";
    public const string Shutdown = "shutdown";
    public const string ShutdownAccepted = "shutdown.accepted";
    public const string Error = "error";
    public const string Fatal = "fatal";
    public const string Log = "log";
}

public sealed record HelperHelloRequest(string AuthToken, string Client, string ClientVersion);

public sealed record HelperHelloResponse(string HelperVersion, IReadOnlyList<string> Capabilities);

public sealed record HelperIdentityInitializeRequest(string PrivateKey);

public sealed record HelperIdentityInitializeResponse(bool Initialized);

public sealed record HelperRoomCreateRequest(string GameSessionId, string LanAddress, bool PreferDirect, bool AllowRelay);

public sealed record HelperRoomJoinRequest(string RoomCode, string? GameSessionId);

public sealed record HelperRoomSetLanAddressRequest(string LanAddress);

public sealed record HelperError(string Code, string Message, bool Retryable = false);

public sealed record HelperLogEvent(string Category, string Level, string Message, DateTimeOffset Timestamp);
