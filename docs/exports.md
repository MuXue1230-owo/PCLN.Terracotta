# 插件间导出（alpha.4）

`cn.pcln.terracotta` 在宿主提供 `pcl.exports` 时注册四个稳定导出。契约类型位于 `PCLN.Terracotta.Contracts`，应在默认 `AssemblyLoadContext` 中加载，以便其他插件 `Import`。

## 导出表

| 名称 | 契约 | 版本 | 说明 |
|---|---|---|---|
| `room-service` | `ITerracottaRoomService` | `0.1` | 创建/加入/退出房间，刷新状态，订阅快照 |
| `session-service` | `ITerracottaSessionService` | `0.1` | 解析运行中的 Minecraft 会话 |
| `network-status` | `ITerracottaNetworkStatusService` | `0.1` | 当前网络快照与诊断 |
| `diagnostics` | `ITerracottaDiagnosticsService` | `0.1` | 脱敏 JSON 报告生成与导出 |

完整 ID 形如：`cn.pcln.terracotta:room-service`。

## 消费示例

```csharp
if (context.Services.TryGet(out IPluginExportRegistry? exports) && exports is not null)
{
    PluginImport<ITerracottaRoomService> room = exports.Import<ITerracottaRoomService>(
        new PluginExportId("cn.pcln.terracotta", "room-service"),
        PluginApiVersionRange.Parse(">=0.1 <1.0"));
    if (room.IsAvailable)
    {
        TerracottaRoomSnapshot snapshot = room.Require().CurrentRoom;
    }
}
```

## 约束

- 不导出 UI ViewModel 或 Helper 私有类型；
- 所有写操作仍串行经过 `TerracottaController`；
- 导出注册经 `context.Lifetime.Track`，停用插件时自动撤销；
- 宿主未提供 `pcl.exports` 时插件仍可独立运行，仅跳过导出。
