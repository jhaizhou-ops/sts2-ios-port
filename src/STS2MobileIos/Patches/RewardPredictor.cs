using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace STS2MobileIos.Patches;

// 奖励预测器(游戏内, 用 Rng 克隆 + 候选池)。
// 给定"发牌位置的 Rewards 流克隆"+ options + pity, 精确复现将生成的三张卡。
// 用游戏自己的 GetPossibleCards(全部过滤) 和 Rng.NextItem, 消除硬编码卡池误差, 通用所有角色。
public static class RewardPredictor
{
    public struct Pred { public string Entry; public CardRarity Rarity; public bool Upgraded; public int Type; }

    // clone: 已定位到发牌位置的 Rewards 流独立副本; pity: 当前卡稀有度保底值; a7: 进阶≥7
    // dbg: 非空时逐卡记录内部 roll/阈值/offset/稀有度(定位分叉)
    public static List<Pred> Predict(Player player, CardCreationOptions options, Rng clone,
        float pity, int actIndex, bool a7, List<string> dbg = null)
        => Predict(player, options, clone, pity, actIndex, a7, out _, dbg);

    // finalPity: 三张卡发完后的保底值(供多步前瞻跨战斗结转)
    public static List<Pred> Predict(Player player, CardCreationOptions options, Rng clone,
        float pity, int actIndex, bool a7, out float finalPity, List<string> dbg = null,
        int count = 3, bool usePity = true)
    {
        var res = new List<Pred>();
        // ★扩池钩子: 游戏 CreateForReward 第一步就 Hook.ModifyCardRewardCreationOptions(扩池遗物如PrismaticGem并入全职业牌池)。
        // 我们hook的是CreateForReward入参(钩子前的options), 必须自己应用同一钩子, 否则扩池遗物在场时池顺序错→NextItem选错卡。
        options = ApplyRewardOptionsHook(player, options);
        var oddsType = options.RarityOdds;
        // 真实候选池: GetPossibleCards + 游戏的 FilterForPlayerCount(单人剔除 MultiplayerOnly 卡, 保序)。
        // 少这一步会让 NextItem 下标错位 → 选错牌(Elite 尤其暴露)。
        var baseCards = GameFilterForPlayerCount(player, options.GetPossibleCards(player)).ToList();
        var black = new HashSet<CardModel>();
        float upScale = a7 ? 0.125f : 0.25f;

        for (int i = 0; i < count; i++)
        {
            // ① 稀有度: 直接用游戏静态 GetBaseOdds 拿阈值(反射), 零常量误差
            float rareT = GameBaseOdds(oddsType, CardRarity.Rare);
            float uncT = GameBaseOdds(oddsType, CardRarity.Uncommon);
            float offset = (!usePity || oddsType == CardRarityOddsType.BossEncounter) ? 0f : pity;
            float roll = clone.NextFloat(1f);
            CardRarity rarity;
            float pityBefore = pity;
            // 游戏逻辑: 稀有阈=rareBase+offset; 非稀有阈=uncommonBase+(rareBase+offset)。uncommon 阈必须叠加 rareBase!
            float rareThr = rareT + offset;
            float uncThr = uncT + rareThr;
            if (roll < rareThr) { rarity = CardRarity.Rare; if (usePity) pity = -0.05f; }
            else if (roll < uncThr) { rarity = CardRarity.Uncommon; if (usePity) pity = Bump(pity, a7); }
            else { rarity = CardRarity.Common; if (usePity) pity = Bump(pity, a7); }
            dbg?.Add($"卡{i + 1}: roll={roll:R} rareT={rareT:R} uncT={uncT:R} off={offset:R}(pity前{pityBefore:R}) → {rarity} (rareThr={rareThr:R} uncThr={uncThr:R})");

            // 候选: 真实池去黑名单、取该稀有度(保序)。空则向上顺延(GetNextAllowedRarity)
            var cands = Filter(baseCards, black, rarity);
            if (cands.Count == 0)
                rarity = NextAllowed(baseCards, black, rarity, out cands);
            if (cands.Count == 0) break;

            // ② 选卡(游戏原生 NextItem = 精确)
            var card = clone.NextItem<CardModel>(cands);
            black.Add(card.CanonicalInstance);

            // ③ 升级(复现 RollForUpgrade, 恒消耗一次)
            float up = clone.NextFloat(1f);
            float chance = rarity == CardRarity.Rare ? 0f : actIndex * upScale;
            bool upg = card.IsUpgradable && up <= chance;

            res.Add(new Pred { Entry = card.Id.Entry, Rarity = rarity, Upgraded = upg, Type = (int)card.Type });
        }
        finalPity = pity;
        return res;
    }

    static float Bump(float pity, bool a7) => System.MathF.Min(pity + (a7 ? 0.005f : 0.01f), 0.4f);

    // 直接调游戏静态 CardRarityOdds.GetBaseOdds(oddsType, rarity)(非公开, 反射), 零常量误差。
    static System.Reflection.MethodInfo _getBaseOdds;
    static float GameBaseOdds(CardRarityOddsType t, CardRarity rarity)
    {
        _getBaseOdds ??= typeof(MegaCrit.Sts2.Core.Odds.CardRarityOdds).GetMethod("GetBaseOdds",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        return (float)_getBaseOdds.Invoke(null, new object[] { t, rarity });
    }

    // 应用游戏静态 Hook.ModifyCardRewardCreationOptions(runState, player, options) → 复现扩池遗物(PrismaticGem等)。
    // 无扩池遗物时钩子原样返回 → 零副作用(基础预测不变)。反射调用(Hook 在 Core.Hooks)。
    static System.Reflection.MethodInfo _rewardOptHook;
    static CardCreationOptions ApplyRewardOptionsHook(Player player, CardCreationOptions options)
    {
        try
        {
            _runStateProp ??= typeof(Player).GetProperty("RunState");
            var runState = _runStateProp.GetValue(player);
            _rewardOptHook ??= typeof(CardCreationOptions).Assembly
                .GetType("MegaCrit.Sts2.Core.Hooks.Hook")?
                .GetMethod("ModifyCardRewardCreationOptions",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (_rewardOptHook != null)
                return (CardCreationOptions)_rewardOptHook.Invoke(null, new object[] { runState, player, options });
        }
        catch { }   // 钩子应用失败 → 退回原 options(基础预测仍准)
        return options;
    }

    // 直接调游戏私有静态 CardFactory.FilterForPlayerCount(runState, options) → 逐位一致。
    static System.Reflection.MethodInfo _filterPlayerCount;
    static System.Reflection.PropertyInfo _runStateProp;
    static IEnumerable<CardModel> GameFilterForPlayerCount(Player player, IEnumerable<CardModel> pool)
    {
        _filterPlayerCount ??= typeof(MegaCrit.Sts2.Core.Factories.CardFactory).GetMethod("FilterForPlayerCount",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        _runStateProp ??= typeof(Player).GetProperty("RunState");
        var runState = _runStateProp.GetValue(player);
        return (IEnumerable<CardModel>)_filterPlayerCount.Invoke(null, new object[] { runState, pool });
    }

    static List<CardModel> Filter(List<CardModel> pool, HashSet<CardModel> black, CardRarity rar) =>
        pool.Where(c => c.Rarity == rar && !black.Contains(c.CanonicalInstance)).ToList();

    // 向上顺延到池中存在的稀有度(带回绕): Common→Uncommon→Rare→Common
    static CardRarity NextAllowed(List<CardModel> pool, HashSet<CardModel> black, CardRarity rar, out List<CardModel> cands)
    {
        var order = new[] { CardRarity.Common, CardRarity.Uncommon, CardRarity.Rare };
        int start = System.Array.IndexOf(order, rar);
        for (int k = 1; k <= 3; k++)
        {
            var next = order[(start + k) % 3];
            cands = Filter(pool, black, next);
            if (cands.Count > 0) return next;
        }
        cands = new List<CardModel>();
        return rar;
    }
}
