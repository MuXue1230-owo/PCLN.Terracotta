using Cn.Pcln.Terracotta.Contracts;
using Cn.Pcln.Terracotta.Services;
using PCL.N.Plugin;
using System.Security.Cryptography;

namespace Cn.Pcln.Terracotta.Infrastructure;

public sealed class HelperRoomGateway
{
    private readonly HelperProcessManager _processManager;
    private readonly SecureIdentityStore _identityStore;
    private readonly IPluginTaskService _tasks;
    private HelperIpcClient? _initializedClient;
    private CancellationTokenSource? _eventPumpCts;
    private IPluginTaskRegistration? _eventPumpTask;

    public HelperRoomGateway(
        HelperProcessManager processManager,
        SecureIdentityStore identityStore,
        IPluginTaskService tasks)
    {
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _identityStore = identityStore ?? throw new ArgumentNullException(nameof(identityStore));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
    }

    public event EventHandler<HelperPushEvent>? PushEventReceived;

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
        await StopAsync(cancellationToken).ConfigureAwait(false);
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

    public async ValueTask<TerracottaRoomSnapshot> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        HelperIpcClient client = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await client.SendAsync<object, TerracottaRoomSnapshot>(
            HelperMessageTypes.RoomStatus,
            new { },
            HelperMessageTypes.RoomStatusResult,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await StopEventPumpAsync().ConfigureAwait(false);
        _initializedClient = null;
        await _processManager.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<HelperIpcClient> EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        HelperIpcClient client = await _processManager.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (ReferenceEquals(_initializedClient, client))
            return client;

        byte[] privateKey = await _identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
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
            StartEventPump(client);
            return client;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    private void StartEventPump(HelperIpcClient client)
    {
        _ = StopEventPumpAsync();
        CancellationTokenSource cts = new();
        _eventPumpCts = cts;
        _eventPumpTask = _tasks.Run(PluginIds.Plugin + ".helper-event-pump", async token =>
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, cts.Token);
            try
            {
                await foreach (HelperPushEvent push in client.Events.ReadAllAsync(linked.Token).ConfigureAwait(false))
                    PushEventReceived?.Invoke(this, push);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        });
    }

    private async Task StopEventPumpAsync()
    {
        CancellationTokenSource? cts = Interlocked.Exchange(ref _eventPumpCts, null);
        IPluginTaskRegistration? pump = Interlocked.Exchange(ref _eventPumpTask, null);
        if (cts is null && pump is null)
            return;
        try
        {
            cts?.Cancel();
            if (pump is not null)
                await pump.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore pump teardown faults
        }
        finally
        {
            cts?.Dispose();
        }
    }
}
