# 第三方 iOS 库获取（官方地址 + 确切版本）

移植需要三样第三方库。它们各有独立授权，本仓库**不转发二进制**，但把官方出处、**确切版本**、产物文件名和入口符号全部列清——照着取即可，不用自己找、不会拿错版本。

> ⚠️ **版本必须匹配**：FMOD 引擎、FMOD-Godot 扩展、Spine-Godot 扩展三者的版本要互相兼容，且都为 **Godot 4.5.1** 编译。版本错了会 `FMOD_ERR_HEADER_MISMATCH` 或入口符号对不上，装机后音频/动画崩。本移植实测通过的组合见下。

## 一览

| 产物文件（放进 `config.sh` 指定目录） | 来自 | 确切版本 | 入口符号（build-ios.sh 会校验） |
|---|---|---|---|
| `libfmod_iphoneos.a` + `libfmodstudio_iphoneos.a` | FMOD Engine（官方 SDK） | **2.03.xx** | 静态链接 |
| `libGodotFmod.ios.template_release.xcframework` | utopia-rise/fmod-gdextension（自行编译） | 对应 FMOD 2.03 / Godot 4.5.1 | `fmod_library_init` |
| `libspine_godot.ios.template_release.framework` | EsotericSoftware/spine-runtimes（自行编译） | Spine **4.2** 分支 / Godot 4.5.1 | `spine_godot_library_init` |

- 前两个放进 `STS2_IOS_LIBS_DIR`（xcframework/framework）与 `STS2_FMOD_STATIC_DIR`（`.a`）。
- 目录约定见 `ios-export/config.example.sh`。

## 1) FMOD Engine（音频，静态库）

- **官方下载**：https://www.fmod.com/download → 注册免费账号 → 下 **FMOD Engine**（含 Core + Studio API）**iOS** 版，选 **2.03** 版本线。
- 取其中：`libfmod_iphoneos.a`、`libfmodstudio_iphoneos.a`（iOS 真机；simulator 版用不到）。
- **授权**：FMOD 对营收低于阈值的独立开发者免费，但**必须在 fmod.com 注册**并遵守其许可。自己去下，别找别人转发的。

## 2) FMOD-Godot 扩展（`libGodotFmod`）

- **官方仓库**：https://github.com/utopia-rise/fmod-gdextension （FMOD Studio 的 Godot GDExtension，产物名就叫 `libGodotFmod`）
- 文档：https://fmod-gdextension.readthedocs.io/
- **怎么来**：按其构建说明，为 **Godot 4.5.1 + FMOD 2.03** 编 iOS `template_release` → 得 `libGodotFmod.ios.template_release.xcframework`。编译时需把上面 (1) 的 FMOD SDK 喂给它。
- iOS 模板只能在 **macOS** 上编。

## 3) Spine-Godot 扩展（`libspine_godot`）

- **官方仓库**：https://github.com/EsotericSoftware/spine-runtimes → 子目录 `spine-godot`，用 **`4.2` 分支**。
- 文档：https://en.esotericsoftware.com/spine-godot
- **怎么来**：按其构建脚本为 **Godot 4.5.1** 编 iOS `template_release` → 得 `libspine_godot.ios.template_release.framework`。
- **授权**：Spine 运行时可免费集成，但**你的软件的使用者需各自持有 Spine 许可证**（Esoteric Software 条款）。

## 放好后

三样就位、路径填进 `ios-export/config.sh` 后，`build-ios.sh` 的第 0 步前置检查会逐一验在不在、第 3 步把它们放进导出工程 `addons/`。缺哪个都会响亮报错并指名。库名/入口符号的对应关系另见 [`../ios-export/README.md`](../ios-export/README.md) 「库映射」。
