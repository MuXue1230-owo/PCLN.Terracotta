namespace Cn.Pcln.Terracotta.Diagnostics;

public static class ErrorCodeCatalog
{
    public const string NoRunningGame = "TC-GAME-001";
    public const string LanPortUnavailable = "TC-GAME-002";
    public const string HelperMissing = "TC-HELPER-001";
    public const string HelperIntegrityFailure = "TC-HELPER-002";
    public const string HelperProtocolMismatch = "TC-IPC-001";
    public const string HelperDisconnected = "TC-IPC-002";
    public const string InvalidRoomCode = "TC-ROOM-001";
    public const string NetworkUnavailable = "TC-NET-001";
    public const string SecureStorageUnavailable = "TC-SEC-001";
}
