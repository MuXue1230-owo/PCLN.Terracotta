# 发布前检查清单

## 代码与测试

- [ ] `scripts/pre-release-check.ps1` 全绿
- [ ] Windows / Ubuntu / macOS CI 全绿
- [ ] 六个 RID Helper release 产物齐全
- [ ] （可选）六个 RID `easytier-core` 与 `easytier-cli` 齐全
- [ ] `scripts/generate-sbom.ps1` 生成 `artifacts/sbom/`

## 功能验收

- [ ] 创建房间 → 房间码 / 成员列表 / 诊断
- [ ] 同机加入 → 本地地址可连
- [ ] 跨机加入（有 EasyTier）→ mesh 路径或 TUN 路径
- [ ] Helper 异常退出一次 → 自动恢复提示与房间重建
- [ ] 10 秒内连续两次崩溃 → 进入 Faulted，不再死循环重启
- [ ] 插件停用无残留 Helper 进程
- [ ] 诊断 JSON 无 Secret / 完整房间码 / 私钥

## 包与签名

- [ ] `TerracottaRequireNativeHelpers=true` 打包 `.pnp`
- [ ] （正式）`TerracottaRequireEasyTier=true`
- [ ] OpenPGP 签名与文件表哈希校验
- [ ] Unix Helper / EasyTier 可执行位
- [ ] 版本号与 `CHANGELOG.md` 一致

## 互通矩阵（发布前至少抽样）

| 场景 | 状态 |
|---|---|
| PCL N ↔ PCL N 同机 | 必测 |
| PCL N ↔ PCL N 跨机 | 必测（有 sidecar） |
| Scaffolding 协议自测 | 自动化已覆盖 |
| PCL N ↔ PCL CE | 发布前手工 |
| PCL N ↔ HMCL/FCL | 发布前手工 |

## 已知限制（写入发行说明）

- EasyTier CLI 诊断依赖同目录 `easytier-cli`；缺失时回退 TCP 探测
- 跨机默认 `--no-tun`；困难网络可 `TERRACOTTA_EASYTIER_ALLOW_TUN=1`
- 房间恢复在 Helper 崩溃后重建会话，不保证 Minecraft 侧不断线
