using System.Collections.Generic;

namespace STS2MobileIos.Patches;

// 事件价值表 — 每事件最优选项的收益估值。
// 值域 ~0-9(免费稀有遗物≈7 / 远古3选1遗物≈7 / 移除升级附魔≈4 / 金币小收益≈2-3 / 纯叙事0 / 带诅咒下调)。
// RewardsSteps = 该事件"进门或最优选项"消耗 PlayerRng.Rewards 流的步数(发随机卡=2/卡, 随机药水=1, roll稀有度遗物=1; 固定物/转化=0)。
public static class EventData
{
    public static readonly Dictionary<string, (double Value, string Desc, int RewardsSteps)> Table = new()
    {
        // A-F
        ["ABYSSAL_BATHS"] = (2.0, "叠最大生命(受递增不可挡伤)或弃疗+10, 净收益低", 0),
        ["AMALGAMATOR"] = (4.5, "2基础牌合1究极牌: 减2张+得强化基础牌", 0),
        ["AROMA_OF_CHAOS"] = (4.0, "升级1张牌(优于随机转化)", 0),
        ["BATTLEWORN_DUMMY"] = (4.5, "有把握3回合杀300血木桩→拿遗物; 没把握→选150血档换2张升级(保守)", 1),
        ["BRAIN_LEECH"] = (3.0, "本职池生成5张选1入库(必选), 无亏血", 15),
        ["BUGSLAYER"] = (3.0, "得固定无色强牌(灭绝/碾压)二选一", 0),
        ["BYRDONIS_NEST"] = (3.5, "吃蛋+7最大生命 或 拿随从牌", 0),
        ["COLORFUL_PHILOSOPHERS"] = (6.0, "选异色池, 普/罕/稀三档各选1共拿3张异色牌", 18),
        ["COLOSSAL_FLOWER"] = (5.5, "挖到底得PollinousCore遗物(累计伤) 或 135金", 0),
        ["CRYSTAL_SPHERE"] = (3.0, "翻牌小游戏赌遗物/药水/卡/金, 收益不稳(置信低)", 0),
        ["DARV"] = (7.0, "远古: 3选1 boss级遗物(星盘/黑星/潘多拉等)", 0),
        ["DENSE_VEGETATION"] = (2.5, "受8伤换约61-100金 或 休息后被迫战斗", 0),
        ["DOLL_ROOM"] = (4.5, "-15血从3事件遗物挑最优 或 随机免费拿1", 0),
        ["DOORS_OF_LIGHT_AND_DARK"] = (4.5, "升级2张随机牌 或 去除1张自选", 0),
        ["DROWNING_BEACON"] = (4.5, "-13最大生命得FresnelLens(后续牌自动附魔) 或 免费药水", 0),
        ["ENDLESS_CONVEYOR"] = (4.5, "付费反复抓传送带菜品(强化/回血/金/药水/无色牌)", 0),
        ["FAKE_MERCHANT"] = (4.5, "扔恶臭药水开战胜利白拿地毯+全部假遗物 或 花金买", 0),
        ["FIELD_OF_MAN_SIZED_HOLES"] = (4.0, "给1牌附魔PerfectFit 或 去2张加诅咒", 0),
        // G-R
        ["GRAVE_OF_THE_FORGOTTEN"] = (7.0, "白拿 ForgottenSoul 遗物", 0),
        ["HUNGRY_FOR_MUSHROOMS"] = (7.0, "白拿 BigMushroom 遗物(另项扣血)", 0),
        ["INFESTED_AUTOMATON"] = (3.0, "白拿1张随机能力/0费牌入桌", 1),
        ["JUNGLE_MAZE_ADVENTURE"] = (3.0, "独闯~135-165金但扣18血 或 合伙~35-65金无损", 0),
        ["LOST_WISP"] = (3.0, "搜索白拿~45-75金(另项遗物带诅咒)", 0),
        ["LUMINOUS_CHOIR"] = (5.0, "献祭~99-149金买1roll遗物", 1),
        ["MORPHIC_GROVE"] = (2.0, "独行+5最大生命无代价", 0),
        ["NONUPEIPE"] = (7.0, "远古: 9遗物池随机3件任选1, 全免费", 0),
        ["OROBAS"] = (7.0, "远古: 三组遗物各roll一件供选, 免费", 0),
        ["PAEL"] = (7.0, "远古: 三组Pael系遗物各选1, 免费", 0),
        ["POTION_COURIER"] = (3.0, "抓3瓶固定药水 或 洗劫1瓶随机稀有药水", 0),
        ["PUNCH_OFF"] = (6.0, "打赢=遗物+药水(需一场战斗) 或 Nab白拿roll遗物带诅咒", 2),
        ["RANWID_THE_ELDER"] = (6.0, "交1可交易遗物换2roll遗物(净+1)", 2),
        ["REFLECTIONS"] = (4.5, "降级≤2升级≤4张牌, 净赚升级", 0),
        ["RELIC_TRADER"] = (2.0, "3自有遗物之一换1roll新遗物(侧向赌, 进门即消耗3步)", 3),
        ["ROOM_FULL_OF_CHEESE"] = (6.0, "搜寻拿 ChosenCheese 遗物, 代价14伤", 0),
        ["ROUND_TEA_PARTY"] = (8.0, "喝茶白拿 RoyalPoison 遗物+回满血, 无代价", 0),
        // S-Z
        ["SAPPHIRE_SEED"] = (4.0, "吃=回9血+升级1张卡, 无代价", 0),
        ["SELF_HELP_BOOK"] = (4.0, "给某类型牌附魔2层, 永久增益无代价", 0),
        ["SLIPPERY_BRIDGE"] = (4.0, "免费移除1张随机非基础牌", 0),
        ["SPIRALING_WHIRLPOOL"] = (3.0, "给1牌附魔Spiral 或 回33%最大生命", 0),
        ["SPIRIT_GRAFTER"] = (4.0, "回25血+得Metamorphosis能力牌", 0),
        ["STONE_OF_ALL_TIME"] = (4.0, "弃1药水换+10最大生命", 0),
        ["SUNKEN_STATUE"] = (6.0, "抓剑白拿石之剑遗物", 0),
        ["SUNKEN_TREASURY"] = (4.0, "大箱约333金+贪婪诅咒 或 小箱60金", 0),
        ["SYMBIOTE"] = (4.0, "给1牌上腐化强附魔 或 转化1牌(第2幕后)", 0),
        ["TABLET_OF_TRUTH"] = (3.0, "回20血 或 掉上限血升级卡", 0),
        ["TANX"] = (7.0, "远古: 3选1近战遗物", 0),
        ["TEA_MASTER"] = (6.0, "免费失礼之茶遗物+可买茶(第2幕前需150金)", 0),
        ["TEZCATARA"] = (7.0, "远古: 3池各roll食物系遗物3选1", 0),
        ["THE_ARCHITECT"] = (0.0, "终局计分战斗, 无可收集奖励", 0),
        ["THE_FUTURE_OF_POTIONS"] = (4.0, "弃1药水换同稀有度已升级卡3选1(需2+药水)", 2),
        ["THE_LANTERN_KEY"] = (3.0, "还钥匙100金 或 留钥匙(通向战史官大奖任务链)", 0),
        ["THE_LEGENDS_WERE_TRUE"] = (3.0, "夺图得战利品图卡(仅第1幕)", 0),
        ["THIS_OR_THAT"] = (6.0, "华丽=下一件遗物+笨拙诅咒, 白嫖遗物", 0),
        ["TINKER_TIME"] = (3.0, "定制1张疯狂科学卡入库", 0),
        ["TRASH_HEAP"] = (6.0, "跳入-8血换5选1随机遗物", 0),
        ["TRIAL"] = (5.0, "随机3审判之一(遗物/金/卡, 部分带诅咒)", 0),
        ["UNREST_SITE"] = (6.0, "击杀-8上限血换下一件遗物(需HP≤70%)", 0),
        ["VAKUU"] = (7.0, "远古: 3池各roll遗物3选1", 0),
        ["WAR_HISTORIAN_REPY"] = (9.0, "任务门控(需LanternKey): 历史课遗物+2药水+2遗物", 0),
        ["WATERLOGGED_SCRIPTORIUM"] = (4.0, "血墨+6最大生命(免费) 或 付费附魔(需55金)", 0),
        ["WELCOME_TO_WONGOS"] = (5.0, "第2幕商店, 200金买主打稀有遗物", 0),
        ["WELLSPRING"] = (3.0, "装瓶1随机药水", 1),
        ["WHISPERING_HOLLOW"] = (4.0, "约35金换2药水(需44金)", 2),
        ["WOOD_CARVINGS"] = (3.0, "转化1基础牌 或 给可附魔牌上游蛇", 0),
        ["ZEN_WEAVER"] = (4.0, "付费精简牌库(125金移1卡/250金移2卡)", 0),
    };

    // 事件价值; 未知事件给中性默认(事件平均约 4)
    public static double Value(string eventId) =>
        eventId != null && Table.TryGetValue(eventId, out var v) ? v.Value : 4.0;

    // 该事件进门/最优选项消耗 Rewards 流步数(默认 0)
    public static int RewardsSteps(string eventId) =>
        eventId != null && Table.TryGetValue(eventId, out var v) ? v.RewardsSteps : 0;

    public static string Desc(string eventId) =>
        eventId != null && Table.TryGetValue(eventId, out var v) ? v.Desc : null;
}
