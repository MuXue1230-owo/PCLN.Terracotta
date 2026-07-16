namespace Cn.Pcln.Terracotta.Contracts;

/// <summary>Stable export names under plugin id <c>cn.pcln.terracotta</c>.</summary>
public static class TerracottaExportNames
{
    public const string RoomService = "room-service";
    public const string SessionService = "session-service";
    public const string NetworkStatus = "network-status";
    public const string Diagnostics = "diagnostics";
}

/// <summary>Read-only access to the current Terracotta room snapshot and change notifications.</summary>
public interface ITerracottaNetworkStatusService
{
    TerracottaRoomSnapshot CurrentRoom { get; }

    TerracottaNetworkStatus? CurrentNetwork { get; }

    event EventHandler<TerracottaRoomSnapshot>? SnapshotChanged;

    ValueTask<TerracottaNetworkStatus> DiagnoseAsync(CancellationToken cancellationToken = default);
}

/// <summary>Read-only Minecraft session selection used by Terracotta room operations.</summary>
public interface ITerracottaSessionService
{
    /// <summary>Session id currently bound to the active or pending room, if any.</summary>
    string? BoundGameSessionId { get; }

    /// <summary>
    /// Resolves a running Minecraft session using the same policy as the Terracotta UI
    /// (explicit id, single running, current instance, most recent).
    /// </summary>
    TerracottaSessionSelection SelectRunningSession(
        string? explicitSessionId = null,
        string? preferredInstanceId = null,
        bool selectMostRecent = true);
}

/// <summary>User-initiated diagnostic report generation without exposing UI types.</summary>
public interface ITerracottaDiagnosticsService
{
    /// <summary>Builds a redacted JSON diagnostic report for the current plugin/helper state.</summary>
    string CreateDiagnosticReportJson();

    /// <summary>Writes the latest redacted report under the plugin data directory when file IO is available.</summary>
    ValueTask<string?> ExportDiagnosticReportAsync(CancellationToken cancellationToken = default);
}

/// <summary>Result of session selection for plugin-to-plugin consumers.</summary>
public sealed record TerracottaSessionSelection(
    string? SessionId,
    string? InstanceId,
    string? DisplayName,
    int CandidateCount,
    string Reason);
