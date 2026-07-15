#!/usr/bin/env bash
# STS2 → iPhone 一键构建。幂等；每步缺失即响亮报错退出（绝不静默糊过）。
# 签名身份/设备/路径全部来自 ios-export/config.sh（见 config.example.sh）。
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
EXPORT_DIR="$SCRIPT_DIR"

# ── 载入本地配置 ──
[ -f "$SCRIPT_DIR/config.sh" ] || { echo "❌ 缺少 ios-export/config.sh，先 cp config.example.sh config.sh 并填写" >&2; exit 1; }
# shellcheck disable=SC1091
source "$SCRIPT_DIR/config.sh"
: "${STS2_TEAM_ID:?config.sh 未设置 STS2_TEAM_ID}"
: "${STS2_BUNDLE_ID:?config.sh 未设置 STS2_BUNDLE_ID}"
: "${STS2_GODOT_BIN:?config.sh 未设置 STS2_GODOT_BIN}"
: "${STS2_GAME_APP:?config.sh 未设置 STS2_GAME_APP}"
: "${STS2_IOS_LIBS_DIR:?config.sh 未设置 STS2_IOS_LIBS_DIR}"
: "${STS2_FMOD_STATIC_DIR:?config.sh 未设置 STS2_FMOD_STATIC_DIR}"

GODOT="$STS2_GODOT_BIN"
GAME="$STS2_GAME_APP"
GAME_DATA="$GAME/data_sts2_macos_arm64"
WORK="$SCRIPT_DIR/.work"                # 中间产物（.gitignore 忽略）
WOVEN="$WORK/sts2-woven.dll"            # STS2Weaver 最新产物
PREBUILT="$EXPORT_DIR/prebuilt"
mkdir -p "$WORK"
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"

fail(){ echo "❌ $1" >&2; exit 1; }
ok(){ echo "✅ $1"; }
step(){ echo; echo "▶ $1"; }

step "0/6 前置检查"
[ -x "$GODOT" ] || fail "Godot 引擎缺失: $GODOT（在 config.sh 里 STS2_GODOT_BIN 指向 Godot 4.5.1 mono 可执行）"
command -v dotnet >/dev/null || fail "dotnet 不在 PATH（装 .NET 9 SDK）"
[ -d "$GAME_DATA" ] || fail "游戏数据目录缺失（Steam 游戏没装或路径不对）: $GAME_DATA"
TEMPLATES="$HOME/Library/Application Support/Godot/export_templates/4.5.1.stable.mono"
[ -d "$TEMPLATES" ] || fail "iOS 导出模板未安装到 $TEMPLATES（Godot 里先 import templates.tpz）"
[ -d "$STS2_IOS_LIBS_DIR/libspine_godot.ios.template_release.framework" ] || fail "Spine iOS 库缺失于 $STS2_IOS_LIBS_DIR（自备，见 README）"
[ -d "$STS2_IOS_LIBS_DIR/libGodotFmod.ios.template_release.xcframework" ] || fail "FMOD iOS 库缺失于 $STS2_IOS_LIBS_DIR（自备）"
[ -f "$STS2_FMOD_STATIC_DIR/libfmod_iphoneos.a" ] || fail "FMOD 静态库缺失于 $STS2_FMOD_STATIC_DIR（自备）"
ok "前置齐全"

step "1/6 重新织入最新 sts2.dll（补丁 + 游戏原始程序集）"
cd "$ROOT" || fail "cd 失败"
cp "$GAME_DATA/sts2.dll" "$WORK/sts2.dll" || fail "拷贝游戏 sts2.dll 失败"
dotnet build src/STS2MobileIos -c Release -o "$WORK/mobilepatch-out" >"$WORK/patch-build.log" 2>&1 \
  || fail "补丁库编译失败，见 $WORK/patch-build.log"
dotnet run --project src/STS2Weaver -c Release -- \
  "$WORK/sts2.dll" "$WORK/mobilepatch-out/STS2MobileIos.dll" \
  src/STS2MobileIos/manifest.json "$WOVEN" 2>&1 | tee "$WORK/weave.log" | grep -E "\[FAIL\]|完成"
grep -q "完成" "$WORK/weave.log" || fail "织入失败，见 $WORK/weave.log"
ok "织入完成: $WOVEN"

step "2/6 组装 prebuilt/（织入主程序集 + 全部托管依赖）"
rm -rf "$PREBUILT"; mkdir -p "$PREBUILT/deps"
cp "$WOVEN" "$PREBUILT/sts2.dll" || fail "拷贝织入 dll 失败"
cp "$WORK/mobilepatch-out/STS2MobileIos.dll" "$PREBUILT/deps/" || fail "拷贝补丁 dll 失败"
# 只拷第三方托管依赖。判定基准 = 框架实际安装的运行时库目录（权威清单，非名字猜测）：
#   凡在 shared/Microsoft.NETCore.App 里的 = 框架自带（NativeAOT runtime pack 提供）→ 排除
#   否则 = 第三方库 → 保留（含 System.IO.Hashing 这类名字像框架但其实是 NuGet 包的）
#   另排除 sts2.dll（主程序集，已单独注入）与 GodotSharp.dll（Godot SDK 提供）
SHARED_FW=$(find "$HOME/.dotnet/shared/Microsoft.NETCore.App" -maxdepth 1 -type d -name "9.0.*" | sort -V | tail -1)
[ -d "$SHARED_FW" ] || fail "找不到 .NET 共享框架目录，无法判定依赖归属"
for dll in "$GAME_DATA"/*.dll; do
  base=$(basename "$dll")
  case "$base" in sts2.dll|GodotSharp.dll) continue;; esac
  [ -f "$SHARED_FW/$base" ] && continue   # 框架自带，跳过
  cp "$dll" "$PREBUILT/deps/$base"
done
DEPCOUNT=$(ls "$PREBUILT/deps" | wc -l | tr -d ' ')
ok "依赖组装完成: $DEPCOUNT 个第三方托管程序集（按框架清单精确滤除）"
echo "  保留: $(ls "$PREBUILT/deps" | tr '\n' ' ')"

step "3/6 放置 GDExtension iOS 库到导出工程 addons/"
ADDONS="$EXPORT_DIR/addons"
mkdir -p "$ADDONS/spine/ios" "$ADDONS/fmod/libs/ios"
cp -R "$STS2_IOS_LIBS_DIR/libspine_godot.ios.template_release.framework" "$ADDONS/spine/ios/"
cp -R "$STS2_IOS_LIBS_DIR/libGodotFmod.ios.template_release.xcframework" "$ADDONS/fmod/libs/ios/"
cp "$STS2_FMOD_STATIC_DIR/"libfmod{,studio}_iphoneos.a "$ADDONS/fmod/libs/ios/" 2>/dev/null
ok "GDExtension 库就位"

step "4/6 dotnet publish（NativeAOT 预编译织入程序集 → sts2.framework）"
cd "$EXPORT_DIR" || fail "cd 失败"
dotnet publish sts2.csproj -c ExportRelease -r ios-arm64 \
  -p:GodotTargetPlatform=ios --self-contained \
  2>&1 | tee "$WORK/publish.log" | grep -E "error CS|error MSB|error :|注入|-> " | tail -20
# 用真实退出码 + 产物存在判成败（publish.log 里有海量 IL 裁剪 warning，文本含 "error" 子串，不能靠 grep）
PUBRC=${PIPESTATUS[0]}
PUB_DIR="$EXPORT_DIR/.godot/mono/temp/bin/ExportRelease/ios-arm64/publish"
[ "$PUBRC" = "0" ] || fail "dotnet publish 退出码 $PUBRC，见 $WORK/publish.log"
[ -f "$PUB_DIR/sts2.dylib" ] || fail "NativeAOT 未产出 sts2.dylib，见 $WORK/publish.log"
file "$PUB_DIR/sts2.dylib" | grep -q "arm64" || fail "sts2.dylib 不是 arm64"
ok "sts2.framework 预编译完成: $(du -h "$PUB_DIR/sts2.dylib" | cut -f1) arm64 原生库"

step "5/6 Godot 导出 iOS 工程"
mkdir -p "$EXPORT_DIR/build"
# 导出 Xcode 工程（--main-pack 是运行时参数不是导出参数,不能放这里;
# 游戏内容通过 step 5.5 替换 pck 进入应用包）
"$GODOT" --headless --path "$EXPORT_DIR" \
  --export-release "iOS" "$EXPORT_DIR/build/StS2.ipa" 2>&1 | tee "$WORK/export.log" | tail -8
[ -d "$EXPORT_DIR/build/StS2.xcodeproj" ] || fail "Godot 未生成 Xcode 工程，见 $WORK/export.log"
ok "Xcode 工程已生成"

step "5.15/6 修 pbxproj 签名（Godot 每次导出重生成工程: Distribution→Development + 钉死团队）"
PBX="$EXPORT_DIR/build/StS2.xcodeproj/project.pbxproj"
sed -i '' 's/Apple Distribution/Apple Development/g' "$PBX"
sed -i '' "s/DEVELOPMENT_TEAM = [^;]*;/DEVELOPMENT_TEAM = ${STS2_TEAM_ID};/g" "$PBX"
grep -q "Apple Distribution" "$PBX" && fail "pbxproj 仍含 Apple Distribution"
ok "pbxproj 签名已修 (Development + $STS2_TEAM_ID)"

step "5.2/6 注入 FMOD 空插件实现到 dummy.cpp（Godot 每次导出重生成 dummy.cpp,必补）"
# FMOD 静态库需 load_all_fmod_plugins 符号(正常由 Godot FMOD 编辑器插件生成,命令行导出没触发)。
# 游戏无自定义 FMOD 插件,零插件空实现即可,否则 FMOD 初始化调空地址(0x0)崩。
DUMMY="$EXPORT_DIR/build/StS2/dummy.cpp"
if ! grep -q "load_all_fmod_plugins" "$DUMMY" 2>/dev/null; then
  cat >> "$DUMMY" <<'CPPEOF'

// [STS2-iOS-fix] FMOD 静态链接需要 load_all_fmod_plugins;游戏无自定义 FMOD 插件,零插件空实现。
extern "C" __attribute__((visibility("default"))) __attribute__((used))
unsigned int *load_all_fmod_plugins(void *p_interface, unsigned int *r_count) {
  if (r_count) { *r_count = 0; }
  return (unsigned int *)0;
}
CPPEOF
fi
grep -q "load_all_fmod_plugins" "$DUMMY" && ok "FMOD 空实现已注入" || fail "FMOD 注入失败"

step "5.3/6 写内存权限 entitlements（治本:突破 iOS 单app内存上限,否则保存退出/预热 OOM 被杀）"
# increased-memory-limit 免费账号可用(AltStore v2.2证实),但需 Xcode GUI 或含此entitlement的
# provisioning profile 才能签(命令行直接 codesign+旧profile 会 ApplicationVerificationFailed)。
ENT="$WORK/ent_mem.plist"
cat > "$ENT" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>application-identifier</key><string>${STS2_TEAM_ID}.${STS2_BUNDLE_ID}</string>
<key>com.apple.developer.team-identifier</key><string>${STS2_TEAM_ID}</string>
<key>get-task-allow</key><true/>
<key>com.apple.developer.kernel.increased-memory-limit</key><true/>
</dict></plist>
EOF
ok "内存权限 entitlements 已备: $ENT"

step "5.5/6 游戏内容包三连处理（移 sentry + 补 672 脚本占位 → 官方引擎能加载）"
# MegaDot 私有引擎打的 pck 与官方引擎的 C# 脚本/扩展机制冲突,必须两处修:
#  ① 移 sentry.gdextension(无 iOS 版,启动加载会崩)
#  ② 补占位 .cs(官方引擎按 path 找 .cs 资源,MegaDot pck 里 0 个 → 场景脚本 reload 崩)
GAME_PCK="$GAME/Slay the Spire 2.pck"
python3 "$ROOT/tools/pck_patch_sentry.py" "$GAME_PCK" "$WORK/pck_nosentry.pck" >"$WORK/pck1.log" 2>&1 \
  || fail "pck 移 sentry 失败,见 $WORK/pck1.log"
strings "$PREBUILT/sts2.dll" | grep -oE 'res://[A-Za-z0-9_/.]+\.cs' | sort -u > "$WORK/cs_paths.txt"
CSN=$(wc -l < "$WORK/cs_paths.txt" | tr -d ' ')
python3 "$ROOT/tools/pck_add_cs.py" "$WORK/pck_nosentry.pck" "$WORK/cs_paths.txt" "$EXPORT_DIR/build/StS2.pck" >"$WORK/pck2.log" 2>&1 \
  || fail "pck 补脚本占位失败,见 $WORK/pck2.log"
GOTCS=$(python3 "$ROOT/tools/pck_ls.py" "$EXPORT_DIR/build/StS2.pck" ls 2>/dev/null | grep -c '\.cs$')
GOTSENT=$(python3 "$ROOT/tools/pck_ls.py" "$EXPORT_DIR/build/StS2.pck" ls 2>/dev/null | grep -c 'sentry.gdextension')
[ "$GOTCS" -ge 600 ] || fail "pck 脚本占位数异常: $GOTCS (期望 ~$CSN)"
[ "$GOTSENT" = "0" ] || fail "pck 仍含 sentry: $GOTSENT"
ok "游戏内容包就位: $(du -h "$EXPORT_DIR/build/StS2.pck"|cut -f1), $GOTCS 脚本占位, 0 sentry"

step "6/6 签名 + 装机（免费账号必须走 Xcode GUI,命令行 xcodebuild 会 No Accounts）"
cat <<GUIDE
────────────────────────────────────────────────────────────────────
命令行部分已完成:织入/AOT/导出/pck三连/FMOD注入/entitlement 全部就绪。
最后签名装机因免费账号限制必须 Xcode GUI(命令行看不到账号会话)。两条路:

【A. 首次/游戏更新(需编译 dummy.cpp + 签名)——Xcode GUI】
 1. Xcode 打开  ios-export/build/StS2.xcodeproj
 2. 选 StS2 target → Signing & Capabilities:
    - Team 选你的账号, 勾 Automatically manage signing
    - 点 "+ Capability" → 搜 "Increased Memory" → 双击加入(治本:突破内存上限)
 3. 顶部选你的 iPhone → 点 ▶ Run(会编译+签名+装机)
 4. 装好后如需迁移旧存档: Xcode Devices窗口 → Replace Container

【B. 仅换补丁不动游戏内容(增量,已构建过一次后)——命令行】
 已构建的 .app 在 DerivedData;换新 sts2.dylib 到 Frameworks/sts2.framework/sts2,
 codesign -f -s "\$STS2_SIGN_IDENTITY"
   --generate-entitlement-der --entitlements "$ENT" <各framework 再 .app>
 xcrun devicectl device install app --device "\$STS2_DEVICE_UDID" <.app>
 ⚠️ 覆盖安装,绝不 uninstall(会清存档!)  ⚠️ entitlement 版仍需先有含该entitlement的profile
 (增量装机可直接用 deploy-slim.sh)

产物就绪:
  pck: ios-export/build/StS2.pck (含脚本占位+移sentry)
  AOT: ios-export/.godot/mono/temp/bin/ExportRelease/ios-arm64/publish/sts2.dylib
  entitlements: $ENT (含 increased-memory-limit)
────────────────────────────────────────────────────────────────────
GUIDE
ok "构建准备完成,按上方 Xcode GUI 步骤完成签名装机"
