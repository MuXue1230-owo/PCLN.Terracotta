using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Cn.Pcln.Terracotta.Application;
using Cn.Pcln.Terracotta.Contracts;

namespace Cn.Pcln.Terracotta.Views;

public sealed class TerracottaDiagnosticsWindow : Window
{
    private readonly TerracottaController _controller;
    private readonly TextBox _report;

    public TerracottaDiagnosticsWindow(TerracottaController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        Title = "陶瓦联机诊断";
        Width = 760;
        Height = 620;
        MinWidth = 560;
        MinHeight = 420;
        CanResize = true;

        _report = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(
            _report,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(
            _report,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        Button diagnose = new() { Content = "运行网络诊断" };
        diagnose.Click += (_, _) => _controller.QueueDiagnose();
        Button refresh = new() { Content = "刷新" };
        refresh.Click += (_, _) => Render();
        Button copy = new() { Content = "复制报告" };
        copy.Click += (_, _) => _controller.QueueCopyDiagnostics();
        Button export = new() { Content = "保存报告" };
        export.Click += (_, _) => _controller.QueueExportDiagnostics();

        Grid layout = new()
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 12
        };
        TextBlock heading = new()
        {
            Text = "诊断报告不会自动上传，Token、密钥和认证信息会在生成前脱敏。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        Grid.SetRow(heading, 0);
        Grid.SetRow(_report, 1);
        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { diagnose, refresh, copy, export }
        };
        Grid.SetRow(actions, 2);
        layout.Children.Add(heading);
        layout.Children.Add(_report);
        layout.Children.Add(actions);
        Content = layout;

        _controller.SnapshotChanged += OnSnapshotChanged;
        Closed += OnClosed;
        Render();
    }

    private void OnSnapshotChanged(object? sender, TerracottaRoomSnapshot snapshot) => Render();

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        _controller.SnapshotChanged -= OnSnapshotChanged;
        Closed -= OnClosed;
    }

    private void Render() => _report.Text = _controller.CreateDiagnosticReportJson();
}
