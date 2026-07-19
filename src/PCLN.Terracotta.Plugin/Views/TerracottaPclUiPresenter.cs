using Cn.Pcln.Terracotta.Application;
using Cn.Pcln.Terracotta.Contracts;
using PCL.N.Plugin;

namespace Cn.Pcln.Terracotta.Views;

/// <summary>Keeps Terracotta's launcher-native PclUi page synchronized with room state.</summary>
internal sealed class TerracottaPclUiPresenter : IAsyncDisposable
{
    internal const string RoomCodeInputId = "terracotta-room-code";
    internal const string CreateButtonId = "terracotta-create";
    internal const string JoinButtonId = "terracotta-join";
    internal const string CopyButtonId = "terracotta-copy";
    internal const string LeaveButtonId = "terracotta-leave";

    private readonly PclUiService _ui;
    private readonly TerracottaController _controller;
    private readonly IPluginLogger _logger;
    private readonly PclUiPageRegistration _registration;
    private string _roomCodeInput = string.Empty;
    private int _disposed;

    public TerracottaPclUiPresenter(
        PclUiService ui,
        TerracottaController controller,
        IPluginLogger logger)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _registration = _ui.RegisterDynamicPage(new PclUiPage
        {
            OperationId = PluginIds.PageRegistration,
            Route = PluginIds.PageRoute,
            Title = Text("terracotta.title", "陶瓦联机"),
            Icon = "lucide/network",
            Order = 420,
            Content = BuildContent(_controller.CurrentRoom, _roomCodeInput)
        });
        _ui.EventRaised += OnUiEvent;
        _controller.SnapshotChanged += OnSnapshotChanged;
    }

    internal static PclUiElement BuildContent(TerracottaRoomSnapshot snapshot, string roomCodeInput)
    {
        bool hasRoom = snapshot.State is TerracottaRoomState.Connected or
            TerracottaRoomState.Reconnecting or TerracottaRoomState.Leaving or
            TerracottaRoomState.Diagnosing;
        bool isBusy = snapshot.State is not (TerracottaRoomState.Idle or TerracottaRoomState.Faulted);
        List<PclUiElement> children =
        [
            new PclUiText
            {
                Text = Text("terracotta.title", "陶瓦联机"),
                Style = PclUiTextStyle.Heading
            },
            new PclUiText
            {
                Text = Text("terracotta.subtitle", "安全、跨平台的 Minecraft P2P 联机"),
                Style = PclUiTextStyle.Body,
                Margin = new PclUiThickness(0, 0, 0, 8)
            },
            BuildStatusCard(snapshot)
        ];

        if (hasRoom)
            children.Add(BuildCurrentRoomCard(snapshot));
        else
            children.Add(BuildIdleCard(roomCodeInput, isBusy));

        return new PclUiStack
        {
            Margin = new PclUiThickness(30),
            Spacing = 16,
            Children = children
        };
    }

    private static PclUiCard BuildStatusCard(TerracottaRoomSnapshot snapshot)
    {
        List<PclUiElement> content =
        [
            new PclUiText
            {
                Text = StateLabel(snapshot.State),
                Style = PclUiTextStyle.Title
            },
            new PclUiText
            {
                Text = string.IsNullOrWhiteSpace(snapshot.ErrorMessage)
                    ? StateDescription(snapshot.State)
                    : Text("terracotta.description.runtime", snapshot.ErrorMessage),
                Style = PclUiTextStyle.Body
            }
        ];
        if (snapshot.State is TerracottaRoomState.WaitingForGame or
            TerracottaRoomState.WaitingForLan or
            TerracottaRoomState.Creating or
            TerracottaRoomState.Joining or
            TerracottaRoomState.Reconnecting or
            TerracottaRoomState.Leaving or
            TerracottaRoomState.Diagnosing)
        {
            content.Add(new PclUiProgress
            {
                Value = ProgressFor(snapshot.State),
                Text = StateLabel(snapshot.State)
            });
        }

        return new PclUiCard
        {
            Title = Text("terracotta.status", "连接状态"),
            Content = new PclUiStack { Spacing = 8, Children = content }
        };
    }

    private static PclUiCard BuildIdleCard(string roomCodeInput, bool isBusy) => new()
    {
        Title = Text("terracotta.joinExisting", "加入已有房间"),
        Content = new PclUiStack
        {
            Spacing = 10,
            Children =
            [
                new PclUiTextBox
                {
                    Id = RoomCodeInputId,
                    Label = Text("terracotta.roomCode", "房间码"),
                    Placeholder = Text("terracotta.roomCode.placeholder", "XXXX-XXXX-XXXX"),
                    Value = roomCodeInput,
                    MinWidth = 240,
                    MaxWidth = 420,
                    IsEnabled = !isBusy
                },
                new PclUiStack
                {
                    Orientation = PclUiOrientation.Horizontal,
                    Spacing = 10,
                    Children =
                    [
                        new PclUiButton
                        {
                            Id = CreateButtonId,
                            Text = Text("terracotta.createRoom", "创建房间"),
                            Style = PclUiButtonStyle.Primary,
                            IsEnabled = !isBusy
                        },
                        new PclUiButton
                        {
                            Id = JoinButtonId,
                            Text = Text("terracotta.joinRoom", "加入房间"),
                            IsEnabled = !isBusy && !string.IsNullOrWhiteSpace(roomCodeInput)
                        }
                    ]
                },
                new PclUiText
                {
                    Text = Text("terracotta.lanHint", "创建房间前，请先启动 Minecraft 并在单人世界中打开局域网。"),
                    Style = PclUiTextStyle.Caption
                }
            ]
        }
    };

    private static PclUiCard BuildCurrentRoomCard(TerracottaRoomSnapshot snapshot) => new()
    {
        Title = Text("terracotta.currentRoom", "当前房间"),
        Content = new PclUiStack
        {
            Spacing = 10,
            Children =
            [
                new PclUiText
                {
                    Text = Text("terracotta.roomCode.value", snapshot.RoomCode ?? "—"),
                    Style = PclUiTextStyle.Title
                },
                new PclUiText
                {
                    Text = Text("terracotta.address", "联机地址：{0}").Format(snapshot.LocalAddress ?? "—")
                },
                new PclUiStack
                {
                    Orientation = PclUiOrientation.Horizontal,
                    Spacing = 10,
                    Children =
                    [
                        new PclUiButton
                        {
                            Id = CopyButtonId,
                            Text = Text("terracotta.copyRoomCode", "复制房间码"),
                            IsEnabled = !string.IsNullOrWhiteSpace(snapshot.RoomCode)
                        },
                        new PclUiButton
                        {
                            Id = LeaveButtonId,
                            Text = Text("terracotta.leaveRoom", "退出房间"),
                            Style = PclUiButtonStyle.Danger,
                            IsEnabled = snapshot.State is not TerracottaRoomState.Leaving
                        },
                        new PclUiButton
                        {
                            Text = Text("terracotta.openDiagnostics", "打开陶瓦诊断"),
                            Style = PclUiButtonStyle.Subtle,
                            CommandId = PluginIds.OpenDiagnosticsCommand
                        }
                    ]
                }
            ]
        }
    };

    private void OnUiEvent(object? sender, PclUiEventArgs eventArgs)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        switch (eventArgs)
        {
            case { ElementId: RoomCodeInputId, Kind: PclUiEventKind.ValueChanged, Value: string value }:
                _roomCodeInput = value.Trim();
                _ = RefreshAsync(_controller.CurrentRoom);
                break;
            case { ElementId: CreateButtonId, Kind: PclUiEventKind.Click }:
                _controller.QueueCreate();
                break;
            case { ElementId: JoinButtonId, Kind: PclUiEventKind.Click }:
                _controller.QueueJoin(_roomCodeInput);
                break;
            case { ElementId: CopyButtonId, Kind: PclUiEventKind.Click }:
                _controller.QueueCopyRoomCode();
                break;
            case { ElementId: LeaveButtonId, Kind: PclUiEventKind.Click }:
                _controller.QueueLeave();
                break;
        }
    }

    private void OnSnapshotChanged(object? sender, TerracottaRoomSnapshot snapshot) =>
        _ = RefreshAsync(snapshot);

    private async Task RefreshAsync(TerracottaRoomSnapshot snapshot)
    {
        try
        {
            await _registration.UpdateContentAsync(BuildContent(snapshot, _roomCodeInput)).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
            // A queued room event raced with plugin shutdown.
        }
        catch (Exception exception)
        {
            _logger.LogError("Failed to refresh the Terracotta PclUi page.", exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _controller.SnapshotChanged -= OnSnapshotChanged;
        _ui.EventRaised -= OnUiEvent;
        await _registration.DisposeAsync().ConfigureAwait(false);
    }

    private static PclLocalizedString Text(string key, string fallback) => new(key, fallback);

    private static PclLocalizedString StateLabel(TerracottaRoomState state) => state switch
    {
        TerracottaRoomState.Idle => Text("terracotta.state.idle", "未连接"),
        TerracottaRoomState.WaitingForGame => Text("terracotta.state.waitingGame", "等待 Minecraft 启动"),
        TerracottaRoomState.WaitingForLan => Text("terracotta.state.waitingLan", "等待打开局域网"),
        TerracottaRoomState.Creating => Text("terracotta.state.creating", "正在创建房间"),
        TerracottaRoomState.Joining => Text("terracotta.state.joining", "正在加入房间"),
        TerracottaRoomState.Connected => Text("terracotta.state.connected", "已连接"),
        TerracottaRoomState.Reconnecting => Text("terracotta.state.reconnecting", "正在重连"),
        TerracottaRoomState.Leaving => Text("terracotta.state.leaving", "正在退出"),
        TerracottaRoomState.Faulted => Text("terracotta.state.faulted", "连接失败"),
        TerracottaRoomState.Diagnosing => Text("terracotta.state.diagnosing", "正在诊断"),
        _ => Text("terracotta.state.unknown", state.ToString())
    };

    private static PclLocalizedString StateDescription(TerracottaRoomState state) => state switch
    {
        TerracottaRoomState.Idle => Text("terracotta.description.idle", "创建房间，或输入 12 位房间码加入。"),
        TerracottaRoomState.WaitingForGame => Text("terracotta.description.waitingGame", "启动 Minecraft 后会自动继续。"),
        TerracottaRoomState.WaitingForLan => Text("terracotta.description.waitingLan", "在暂停菜单中选择“对局域网开放”。"),
        TerracottaRoomState.Connected => Text("terracotta.description.connected", "陶瓦网络已建立，可以邀请好友加入。"),
        TerracottaRoomState.Reconnecting => Text("terracotta.description.reconnecting", "网络暂时中断，正在尝试恢复。"),
        _ => Text("terracotta.description.wait", "请稍候…")
    };

    private static double ProgressFor(TerracottaRoomState state) => state switch
    {
        TerracottaRoomState.WaitingForGame => 0.1,
        TerracottaRoomState.WaitingForLan => 0.2,
        TerracottaRoomState.Creating or TerracottaRoomState.Joining => 0.55,
        TerracottaRoomState.Reconnecting => 0.7,
        TerracottaRoomState.Diagnosing => 0.8,
        TerracottaRoomState.Leaving => 0.9,
        _ => 0
    };
}
