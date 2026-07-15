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

### 拆成哪两块

| 块 | 内容 | 体积 | 去向 | 参与续签吗 |
|---|---|---|---|---|
| **瘦身签名包** | 可执行文件 + 框架(sts2.framework 等) + `Info.plist` | **.app ~165M / IPA ~55M** | SideStore 反复重签的就是它 | ✅ 每次续签 |
| **素材包** `StS2.pck` | 游戏全部场景/卡牌/图像/音频资源 | **1.77G** | 一次性推到 App 文档区 `Documents/StS2.pck` | ❌ 从不参与 |

要害：SideStore 每次续签/安装会把签名包**整包读进内存**处理。若 pck 在包里，2G 整包必被 iOS 内存看门狗(jetsam)杀。拆出来后签名包只有 55M，续签秒过；而 SideStore 续签/同 bundle 重装**都不动 `Documents`**，所以素材包推一次就长驻，续签期间纹丝不动。

### 瘦身包怎么来的（剥离机制）

`build-ios.sh` 走 Xcode GUI 首次构建出的 `.app` 里 **是带 pck 的**（大）。要得到瘦身版，把 pck 从 `.app` 里剥掉、并告诉引擎改从文档区加载——这三步 `deploy-slim.sh` 已封装：

1. `rm StS2.pck`（从 `.app` 里删掉打进去的 pck）
2. 往 `Info.plist` 注入 `godot_cmdline = ["--main-pack","user://StS2.pck"]`
   （iOS 上 `user://` = App 的 `Documents/`；Godot 启动即从这里找 pck。引擎读该键的位置：`drivers/apple_embedded/main_utilities.mm`）
3. 重新签名 → 得到只有壳的瘦身包

> 首次也可直接用 `deploy-slim.sh` 产出瘦身包用于 USB 测试装机；走 SideStore 永久续签则把瘦身包打成 IPA 交给 SideStore。

### 素材包怎么上机（一次性，别踩坑）

签名包装好、App 容器已存在后，把 1.77G 的 `StS2.pck` 推进它的文档区。仓库提供 turnkey 脚本：

```bash
bash ios-export/push-pck.sh          # 默认推 build/StS2.pck，读 config.sh 里的设备和 bundle
```

它用 `xcrun devicectl device copy to … --domain-type appDataContainer --domain-identifier <你的bundle> --source StS2.pck --destination Documents/StS2.pck`，推完**校验文档区确实出现 `Documents/StS2.pck`**（只信退出码会踩“报成功却没落地”的坑）。

- **前置**：手机上必须**先装好瘦身 App**（经 SideStore 或 Xcode），容器才存在，才能往里推。
- **bundle 要对**：SideStore 装的 App bundle 带团队后缀，`config.sh` 里 `STS2_BUNDLE_ID_SIGNED` 必须填这个带后缀的实际 bundle，否则推错容器。
- **devicectl 不通时的兜底**：Finder 连线 → 手机 → 文件 → 找到该 App → 把 `StS2.pck` 拖进它的文档区（文件名必须是 `StS2.pck`）。

架构全景见 [`DESIGN.md`](DESIGN.md) 「部署架构 v2」。

## 搭建步骤

**桌面端（一次性）**
1. 装 [iLoader](https://iloader.site/)，按其指引把 **SideStore** 装到你的 iPhone，并生成**配对文件**。
2. 用 iLoader 把配对文件导入设备（之后无线续签靠它）。

**手机端（一次性）**
3. App Store 装 [LocalDevVPN](https://apps.apple.com/us/app/localdevvpn/id6755608044)，开启它。
4. 在 SideStore 里 sideload 本移植的**瘦身签名 IPA**（`build-ios.sh` + Xcode 产出，只有壳，不含 pck）。
   - 大内存权限（`increased-memory-limit`）需在你的 App ID 能力里登记；SideStore 重签会**保留**该 entitlement（实测续签后可用额度约 5.4GB，无权限版全程仅 ~3GB）。
5. 素材包上机（一次即可）：`bash ios-export/push-pck.sh` 把 `build/StS2.pck` 推到 App 的 `Documents/StS2.pck`（详见上「素材包怎么上机」，含校验与兜底）。

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
