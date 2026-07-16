# PCL N 陶瓦联机插件

`cn.pcln.terracotta` 是 PCL N 官方 Minecraft P2P 联机插件。项目严格采用“PCL N 托管插件 + 独立 Rust Helper”的双进程结构：插件负责页面、游戏会话与生命周期，Helper 负责本地 IPC、网络与后续的 EasyTier/Scaffolding 接入。

## 当前实现状态

当前为 `0.1.0-alpha.4`：

- 插件导出：`room-service` / `session-service` / `network-status` / `diagnostics`（需宿主 `pcl.exports`）；
- 默认 `EasyTierRoomBackend`：凭据、Scaffolding、同机 discovery、跨机 mesh 端口映射；
- 连接中自动刷新房间成员与 RTT；诊断输出粗粒度 NAT/质量启发式；
- 可选打包 `easytier-core`；CI 可通过仓库变量 `EASYTIER_VERSION` 下载 sidecar；
- 缺 sidecar 时 `network.easytier-missing`；跨机困难可设 `TERRACOTTA_EASYTIER_ALLOW_TUN=1`。

详见 [实现状态](docs/implementation-status.md)、[网络后端](docs/network.md)、[插件导出](docs/exports.md)、[CHANGELOG](CHANGELOG.md)。

## 构建

要求：

- .NET SDK `10.0.301`；
- Rust `1.85.0` 或更高兼容版本；
- GnuPG（开发 `.pnp` 签名）；
- Windows、Linux 或 macOS。

运行托管测试：

```powershell
dotnet test PCLN.Terracotta.slnx -c Release
```

运行 Helper 检查：

```powershell
cargo fmt --manifest-path src/Terracotta.Helper/Cargo.toml -- --check
cargo clippy --manifest-path src/Terracotta.Helper/Cargo.toml --locked --all-targets -- -D warnings
cargo test --manifest-path src/Terracotta.Helper/Cargo.toml --locked --all-targets
```

只生成不含原生 Helper 的开发检查包：

```powershell
dotnet build src/PCLN.Terracotta.Plugin/PCLN.Terracotta.Plugin.csproj -c Release
```

正式 CI 会构建六个 RID 的 Helper，将其放入 `native/<rid>/`，并以 `TerracottaRequireNativeHelpers=true` 生成完整 `.pnp`。任何一个目标缺失都会使打包失败。

## 项目布局

```text
src/
├─ PCLN.Terracotta.Contracts/  # IPC 与公开房间契约
├─ PCLN.Terracotta.Plugin/     # PCL N 插件、页面和控制器
└─ Terracotta.Helper/          # Rust 本地 Helper
tests/
├─ PCLN.Terracotta.Contracts.Tests/
└─ PCLN.Terracotta.Plugin.Tests/
docs/                          # 架构、协议、网络、安全和实施状态
```

## 文档

- [架构](docs/architecture.md)
- [IPC 协议](docs/protocol.md)
- [Scaffolding 兼容协议](docs/scaffolding.md)
- [网络后端](docs/network.md)
- [插件导出](docs/exports.md)
- [安全边界](docs/security.md)
- [开发与发布](docs/development.md)
- [实现状态与下一阶段](docs/implementation-status.md)

## 许可

项目按 `Apache-2.0` 发布。正式公开前还需完成第三方依赖许可与 SBOM 清单。
