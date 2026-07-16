namespace Cn.Pcln.Terracotta.Models;

public sealed record TerracottaSettings(
    bool AutoDetectGameSession = true,
    bool AutoCloseOnGameExit = true,
    bool PreferDirectConnection = true,
    bool AllowRelay = true,
    bool AutoCopyRoomCode = true,
    bool AutoCopyConnectAddress = true,
    bool ShowAdvancedNetworkInfo = false,
    string DiagnosticLogLevel = "Information",
    string? LastSelectedInstanceId = null);
