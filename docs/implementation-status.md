# 实现状态

## 已完成

- alpha.1–alpha.5：双进程边界、EasyTier 后端、mesh、导出、推送事件、质量探测；
- **rc.1（发布候选）：**
  - Helper 异常退出自动恢复（10 秒窗口内最多 1 次），并尝试重建房间；
  - EasyTier CLI 诊断解析（`easytier-cli peer`）+ TCP 回退；
  - 确定性 RPC portal；
  - `scripts/pre-release-check.ps1`、`scripts/generate-sbom.ps1`、`docs/release-checklist.md`。

## 发布后跟踪

1. 六 RID EasyTier **强制**门禁默认开启（需稳定下载源与体积策略）；
2. 与 PCL CE / HMCL / FCL 实机互通签字验收；
3. 官方 OpenPGP 密钥与商店签名；
4. CycloneDX SBOM 进入 CI 工件。

## 非目标（rc.1 明确不做）

- 进程内强沙箱；
- 默认永久 TUN；
- Helper 崩溃时 Minecraft 玩家零感知无缝迁移。
