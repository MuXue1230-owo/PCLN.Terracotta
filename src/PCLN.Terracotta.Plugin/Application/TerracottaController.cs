using Cn.Pcln.Terracotta.Contracts;
using Cn.Pcln.Terracotta.Diagnostics;
using Cn.Pcln.Terracotta.Infrastructure;
using Cn.Pcln.Terracotta.Models;
using Cn.Pcln.Terracotta.Services;
using PCL.N.Plugin;
using System.Globalization;
using System.Text;

namespace Cn.Pcln.Terracotta.Application;

public sealed class TerracottaController :
    ITerracottaRoomService,
    ITerracottaNetworkStatusService,
    ITerracottaSessionService,
    ITerracottaDiagnosticsService,
    IAsyncDisposable
{
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(3);

    private readonly IPluginContext _context;
    private readonly PluginStateStore _stateStore;
    private readonly IPluginCommandService _commands;
    private readonly IPluginTaskService _tasks;
    private readonly GameSessionCoordinator _sessions;
    private readonly IPluginGameOutputService _output;
    private readonly IPluginLaunchEventService _launchEvents;
    private readonly IPluginNavigationService _navigation;
    private readonly IAvaloniaPluginWindowService? _windows;
    private readonly HelperRoomGateway _helper;
    private readonly HelperProcessManager _helperProcess;
    private readonly RoomStateMachine _stateMachine = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private long _operationSequence;
    private TerracottaRoomSnapshot _snapshot = TerracottaRoomSnapshot.Idle;
    private TerracottaCreateRoomRequest? _pendingCreate;
    private TerracottaSettings _settings = new();
    private bool _started;
    private CancellationTokenSource? _statusPollCts;

    public TerracottaController(
        IPluginContext context,
        PluginStateStore stateStore,
        IPluginCommandService commands,
        IPluginTaskService tasks,
        GameSessionCoordinator sessions,
        IPluginGameOutputService output,
        IPluginLaunchEventService launchEvents,
        IPluginNavigationService navigation,
        IAvaloniaPluginWindowService? windows,
        HelperRoomGateway helper,
        HelperProcessManager helperProcess)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _launchEvents = launchEvents ?? throw new ArgumentNullException(nameof(launchEvents));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _windows = windows;
        _helper = helper ?? throw new ArgumentNullException(nameof(helper));
        _helperProcess = helperProcess ?? throw new ArgumentNullException(nameof(helperProcess));
    }

    public event EventHandler<TerracottaRoomSnapshot>? SnapshotChanged;

    public TerracottaRoomSnapshot CurrentRoom => _snapshot;

    public TerracottaNetworkStatus? CurrentNetwork => _snapshot.Network;

    public string? BoundGameSessionId => _snapshot.GameSessionId ?? _pendingCreate?.GameSessionId;

    public void Start()
    {
        if (_started)
            return;
        _started = true;
        _context.Lifetime.Track(_output.Subscribe(OnGameOutput));
        _context.Lifetime.Track(_launchEvents.Subscribe(OnLaunchEvent));
        QueueOperation("load-settings", LoadSettingsAsync);
    }

    public void RegisterCommands()
    {
        TrackCommand(PluginIds.CreateRoomCommand, "创建陶瓦房间", token =>
            CreateAsync(new TerracottaCreateRoomRequest(), token).AsTask());
        TrackCommand(PluginIds.JoinRoomCommand, "加入陶瓦房间", token =>
            _navigation.NavigateAsync(PluginIds.PageRoute, token).AsTask());
        TrackCommand(PluginIds.LeaveRoomCommand, "退出陶瓦房间", token => LeaveAsync(token).AsTask());
        TrackCommand(PluginIds.CopyRoomCodeCommand, "复制陶瓦房间码", CopyRoomCodeAsync);
        TrackCommand(PluginIds.CopyConnectAddressCommand, "复制联机地址", CopyConnectAddressAsync);
        TrackCommand(PluginIds.OpenDiagnosticsCommand, "打开陶瓦诊断", OpenDiagnosticsAsync);
        TrackCommand(PluginIds.ExportDiagnosticsCommand, "导出陶瓦诊断", ExportDiagnosticsAsync);
        TrackCommand(PluginIds.RestartHelperCommand, "重启陶瓦核心", RestartHelperAsync);
        TrackCommand(PluginIds.OpenHelpCommand, "打开陶瓦帮助", OpenHelpAsync);
    }

    public void QueueCreate() =>
        QueueOperation("create-room", token => CreateAsync(new TerracottaCreateRoomRequest(), token).AsTask());

    public void QueueJoin(string roomCode) =>
        QueueOperation("join-room", token => JoinAsync(new TerracottaJoinRoomRequest(roomCode), token).AsTask());

    public void QueueLeave() => QueueOperation("leave-room", token => LeaveAsync(token).AsTask());

    public void QueueCopyRoomCode() => QueueOperation("copy-room-code", CopyRoomCodeAsync);

    public void QueueDiagnose() => QueueOperation("diagnose", RunDiagnoseCommandAsync);

    public void QueueCopyDiagnostics() => QueueOperation("copy-diagnostics", CopyDiagnosticsAsync);

    public void QueueExportDiagnostics() => QueueOperation("export-diagnostics", ExportDiagnosticsAsync);

    public string CreateDiagnosticReportJson() => DiagnosticCollector.CreateJson(
        _context.Plugin.Version.ToString(),
        _helperProcess.LastHelperVersion,
        _snapshot,
        _helperProcess.LastResult);

    public async ValueTask<TerracottaRoomSnapshot> CreateAsync(
        TerracottaCreateRoomRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ResetFaultIfNeeded();
            if (_stateMachine.State is not (TerracottaRoomState.Idle or TerracottaRoomState.WaitingForGame or TerracottaRoomState.WaitingForLan))
                throw new InvalidOperationException("A Terracotta room operation is already active.");

            GameSessionSelection selection = _sessions.Select(
                request.GameSessionId,
                _settings.LastSelectedInstanceId,
                _settings.AutoDetectGameSession);
            if (selection.Selected is null)
            {
                _pendingCreate = request;
                TransitionAndPublish(
                    TerracottaRoomState.WaitingForGame,
                    errorCode: ErrorCodeCatalog.NoRunningGame,
                    errorMessage: selection.Reason);
                return _snapshot;
            }

            int? lanPort = request.LanPort ?? ResolveLanPort(selection.Selected);
            if (lanPort is null)
            {
                _pendingCreate = request with { GameSessionId = selection.Selected.SessionId };
                TransitionAndPublish(
                    TerracottaRoomState.WaitingForLan,
                    gameSessionId: selection.Selected.SessionId,
                    errorCode: ErrorCodeCatalog.LanPortUnavailable,
                    errorMessage: "请在 Minecraft 中打开局域网世界，陶瓦会自动继续。" );
                return _snapshot;
            }

            _pendingCreate = null;
            _stateMachine.TransitionTo(TerracottaRoomState.Creating);
            Publish(new TerracottaRoomSnapshot(
                TerracottaRoomState.Creating,
                TerracottaRoomRole.Host,
                null,
                LanAddressResolver.ToLoopbackAddress(lanPort.Value),
                selection.Selected.SessionId,
                null,
                []));

            TerracottaRoomSnapshot connected = await _helper.CreateAsync(
                selection.Selected.SessionId,
                LanAddressResolver.ToLoopbackAddress(lanPort.Value),
                request.PreferDirectConnection,
                request.AllowRelay,
                cancellationToken).ConfigureAwait(false);
            _stateMachine.TransitionTo(TerracottaRoomState.Connected);
            Publish(connected with { State = TerracottaRoomState.Connected });
            StartStatusPolling();
            await PersistSelectedSessionAsync(selection.Selected.SessionId, cancellationToken).ConfigureAwait(false);
            if (_settings.AutoCopyRoomCode)
                await CopyRoomCodeAsync(cancellationToken).ConfigureAwait(false);
            return _snapshot;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fault(exception);
            return _snapshot;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<TerracottaRoomSnapshot> JoinAsync(
        TerracottaJoinRoomRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!RoomCode.TryParse(request.RoomCode, out RoomCode parsedRoomCode))
        {
            PublishFault(ErrorCodeCatalog.InvalidRoomCode, "房间码格式应为 XXXX-XXXX-XXXX。");
            return _snapshot;
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ResetFaultIfNeeded();
            if (_stateMachine.State != TerracottaRoomState.Idle)
                throw new InvalidOperationException("A Terracotta room operation is already active.");
            _stateMachine.TransitionTo(TerracottaRoomState.Joining);
            Publish(new TerracottaRoomSnapshot(
                TerracottaRoomState.Joining,
                TerracottaRoomRole.Member,
                parsedRoomCode.ToString(),
                null,
                request.GameSessionId,
                null,
                []));

            TerracottaRoomSnapshot connected = await _helper.JoinAsync(
                parsedRoomCode.ToString(),
                request.GameSessionId,
                cancellationToken).ConfigureAwait(false);
            _stateMachine.TransitionTo(TerracottaRoomState.Connected);
            Publish(connected with { State = TerracottaRoomState.Connected });
            StartStatusPolling();
            if (connected.GameSessionId is not null)
                await PersistSelectedSessionAsync(connected.GameSessionId, cancellationToken).ConfigureAwait(false);
            if (request.AutoCopyConnectAddress && _settings.AutoCopyConnectAddress)
                await CopyConnectAddressAsync(cancellationToken).ConfigureAwait(false);
            return _snapshot;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fault(exception);
            return _snapshot;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask LeaveAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StopStatusPolling();
            _pendingCreate = null;
            if (_stateMachine.State == TerracottaRoomState.Idle)
                return;
            if (_stateMachine.State is TerracottaRoomState.WaitingForGame or TerracottaRoomState.WaitingForLan or TerracottaRoomState.Faulted)
            {
                _stateMachine.TransitionTo(TerracottaRoomState.Idle);
                Publish(TerracottaRoomSnapshot.Idle);
                await _helper.StopAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            _stateMachine.TransitionTo(TerracottaRoomState.Leaving);
            Publish(_snapshot with { State = TerracottaRoomState.Leaving });
            await _helper.LeaveAsync(cancellationToken).ConfigureAwait(false);
            _stateMachine.TransitionTo(TerracottaRoomState.Idle);
            Publish(TerracottaRoomSnapshot.Idle);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fault(exception);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<TerracottaRoomSnapshot> RefreshStatusAsync(
        CancellationToken cancellationToken = default)
    {
        if (_snapshot.State is not (TerracottaRoomState.Connected or TerracottaRoomState.Reconnecting or TerracottaRoomState.Diagnosing))
            return _snapshot;

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TerracottaRoomSnapshot helperSnapshot = await _helper.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (helperSnapshot.State is TerracottaRoomState.Connected or TerracottaRoomState.Reconnecting)
            {
                Publish(helperSnapshot with
                {
                    // Keep plugin-facing lifecycle unless helper reports a hard fault.
                    State = _stateMachine.State == TerracottaRoomState.Diagnosing
                        ? TerracottaRoomState.Diagnosing
                        : helperSnapshot.State
                });
            }
            else if (helperSnapshot.State == TerracottaRoomState.Faulted)
            {
                StopStatusPolling();
                _stateMachine.TransitionTo(TerracottaRoomState.Faulted);
                Publish(helperSnapshot);
            }
            return _snapshot;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Soft-fail status polling so a transient IPC blip does not drop the room.
            _context.Logger.Warn($"Terracotta status refresh failed: {exception.Message}");
            return _snapshot;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public TerracottaSessionSelection SelectRunningSession(
        string? explicitSessionId = null,
        string? preferredInstanceId = null,
        bool selectMostRecent = true)
    {
        GameSessionSelection selection = _sessions.Select(
            explicitSessionId,
            preferredInstanceId ?? _settings.LastSelectedInstanceId,
            selectMostRecent);
        return new TerracottaSessionSelection(
            selection.Selected?.SessionId,
            selection.Selected?.InstanceId,
            selection.Selected?.InstanceId,
            selection.Candidates.Count,
            selection.Reason ?? "No selection");
    }

    public async ValueTask<TerracottaNetworkStatus> DiagnoseAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_snapshot.State is not (TerracottaRoomState.Connected or TerracottaRoomState.Reconnecting or TerracottaRoomState.Diagnosing))
                throw new InvalidOperationException("Network diagnostics require an active Terracotta room.");

            TerracottaRoomState previous = _stateMachine.State;
            if (previous != TerracottaRoomState.Diagnosing)
            {
                _stateMachine.TransitionTo(TerracottaRoomState.Diagnosing);
                Publish(_snapshot with { State = TerracottaRoomState.Diagnosing });
            }

            TerracottaNetworkStatus network = await _helper.DiagnoseAsync(cancellationToken).ConfigureAwait(false);
            if (_stateMachine.State == TerracottaRoomState.Diagnosing && previous != TerracottaRoomState.Diagnosing)
                _stateMachine.TransitionTo(previous == TerracottaRoomState.Reconnecting
                    ? TerracottaRoomState.Reconnecting
                    : TerracottaRoomState.Connected);
            Publish(_snapshot with
            {
                State = _stateMachine.State,
                Network = network
            });
            return network;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fault(exception);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<string?> ExportDiagnosticReportAsync(CancellationToken cancellationToken = default)
    {
        if (!_context.Services.TryGet(out IPluginFileService? files) || files is null)
            return null;

        string report = CreateDiagnosticReportJson();
        byte[] content = Encoding.UTF8.GetBytes(report);
        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string relativePath = $"diagnostics/terracotta-{timestamp}.json";
        await files.WriteAsync(relativePath, content, cancellationToken).ConfigureAwait(false);
        await files.WriteAsync("diagnostics/latest.json", content, cancellationToken).ConfigureAwait(false);
        return relativePath;
    }

    public async ValueTask DisposeAsync()
    {
        StopStatusPolling();
        try
        {
            await LeaveAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _helperProcess.DisposeAsync().ConfigureAwait(false);
            _operationGate.Dispose();
        }
    }

    private void TrackCommand(string id, string title, Func<CancellationToken, Task> executeAsync) =>
        _context.Lifetime.Track(_commands.Register(new PluginCommandDescriptor(id, title, executeAsync)));

    private void QueueOperation(string name, Func<CancellationToken, Task> operation)
    {
        long sequence = Interlocked.Increment(ref _operationSequence);
        _context.Lifetime.Track(_tasks.Run($"{PluginIds.Plugin}.{name}.{sequence}", operation));
    }

    private async Task LoadSettingsAsync(CancellationToken cancellationToken)
    {
        _settings = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistSelectedSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        _settings = _settings with { LastSelectedInstanceId = sessionId };
        await _stateStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
    }

    private int? ResolveLanPort(PluginGameSessionSnapshot session)
    {
        if (LanAddressResolver.TryResolvePort(session.LanAddress, out int snapshotPort))
            return snapshotPort;

        foreach (PluginGameProcessOutput line in _output.Read(session.SessionId, 0, 2048).Reverse())
        {
            if (LanAddressResolver.TryResolvePort(line.Text, out int outputPort))
                return outputPort;
        }

        return null;
    }

    private void OnGameOutput(PluginGameProcessOutput output)
    {
        if (_stateMachine.State != TerracottaRoomState.WaitingForLan ||
            _pendingCreate?.GameSessionId is not { } sessionId ||
            !string.Equals(sessionId, output.SessionId, StringComparison.Ordinal) ||
            !LanAddressResolver.TryResolvePort(output.Text, out int port))
        {
            return;
        }

        TerracottaCreateRoomRequest pending = _pendingCreate with { LanPort = port };
        QueueOperation("resume-create-room", token => CreateAsync(pending, token).AsTask());
    }

    private void OnLaunchEvent(PluginLaunchEvent launchEvent)
    {
        if (launchEvent.Session.State == PluginGameSessionState.Running &&
            _stateMachine.State == TerracottaRoomState.WaitingForGame &&
            _pendingCreate is not null)
        {
            TerracottaCreateRoomRequest pending = _pendingCreate with { GameSessionId = launchEvent.SessionId };
            QueueOperation("resume-create-room", token => CreateAsync(pending, token).AsTask());
            return;
        }

        if (_settings.AutoCloseOnGameExit &&
            _snapshot.GameSessionId is { } activeSession &&
            string.Equals(activeSession, launchEvent.SessionId, StringComparison.Ordinal) &&
            launchEvent.Session.State is PluginGameSessionState.Exited or PluginGameSessionState.Crashed or PluginGameSessionState.Terminated)
        {
            QueueLeave();
        }
    }

    private void ResetFaultIfNeeded()
    {
        if (_stateMachine.State == TerracottaRoomState.Faulted)
            _stateMachine.TransitionTo(TerracottaRoomState.Idle);
    }

    private void TransitionAndPublish(
        TerracottaRoomState state,
        string? gameSessionId = null,
        string? errorCode = null,
        string? errorMessage = null)
    {
        _stateMachine.TransitionTo(state);
        Publish(new TerracottaRoomSnapshot(
            state,
            TerracottaRoomRole.None,
            null,
            null,
            gameSessionId,
            null,
            [],
            errorCode,
            errorMessage));
    }

    private void Fault(Exception exception)
    {
        string code = exception switch
        {
            SecureIdentityException => ErrorCodeCatalog.SecureStorageUnavailable,
            FileNotFoundException => ErrorCodeCatalog.HelperMissing,
            HelperProtocolException protocol => MapHelperErrorCode(protocol.Code),
            _ => ErrorCodeCatalog.NetworkUnavailable
        };
        PublishFault(code, SensitiveDataRedactor.Redact(exception.Message));
        _context.Logger.LogError($"Terracotta operation failed ({code}).", exception);
    }

    private static string MapHelperErrorCode(string? helperCode) => helperCode switch
    {
        "network.easytier-missing" => ErrorCodeCatalog.EasyTierMissing,
        "network.easytier-start-failed" or "network.easytier-stop-failed" => ErrorCodeCatalog.EasyTierStartFailed,
        "network.peer-unreachable" => ErrorCodeCatalog.PeerUnreachable,
        "network.mesh-ingress-failed" => ErrorCodeCatalog.MeshIngressFailed,
        "room.invalid-code" => ErrorCodeCatalog.InvalidRoomCode,
        "identity.not-initialized" or "identity.invalid-key" => ErrorCodeCatalog.SecureStorageUnavailable,
        null or "" => ErrorCodeCatalog.HelperDisconnected,
        _ when helperCode.StartsWith("network.", StringComparison.Ordinal) => ErrorCodeCatalog.NetworkUnavailable,
        _ when helperCode.StartsWith("ipc.", StringComparison.Ordinal) => ErrorCodeCatalog.HelperDisconnected,
        _ => ErrorCodeCatalog.NetworkUnavailable
    };

    private void PublishFault(string code, string message)
    {
        if (_stateMachine.State != TerracottaRoomState.Faulted)
            _stateMachine.TransitionTo(TerracottaRoomState.Faulted);
        Publish(_snapshot with
        {
            State = TerracottaRoomState.Faulted,
            ErrorCode = code,
            ErrorMessage = message
        });
        if (_context.Services.TryGet<IPluginNotificationService>(out IPluginNotificationService? notifications) && notifications is not null)
            notifications.ShowWarning($"陶瓦联机：{message} ({code})");
    }

    private void Publish(TerracottaRoomSnapshot snapshot)
    {
        _snapshot = snapshot;
        _context.Dispatcher.Post(() => SnapshotChanged?.Invoke(this, snapshot));
    }

    private async Task CopyRoomCodeAsync(CancellationToken cancellationToken)
    {
        if (_snapshot.RoomCode is null)
            return;
        if (_context.Services.TryGet<IPluginClipboardService>(out IPluginClipboardService? clipboard) && clipboard is not null)
            await clipboard.WriteTextAsync(_snapshot.RoomCode, cancellationToken).ConfigureAwait(false);
    }

    private async Task CopyConnectAddressAsync(CancellationToken cancellationToken)
    {
        if (_snapshot.LocalAddress is null)
            return;
        if (_context.Services.TryGet<IPluginClipboardService>(out IPluginClipboardService? clipboard) && clipboard is not null)
            await clipboard.WriteTextAsync(_snapshot.LocalAddress, cancellationToken).ConfigureAwait(false);
    }

    private async Task OpenDiagnosticsAsync(CancellationToken cancellationToken)
    {
        if (_windows is not null)
        {
            await _windows.ShowAsync(PluginIds.DiagnosticsWindow, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (_context.Services.TryGet<IPluginNotificationService>(out IPluginNotificationService? notifications) && notifications is not null)
            notifications.ShowWarning("当前宿主未提供插件窗口服务，可使用“导出陶瓦诊断”命令生成报告。");
    }

    private async Task RunDiagnoseCommandAsync(CancellationToken cancellationToken)
    {
        if (_snapshot.State is not (TerracottaRoomState.Connected or TerracottaRoomState.Reconnecting or TerracottaRoomState.Diagnosing))
        {
            if (_context.Services.TryGet(out IPluginNotificationService? unavailable) && unavailable is not null)
                unavailable.ShowInformation("进入陶瓦房间后才能运行网络诊断。");
            return;
        }

        try
        {
            TerracottaNetworkStatus network = await DiagnoseAsync(cancellationToken).ConfigureAwait(false);
            if (_context.Services.TryGet(out IPluginNotificationService? completed) && completed is not null)
                completed.ShowInformation(network.IsHealthy ? "陶瓦网络状态正常。" : "陶瓦网络存在异常，请导出诊断报告。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Fault already applied by the public DiagnoseAsync implementation.
            if (_snapshot.State != TerracottaRoomState.Faulted)
                Fault(exception);
        }
    }

    private async Task CopyDiagnosticsAsync(CancellationToken cancellationToken)
    {
        if (_context.Services.TryGet(out IPluginClipboardService? clipboard) && clipboard is not null)
            await clipboard.WriteTextAsync(CreateDiagnosticReportJson(), cancellationToken).ConfigureAwait(false);
    }

    private async Task ExportDiagnosticsAsync(CancellationToken cancellationToken)
    {
        string? path = await ExportDiagnosticReportAsync(cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            if (_context.Services.TryGet(out IPluginNotificationService? unavailable) && unavailable is not null)
                unavailable.ShowWarning("当前宿主未提供插件文件服务，无法保存诊断报告。");
            return;
        }

        if (_context.Services.TryGet(out IPluginNotificationService? completed) && completed is not null)
            completed.ShowInformation($"陶瓦诊断已保存到插件数据目录：{path}");
    }

    private void StartStatusPolling()
    {
        StopStatusPolling();
        CancellationTokenSource cts = new();
        _statusPollCts = cts;
        _context.Lifetime.Track(_tasks.Run($"{PluginIds.Plugin}.status-poll", async token =>
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, cts.Token);
            while (!linked.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(StatusPollInterval, linked.Token).ConfigureAwait(false);
                    if (_snapshot.State is TerracottaRoomState.Connected or TerracottaRoomState.Reconnecting)
                        await RefreshStatusAsync(linked.Token).ConfigureAwait(false);
                    else
                        break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }));
    }

    private void StopStatusPolling()
    {
        CancellationTokenSource? cts = Interlocked.Exchange(ref _statusPollCts, null);
        if (cts is null)
            return;
        try
        {
            cts.Cancel();
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task RestartHelperAsync(CancellationToken cancellationToken)
    {
        await _helper.StopAsync(cancellationToken).ConfigureAwait(false);
        if (_stateMachine.State != TerracottaRoomState.Idle)
            PublishFault(ErrorCodeCatalog.HelperDisconnected, "陶瓦核心已重启，请重新进入房间。");
    }

    private async Task OpenHelpAsync(CancellationToken cancellationToken)
    {
        if (_context.Services.TryGet<IPluginUriLauncher>(out IPluginUriLauncher? launcher) && launcher is not null)
            await launcher.OpenAsync(new Uri("https://docs.pcln.top/plugins/terracotta/"), cancellationToken).ConfigureAwait(false);
    }
}
