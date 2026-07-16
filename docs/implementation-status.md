# 实现状态

## 已完成

- alpha.1：插件/Helper 边界、IPC、状态机、Scaffolding、诊断与安全存储；
- alpha.2：`EasyTierRoomBackend`、房间凭据、sidecar 生命周期、同机 discovery；
- alpha.3：确定性 mesh 端点、房主 mesh ingress、成员 `--port-forward`、可选 TUN；
- **alpha.4：**
  - 四个 `pcl.exports`：`room-service` / `session-service` / `network-status` / `diagnostics`；
  - Contracts 扩展与 `RefreshStatusAsync`；
  - 连接中状态轮询；诊断/刷新拉取成员与 RTT；
  - Helper `BackendRefresh` + 质量探测模块；
  - 可选 EasyTier 原生资产打包与 CI 变量 `EASYTIER_VERSION`；
  - 文档 `docs/exports.md`、`CHANGELOG.md`。

## 正在推进

- EasyTier RPC 精确 NAT/中继节点字段（当前为探测启发式）；
- 正式六 RID EasyTier 强制打包门禁（`TerracottaRequireEasyTier=true`）。

## 后续里程碑

1. 默认 ALC 共享 Contracts 的宿主侧固化与互通测试；
2. 跨 PCL N / PCL CE 端到端联机矩阵；
3. 崩溃重连 UX 与推送式 `peer.*` IPC 事件；
4. SBOM、第三方许可、官方签名与商店审核。
