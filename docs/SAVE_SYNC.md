# 存档迁移与双端同步

移植完成后，你多半想把**电脑上已有的进度**搬到手机，之后两端随便玩、自动同步。本文档讲清：存档在哪、首次怎么迁、之后怎么自动同步，以及非 macOS（Windows/Linux）怎么做。

## 存档在两端的位置

| 端 | 路径 |
|---|---|
| 电脑（macOS Steam） | `~/Library/Application Support/SlayTheSpire2/steam/<你的SteamID64>/` |
| 手机（App 沙盒） | App 容器内 `Documents/default/1/`（内含 `profile.save` 等） |

两者内容一一对应：电脑的 `steam/<SteamID>/` ↔ 手机的 `default/1/`。

## 一、首次迁移（电脑 → 手机）

手机全新装好、还没任何存档时，常规同步脚本会跳过（它要先能拉到手机存档比时间）。首次迁移用专用脚本：

```bash
# 前置：手机已装好 App、启动过一次（容器已建），config.sh 填好 STS2_STEAM_ID
bash ios-export/push-save.sh
# 然后【启动游戏】——SyncImportPatch 会把进度自动导入到 default/1
```

原理：脚本把电脑存档推到手机 `Documents/sync_inbox/<版本>/1/**`，游戏启动时 [`SyncImportPatch`](../src/STS2MobileIos/Patches/SyncImportPatch.cs) 校验后**原子导入**到 `default/1`。手机没存档时任何推送都会被导入；手机若已有更新的进度，旧推送会被判为过期丢弃（最新者胜，见下）。导入前手机原树留时间戳备份（`sync_replaced_*`，保留最近 2 份）。

## 二、之后的双端自动同步

```bash
bash ios-export/sts2_save_sync.sh
```

「最新者胜」：比较两端存档树的最新修改时间——

- **手机新** → 备份电脑存档后，精确镜像到电脑（`rsync --delete`）
- **电脑新** → 推送到手机收件箱，游戏下次启动由 `SyncImportPatch` 二次裁决后导入（手机端也执行最新者胜，旧的绝不覆盖新的）
- 手机不可达/锁屏、电脑端游戏运行中 → 本轮静默跳过

**挂成定时任务**（macOS，每 5 分钟自动同步）——用 launchd 或 cron 定期跑 `sts2_save_sync.sh` 即可。每次覆盖前都留时间戳备份于 `ios-export/.work/save_backups/`（自动保留最近 30 份）。

## 三、非 macOS（Windows / Linux）怎么做

`push-save.sh` / `sts2_save_sync.sh` 用的是 macOS 的 `xcrun devicectl`。在 Windows/Linux 上没有它，但**目标是一样的**：把电脑存档放进手机 App 容器的 `Documents/`。用跨平台工具做同一件事：

- **命令行**：[`pymobiledevice3`](https://github.com/doronz88/pymobiledevice3)（Python，Windows/macOS/Linux）——它能访问 App 沙盒文件（`afc` / house-arrest）。把电脑存档目录作为 `1/` 放进 `Documents/sync_inbox/<版本>/1/`（版本用任意递增数字即可，手机没存档时都会导入），然后启动游戏由 `SyncImportPatch` 导入。也可直接写入 `Documents/default/1/`（跳过收件箱，适合手机确无存档时的干净首装）。
- **图形工具**：iMazing / 3uTools 等能浏览 App 的文档区，直接把存档拖进 `Documents/`（同上路径）。

> 说明：本仓库暂只提供 macOS 的存档脚本；Windows/Linux 走上面的跨平台工具手动完成同一动作（欢迎 PR 补 Windows 脚本）。SideStore 签名的 App 是开发签名，`pymobiledevice3` 的沙盒文件访问对它可用。

## 坑位

- **bundle 要对**：SideStore 签名后 bundle 带团队后缀，`config.sh` 的 `STS2_BUNDLE_ID_SIGNED` 必须是手机上**实际已装**的 bundle，否则读写错容器。
- **别 uninstall**：卸载会清空整个容器（存档 + pck 全没）。更新 App 一律覆盖装（`deploy-slim.sh`）。
- **电脑端游戏运行时别同步**：正在写存档时同步可能读到半截；`sts2_save_sync.sh` 已自动检测并跳过。
- **导入靠"启动游戏"触发**：推送后必须启动一次游戏，`SyncImportPatch` 才在最早启动点导入。
