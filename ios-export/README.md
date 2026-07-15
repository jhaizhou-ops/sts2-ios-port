# STS2 iOS 导出工程

这个工程是一个**驱动壳**：它本身几乎没有内容，作用是驱动三件事产出一个可装 iPhone 的 .ipa：
1. 用 NativeAOT 把**织入后的 sts2.dll**（游戏主程序集 + 补丁）预编译成 `sts2.framework`
2. 套上 Godot 4.5.1 iOS 导出模板（原生壳 + Metal 渲染）
3. 装入两个 GDExtension（FMOD 音频、Spine 动画）的 iOS 库 + 游戏 pck 内容

游戏真正的内容（场景/卡牌/资源）来自游戏**自己的 pck**，不在本工程里。本工程的 `project.godot`
只需 assembly_name=sts2 与主场景指向一致，导出时用游戏 pck 作为主内容包。

## 核心机制：预编译主程序集注入

难点：Godot 的 iOS 导出会跑 `dotnet publish sts2.csproj`，正常流程是 `csc` 把 C# **源码**
编译成 sts2.dll 再交给 NativeAOT。但我们没有源码，只有织入好的 sts2.dll。

解法（顺着官方流程插一个替换点，不 hack 工具链）：
- `sts2.csproj` 不含任何 .cs 源码（EnableDefaultCompileItems=false）→ csc 产出一个空的占位 sts2.dll
- 一个 `BeforeTargets="IlcCompile"` 的 MSBuild target，在 NativeAOT 编译器（ILC）读取托管程序集
  **之前**，用我们织入好的 sts2.dll 覆盖掉中间产物路径的占位 dll
- 所有 200+ 依赖程序集作为 `<ReferencePath>` 提供给 ILC 解析
- `TrimMode=partial` + 显式 root，保住游戏靠反射注册的 1624 个类型不被裁剪

这样 ILC 实际编译的是我们的织入版游戏程序集，assembly_name=sts2 天然满足 Godot 的三处硬耦合
（导出校验 / framework 打包 / 运行时 dlopen）。

## 文件
- `project.godot` — 驱动壳工程设置
- `sts2.csproj` / `sts2.sln` — 空源码 + 预编译注入 target
- `export_presets.cfg` — iOS 导出预设（arm64、两个 GDExtension、pck 策略）
- `build-ios.sh` — 一键：刷新织入 dll → 注入依赖 → Godot 导出 → 产出 .ipa/Xcode 工程
- `Directory.Build.props` — 全局 TrimmerRoot 配置

## 库映射（已验证名字/入口符号匹配游戏要求）
| 插件 | 游戏要求路径 | 我编出的产物 | 入口符号 |
|---|---|---|---|
| Spine | ios/libspine_godot.ios.template_release.framework | ios-libs/ 同名 ✓ | spine_godot_library_init ✓ |
| FMOD | res://addons/fmod/libs/ios/libGodotFmod.ios.template_release.xcframework | ios-libs/ 同名 ✓ | fmod_library_init ✓ |
| FMOD 依赖 | libs/ios/libfmod_iphoneos.a + libfmodstudio_iphoneos.a | fmod-ios/lib/ ✓ | (静态链接) |
| Sentry | (无 iOS) | 禁用 | — |
