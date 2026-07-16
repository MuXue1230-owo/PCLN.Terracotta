# 实现状态

## 已完成

- 独立解决方案、统一严格编译配置和 NuGet SDK 依赖；
- 完整 Manifest 基线与设计文档中的稳定 ID；
- Avalonia 主页面、命令注册和插件启停清理；
- 房间状态机、设置模型、运行会话选择、LAN 输出解析、游戏结束自动退出；
- IPC Envelope、little-endian framing、帧上限和协议测试；
- Helper 进程请求、stdin Secret、超时、正常/强制退出路径；
- Rust Helper 参数校验、父进程监测、Windows named pipe、Unix socket、握手、Idle 状态和 shutdown；
- Rust Helper 并发安全房间状态、可替换 `RoomBackend`、完整 create/join/leave/set-lan-address/diagnose IPC 路径；
- Scaffolding v1 兼容帧、协议协商、Minecraft 端口、玩家心跳/列表和本地双端集成测试；
- Windows named pipe 当前用户/SYSTEM DACL、客户端 PID 核对与真实子进程端到端测试；
- 已生成锁定依赖的 `Cargo.lock`，Rust 1.85 下 `fmt`、`clippy -D warnings` 和全部测试通过；
- `secure-storage` 权限白名单修复；
- SDK Build NuGet 工具目录修复；
- `.pnp` native mode 写入和 Unix 安装执行位恢复。
- `pcl.package-assets` 签名文件表解析、路径约束和 SHA-256 复核已在 SDK/宿主源码实现并有回归测试。
- 可选诊断窗口、网络诊断命令、剪贴板复制和脱敏 JSON 报告导出；
- 身份初始化、secure storage fail-closed 状态处理，以及 Helper 会话内身份种子清零。

## 正在推进

- 发布包含 `pcl.package-assets` 的下一版 SDK/宿主，升级插件依赖并替代不可用的 `Assembly.Location`；
- EasyTier 2.6.x 节点生命周期、房间凭据与本地端口转发适配。

## 后续里程碑

1. Helper RoomService 接入 EasyTier 房间凭据、成员事件和重连；
2. EasyTier 节点生命周期、NAT 探测、直连/中继与网络质量；
3. 生产创建/加入响应替换当前 `room.backend-not-ready`；
4. 解决公共 Contracts 的默认 ALC 共享机制并注册四个插件导出；
5. 跨 PCL N、PCL CE 和其他兼容启动器的端到端联机测试；
6. 完整许可证、SBOM、第三方许可、官方签名和商店审核发布。
