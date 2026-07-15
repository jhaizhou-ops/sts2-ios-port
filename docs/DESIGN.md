# STS2 iOS 移植 — 总体设计

日期: 2026-07-08。用户已接受: 每次游戏更新需重打包; 仅个人使用自己的正版拷贝。

## 发行模式（已定）
Balatro 模式: 打包时直接把用户本机 Steam 拷贝的 pck + 程序集封进 .ipa。
不移植: LauncherPatches / Steam/ 目录(SteamKit2 登录+下载+云存档) / ModLoaderPatches / LanMultiplayerPatcher(一期)。
存档: 本地 + UIFileSharingEnabled 开放文件共享，用户可通过访达备份。

## 技术路线
- 引擎: 官方 Godot 4.5.1-stable (mono) iOS 导出模板。游戏用私有分支 MegaDot 4.5.1.m.12，
  赌注: 其改动不影响运行时 pck 兼容。**验证方法(M1.5): 官方引擎二进制替换 testbed.app 里的
  MegaDot，桌面端能跑 = 兼容成立**。失败则需逆向 MegaDot 差异(风险项)。
- .NET: iOS 无 JIT → NativeAOT 预编译（细节等 design-ios-dotnet-export.md 研究结论）。
  Harmony 运行时补丁不可用 → 静态 IL 织入。
- 织入器 (STS2Weaver): Mono.Cecil 控制台工具。输入 sts2.dll + STS2MobileIos.dll(移植后的补丁) +
  manifest.json(目标方法→钩子映射)。按 Harmony 语义织入:
  - bool 前钩子返回 false → 跳过原方法体（含 ref __result 赋值）
  - void 前钩子 → 方法开头插调用
  - 后钩子 → 每个 ret 前插调用
  - 支持 __instance / __result(ref) / 原方法参数透传 / 属性 getter 目标(get_UploadData)
- 补丁移植 (STS2MobileIos): 从安卓项目移植 11 个必需补丁文件（见 patch-catalog.md 汇总表
  REQUIRED 行），去 HarmonyLib 依赖（AccessTools.X → 普通反射），QuitPrefix 重写为 iOS 语义
  （前台化即挂起，不做重启），ModelDbInitPatch 去掉运行时 Patch/Unpatch（Contains 前钩子永久
  织入 + _suppressContains 门控）。织入顺序: ModelDb → Platform → Settings → UiScale →
  各布局 → 生命周期 → 触屏。
- NativeAOT 反裁剪: sts2.dll 整体 root（TrimmerRootAssembly / rd.xml），确保
  GetUninitializedObject + 私有 ctor Invoke 的开放类型集全保留。
- GDExtension:
  - spine_godot: 从 spine-runtimes(4.2 分支, 已 clone) 编译 ios framework，产物名对齐
    pck 内配置: ios/libspine_godot.ios.template_release.framework
  - FMOD: fmod-gdextension(已 clone) 编译 ios xcframework + FMOD Engine iOS 静态库
    (fmod.com 需注册账号下载 — 用户触点)
  - Sentry: 无 iOS 支持 → 补丁 pck: extension_list.cfg 去掉 sentry 行 + PlatformPatches 已 stub
- pck 处理: 需要重打包（改 extension_list.cfg）或附加 override pck。工具: 自写
  tools/pck_ls.py(已有解包) + 打包用官方 godot editor headless --export-pack 或自写 writer。

## 里程碑与验证
- M0 环境: Xcode(用户装中) / dotnet9(装中) / godot 4.5.1 editor+templates(下载中)
- M1 hello-world .NET Godot 项目 → iPhone 真机跑通（验证签名+NativeAOT 工具链）
- M1.5 官方引擎跑桌面版游戏（MegaDot 兼容性验证，不需要 Xcode，优先做）
- M2 织入器+补丁在 **桌面 mac 版** 验证: 织入后的 sts2.dll 替换进 testbed.app，游戏能跑、
  UI 缩放生效（桌面 CoreCLR 下验证织入正确性，与 AOT 解耦）
- M3 iOS 引擎壳: 官方模板 + 游戏 pck + 织入 dll NativeAOT 编译 + 两个 GDExtension + Metal 渲染
- M4 真机联调: 触屏/生命周期/性能/存档，Sideloadly 装机 + 免费账号 7 天续签自动化
- 每步都要跑起来见到证据再进下一步。

## 已验证事实
- testbed.app(游戏拷贝) 脱离 Steam 客户端可运行（ad-hoc 重签后），Steam 初始化失败自动重试
  且不退出; 加 steam_appid.txt(2868840) 可完全正常
- Apple 芯片必须至少 ad-hoc 签名，否则秒退无日志
- 游戏引擎横幅: MegaDot v4.5.1.m.12.mono.custom_build（私有分支，无公开源码）
- pck v3 未加密，12328 文件已全量解出到 extracted-full/
- 51 个 .gdc 全是编辑器工具脚本，运行时无关

## 环境坑备忘
- zsh 不按空格拆分变量（下载脚本踩过）
- brew cask dotnet-sdk 需要 sudo 密码 → 改用 dotnet-install.sh 装 ~/.dotnet
- GitHub release 大文件: 直连~80KB/s 最快但会断，须 curl -C - 循环续传; 用户代理 8118 不通
  objects.githubusercontent

## 部署架构 v2: 内容拆分 + SideStore 永久续签 (2026-07-12)

**问题**: 免费证书 7 天过期; SideStore 手机端装机把整包读进内存, 2G 游戏包必被系统杀
(内存超限 jetsam / CPU 看门狗), 且每周续签重复此过程。

**解法(内容拆分)**: 游戏资源不进签名包。
- 签名包只含 exec+框架+Info.plist (~165M, IPA 54M): SideStore 装/续签轻松
- StS2.pck (1.77G) 一次性 USB 推到应用文档区 `Documents/StS2.pck`, 续签/重装(同 bundle id)不清
- 引擎经 Info.plist `godot_cmdline = ["--main-pack", "user://StS2.pck"]` 从文档区加载
  (Godot: drivers/apple_embedded/main_utilities.mm 读该键; user:// 在 iOS = Documents)
- NativeAOT: C# 代码在 sts2.framework 里, pck 纯资源, 拆分不涉代码加载

**关键账号/身份**（用你自己的开发者账号；下面列的是这套架构需要的角色，不是特定值）:
- 签名 Apple ID: 你自己的 Apple ID（免费即可），经 SideStore/iloader 安装
- bundle id: SideStore 签名会自动加团队后缀（形如 `<你的bundle>.<SideStoreTeamID>`）; USB 测试副本用无后缀的原 bundle
- 大内存权限 (increased-memory-limit): 需在你的 App ID 能力里登记
  （Xcode 里给 target 加 "Increased Memory" capability 即会写进 provisioning profile）
- 手机端配套: SideStore 0.6.3 + LocalDevVPN (App Store 外区, Coxson) + 配对文件(iloader 放置)
- DDI 调试镜像若下载困难: 可预下载后推到 `SideStore容器/Documents/DMG/`

**工具链**:
- `ios-export/deploy-slim.sh`: 换织入 dylib → 剥 pck → 注入 cmdline → 签名 → USB 装 (测试用)
- SideStore 部署: 打瘦身 IPA 推其 Documents, 手机上 My Apps ➕ 安装
- 存档同步 `sts2_save_sync.sh`: 在 `config.sh` 里把 `STS2_BUNDLE_ID_SIGNED` 设成 SideStore 签名后带后缀的 bundle

**已实测定案 (2026-07-12)**:
- SideStore Refresh(续签)不动 Documents: pck/存档/标记全存活(手动 Refresh 实测, mtime 未变)
- SideStore 签名保留 increased-memory-limit: 启动后剩余额度实测 5436MB(无权限版全程仅~3GB)
- SideStore 对"同名同版本" IPA 会用缓存旧包 → 推新包必须 bump CFBundleVersion(现 0.107.2)
- 内存自检默认关(v0.107.3 起): 容器放 memcheck_on 文件开启写 memcheck.txt, 删文件关闭

**修过的坑**:
- HitStop(打击停顿, Vantom 断肢等)结尾硬编码写回 TimeScale=1.0 覆盖 4 倍速
  → 织入 NHitStop.SetTimeScale 乘 TARGET_SCALE (唯一咽喉, 全程序集扫描证实)
- ShaderWarmup 首启在冷 pck 上连续加载 >10s → 看门狗 0x8BADF00D 杀
  → 收集阶段高频让帧(每4个资源一帧) + 加载即去重(晚了材质被回收, ObjectDisposedException)
- 强杀游戏时 FMOD 线程读 pck 撞退出流程 SIGSEGV (仅外力杀时, 低害, 未修)
- MemCheck 自检: os_proc_available_memory P/Invoke (libSystem.B.dylib; __Internal 不可用)

## Mac 桌面版补丁 (2026-07-13)
需求: 给用户 Steam 正版 Mac 版加 4 倍速 + 一键重开(不带手机专属的触摸/UI缩放/联机/6格快照)。
- sts2.dll 与 iOS game-refs 同哈希(e424ace) → 补丁复用; manifest.mac.json 只挑 3 个钩子
  (TimeScale ReadyPostfix + HitStop prefix + QuickRestart.RestartOnlyPostfix 纯重开不带快照)
- QuickRestartPatch 重构: 抽出 AddRestartButton() 共享; ReadyPostfix(iOS)=重开+快照,
  RestartOnlyPostfix(Mac)=仅重开。iOS 行为不变。
- ★关键坑: 补丁库作外部 dll 放进游戏目录 → FileNotFoundException! 桌面 GodotSharp 用
  PluginLoadContext(AssemblyDependencyResolver, 走 deps.json), 该游戏 deps.json 的 targets 空,
  补 deps 条目无效, 且插件 ALC 不回退目录探测。C# 崩→菜单退英文+按钮全死。
  ★正解: ILRepack 把 STS2MobileIos.dll 并入 sts2.dll(自包含零外部依赖)。
  merge.csproj 用 ILRepack.Lib 2.0.34; 输出文件名必须 sts2.dll(身份=sts2, 游戏认这个名)。
  先织入(外部引用)再 ILRepack 合并(引用重写为内部)。产物 /tmp/sts2-merged-out/sts2.dll。
- 签名: 游戏 adhoc+hardened runtime, entitlements 有 disable-library-validation(改dll不被拦)
  +allow-jit。改 dll 后 codesign -f -s - --deep --options runtime --entitlements(保留原entitlements)
  重建 CodeResources 封印, 避免"已损坏"。原 entitlements 存 /tmp/sts2_mac_ent.plist。
- 部署脚本 tools/deploy-mac.sh (幂等, .orig 备份, --revert 一键还原)。
  ⚠️ Steam 验证完整性/游戏更新会还原, 需重跑。data_sts2_macos_arm64(本机arm64跑这个)。
