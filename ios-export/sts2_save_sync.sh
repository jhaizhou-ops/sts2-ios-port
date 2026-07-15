#!/usr/bin/env bash
# STS2 存档"最新者胜"同步器 (电脑 ↔ iPhone)。
# 规则: 比较两端存档树的最新修改时间, 旧的一侧被新的一侧镜像。
#  - 手机新 → 备份电脑存档后, 精确镜像到电脑 (rsync --delete)
#  - 电脑新 → 推送到手机 user://sync_inbox/<时间戳>/1/**, 游戏下次启动时
#             由 SyncImportPatch 校验"确实更新"后原子导入(手机端二次裁决,防旧覆新)
#  - 手机不可达/已锁屏/电脑游戏运行中 → 本轮静默跳过
# 每次覆盖前都留时间戳备份于 .work/save_backups/ (自动保留最近30份,更旧的清理)。
# 可挂 launchd/cron 每 5 分钟跑一次。
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[ -f "$SCRIPT_DIR/config.sh" ] || { echo "❌ 缺少 ios-export/config.sh，先 cp config.example.sh config.sh 并填写" >&2; exit 1; }
# shellcheck disable=SC1091
source "$SCRIPT_DIR/config.sh"
: "${STS2_DEVICE_UDID:?config.sh 未设置 STS2_DEVICE_UDID}"
: "${STS2_BUNDLE_ID_SIGNED:?config.sh 未设置 STS2_BUNDLE_ID_SIGNED}"
: "${STS2_STEAM_ID:?config.sh 未设置 STS2_STEAM_ID（电脑存档目录名，SteamID64）}"

DEV="$STS2_DEVICE_UDID"
BUNDLE="$STS2_BUNDLE_ID_SIGNED"          # 手机端容器 bundle（SideStore 签名会带团队后缀）
DESK="$HOME/Library/Application Support/SlayTheSpire2/steam/$STS2_STEAM_ID"
WORK="$SCRIPT_DIR/.work"
BACKUPS="$WORK/save_backups"
LOG="$WORK/save_sync.log"
STAGE="$(mktemp -d /tmp/sts2sync.XXXXXX)"
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$WORK"

say(){ echo "$(date '+%m-%d %H:%M:%S') $1" | tee -a "$LOG"; }
prune(){ ls -1dt "$BACKUPS"/*/ 2>/dev/null | tail -n +31 | while read -r d; do rm -rf "$d"; done; }

[ -d "$DESK" ] || { say "跳过: 电脑存档目录不存在 ($DESK)"; exit 0; }
# 电脑端游戏运行中不动它的文件
pgrep -f "SlayTheSpire2" >/dev/null 2>&1 && { say "跳过: 电脑端游戏运行中"; exit 0; }

# 1) 拉手机存档 (devicectl 保留修改时间; 失败=不可达/锁屏 → 跳过)
if ! xcrun devicectl device copy from --device "$DEV" \
      --domain-type appDataContainer --domain-identifier "$BUNDLE" \
      --source "Documents/default/1" --destination "$STAGE/phone" >/dev/null 2>&1; then
  say "跳过: 手机不可达或锁屏"; exit 0
fi
[ -f "$STAGE/phone/profile.save" ] || { say "跳过: 手机存档拉取不完整"; exit 0; }

newest(){ /usr/bin/find "$1" -type f -not -name "sync_*" -exec stat -f %m {} \; 2>/dev/null | sort -n | tail -1; }
P=$(newest "$STAGE/phone"); D=$(newest "$DESK")
[ -n "$P" ] && [ -n "$D" ] || { say "跳过: 时间戳读取失败(P=$P D=$D)"; exit 0; }

TS=$(date +%Y%m%d_%H%M%S)
if [ "$P" -gt $((D + 2)) ]; then
  # 手机新 → 镜像到电脑
  mkdir -p "$BACKUPS"
  cp -a "$DESK" "$BACKUPS/${TS}_desktop_before_pull" || { say "失败: 电脑备份失败,中止"; exit 1; }
  rsync -a --delete "$STAGE/phone/" "$DESK/" || { say "失败: rsync 镜像失败"; exit 1; }
  prune
  say "同步 手机→电脑 (手机新 $P > 电脑 $D); 电脑旧档备份于 save_backups/${TS}_desktop_before_pull"
elif [ "$D" -gt $((P + 2)) ]; then
  # 电脑新 → 推送收件箱 (手机端启动时校验+导入; 手机当前状态已在 $STAGE/phone, 存为备份)
  # 去重: 同一时间戳已推送过(手机还没启动游戏消化收件箱) → 跳过, 避免重复推送+重复备份
  LASTPUSH_FILE="$WORK/.sts2_last_push"
  [ -f "$LASTPUSH_FILE" ] && [ "$(cat "$LASTPUSH_FILE")" = "$D" ] && { say "跳过: 该版本($D)已推送,等手机启动导入"; exit 0; }
  mkdir -p "$BACKUPS"
  cp -a "$STAGE/phone" "$BACKUPS/${TS}_phone_before_push"
  mkdir -p "$STAGE/inbox/$D"
  cp -a "$DESK" "$STAGE/inbox/$D/1"
  if xcrun devicectl device copy to --device "$DEV" \
       --domain-type appDataContainer --domain-identifier "$BUNDLE" \
       --source "$STAGE/inbox" --destination "Documents/sync_inbox" >/dev/null 2>&1; then
    prune
    echo "$D" > "$LASTPUSH_FILE"
    say "同步 电脑→手机收件箱 (电脑新 $D > 手机 $P); 游戏下次启动自动导入; 手机旧档备份于 save_backups/${TS}_phone_before_push"
  else
    say "失败: 推送到手机失败"
  fi
else
  say "已同步 (手机 $P ≈ 电脑 $D), 无动作"
fi
