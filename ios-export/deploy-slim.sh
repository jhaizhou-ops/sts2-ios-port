#!/usr/bin/env bash
# 增量装机: 把新织入的 sts2.dylib 换进已构建的 .app → 剥离 pck(资源走文档区) →
# 注入 --main-pack user://StS2.pck → 签名(含大内存权限) → USB 装机(保留文档区 pck+存档)。
# pck 已常驻手机 Documents, 同 bundle id 重装不会清; 故本脚本不重推 pck。
# 前置: 已用 build-ios.sh + Xcode GUI 完整构建过一次(DerivedData 里有 .app)。
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[ -f "$SCRIPT_DIR/config.sh" ] || { echo "❌ 缺少 ios-export/config.sh，先 cp config.example.sh config.sh 并填写" >&2; exit 1; }
# shellcheck disable=SC1091
source "$SCRIPT_DIR/config.sh"
: "${STS2_DEVICE_UDID:?config.sh 未设置 STS2_DEVICE_UDID}"
: "${STS2_SIGN_IDENTITY:?config.sh 未设置 STS2_SIGN_IDENTITY}"
: "${STS2_BUNDLE_ID:?config.sh 未设置 STS2_BUNDLE_ID}"

DEV="$STS2_DEVICE_UDID"
IDENT="$STS2_SIGN_IDENTITY"
# 已构建的 .app（DerivedData 里的哈希目录不固定，自动定位）
APP=$(find "$HOME/Library/Developer/Xcode/DerivedData" -maxdepth 4 -type d -path "*Release-iphoneos/StS2.app" 2>/dev/null | head -1)
PUB="$SCRIPT_DIR/.godot/mono/temp/bin/ExportRelease/ios-arm64/publish/sts2.dylib"
STG="$(mktemp -d /tmp/sts2slim.XXXXXX)/StS2.app"
ENT="$SCRIPT_DIR/.work/ent_mem.plist"
fail(){ echo "❌ $1"; exit 1; }

[ -f "$PUB" ] || fail "新 dylib 不存在: $PUB（先跑 build-ios.sh）"
[ -n "$APP" ] && [ -d "$APP" ] || fail "DerivedData 里找不到已构建的 StS2.app（先用 Xcode GUI 完整构建一次）"
[ -f "$ENT" ] || fail "entitlements 不存在: $ENT（先跑 build-ios.sh 生成）"
# 1) 换新织入 dylib 进 .app
cp "$PUB" "$APP/Frameworks/sts2.framework/sts2" || fail "换 dylib 失败"
echo "✅ 新 dylib 已换入 ($(shasum "$PUB" | cut -c1-12))"
# 2) 造瘦身包
mkdir -p "$(dirname "$STG")"; cp -R "$APP" "$STG"
rm -f "$STG/StS2.pck"
plutil -remove godot_cmdline "$STG/Info.plist" 2>/dev/null
plutil -insert godot_cmdline -json '["--main-pack", "user://StS2.pck"]' "$STG/Info.plist" || fail "注入 cmdline 失败"
echo "✅ 瘦身包 $(du -sh "$STG" | cut -f1), 已注入 --main-pack user://StS2.pck"
# 3) 签名
for fw in "$STG/Frameworks/"*.framework; do
  codesign -f -s "$IDENT" --generate-entitlement-der "$fw" >/dev/null 2>&1 || fail "签框架失败 $fw"
done
codesign -f -s "$IDENT" --generate-entitlement-der --entitlements "$ENT" "$STG" >/dev/null 2>&1 || fail "签 .app 失败"
codesign -d --entitlements - "$STG" 2>/dev/null | grep -q increased-memory && echo "✅ 大内存权限已嵌入" || fail "大内存权限缺失"
codesign -vv "$STG" 2>&1 | grep -q "satisfies its Designated Requirement" && echo "✅ 签名有效"
# 4) 装机(保留文档区)。⚠️ 必须查真实退出码, 曾有装机失败仍报成功的教训。
echo "▶ USB 装机(保留 Documents/StS2.pck + 存档)..."
INSTALL_OUT=$(xcrun devicectl device install app --device "$DEV" "$STG" 2>&1)
RC=$?
echo "$INSTALL_OUT" | grep -iE "installationURL|error" | head -3
[ "$RC" = "0" ] || fail "装机失败(退出码 $RC)"
echo "$INSTALL_OUT" | grep -q "installationURL" || fail "装机输出异常(无 installationURL)"
# 装完核验文档区 pck 是否仍在(升级安装应保留; 若被当新装会清空)
if ! xcrun devicectl device info files --device "$DEV" \
     --domain-type appDataContainer --domain-identifier "$STS2_BUNDLE_ID" 2>/dev/null \
     | grep -q "Documents/StS2.pck"; then
  echo "⚠️ 警告: 容器里没有 Documents/StS2.pck(可能被当新装清空), 需重推 pck!"
fi
echo "✅ 装机完成"
