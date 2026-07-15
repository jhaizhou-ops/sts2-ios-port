# STS2 iOS 移植 · 本地配置
# ─────────────────────────────────────────────────────────────
# 用法: 复制本文件为 `config.sh`，填入你自己的值。
#   cp ios-export/config.example.sh ios-export/config.sh
# config.sh 已被 .gitignore 忽略，不会进版本库——你的签名身份不会外泄。
# build-ios.sh / deploy-slim.sh / sts2_save_sync.sh 启动时会自动 source 它。
# ─────────────────────────────────────────────────────────────

# ── Apple 签名身份（你自己的开发者账号，免费 Apple ID 即可）──
# Team ID: Xcode → Settings → Accounts → 选账号 → 团队；或 developer.apple.com 会员信息。
STS2_TEAM_ID="XXXXXXXXXX"
# 你为这个 App 选的 bundle id（自定，建议用你自己的反向域名）。
STS2_BUNDLE_ID="com.yourname.sts2"
# codesign 用的签名身份全名。列出可用身份: security find-identity -v -p codesigning
STS2_SIGN_IDENTITY="Apple Development: you@example.com (XXXXXXXXXX)"
# 仅走 SideStore 自助续签时: SideStore 签名后 bundle 会带团队后缀(形如 ${STS2_BUNDLE_ID}.<SideStoreTeamID>)。
# 只用 Xcode/USB 装机的话保持与 STS2_BUNDLE_ID 相同即可。存档同步脚本用它定位手机容器。
STS2_BUNDLE_ID_SIGNED="${STS2_BUNDLE_ID}"

# ── 目标设备 ──
# 你的 iPhone 的 UDID。列出已连接设备: xcrun devicectl list devices
STS2_DEVICE_UDID=""

# ── 本机路径 ──
# 游戏安装目录(到 .../Contents/Resources)。下面是 Steam 默认 macOS 路径，按需改。
STS2_GAME_APP="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources"
# Godot 4.5.1 mono 版可执行文件路径。
STS2_GODOT_BIN="$HOME/Godot_mono.app/Contents/MacOS/Godot"

# ── 第三方 iOS 库（官方地址+确切版本见 docs/THIRD_PARTY_LIBS.md；本仓库不转发）──
# 目录里需含: libspine_godot.ios.template_release.framework   (Spine 4.2 / Godot 4.5.1)
#            libGodotFmod.ios.template_release.xcframework    (FMOD 2.03 / Godot 4.5.1)
STS2_IOS_LIBS_DIR="$HOME/sts2-ios-userlibs/ios-libs"
# FMOD iOS 静态库目录，需含: libfmod_iphoneos.a  libfmodstudio_iphoneos.a
STS2_FMOD_STATIC_DIR="$HOME/sts2-ios-userlibs/fmod-ios/lib"

# ── 存档同步（可选，仅 sts2_save_sync.sh 用）──
# 你的 SteamID64（电脑存档目录名）。查询: 游戏内或 steamid.io。
STS2_STEAM_ID=""
