using Cn.Pcln.Terracotta.Contracts;
using Cn.Pcln.Terracotta.Services;
using System.Security.Cryptography;

namespace Cn.Pcln.Terracotta.Infrastructure;

public sealed class HelperRoomGateway(
    HelperProcessManager processManager,
    SecureIdentityStore identityStore)
{
    private HelperIpcClient? _initializedClient;

    public async ValueTask<TerracottaRoomSnapshot> CreateAsync(
        string gameSessionId,
        string lanAddress,
        bool preferDirect,
        bool allowRelay,
        CancellationToken cancellationToken = default)
    {
        HelperIpcClient client = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await client.SendAsync<HelperRoomCreateRequest, TerracottaRoomSnapshot>(
            HelperMessageTypes.RoomCreate,
            new HelperRoomCreateRequest(gameSessionId, lanAddress, preferDirect, allowRelay),
            HelperMessageTypes.RoomCreated,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TerracottaRoomSnapshot> JoinAsync(
        string roomCode,
        string? gameSessionId,
        CancellationToken cancellationToken = default)
    {
        HelperIpcClient client = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await client.SendAsync<HelperRoomJoinRequest, TerracottaRoomSnapshot>(
            HelperMessageTypes.RoomJoin,
            new HelperRoomJoinRequest(roomCode, gameSessionId),
            HelperMessageTypes.RoomJoined,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask LeaveAsync(CancellationToken cancellationToken = default)
    {
        HelperIpcClient client = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await client.SendAsync(
            HelperMessageTypes.RoomLeave,
            new { },
            HelperMessageTypes.RoomLeft,
            cancellationToken).ConfigureAwait(false);
        await processManager.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TerracottaRoomSnapshot> SetLanAddressAsync(
        string lanAddress,
        CancellationToken cancellationToken = default)
    {
        HelperIpcClient client = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await client.SendAsync<HelperRoomSetLanAddressRequest, TerracottaRoomSnapshot>(
            HelperMessageTypes.RoomSetLanAddress,
            new HelperRoomSetLanAddressRequest(lanAddress),
            HelperMessageTypes.RoomStateChanged,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TerracottaNetworkStatus> DiagnoseAsync(
        CancellationToken cancellationToken = default)
    {
        HelperIpcClient client = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await client.SendAsync<object, TerracottaNetworkStatus>(
            HelperMessageTypes.NetworkDiagnose,
            new { },
            HelperMessageTypes.DiagnosticUpdated,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        _initializedClient = null;
        await processManager.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<HelperIpcClient> EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        HelperIpcClient client = await processManager.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (ReferenceEquals(_initializedClient, client))
            return client;

        byte[] privateKey = await identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HelperIdentityInitializeResponse response =
                await client.SendAsync<HelperIdentityInitializeRequest, HelperIdentityInitializeResponse>(
                    HelperMessageTypes.IdentityInitialize,
                    new HelperIdentityInitializeRequest(Convert.ToHexString(privateKey)),
                    "identity.initialized",
                    cancellationToken).ConfigureAwait(false);
            if (!response.Initialized)
                throw new SecureIdentityException("陶瓦核心未接受安全身份。");
            _initializedClient = client;
            return client;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }
}
