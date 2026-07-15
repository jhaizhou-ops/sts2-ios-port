# Godot 4.5.1 .NET iOS 导出管线 — 技术设计简报

> 目标：搞清 Godot 4.5.1 (stable) 的 C#/.NET iOS 导出内部机制，以及如何用**预编译好的游戏 assembly（sts2.dll, net9.0 + ~200 个依赖 DLL）**替代"编辑器现场编译 csproj"。
> 所有结论均直接读自 4.5.1-stable tag 源码，附行号级出处。撰写日期 2026-07-08。

---

## 0. 一句话结论

Godot iOS 的 C# 路径 = **NativeAOT 唯一路径**：编辑器对项目 csproj 跑 `dotnet publish -r ios-arm64 --self-contained true`，由 Godot.NET.Sdk 注入 `PublishAot=true`，ILC 把主 assembly + 所有引用的 IL 编成一个 `{assembly_name}.dylib`，打成 `{assembly_name}_aot.xcframework` 嵌入导出的 Xcode 工程；运行时 dlopen 这个 dylib 并调用导出符号 `godotsharp_game_main_init`。**没有 Mono 解释器 fallback，Harmony 运行时补丁不可行。** 替换预编译 DLL 的正解是：把 Godot 项目的 csproj 做成 thin shim，`<Reference>` 全部预编译 DLL，让 ILC 一起 AOT——编辑器照常跑管线，重活本来就在 ILC 而不在 csc。

---

## 1. 管线全流程（源码级）

### 1.1 导出入口：GodotTools ExportPlugin (C#)

文件：[`modules/mono/editor/GodotTools/GodotTools/Export/ExportPlugin.cs`](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/editor/GodotTools/GodotTools/Export/ExportPlugin.cs)

`_ExportBeginImpl()` 关键逻辑（行号为 4.5.1-stable）：

- L27：前置条件是**项目根目录必须存在 `{assembly_name}.sln`**（`ProjectContainsDotNet()` 检查 `GodotSharpDirs.ProjectSlnPath`），否则整个 .NET 导出流程直接跳过。
- L189-197：构造 `PublishConfig { BuildConfig = isDebug ? "ExportDebug" : "ExportRelease", RidOS = "ios", UseTempDir = false /* iOS 专门不用临时目录 */, BundleOutputs = true }`。
  - 源码注释原文：`// xcode project links directly to files in the publish dir, so use one that sticks around.`
- L230-241：iOS 额外追加**第二个 publish 目标**：`RidOS = iossimulator, Archs = ["arm64","x86_64"], BundleOutputs = false`（模拟器 slice 只进 xcframework，不进游戏数据）。
- L266-287：publish 输出目录（iOS，非临时）：
  `{项目根}/.godot/mono/temp/bin/godot-publish-dotnet/{ExportRelease|ExportDebug}-{rid}/`
  然后调用 `BuildManager.PublishProjectBlocking(buildConfig, platform, runtimeIdentifier, publishOutputDir, includeDebugSymbols)`。
- L292-307：publish 完成后检查产物，iOS 期望的是 **`{ProjectAssemblyName}.dylib`**（NativeAOT 产物；`soExt` 对 iOS = `dylib`）。找不到 `.dll` 也找不到 `.dylib` 直接报错。
- L316-420：遍历 publish 目录收集文件：
  - **排除** `{assembly}.dylib`（它单独走 xcframework，L331-333）；
  - **排除** `.dsym` 目录；
  - `.dat` 文件（即 icudt.dat）走 `AddAppleEmbeddedPlatformBundleFile()` → 拷进 app bundle（L407-410）；
  - 其余文件（NativeAOT 下基本没有）走 `AddSharedObject()`，目录名 `data_{CSharpProjectName}_{platform}_{arch}`。
- L430-457（iOS 收尾）：
  1. 多个模拟器 slice 用 `lipo` 合成 fat dylib（`Internal.LipOCreateFile`）；
  2. `xcrun xcodebuild -create-xcframework -library <device dylib> -debug-symbols <dSYM> -library <sim dylib> ... -output {assembly_name}_aot.xcframework`（`BuildManager.GenerateXCFrameworkBlocking`，实现在 [`BuildSystem.cs` L354-373](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/editor/GodotTools/GodotTools/Build/BuildSystem.cs)）；xcframework 落在 `{项目根}/.godot/mono/temp/bin/{ExportRelease|ExportDebug}/{assembly_name}_aot.xcframework`；
  3. `AddAppleEmbeddedPlatformEmbeddedFramework(xcFrameworkPath)` 把它注册为 embedded framework。

另外：`dotnet/embed_build_outputs` 导出选项在 iOS 上被平台层**强制隐藏**（"Hide unsupported .NET embedding option"，[`editor/export/editor_export_platform_apple_embedded.cpp` L217-220](https://github.com/godotengine/godot/blob/4.5.1-stable/editor/export/editor_export_platform_apple_embedded.cpp)）。

### 1.2 精确的 dotnet publish 命令行

文件：[`BuildSystem.cs` L192-243](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/editor/GodotTools/GodotTools/Build/BuildSystem.cs) + [`BuildManager.cs` L291-314](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/editor/GodotTools/GodotTools/Build/BuildManager.cs)

Release 导出 iOS 设备时，编辑器实际执行（逐参数还原自 `BuildPublishArguments`）：

```
dotnet publish {项目根}/{assembly_name}.csproj \
    -c ExportRelease \
    -r ios-arm64 \
    --self-contained true \
    -v normal \
    -l:GodotTools.BuildLogger.GodotBuildLogger,{editor_tools_dir}/GodotTools.BuildLogger.dll;{logs_dir} \
    -p:GodotTargetPlatform=ios \
    -o {项目根}/.godot/mono/temp/bin/godot-publish-dotnet/ExportRelease-ios-arm64
```

条件性追加：
- 导出选项 `dotnet/include_debug_symbols=false` 时 → `-p:DebugType=None -p:DebugSymbols=false`
- double 精度引擎 → `-p:GodotFloat64=true`
- **命令行上没有任何 PublishAot/RID 之外的 AOT 参数** —— AOT 全在 MSBuild SDK 层注入（见 1.3）。

模拟器再跑两次：`-r iossimulator-arm64` 与 `-r iossimulator-x64`（输出目录同规律）。

### 1.3 PublishAot 从哪来：Godot.NET.Sdk 的 MSBuild 注入

游戏 csproj 用 `<Project Sdk="Godot.NET.Sdk/4.5.1">`。SDK 里：

[`Sdk/Sdk.props` L107](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/editor/Godot.NET.Sdk/Godot.NET.Sdk/Sdk/Sdk.props)：`GodotTargetPlatform == 'ios'` 时 import `iOSNativeAOT.props`（RID 以 `ios` 开头时也会自动推断 platform=ios，Sdk.props L65）。

[`iOSNativeAOT.props`](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/editor/Godot.NET.Sdk/Godot.NET.Sdk/Sdk/iOSNativeAOT.props) 全文：

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <PublishAotUsingRuntimePack>true</PublishAotUsingRuntimePack>
  <UseNativeAOTRuntime>true</UseNativeAOTRuntime>
  <TrimmerSingleWarn>false</TrimmerSingleWarn>
</PropertyGroup>
```

[`iOSNativeAOT.targets`](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/editor/Godot.NET.Sdk/Godot.NET.Sdk/Sdk/iOSNativeAOT.targets)（由 Sdk.targets L38 在 ios 时 import）：

```xml
<ItemGroup>
  <TrimmerRootAssembly Include="GodotSharp" />
  <TrimmerRootAssembly Include="$(TargetName)" />
  <LinkerArg Include="-install_name '@rpath/$(TargetName)$(NativeBinaryExt)'" />
</ItemGroup>
```

外加三个 target：
- `PrepareBeforeIlcCompile`：从 runtime pack 拷 `icudt.dat` 进 PublishDir（.NET 9 兼容修复，见 §7）；`xcrun xcode-select -p` 找 Xcode，给 ILC 链接器注入 `-miphoneos-version-min=12.0 -isysroot .../iPhoneOS.sdk`（模拟器 RID 用 iPhoneSimulator.sdk）。
- `FixSymbols`：把 dSYM 目录改名成 `{TargetName}.framework.dSYM` 供 create-xcframework 使用。

**结论 A**：`PublishAot=true` 不是编辑器命令行传的，而是 Godot.NET.Sdk 对 `GodotTargetPlatform=ios` 无条件强制的。`UseMonoRuntime` 完全不出现在 Godot 代码库里——没有 Mono 路径。产物形态是**动态库**（`-install_name @rpath/...` + `xcodebuild -create-xcframework -library`），不是静态 .a。

### 1.4 Xcode 工程如何链接产物

文件：[`editor/export/editor_export_platform_apple_embedded.cpp`](https://github.com/godotengine/godot/blob/4.5.1-stable/editor/export/editor_export_platform_apple_embedded.cpp)

- L1459-1461：导出时收集所有 export plugin 的 `apple_embedded_platform_embedded_frameworks`（即上面注册的 `{assembly}_aot.xcframework`），以 `is_framework=true, should_embed=true` 拷入工程。
- L1326-1360：含 dylib 的 xcframework 会被 `_convert_to_framework()` **把每个 dylib slice 转成 .framework**（源码注释："we need to turn .dylib inside .xcframework into .framework to be able to send application to AppStore"），落在导出工程的 `dylibs/` 目录下，并写入 pbxproj 的 Embed Frameworks（签名 + 拷入 app 的 `Frameworks/`）。
- 引擎本体是静态库 `libgodot.ios.release.xcframework`（L1843），与 C# dylib 无链接关系——dylib 是运行时 dlopen 的，不在链接命令里。
- `icudt.dat` 作为 bundle file 拷入 app bundle 根（L1470-1471）。

### 1.5 设备上运行时如何加载

文件：[`modules/mono/mono_gd/gd_mono.cpp`](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/mono_gd/gd_mono.cpp)

`GDMono::initialize()`（L637 起）顺序尝试：hostfxr → coreclr → **NativeAOT**。iOS 上前两者不存在（模板里根本没打包），必然走 `try_load_native_aot_library()`（L489-517）：

1. `assembly_name = Path::get_csharp_project_name()` —— 读项目设置 **`dotnet/project/assembly_name`**，为空则回退到净化后的 `application/config/name`（[`path_utils.cpp` L235-250](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/utils/path_utils.cpp)）。
2. 拼 `{assembly_name}.dylib`，经 `OS_AppleEmbedded::open_dynamic_library()`（[`drivers/apple_embedded/os_apple_embedded.mm` L253](https://github.com/godotengine/godot/blob/4.5.1-stable/drivers/apple_embedded/os_apple_embedded.mm)）逐级 fallback，最终命中 `<app>/Frameworks/{assembly_name}.framework/{assembly_name}` → `dlopen`。
3. `dlsym("godotsharp_game_main_init")` 拿初始化函数。

这个符号来自 **Godot.SourceGenerators 的 `GodotPluginsInitializerGenerator`**（[源码](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/editor/Godot.NET.Sdk/Godot.SourceGenerators/GodotPluginsInitializerGenerator.cs)）：它向**主项目 assembly** 注入 `GodotPlugins.Game.Main.InitializeFromGameProject`，标注 `[UnmanagedCallersOnly(EntryPoint = "godotsharp_game_main_init")]`，内部调用 `ScriptManagerBridge.LookupScriptsInAssembly(typeof(GodotPlugins.Game.Main).Assembly)` —— **只注册主 assembly 里的脚本类**。

---

## 2. 设计问题逐条回答

### A. 编辑器跑的确切命令与注入属性

见 §1.2/§1.3。要点：
- `dotnet publish {assembly_name}.csproj -c ExportRelease -r ios-arm64 --self-contained true -p:GodotTargetPlatform=ios -o <固定输出目录>`；
- `PublishAot=true / PublishAotUsingRuntimePack=true / UseNativeAOTRuntime=true` 由 Godot.NET.Sdk 的 iOSNativeAOT.props 注入；
- Trimming root 只有两个：`GodotSharp` 和 `$(TargetName)`（= 主 csproj 的 assembly）；
- 模拟器 RID（iossimulator-arm64/x64）会**无条件**追加 publish 并进 xcframework——即使你只想出真机包，模拟器 publish 失败也会导致整个导出失败（这是替换方案要一起吃下的成本）。

### B. thin shim csproj + 预编译 DLL：可行，且是正解

**机制上完全成立**：`dotnet publish` + PublishAot 时，ILC 编译的是"主 assembly + 传递闭包内全部引用 assembly 的 IL"。csc 只编译 shim 里那几行代码；sts2.dll 及 200 个依赖以 `<Reference HintPath>` 进来后，ILC 一视同仁地 AOT。Godot 管线对此零感知——它只关心 publish 输出目录里有没有 `{assembly_name}.dylib`。

shim csproj 骨架：

```xml
<Project Sdk="Godot.NET.Sdk/4.5.1">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>   <!-- 必须 >= sts2.dll 的 TFM -->
    <!-- 202 个 DLL 全量保根，防 trimming 砍掉反射目标 -->
    <TrimMode>partial</TrimMode>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="sts2"><HintPath>libs/sts2.dll</HintPath></Reference>
    <!-- 200 个依赖同理，可用 <Reference Include="@(...)"> 通配批量生成 -->
  </ItemGroup>
</Project>
```

Trimming/rooting 要点（这是 B 的核心风险区）：
1. Godot 默认只 root `GodotSharp` + shim 本身，**sts2.dll 会被当普通库 trim**。反射重的游戏代码必被砍出运行时崩溃。两种对策：
   - `<TrimMode>partial</TrimMode>`：NativeAOT 支持，未标 `IsTrimmable` 的 assembly 全量保根——一行解决 200 个 DLL，代价是二进制体积和 ILC 编译时间显著上涨（Godot 自己的讨论里 18MB vs 1.8MB 量级差，见 [godot-proposals#9173](https://github.com/godotengine/godot-proposals/issues/9173)）；
   - 或逐个 `<TrimmerRootAssembly Include="sts2"/>`（可以对 Reference item 做 MSBuild transform 批量生成），更细但等价。
2. **必须是 implementation assembly，不能是 ref assembly**（NuGet 包 `ref/` 目录那种没有方法体的不行，ILC 需要真 IL）。
3. TFM 链一致性：sts2.dll 是 net9.0 → shim 必须 net9.0；Godot 4.5 生成的默认 csproj 是 net8.0（[`ProjectGenerator.cs` L15](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/editor/GodotTools/GodotTools.ProjectEditor/ProjectGenerator.cs)），手改成 net9.0 是支持的（.NET 9 iOS 的 icudt.dat 问题已在 4.5 修复，见 §7）。BCL 由 `Microsoft.NETCore.App.Runtime.NativeAOT.ios-arm64` 9.x runtime pack 提供。
4. 预编译 DLL 里如果有 P/Invoke 到自带 native 库的，需要另行把对应 iOS 静态库/framework 塞进 Xcode 工程（ILC 不会变出 native 依赖）。
5. **脚本注册边界**（要验证的点）：`godotsharp_game_main_init` 只对 shim assembly 调 `LookupScriptsInAssembly`。挂在 Godot 节点上的 C# 脚本类应放在 shim 项目源码里；sts2.dll 作为纯逻辑库被调用没问题，但若想把 Godot Node 子类放进预编译 DLL 当场景脚本用，跨 assembly 注册路径需要实机验证（源码上没看到导出游戏对引用 assembly 的脚本扫描）。

### C. 产物位置与命名约束

- 命名铁律：**dylib 名 = `dotnet/project/assembly_name`**。链条上三处强耦合：ExportPlugin 找产物（L299-301）、xcframework 打包（BuildSystem L357）、设备运行时 dlopen（gd_mono L495 + path_utils L236）。全部读同一个项目设置。
- 同时 `{assembly_name}.csproj` 必须在项目根、`{assembly_name}.sln` 必须存在（`GodotSharpDirs.DetermineProjectLocation`，[GodotSharpDirs.cs L63-87](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/editor/GodotTools/GodotTools/Internals/GodotSharpDirs.cs)）——编辑器 publish 的就是这个固定路径的 csproj，**没有"指定别的 csproj"或"跳过 publish"的官方开关**。
- 落点：publish 目录 `.godot/mono/temp/bin/godot-publish-dotnet/{config}-{rid}/` → xcframework `.godot/mono/temp/bin/{config}/{assembly}_aot.xcframework` → 导出工程 `dylibs/.../{assembly}.framework`（dylib 已转 framework）→ app 的 `Frameworks/{assembly}.framework/{assembly}`。
- 所以"替换"最省事的接缝有两个：① shim csproj（推荐，全自动）；② 导出 Xcode 工程后手工重打 `{assembly}_aot.xcframework` 并替换 `dylibs/` 下的 framework（可做 CI 后处理，但要自己维护 dSYM/签名/install_name `@rpath/{assembly}.dylib`）。

### D. Mono 解释器 fallback：没有

- Godot 4.x 彻底移除了 Mono 运行时，桌面用 hostfxr/CoreCLR，iOS **只有 NativeAOT**（官方博客原话："NativeAOT is currently the only supported runtime for exporting to iOS"，[Godot 4.2 C# 平台状态](https://godotengine.org/article/platform-state-in-csharp-for-godot-4-2/)）。gd_mono.cpp 的加载链在 iOS 上事实只剩 `try_load_native_aot_library` 一条活路。
- `UseMonoRuntime`、mono interp 在 4.5.1 代码库中零出现；"把 Godot 3 式 Mono embedding 带回来"仍停留在 proposal 阶段（[godot-proposals#2333](https://github.com/godotengine/godot-proposals/issues/2333)）。
- **推论：Harmony/MonoMod 这类运行时 IL patch 在 Godot 4.5 iOS 上不可行**（NativeAOT 无 JIT、无 Reflection.Emit、无法换方法体）。可行替代：
  - **构建期 IL patch**：用 Mono.Cecil/HarmonyX 的静态化思路，在 `dotnet publish` 之前把补丁烘进 sts2.dll 副本（shim 引用打好补丁的 DLL）——干净、可控、与本管线完全兼容；
  - 非官方 hack：.NET 的 Mono "library mode"（`UseMonoRuntime` + library 模式产 dylib，内含 interp）理论上也能导出 `godotsharp_game_main_init`，但 Godot 完全没接线、dotnet 侧也是实验特性，不建议押注。

### E. NativeAOT 已知地雷（对反射重的游戏代码）

| 能力 | NativeAOT 下状态 |
|---|---|
| 反射读类型/字段/属性/私有 ctor `Invoke` | 可用，**前提是 metadata 没被 trim**（→ TrimMode partial / 全量 root 后基本安全） |
| `RuntimeHelpers.GetUninitializedObject` + 私有 ctor | 支持（类型被 root 即可）；这是很多存档反序列化的路径，能走通 |
| `Reflection.Emit` / `DynamicMethod` / `Assembly.Load*` 动态加载 | **完全不可用**（无 JIT、单镜像）；mod 加载器、表达式树编译 (`Expression.Compile` 走解释器慢速路径，能跑) 需逐一排查 |
| Harmony / MonoMod 运行时 patch | 不可用（见 D） |
| `MakeGenericType/MakeGenericMethod` | 引用类型参数可用；**值类型泛型实例若未被 ILC 静态看见会运行时抛异常**（AOT 无法现场生成代码），反射驱动的泛型集合/序列化器是重灾区 |
| Newtonsoft.Json | `RuntimeFeature.IsDynamicCodeSupported=false` 时自动退回纯反射（LateBound）路径，能跑但慢；需保根 + 大量 trim warning |
| System.Text.Json 反射模式 | 官方标注 RequiresDynamicCode，全量 root 后大多能跑，正路是 source-generated 序列化 |
| 已知 Godot 侧案例 | 反射 + 异步资源加载崩溃 [#87752](https://github.com/godotengine/godot/issues/87752)；Release 下节点实例化失败 [#96072](https://github.com/godotengine/godot/issues/96072)；项目名带空格 publish 失败 [#102747](https://github.com/godotengine/godot/issues/102747) |

工程建议：shim 里开 `<TrimMode>partial</TrimMode>` 后，把 `dotnet publish -r ios-arm64` 的 **AOT/trim warning（IL2026/IL2067/IL3050 系列）当强制清单**逐条过；再在真机跑一轮存档读写 + 全卡池实例化冒烟。ILC 编 200 个 DLL 单次可能 10 分钟级、产物几百 MB 级（未 strip 前），CI 要预留。

---

## 7. 版本相关注意事项

- **.NET 9 + iOS 在 4.3/4.4 会导出失败**（icudt.dat 从 runtime pack 挪位，[#100123](https://github.com/godotengine/godot/issues/100123)），**4.5 起已修**（[#100187](https://github.com/godotengine/godot/pull/100187)；对应 4.5.1 iOSNativeAOT.targets 里 `IcuEnabled Condition="Exists(...)"` 的条件拷贝）。用 4.5.1 + net9.0 是安全组合。
- iOS C# 支持自 4.2 起（[PR #82729](https://github.com/godotengine/godot/pull/82729)），官方文档仍标 experimental（[C# 平台支持](https://docs.godotengine.org/en/4.5/tutorials/scripting/c_sharp/index.html)）；模拟器官方模板仅 x64（自编模板可 arm64）。
- 导出必须在 macOS + Xcode（lipo / xcodebuild / iPhoneOS.sdk 硬依赖，[iOS 导出文档](https://docs.godotengine.org/en/4.5/tutorials/export/exporting_for_ios.html)）。
- 迭代技巧：导出的 Xcode 工程可用 `godot_path` Info.plist 键直连项目目录，改资源不必重导出（改 C# 仍需重跑 publish）。

## 8. 源码与资料索引

**4.5.1-stable 源码**（行号以此 tag 为准）：
- [ExportPlugin.cs](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/editor/GodotTools/GodotTools/Export/ExportPlugin.cs) · [BuildSystem.cs](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/editor/GodotTools/GodotTools/Build/BuildSystem.cs) · [BuildManager.cs](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/editor/GodotTools/GodotTools/Build/BuildManager.cs) · [GodotSharpDirs.cs](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/editor/GodotTools/GodotTools/Internals/GodotSharpDirs.cs)
- [Sdk.props](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/editor/Godot.NET.Sdk/Godot.NET.Sdk/Sdk/Sdk.props) · [Sdk.targets](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/editor/Godot.NET.Sdk/Godot.NET.Sdk/Sdk/Sdk.targets) · [iOSNativeAOT.props](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/editor/Godot.NET.Sdk/Godot.NET.Sdk/Sdk/iOSNativeAOT.props) · [iOSNativeAOT.targets](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/editor/Godot.NET.Sdk/Godot.NET.Sdk/Sdk/iOSNativeAOT.targets)
- [GodotPluginsInitializerGenerator.cs](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/editor/Godot.NET.Sdk/Godot.SourceGenerators/GodotPluginsInitializerGenerator.cs) · [ProjectGenerator.cs](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/editor/GodotTools/GodotTools.ProjectEditor/ProjectGenerator.cs)
- [gd_mono.cpp](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/mono_gd/gd_mono.cpp) · [godotsharp_dirs.cpp](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/godotsharp_dirs.cpp) · [path_utils.cpp](https://github.com/godotengine/godot/blob/4.5.1-stable/modules/mono/utils/path_utils.cpp)
- [editor_export_platform_apple_embedded.cpp](https://github.com/godotengine/godot/blob/4.5.1-stable/editor/export/editor_export_platform_apple_embedded.cpp) · [os_apple_embedded.mm](https://github.com/godotengine/godot/blob/4.5.1-stable/drivers/apple_embedded/os_apple_embedded.mm)

**文档 / 官方文章**：[C# 平台支持 (4.5 docs)](https://docs.godotengine.org/en/4.5/tutorials/scripting/c_sharp/index.html) · [iOS 导出 (4.5 docs)](https://docs.godotengine.org/en/4.5/tutorials/export/exporting_for_ios.html) · [Godot 4.2 C# 平台状态博客](https://godotengine.org/article/platform-state-in-csharp-for-godot-4-2/)

**Issues / PRs**：[#82729 Add C# iOS support](https://github.com/godotengine/godot/pull/82729) · [#100123 .NET 9 iOS icudt](https://github.com/godotengine/godot/issues/100123) / [修复 #100187](https://github.com/godotengine/godot/pull/100187) · [#87752 NativeAOT 异步资源加载](https://github.com/godotengine/godot/issues/87752) · [#96072 NativeAOT Release 节点实例化](https://github.com/godotengine/godot/issues/96072) · [#102747 项目名空格](https://github.com/godotengine/godot/issues/102747) · [proposals#9173 reflection-free mode](https://github.com/godotengine/godot-proposals/issues/9173) · [proposals#2333 Mono embedding 回归提案](https://github.com/godotengine/godot-proposals/issues/2333)
