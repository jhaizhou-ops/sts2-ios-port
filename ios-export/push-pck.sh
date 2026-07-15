#!/usr/bin/env bash
# 一次性把素材包 StS2.pck 推到手机 App 文档区 Documents/StS2.pck。
#
# 【内容拆分架构的关键一步】永久续签能成立的前提:
#   - 签名瘦身包(.app ~165M / IPA ~55M)只含 可执行文件+框架+Info.plist, 不含 pck
#     → SideStore 每次续签只处理这个小包, 秒过, 不会因 2G 整包读内存被系统杀
#   - 1.77G 的 StS2.pck 走这里推到 App 文档区, 不进签名包
#   - SideStore 续签/同 bundle 重装都【不动 Documents】→ pck 推一次就长驻
# 因此本脚本平时【只需跑一次】; 只有换了新游戏内容(游戏更新)才需重推。
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[ -f "$SCRIPT_DIR/config.sh" ] || { echo "❌ 缺少 ios-export/config.sh，先 cp config.example.sh config.sh 并填写" >&2; exit 1; }
# shellcheck disable=SC1091
source "$SCRIPT_DIR/config.sh"
: "${STS2_DEVICE_UDID:?config.sh 未设置 STS2_DEVICE_UDID}"
: "${STS2_BUNDLE_ID_SIGNED:?config.sh 未设置 STS2_BUNDLE_ID_SIGNED（手机上已装 App 的 bundle；SideStore 签名会带团队后缀）}"

DEV="$STS2_DEVICE_UDID"
BUNDLE="$STS2_BUNDLE_ID_SIGNED"
PCK="${1:-$SCRIPT_DIR/build/StS2.pck}"      # 默认用 build-ios.sh 产出的 pck；也可传路径覆盖
fail(){ echo "❌ $1"; exit 1; }

[ -f "$PCK" ] || fail "素材包不存在: $PCK（先跑 build-ios.sh 生成 build/StS2.pck）"
[ -n "$DEV" ] || fail "STS2_DEVICE_UDID 为空（xcrun devicectl list devices 取 UDID）"

# 前置提醒: 手机上必须【已经装好】瘦身 App(经 SideStore 或 Xcode/USB)，容器才存在。
echo "▶ 目标容器: $BUNDLE 的 Documents/  (设备 $DEV)"
echo "▶ 推送素材包 $(du -h "$PCK" | cut -f1) → Documents/StS2.pck  (USB，1.77G 约数分钟，别拔线)…"

# 用 devicectl 拷进 App 沙盒文档区。file→file，目标写全路径。
if ! xcrun devicectl device copy to --device "$DEV" \
      --domain-type appDataContainer --domain-identifier "$BUNDLE" \
      --source "$PCK" --destination "Documents/StS2.pck" 2>/tmp/pushpck.err; then
  cat /tmp/pushpck.err >&2
  echo "── 若上面报容器不存在: 手机上先用 SideStore(或 Xcode) 把瘦身 App 装上再来。"
  echo "── 若 devicectl 拷贝不通(设备/配对问题)，可改用 Finder: 连线 → 手机 → 文件 →"
  echo "   找到该 App → 把 StS2.pck 拖进它的文档区(文件名须为 StS2.pck)。"
  fail "推送失败"
fi

# 校验: 文档区确实出现 StS2.pck（别只信退出码，实测过“装/拷成功仍没落地”的坑）
if xcrun devicectl device info files --device "$DEV" \
     --domain-type appDataContainer --domain-identifier "$BUNDLE" 2>/dev/null \
     | grep -q "Documents/StS2.pck"; then
  echo "✅ 素材包已就位: $BUNDLE : Documents/StS2.pck"
  echo "   现在启动 App 应能从 user://StS2.pck 加载游戏内容(引擎经 Info.plist godot_cmdline 读取)。"
else
  fail "推送命令返回成功，但文档区没查到 Documents/StS2.pck —— 未真正落地，重试或改用 Finder 拖入"
fi
