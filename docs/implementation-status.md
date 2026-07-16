# 实现状态

## 已完成

- alpha.1–alpha.3：插件/Helper 边界、EasyTier 后端、mesh 映射、discovery；
- alpha.4：四个 `pcl.exports`、状态轮询、质量探测、可选 EasyTier 打包；
- **alpha.5：**
  - 推送式 IPC：`peer.*` / `network.updated` / `room.state-changed`；
  - Helper 事件总线 + 2s 后台 poll；
  - 网络不健康时 `Connected` → `Reconnecting`，恢复后回到 `Connected`；
  - 插件双工 IPC 客户端与 push 事件消费。

## 正在推进

- EasyTier RPC 精确 NAT/中继节点；
- 六 RID EasyTier 强制门禁与跨启动器 E2E。

## 后续里程碑

1. Contracts 默认 ALC 共享的宿主侧互通测试；
2. 崩溃自动重连 Helper 并恢复房间；
3. SBOM、官方签名与商店审核。
