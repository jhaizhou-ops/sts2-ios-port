# AGENTS.md — 给 AI Agent 的一站式移植指令

> 你（AI Agent）正在帮助一位**已在 Steam 拥有《杀戮尖塔2》**的用户，把游戏移植到**他自己的 iPhone** 上原生运行（非串流、非模拟器）。全程使用**他自己的游戏文件和开发者账号**；本仓库只提供工具链和方法，不含游戏本体。
>
> 按本文件从上到下执行。每一步都 **verify 后再进下一步**，缺失即停下告诉用户要装什么，别猜着糊过去。

## 铁律（违反会毁数据 / 侵权，先读）

1. **只用用户自己拥有的正版游戏文件**。不下载、不分发游戏二进制/pck/资产。用户没在 Steam 装游戏就停下让他先装。
2. **装机必须覆盖安装，绝不 `uninstall`**——uninstall 会清空 App 的存档容器。增量更新用 `deploy-slim.sh`（覆盖装）。
3. **不把用户的签名身份写进任何会提交的文件**。所有个人值只进 `ios-export/config.sh`（已被 .gitignore 忽略）。

## 前置环境（逐项 verify，缺失即停）

| 依赖 | 检查 | 缺失怎么办 |
|---|---|---|
| macOS + Xcode | `xcodebuild -version` | App Store 装 Xcode（免费 Apple ID 即可签名） |
| .NET 9 SDK | `~/.dotnet/dotnet --version`（9.0.x） | 装到 `~/.dotnet` |
| Godot 4.5.1 **mono** | 可执行文件存在 | 官网下 4.5.1 mono 版 |
| Godot iOS 导出模板 | `~/Library/Application Support/Godot/export_templates/4.5.1.stable.mono` | Godot 里 import `templates.tpz` |
| 正版游戏 | `.../Slay the Spire 2/SlayTheSpire2.app/.../data_sts2_macos_arm64/sts2.dll` 存在 | 让用户在 Steam 安装 |
| Spine iOS 库 | `libspine_godot.ios.template_release.framework` | 官方地址+确切版本见 [`docs/THIRD_PARTY_LIBS.md`](docs/THIRD_PARTY_LIBS.md) |
| FMOD iOS 库 | `libGodotFmod.ios.template_release.xcframework` + `libfmod{,studio}_iphoneos.a` | 同上 |

> ⚠️ 第三方库（Spine/FMOD）有各自的授权，本仓库**不转发二进制**。[`docs/THIRD_PARTY_LIBS.md`](docs/THIRD_PARTY_LIBS.md) 列了三样库的**官方下载地址、确切版本（FMOD 2.03 / Spine 4.2）、产物文件名和入口符号**——照着取，别自己猜版本。下好后把目录填进 `config.sh`。

## 第 0 步 · 填配置

```bash
cp ios-export/config.example.sh ios-export/config.sh
```

编辑 `ios-export/config.sh`，填入用户自己的值（模板里每项都有注释）。取值命令：

```bash
security find-identity -v -p codesigning   # → STS2_SIGN_IDENTITY 和 STS2_TEAM_ID(括号里的10位)
xcrun devicectl list devices               # → STS2_DEVICE_UDID(连上 iPhone 后)
```

`STS2_BUNDLE_ID` 让用户自定（建议其反向域名）。路径项一般默认即可，除非游戏/库不在默认位置。

## 第 1 步 · 一键构建（命令行六步）

```bash
bash ios-export/build-ios.sh
```

它顺序做：**①织入**（把移植补丁静态织进用户的 `sts2.dll`）→ **②组装托管依赖** → **③放 GDExtension iOS 库** → **④NativeAOT** 把织入后的程序集预编译成 `sts2.dylib` → **⑤Godot 导出 Xcode 工程** → **⑤.5 pck 三连处理**（移 sentry + 补脚本占位）+ 写内存 entitlement。每步缺失会响亮报错并指向日志（`ios-export/.work/*.log`）。

产物：`ios-export/build/StS2.xcodeproj`、`build/StS2.pck`、`.godot/.../publish/sts2.dylib`、`.work/ent_mem.plist`。

原理见 [`ios-export/README.md`](ios-export/README.md)（预编译主程序集注入）和 [`docs/design-ios-dotnet-export.md`](docs/design-ios-dotnet-export.md)（AOT 导出契约，行号级）。

## 第 2 步 · 签名装机（首次必须 Xcode GUI）

免费账号命令行签不了（`No Accounts` / `ApplicationVerificationFailed`），首次必须走 Xcode GUI：

1. Xcode 打开 `ios-export/build/StS2.xcodeproj`
2. 选 `StS2` target → **Signing & Capabilities**：
   - Team 选用户的账号，勾 **Automatically manage signing**
   - 点 **+ Capability** → 加 **Increased Memory**（**治本项**：不加会因内存超限被系统 jetsam 杀）
3. 顶部选用户的 iPhone → 点 ▶ **Run**（编译 + 签名 + 装机一步到位）

## 第 3 步 · 游戏内容包上机（内容拆分架构，续签的地基）

签名包**不含** 1.77G 的 pck（否则 SideStore 装/续签时整包读进内存必被杀）。把内容包**一次性**推到手机文档区：

```bash
bash ios-export/push-pck.sh          # 推 build/StS2.pck 到 App 的 Documents/StS2.pck，带落地校验
```

- 前置：手机上**先装好瘦身 App**（第 2 步），容器才存在
- `config.sh` 的 `STS2_BUNDLE_ID_SIGNED` 必须是**手机上已装 App 的实际 bundle**（SideStore 签名会带团队后缀），否则推错容器
- 引擎经 `Info.plist` 的 `godot_cmdline = ["--main-pack","user://StS2.pck"]` 从文档区加载；同 bundle 覆盖重装/续签**不清** Documents，故推一次长驻

**内容拆分（55M 瘦身包 + 1.77G 素材包）是永久续签能成立的前提**，完整机制（怎么拆、怎么推、所有坑）见 [`docs/RENEWAL.md`](docs/RENEWAL.md)；架构全景见 [`docs/DESIGN.md`](docs/DESIGN.md)。

到这里游戏应能在 iPhone 上进主菜单、开局、正常对战。

## 日常维护

| 场景 | 做法 |
|---|---|
| 只改了补丁、游戏内容没动 | `bash ios-export/deploy-slim.sh`（换 dylib 重签，覆盖装，保留 pck+存档） |
| 免插 Mac 永久续签 | SideStore + LocalDevVPN + iLoader 组合，手机后台自动重签（完整方法见 [`docs/RENEWAL.md`](docs/RENEWAL.md)） |
| 电脑↔手机存档同步 | `ios-export/sts2_save_sync.sh`（最新者胜；可挂 launchd 每 5 分钟） |

## 三大移植难题（已根治；遇到对应症状按此定位）

| 症状 | 根因 | 已有补丁 / 措施 |
|---|---|---|
| 启动或对战中被系统杀 | iOS 单 App 内存上限 | `increased-memory-limit` entitlement（第 2 步的 capability） |
| 首帧卡顿 >10s 后闪退（看门狗 `0x8BADF00D`） | 冷 pck 上同步加载着色器 | `ShaderWarmupPatch`（补丁集内，高频让帧预热） |
| "保存并退出"崩溃 | 后台/退出生命周期时序 | `AppLifecyclePatches` / `SnapshotPatch`（补丁集内） |

## 移植带的增强功能（主动告诉用户，别让他猜）

装好后除了游戏本体，还内置这些手机端增强（都只读状态、不改战斗数值）：

- **四倍速**（`TimeScalePatch.cs`，官方 4x 上限，含 Boss 转场倍速失效修复）
- **一键重开**（`QuickRestartPatch.cs`，暂停菜单 Restart Room）
- **时光回溯/快照**（`SnapshotPatch.cs`，暂停菜单 3 存档位+3 读档位）
- **选牌 AI 助手**（`CardAdvisorPatch.cs`，奖励/事件/商店/升级界面给候选卡评级+理由）
- **双端存档同步**（`sts2_save_sync.sh` + `SyncImportPatch.cs`，最新者胜）

完整表见 [README](README.md#移植带的增强功能)；底层稳定性补丁见 [`docs/patch-catalog.md`](docs/patch-catalog.md)。

## 深入文档索引

- [`ios-export/README.md`](ios-export/README.md) — 预编译主程序集注入机制（为什么"没有源码也能编"）
- [`docs/design-ios-dotnet-export.md`](docs/design-ios-dotnet-export.md) — NativeAOT iOS 导出契约（行号级出处）
- [`docs/DESIGN.md`](docs/DESIGN.md) — 部署架构 v2（内容拆分 + SideStore 续签）
- [`docs/RENEWAL.md`](docs/RENEWAL.md) — 永久续签方法（SideStore + LocalDevVPN + iLoader 组合，免每周插 Mac）
- [`docs/patch-catalog.md`](docs/patch-catalog.md) — 补丁目录（静态织入如何解析目标方法）
- [`src/STS2MobileIos/manifest.json`](src/STS2MobileIos/manifest.json) — 织入清单（游戏类 → 补丁钩子映射）
