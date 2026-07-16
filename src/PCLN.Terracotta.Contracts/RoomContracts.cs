namespace Cn.Pcln.Terracotta.Contracts;

public enum TerracottaRoomState
{
    Idle,
    WaitingForGame,
    WaitingForLan,
    Creating,
    Joining,
    Connected,
    Reconnecting,
    Leaving,
    Faulted,
    Diagnosing
}
public enum TerracottaRoomRole
{
    None,
    Host,
    Member
}

public enum TerracottaConnectionMode
{
    Unknown,
    Direct,
    Relay
}

public sealed record TerracottaRoomMember(
    string Id,
    string DisplayName,
    TerracottaConnectionMode ConnectionMode,
    int? RoundTripTimeMilliseconds,
    double? PacketLossPercent = null);

public sealed record TerracottaNetworkStatus(
    string? NatType,
    TerracottaConnectionMode ConnectionMode,
    int? RoundTripTimeMilliseconds,
    double? PacketLossPercent,
    string? RelayNode,
    bool IsHealthy);

public sealed record TerracottaRoomSnapshot(
    TerracottaRoomState State,
    TerracottaRoomRole Role,
    string? RoomCode,
    string? LocalAddress,
    string? GameSessionId,
    TerracottaNetworkStatus? Network,
    IReadOnlyList<TerracottaRoomMember> Members,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static TerracottaRoomSnapshot Idle { get; } = new(
        TerracottaRoomState.Idle,
        TerracottaRoomRole.None,
        null,
        null,
        null,
        null,
        []);
}

public sealed record TerracottaCreateRoomRequest(
    string? GameSessionId = null,
    int? LanPort = null,
    bool PreferDirectConnection = true,
    bool AllowRelay = true);

public sealed record TerracottaJoinRoomRequest(
    string RoomCode,
    string? GameSessionId = null,
    bool AutoCopyConnectAddress = true);

public interface ITerracottaRoomService
{
    TerracottaRoomSnapshot CurrentRoom { get; }

    event EventHandler<TerracottaRoomSnapshot>? SnapshotChanged;

    ValueTask<TerracottaRoomSnapshot> CreateAsync(
        TerracottaCreateRoomRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<TerracottaRoomSnapshot> JoinAsync(
        TerracottaJoinRoomRequest request,
        CancellationToken cancellationToken = default);

    ValueTask LeaveAsync(CancellationToken cancellationToken = default);

    /// <summary>Pulls the latest helper room snapshot when a room is active.</summary>
    ValueTask<TerracottaRoomSnapshot> RefreshStatusAsync(
        CancellationToken cancellationToken = default);
}
