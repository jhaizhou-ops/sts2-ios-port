using System;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace STS2MobileIos.Patches;

// 游戏原代码 OrbCmd.AddSlots/RemoveSlots(416736) 的界面动画行漏写空保护:
//   NCombatRoom.Instance?.GetCreatureNode(player.Creature).OrbManager?.AddSlotAnim(amount)
//                        ^^^^^^^^^^^^^^^^ 返回 null 时直接 .OrbManager → NRE
// 真玩家永远有界面节点所以从不炸; 战斗评判官的克隆生物没有节点 → 推演打法球牌(冰川等)必炸。
// 修法: bool-prefix 整体替换, 逻辑与原版逐行一致, 只补上空保护。对真局行为零变化。
public static class OrbCmdNullFix
{
    public static bool AddSlotsPrefix(Player player, int amount, ref Task __result)
    {
        __result = Task.CompletedTask;
        if (CombatManager.Instance.IsOverOrEnding) return false;
        amount = Math.Min(10 - player.PlayerCombatState.OrbQueue.Capacity, amount);
        player.PlayerCombatState.OrbQueue.AddCapacity(amount);
        NCombatRoom.Instance?.GetCreatureNode(player.Creature)?.OrbManager?.AddSlotAnim(amount);
        return false;
    }

    public static bool RemoveSlotsPrefix(Player player, int amount)
    {
        if (!CombatManager.Instance.IsOverOrEnding)
        {
            amount = Math.Min(player.PlayerCombatState.OrbQueue.Capacity, amount);
            player.PlayerCombatState.OrbQueue.RemoveCapacity(amount);
            NCombatRoom.Instance?.GetCreatureNode(player.Creature)?.OrbManager?.RemoveSlotAnim(amount);
        }
        return false;
    }

    // NCombatRoom.AddCreature: 召唤链(虫群死亡生小怪等)给新怪建界面节点, 对克隆生物 NRE。
    // 推演期间(SimScope 开测试模式)跳过建节点 — 状态层的 AddCreature 在 CombatState/CombatManager
    // 已完成, 界面节点本就与战斗状态无关; 真局测试模式恒关 → 行为零变化。
    public static bool AddCreatureUiPrefix()
        => !MegaCrit.Sts2.Core.TestSupport.TestMode.IsOn;   // true=照常建节点, false=跳过
}
