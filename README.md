# Slay the Spire 2 · iOS 原生移植工具链

把你**自己拥有的**《杀戮尖塔2》（Slay the Spire 2）搬上 iPhone 原生运行（非串流、非模拟器）的一套开源工具链、补丁与移植说明。全球首个 iOS 原生移植的方法记录。

> ⚠️ **本仓库不含游戏本体。** 这里只有"怎么把你合法拥有的游戏搬上 iPhone"的**方法和工具**，不包含也永远不会包含游戏二进制、`.pck` 资源、美术/字体/音频资产。使用者需**自备正版 Slay the Spire 2（Steam）**，用自己的 Steam 账号获取游戏文件。

> 🤖 **让 AI Agent 帮你一站式移植**：把本仓库交给你的 AI 编码助手（Claude Code / Cursor 等），让它读 **[`AGENTS.md`](AGENTS.md)**——那是一份从环境检查、填配置、一键构建到签名装机的确定性 runbook，Agent 照着走就能把游戏搬上你的 iPhone。你只需自备正版游戏和一个 Apple ID。

## 与出品商的关系（免责声明）

本项目与 **MegaCrit**（《Slay the Spire 2》的开发/发行商）**无任何关联、未获其背书**。"Slay the Spire" 及相关商标归 MegaCrit 所有。本工具链的定位与安卓端先例 [Ekyso/StS2-Launcher](https://github.com/Ekyso/StS2-Launcher) 一致——一套面向**自带正版游戏文件**用户的互操作/移植工具。请支持正版：先在 Steam 购买游戏。

## 这是什么 / 不是什么

| 是 | 不是 |
|---|---|
| Cecil 静态织入器（把移植补丁织进你自己的游戏 dll） | 游戏本体 / 破解版 / 免费获取游戏的途径 |
| iOS 移植补丁源码（触控、UI 缩放、内存、着色器预热、后台/退出等） | 反编译的游戏源码 |
| iOS 构建工程（NativeAOT 导出契约、pck 处理脚本） | FMOD / Spine 等第三方专有库（各自去官网下载） |
| 移植过程的技术文档 | 游戏资产（卡面、音频、字体、场景） |

## 技术要点

iOS 禁止 JIT，Harmony 之类运行时打补丁的方案不可用。本项目改用 **Mono.Cecil 静态织入**：编译期把补丁织进游戏程序集，再用 **NativeAOT** 把织入后的 `sts2.dll` 提前编译成 iOS 原生库 `sts2.framework`。整条链顺着 Godot 官方 iOS 导出流程插一个"预编译主程序集替换点"，不 hack 工具链。详见 [`docs/`](docs/)。

三大移植难题的根治：内存超限被系统杀（提高内存 entitlement）、着色器首帧卡顿（预热）、"保存并退出"崩溃。

## 移植带的增强功能

除了把游戏跑起来，移植还内置了这些手机端增强（都是原创补丁，**只读游戏状态、从不改动任何战斗数值**）：

| 功能 | 说明 | 源文件 |
|---|---|---|
| **四倍速** | 全局 4x（游戏官方钳制上限就是 4.0，安全），含"Boss 二阶段转场后倍速失效"的修复 | [`TimeScalePatch.cs`](src/STS2MobileIos/Patches/TimeScalePatch.cs) |
| **一键重开** | 暂停菜单加"Restart Room"，一键回到进房时的存档点 | [`QuickRestartPatch.cs`](src/STS2MobileIos/Patches/QuickRestartPatch.cs) |
| **时光回溯 / 快照** | 暂停菜单 3 个存档位 + 3 个读档位，随时存读（自由 SL） | [`SnapshotPatch.cs`](src/STS2MobileIos/Patches/SnapshotPatch.cs) |
| **选牌 AI 助手** | 奖励/事件/商店/升级界面按流派给候选卡评级+理由（只加标签，不改概率/数值） | [`CardAdvisorPatch.cs`](src/STS2MobileIos/Patches/CardAdvisorPatch.cs) |
| **双端存档同步** | 电脑↔手机"最新者胜"自动同步，手机侧二次裁决防旧覆新 | [`sts2_save_sync.sh`](ios-export/sts2_save_sync.sh) + [`SyncImportPatch.cs`](src/STS2MobileIos/Patches/SyncImportPatch.cs) |

底层移植/稳定性补丁（触控、UI 缩放、移动布局、内存、着色器预热、生命周期等）见 [`docs/patch-catalog.md`](docs/patch-catalog.md)。

## 仓库结构

```
AGENTS.md              给 AI Agent 的一站式移植 runbook（从这里开始）
src/STS2Weaver/        Mono.Cecil 静态织入器（纯原创工具）
src/STS2MobileIos/     iOS 移植补丁工程
  ├─ Patches/          触控/布局/UI缩放/着色器/快照/生命周期/选牌助手 等补丁
  ├─ manifest.json     织入清单（目标游戏类 → 补丁钩子的映射）
  └─ PatchHelper.cs    反射/日志辅助
ios-export/            iOS 构建工程（build-ios.sh 六步链、NativeAOT 导出契约）
  ├─ config.example.sh 本地配置模板（复制成 config.sh 填你自己的签名身份）
  └─ build/push-pck/deploy/sync 一键构建 / 素材包上机 / 增量装机 / 存档同步脚本
tools/                 pck 处理脚本（操作你自己的合法游戏文件）
docs/                  移植技术文档（部署架构、.NET AOT 导出契约、补丁目录）
share/                 移植技术记录（PDF / HTML）
```

## 使用前提

- 一台 Mac + Xcode（免费 Apple ID 可用，7 天签名周期）
- .NET 9 SDK、Godot 4.5.1 (mono)
- **你自己的**正版 Slay the Spire 2 游戏文件（Steam）
- FMOD iOS SDK、Spine 运行时 iOS 库——官方地址与确切版本（FMOD 2.03 / Spine 4.2）见 [`docs/THIRD_PARTY_LIBS.md`](docs/THIRD_PARTY_LIBS.md)（本仓库不转发二进制）

构建流程见 [`ios-export/README.md`](ios-export/README.md)。

## 许可

本仓库内的**原创工具、补丁与文档**以 [MIT 许可](LICENSE) 发布。许可仅覆盖本仓库中的原创代码，**不涉及**《Slay the Spire 2》游戏本身或任何第三方专有库——它们受各自的授权约束。
