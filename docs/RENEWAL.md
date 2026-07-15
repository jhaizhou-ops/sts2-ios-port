# 永久续签：免费证书下让移植的游戏一直活着

免费 Apple ID 签的 App **7 天过期**。常规做法是每周把手机插回 Mac 重签——对一个要长期玩的游戏太痛苦。本文档记录一套**无需每周插 Mac** 的永久续签方法：把三个第三方工具组合起来，让手机自己在后台重签。

> 本文档是**方法与组合经验**，不含任何第三方工具的代码或二进制。SideStore / LocalDevVPN / iLoader 都是各自独立的项目，请从下方官方渠道获取——本仓库只讲"怎么把它们拼起来解决续签"。

## 三个零件（都是第三方，各有官方出处）

| 工具 | 作用 | 官方出处 |
|---|---|---|
| **SideStore** | 在手机上用你自己的开发证书重签 App，并**后台自动续签**，绕过 7 天过期。AltStore 的分支，不需要常驻 AltServer。 | https://github.com/SideStore/SideStore |
| **LocalDevVPN** | 一个纯本地的网络隧道 App，让 SideStore **无需连电脑**就能无线续签（它把设备骗成"在和一台受信任电脑通信"）。 | App Store（Coxson Engineering LLC）: https://apps.apple.com/us/app/localdevvpn/id6755608044 |
| **iLoader** | 桌面工具：安装 SideStore、生成/导入/备份**配对文件**（pairing file，无线续签所依赖）。 | https://iloader.site/ |

原理：SideStore 用你的证书重签 → LocalDevVPN 提供本地隧道 → 配对文件让设备信任这条隧道 → SideStore 只要 LocalDevVPN 连着 + 有 Wi-Fi 就在后台把 7 天倒计时刷新回去。三者缺一不可。

## 为什么大体积游戏能这样续签（关键：内容拆分）

SideStore 每次续签/安装会把**签名包**读进内存处理。一个 ~2G 的游戏整包必被系统内存看门狗（jetsam）杀。本移植用**内容拆分架构**解决：

- **签名包只含** 可执行文件 + 框架 + `Info.plist`（~165M，IPA ~54M）→ SideStore 装/续签轻松
- **1.77G 的 `StS2.pck`** 一次性 USB 推到手机文档区 `Documents/StS2.pck`，**不进签名包**
- 引擎经 `Info.plist` 的 `godot_cmdline = ["--main-pack","user://StS2.pck"]` 从文档区加载

因为 SideStore 续签只动签名包、**不动 `Documents`**，pck 和存档一次推好后长期存活。架构细节见 [`DESIGN.md`](DESIGN.md) 「部署架构 v2」。

## 搭建步骤

**桌面端（一次性）**
1. 装 [iLoader](https://iloader.site/)，按其指引把 **SideStore** 装到你的 iPhone，并生成**配对文件**。
2. 用 iLoader 把配对文件导入设备（之后无线续签靠它）。

**手机端（一次性）**
3. App Store 装 [LocalDevVPN](https://apps.apple.com/us/app/localdevvpn/id6755608044)，开启它。
4. 在 SideStore 里 sideload 本移植的**瘦身签名 IPA**（`build-ios.sh` + Xcode 产出，只有壳，不含 pck）。
   - 大内存权限（`increased-memory-limit`）需在你的 App ID 能力里登记；SideStore 重签会**保留**该 entitlement（实测续签后可用额度约 5.4GB，无权限版全程仅 ~3GB）。
5. USB 把 `build/StS2.pck` 推到 App 的 `Documents/StS2.pck`（一次即可）。

**之后**
6. 保持 LocalDevVPN 连接 + Wi-Fi，SideStore 会在后台自动续签；也可在 SideStore 里点 App 右侧的「7 DAYS」手动刷新。

## 实战要点 / 坑（都实测过）

- **续签不清 Documents**：SideStore Refresh 不动文档区，pck / 存档 / 标记全存活（手动 Refresh 实测 mtime 未变）。所以更新 App 别 uninstall。
- **同名同版本会用缓存包**：SideStore 对"同 bundle、同 version"的 IPA 会用缓存旧包 → 推新构建必须 **bump `CFBundleVersion`**，否则装的还是旧的。
- **配对文件会失效**：iOS 更新/重置后配对文件可能过期，用 iLoader 重新导入即可。
- **VPN 互斥**：LocalDevVPN 是一条系统 VPN，和你平时的代理 VPN **互斥**——续签时要把代理 VPN 全关，否则 LocalDevVPN 连不上、续签静默失败。
- **bundle 带团队后缀**：SideStore 签名后 bundle 会带 SideStore 团队后缀（形如 `<你的bundle>.<SideStoreTeamID>`）。存档同步脚本要用这个带后缀的 bundle（`config.sh` 里的 `STS2_BUNDLE_ID_SIGNED`）。

## 致谢

本方法完全建立在以下三个第三方开源/免费项目之上，功劳归其作者：
[SideStore](https://github.com/SideStore/SideStore)（AltStore 团队 / SideStore 贡献者）、
[LocalDevVPN](https://apps.apple.com/us/app/localdevvpn/id6755608044)（Coxson Engineering LLC）、
[iLoader](https://iloader.site/)。本仓库不分发它们的任何代码或二进制，只记录把它们组合用于永久续签的方法。
