using System.Collections.Generic;

namespace STS2MobileIos.Patches;

// 事件选项级推演数据 — 逐事件的选项顺序与每选项收益。
// 2026-07-14 按 experiments/research/event_sim_and_table_audit.md 全量修复:
//   A级: 19 个事件选项顺序重排为与源码 GenerateInitialOptions 下标严格一致(下标对齐路径靠此正确);
//   全表补 Key(源码 TextKey "EVID.pages.PAGE.options.KEY" 的 KEY 段, 按名匹配自动兼容 LOCKED 变体);
//   B级: 数值/缺选项/语义逐条落实; C级: 补 THE_ARCHITECT。
// 豁免: NEOW 不入表 — 起始事件走专门 live 逻辑(随机2正面遗物+1诅咒系, Key=遗物id动态, Neow.GenerateInitialOptions L214-278)。
// 把"事件里选哪个"作为决策点: 按当前状态(金钱/卡组/上限)评估每个选项, 状态变化入推演。
public class EvOpt
{
    public string Name;
    public int Hp;              // 当前血变化(负=扣, 全部为不可减免伤)
    public int MaxHp;           // 上限变化
    public int HealPct;         // 回血%(100=回满)
    public int Gold;            // 金钱变化(-9999=清空全部)
    public int AddCards;        // 获得卡数
    public int RemoveCards;     // 移除卡数(自选)
    public int TransformCards;  // 转化卡数
    public int UpgradeCards;    // 升级卡数(负=降级)
    public int AddPotions;      // 药水变化
    public int RewardsSteps;    // 消耗 Rewards 流步数
    public string AddRelic;     // 具体遗物id / "ROLL"=roll一件 / "ROLL_X2" / "CHOICE"=池内可选
    public string AddCurse;     // 诅咒id
    public int MinGold;         // 金钱门槛(不足则选项不可用)
    public string AddCardEntry; // 加入牌库的具体卡 entry(如 byrdonisegg 鸟蛋任务卡, 供跨节点联动: 篝火孵化)
    public int AddCardType;     // 该卡类型(1攻2技3能5诅咒), 默认0
    public double Bonus;        // 额外固定价值(联动收益: 蛋能在篝火孵化 0费14攻, 选项本身也该加分)
    public string Key;          // TextKey选项名后缀(源码 "EVID.pages.PAGE.options.KEY" 的 KEY 段)。
                                // 选项数动态的事件(锁定变体/按状态增减)按名匹配, 不再依赖下标对齐。
                                // LOCKED 变体(如 "SNAKE_LOCKED")不出现在可点列表, 无需单独建项。
    public string Note;
}

public static class EventOptions
{
    public static readonly Dictionary<string, EvOpt[]> Table = new()
    {
        // ═══ A-M ═══
        ["ABYSSAL_BATHS"] = new[] {
            // 源码序 AbyssalBaths.GenerateInitialOptions: [IMMERSE, ABSTAIN]; 后页 keys=LINGER|EXIT_BATHS
            new EvOpt { Name="浸泡2次离开", Key="IMMERSE", Hp=-7, MaxHp=4, Note="伤3+4递增, 边际收益截断在2次(后页LINGER/EXIT_BATHS)" },
            new EvOpt { Name="弃疗", Key="ABSTAIN", Hp=10, Note="固定回10" },
        },
        ["AMALGAMATOR"] = new[] {
            new EvOpt { Name="合并打击", Key="COMBINE_STRIKES", RemoveCards=2, AddCards=1, Note="2基础打击→究极打击(固定)" },
            new EvOpt { Name="合并防御", Key="COMBINE_DEFENDS", RemoveCards=2, AddCards=1, Note="2基础防御→究极防御(固定)" },
        },
        ["AROMA_OF_CHAOS"] = new[] {
            // A级重排: 源码序 AromaOfChaos.GenerateInitialOptions = [LET_GO=转化, MAINTAIN_CONTROL=升级](原表倒序)
            new EvOpt { Name="转化", Key="LET_GO", TransformCards=1, Note="自选1张" },
            new EvOpt { Name="升级", Key="MAINTAIN_CONTROL", UpgradeCards=1, Note="自选1张" },
        },
        ["BATTLEWORN_DUMMY"] = new[] {
            // A级重排: 源码序 BattlewornDummy.GenerateInitialOptions = [SETTING_1(75血), SETTING_2(150), SETTING_3(300)](原表[档2,档3,档1]全错位)
            new EvOpt { Name="档1:75血木桩", Key="SETTING_1", AddPotions=1, RewardsSteps=1, Note="最稳但收益低" },
            new EvOpt { Name="档2:150血木桩", Key="SETTING_2", UpgradeCards=2, Note="3回合打150血→升2张随机(保守推荐)" },
            new EvOpt { Name="档3:300血木桩", Key="SETTING_3", AddRelic="ROLL", RewardsSteps=1, Note="3回合打300血→roll遗物(输出足才选)" },
        },
        ["BRAIN_LEECH"] = new[] {
            new EvOpt { Name="分享知识", Key="SHARE_KNOWLEDGE", AddCards=1, RewardsSteps=15, Note="本职池5张必选1(有精确预测)" },
            new EvOpt { Name="撕开", Key="RIP", Hp=-5, AddCards=1, RewardsSteps=9, Note="-5血, 无色池3选1可跳" },
        },
        ["BUGSLAYER"] = new[] {
            new EvOpt { Name="灭绝", Key="EXTERMINATION", AddCards=1, Note="固定无色Exterminate" },
            new EvOpt { Name="碾压", Key="SQUASH", AddCards=1, Note="固定无色Squash" },
        },
        ["BYRDONIS_NEST"] = new[] {
            new EvOpt { Name="吃蛋", Key="EAT", MaxHp=7 },
            new EvOpt { Name="拿蛋", Key="TAKE", AddCardEntry="byrdonisegg", AddCardType=0, Bonus=3.5, Note="蛋卡进牌库→篝火HATCH孵化0费14攻ByrdSwoop+鸟宠, 联动收益篝火兑现" },
        },
        ["COLORFUL_PHILOSOPHERS"] = new[] {
            // Key=色名大写且动态≤3(源码 ColorfulPhilosophers), 无静态Key可填; 单条抽象项按下标兜底
            new EvOpt { Name="选异色池拿3张", AddCards=3, RewardsSteps=18, Note="普/罕/稀各3选1, 可逐档跳过; Key=色名动态, 按名匹配不适用" },
        },
        ["COLOSSAL_FLOWER"] = new[] {
            // 首页仅2项 [EXTRACT_CURRENT_PRIZE_1, REACH_DEEPER_1], 后页 keys=EXTRACT_INSTEAD|POLLINOUS_CORE;
            // 表3项是多页路径的期望抽象(表长≠实际, 下标对齐不触发), 挖2层/挖到底共用首页 REACH_DEEPER_1
            new EvOpt { Name="立即采收", Key="EXTRACT_CURRENT_PRIZE_1", Gold=35 },
            new EvOpt { Name="挖2层采收", Key="REACH_DEEPER_1", Hp=-11, Gold=135, Note="后页EXTRACT_INSTEAD" },
            new EvOpt { Name="挖到底取核心", Key="REACH_DEEPER_1", Hp=-18, AddRelic="POLLINOUS_CORE", Note="累计-18血, 后页POLLINOUS_CORE" },
        },
        ["CRYSTAL_SPHERE"] = new[] {
            // A级重排: 源码序 CrystalSphere.GenerateInitialOptions = [UNCOVER_FUTURE(付费), PAYMENT_PLAN(Debt)](原表倒序)
            // 预付费用=50+Rng(1,50)→51~100(表-75为期望), MinGold 实际=当次动态费用, 99为保守上界; 占卜小游戏奖池数值属未验证近似
            new EvOpt { Name="预付占卜", Key="UNCOVER_FUTURE", Gold=-75, AddPotions=1, MinGold=99, Note="3次占卜期望; 费用51~100动态" },
            new EvOpt { Name="分期(Debt诅咒)", Key="PAYMENT_PLAN", AddCurse="DEBT", Gold=35, AddPotions=1, Note="6次占卜期望≈2倍, 全走事件流0步" },
        },
        ["DARV"] = new[] {
            // Ancient七件套: Key=遗物id动态(3选1), 无静态Key
            new EvOpt { Name="三选一boss级遗物", AddRelic="CHOICE", Note="免费; DustyTome顶替时固定占第3格(末位, Darv L153-162: Take(2)后Add), 非随机1格" },
        },
        ["DENSE_VEGETATION"] = new[] {
            new EvOpt { Name="硬闯", Key="TRUDGE_ON", Hp=-8, Gold=80, Note="金61~99, 80为期望(源码核准)" },
            new EvOpt { Name="休息", Key="REST", HealPct=30, Note="回30%但强制战斗且无战利品(后页FIGHT)" },
        },
        ["DOLL_ROOM"] = new[] {
            // B级补缺: 源码3项 DollRoom.GenerateInitialOptions L86-91 = [RANDOM, TAKE_SOME_TIME(TakeTimeHpLoss=5), EXAMINE], 原表漏-5血中档
            new EvOpt { Name="随手拿", Key="RANDOM", AddRelic="ROLL", Note="3娃娃随机1(免费)" },
            new EvOpt { Name="花点时间挑", Key="TAKE_SOME_TIME", Hp=-5, AddRelic="CHOICE", Note="3娃娃随机去1后2选1" },
            new EvOpt { Name="仔细检查", Key="EXAMINE", Hp=-15, AddRelic="CHOICE", Note="-15血换3选1" },
        },
        ["DOORS_OF_LIGHT_AND_DARK"] = new[] {
            new EvOpt { Name="光之门", Key="LIGHT", UpgradeCards=2, Note="随机2张" },
            new EvOpt { Name="暗之门", Key="DARK", RemoveCards=1, Note="自选1张" },
        },
        ["DROWNING_BEACON"] = new[] {
            new EvOpt { Name="捞瓶子", Key="BOTTLE", AddPotions=1, Note="固定荧水药水" },
            new EvOpt { Name="爬灯塔", Key="CLIMB", MaxHp=-13, AddRelic="FRESNEL_LENS" },
        },
        ["ENDLESS_CONVEYOR"] = new[] {
            // B级重写: 两条互斥路径(EndlessConveyor L128-132/L259-269) — OBSERVE_CHEF 只在INITIAL页且立即结束;
            // 抓盘后第二页选项是继续抓或LEAVE, 不存在"抓完再观察厨师+升1"路径。每盘-40(不足→LOCKED); MinGold=120 是进事件条件, 非选项门槛
            new EvOpt { Name="抓盘吃(期望按2盘)", Gold=-80, MaxHp=2, MinGold=40, Note="每盘-40, 菜品加权随机收益; Key=菜名动态, 按名匹配不适用" },
            new EvOpt { Name="只观察厨师", Key="OBSERVE_CHEF", UpgradeCards=1, Note="免费升1随机, 选后立即结束事件" },
        },
        ["FAKE_MERCHANT"] = new[] {
            // CUSTOM: 源码 GenerateInitialOptions 返回空(自定义交互UI, 无EventOption) — 表项是策略抽象, 下标/按名匹配均不适用, live 需特殊处理
            new EvOpt { Name="扔恶臭药水开战", AddRelic="ROLL", Note="需持FoulPotion, 胜白拿地毯+全部假货(D档)" },
            new EvOpt { Name="离开", Note="假遗物D档不值得买" },
        },
        ["FIELD_OF_MAN_SIZED_HOLES"] = new[] {
            // A级重排: 源码序 = [RESIST(移2+诅咒), ENTER_YOUR_HOLE(附魔)](原表倒序)
            new EvOpt { Name="抵抗", Key="RESIST", RemoveCards=2, AddCurse="NORMALITY", Note="移2自选+诅咒" },
            new EvOpt { Name="进洞", Key="ENTER_YOUR_HOLE", Note="1卡附魔PerfectFit无代价" },
        },
        ["GRAVE_OF_THE_FORGOTTEN"] = new[] {
            // A级重排: 源码序 = [CONFRONT(诅咒+附魔, ±LOCKED), ACCEPT(遗物)](原表倒序)
            new EvOpt { Name="直面", Key="CONFRONT", AddCurse="DECAY", Note="诅咒+1卡附魔灵魂(无可附魔卡时LOCKED)" },
            new EvOpt { Name="接纳", Key="ACCEPT", AddRelic="FORGOTTEN_SOUL", Note="免费白拿" },
        },
        ["HUNGRY_FOR_MUSHROOMS"] = new[] {
            // RelicOption: Key=遗物id
            new EvOpt { Name="大蘑菇", Key="BIG_MUSHROOM", AddRelic="BIG_MUSHROOM", Note="免费白拿" },
            new EvOpt { Name="芳香蘑菇", Key="FRAGRANT_MUSHROOM", Hp=-15, UpgradeCards=2, AddRelic="FRAGRANT_MUSHROOM", Note="旧表漏了升2张收益" },
        },
        ["INFESTED_AUTOMATON"] = new[] {
            new EvOpt { Name="研究", Key="STUDY", AddCards=1, RewardsSteps=3, Note="随机能力牌强制入库" },
            new EvOpt { Name="触摸核心", Key="TOUCH_CORE", AddCards=1, RewardsSteps=3, Note="随机0费牌强制入库" },
        },
        ["JUNGLE_MAZE_ADVENTURE"] = new[] {
            new EvOpt { Name="独闯", Key="SOLO_QUEST", Hp=-18, Gold=150, Note="金150±15(源码核准)" },
            new EvOpt { Name="结伴", Key="JOIN_FORCES", Gold=50, Note="无损, 金50±15" },
        },
        ["LOST_WISP"] = new[] {
            // A级重排: 源码序 = [CLAIM(遗物+Decay), SEARCH(+60)](原表倒序)
            new EvOpt { Name="认领", Key="CLAIM", AddRelic="LOST_WISP", AddCurse="DECAY", Note="A档遗物但带Decay" },
            new EvOpt { Name="搜索", Key="SEARCH", Gold=60, Note="无损, 金60±15" },
        },
        ["LUMINOUS_CHOIR"] = new[] {
            // A级重排: 源码序 = [REACH_INTO_THE_FLESH, OFFER_TRIBUTE(±LOCKED)](原表倒序)
            new EvOpt { Name="伸手入肉", Key="REACH_INTO_THE_FLESH", RemoveCards=2, AddCurse="SPORE_MIND" },
            new EvOpt { Name="献上贡品", Key="OFFER_TRIBUTE", Gold=-125, AddRelic="ROLL", RewardsSteps=1, MinGold=149, Note="门槛=149-Rng(0,50)→99~149动态, MinGold=149偏保守" },
        },
        ["MORPHIC_GROVE"] = new[] {
            // A级重排: 源码序 = [GROUP(清金+转2), LONER(+5上限)](原表倒序)
            new EvOpt { Name="合群", Key="GROUP", Gold=-9999, TransformCards=2, Note="失去全部金币" },
            new EvOpt { Name="独行", Key="LONER", MaxHp=5, Note="无代价" },
        },
        // ═══ N-Z ═══
        ["NONUPEIPE"] = new[] { new EvOpt { Name="三选一饰品遗物", AddRelic="CHOICE", Note="Key=遗物id动态" } },
        ["OROBAS"] = new[] { new EvOpt { Name="三池遗物三选一", AddRelic="CHOICE", Note="Key=遗物id动态" } },
        ["PAEL"] = new[] { new EvOpt { Name="三池Pael遗物三选一", AddRelic="CHOICE", Note="Key=遗物id动态" } },
        ["POTION_COURIER"] = new[] {
            // A级重排: 源码序 = [GRAB_POTIONS(3恶臭), RANSACK(1罕见)](原表倒序)
            new EvOpt { Name="抓药水", Key="GRAB_POTIONS", AddPotions=3, Note="3瓶固定FoulPotion(恶臭, 可留着开假商人)" },
            new EvOpt { Name="洗劫", Key="RANSACK", AddPotions=1, RewardsSteps=1, Note="1瓶随机罕见药水" },
        },
        ["PUNCH_OFF"] = new[] {
            // A级重排: 源码序 = [NAB(Injury+遗物), I_CAN_TAKE_THEM(战斗)](原表倒序)
            new EvOpt { Name="顺手牵羊", Key="NAB", AddRelic="ROLL", AddCurse="INJURY", RewardsSteps=1 },
            new EvOpt { Name="打一架", Key="I_CAN_TAKE_THEM", AddRelic="ROLL", AddPotions=1, Gold=94, RewardsSteps=2, Note="需打赢2×拳击构装体(后页FIGHT); 金91~98" },
        },
        ["RANWID_THE_ELDER"] = new[] {
            // B级补缺+重排: 源码3项 RanwidTheElder.GenerateInitialOptions L77-108 = [POTION(±LOCKED), GOLD, RELIC(±LOCKED)];
            // 交出的药水/遗物是事件随机指定, 非玩家自选。原表漏药水项且顺序错
            new EvOpt { Name="交出药水", Key="POTION", AddPotions=-1, AddRelic="ROLL", RewardsSteps=1, Note="弃1随机药水→1遗物(无药水时LOCKED)" },
            new EvOpt { Name="交出100金", Key="GOLD", Gold=-100, AddRelic="ROLL", RewardsSteps=1, MinGold=100 },
            new EvOpt { Name="交出遗物换2", Key="RELIC", AddRelic="ROLL_X2", RewardsSteps=2, Note="失1件随机可交易遗物, 净+1(无可交易遗物时LOCKED)" },
        },
        ["REFLECTIONS"] = new[] {
            new EvOpt { Name="触碰镜子", Key="TOUCH_A_MIRROR", UpgradeCards=4, Note="先降级≤2再升级≤4, 净赚" },
            new EvOpt { Name="打碎", Key="SHATTER", AddCurse="BAD_LUCK", Note="牌库翻倍! 对精简流毁灭性, 几乎永不选" },
        },
        ["RELIC_TRADER"] = new[] {
            // Key=TOP|MIDDLE|BOTTOM 且1-3项动态; 单条抽象项按下标兜底
            new EvOpt { Name="换一件", AddRelic="ROLL", RewardsSteps=3, Note="失1得1侧向赌, 进门即3步; Key=TOP/MIDDLE/BOTTOM动态" },
        },
        ["ROOM_FULL_OF_CHEESE"] = new[] {
            // A级重排: 源码序 = [GORGE(8选2), SEARCH(-14+遗物)](原表倒序)
            new EvOpt { Name="大吃", Key="GORGE", AddCards=2, RewardsSteps=16, Note="8张普通卡选2(uniform 2步/卡)" },
            new EvOpt { Name="搜寻", Key="SEARCH", Hp=-14, AddRelic="CHOSEN_CHEESE", Note="A档遗物" },
        },
        ["ROUND_TEA_PARTY"] = new[] {
            new EvOpt { Name="喝茶", Key="ENJOY_TEA", HealPct=100, AddRelic="ROYAL_POISON", Note="回满+D档负面遗物, 回满是主收益" },
            new EvOpt { Name="挑衅", Key="PICK_FIGHT", Hp=-11, AddRelic="ROLL", RewardsSteps=1, Note="后页CONTINUE_FIGHT" },
        },
        ["SAPPHIRE_SEED"] = new[] {
            new EvOpt { Name="吃掉", Key="EAT", Hp=9, UpgradeCards=1, Note="回9血+升1自选" },
            new EvOpt { Name="种下", Key="PLANT", Note="1卡附魔Sown" },
        },
        ["SELF_HELP_BOOK"] = new[] {
            // 源码: 每项=免费给1张对应类型卡附魔2层(有该类型可附魔卡才出现, 否则LOCKED) → 选项数1-4动态
            new EvOpt { Name="读封底(攻卡Sharp+2)", Key="READ_THE_BACK", UpgradeCards=1, Bonus=0.5, Note="攻击卡+2伤, 免费" },
            new EvOpt { Name="读一段(技卡Nimble+2)", Key="READ_PASSAGE", UpgradeCards=1, Note="技能卡+2格挡, 免费" },
            new EvOpt { Name="通读(能力Swift+2)", Key="READ_ENTIRE_BOOK", UpgradeCards=1, Note="能力卡附魔, 免费" },
            new EvOpt { Name="跳过", Key="NO_OPTIONS", Note="无可附魔卡时的唯一选项" },
        },
        ["SLIPPERY_BRIDGE"] = new[] {
            new EvOpt { Name="放手", Key="OVERCOME", RemoveCards=1, Note="免费移除展示的随机非基础卡" },
            new EvOpt { Name="坚持1次再放手", Key="HOLD_ON_0", Hp=-3, RemoveCards=1, Note="-3血重roll目标(后页HOLD_ON_n)" },
        },
        ["SPIRALING_WHIRLPOOL"] = new[] {
            new EvOpt { Name="观察漩涡", Key="OBSERVE", Note="1卡附魔Spiral" },
            new EvOpt { Name="喝下", Key="DRINK", HealPct=33 },
        },
        ["SPIRIT_GRAFTER"] = new[] {
            new EvOpt { Name="接纳", Key="LET_IT_IN", Hp=25, AddCards=1, Note="回25+得Metamorphosis能力牌" },
            new EvOpt { Name="排斥", Key="REJECTION", Hp=-10, UpgradeCards=1 },
        },
        ["STONE_OF_ALL_TIME"] = new[] {
            // 两项均有 ±LOCKED 变体
            new EvOpt { Name="举石", Key="LIFT", MaxHp=10, AddPotions=-1, Note="弃1随机药水+10上限(无药水时LOCKED)" },
            new EvOpt { Name="推石", Key="PUSH", Hp=-6, Note="1卡附魔Vigorous×8" },
        },
        ["SUNKEN_STATUE"] = new[] {
            new EvOpt { Name="拔剑", Key="GRAB_SWORD", AddRelic="SWORD_OF_STONE", Note="免费白拿(杀5精英变玉剑)" },
            new EvOpt { Name="潜水", Key="DIVE_INTO_WATER", Hp=-7, Gold=111, Note="金111±10(源码核准)" },
        },
        ["SUNKEN_TREASURY"] = new[] {
            // A级重排: 源码序 = [FIRST_CHEST(小+60±8), SECOND_CHEST(大+333±30+Greed)](原表倒序)
            new EvOpt { Name="小箱", Key="FIRST_CHEST", Gold=60, Note="金60±8" },
            new EvOpt { Name="大箱", Key="SECOND_CHEST", Gold=333, AddCurse="GREED", Note="金333±30" },
        },
        ["SYMBIOTE"] = new[] {
            new EvOpt { Name="靠近", Key="APPROACH", Note="1卡附魔Corrupted(强); 无可附魔卡时LOCKED" },
            new EvOpt { Name="火烧", Key="KILL_WITH_FIRE", TransformCards=1 },
        },
        ["TABLET_OF_TRUTH"] = new[] {
            // A级重排: 源码序 = [DECIPHER_1, SMASH(回20)](原表倒序); 后页 DECIPHER|GIVE_UP
            // 解读全档费用 -3/-6/-12/-24/-(MaxHp-1), 第5档升全部牌 (TabletOfTruth.GetDecipherCost L76-93)
            new EvOpt { Name="解读2档", Key="DECIPHER_1", MaxHp=-9, UpgradeCards=2, Note="-3-6上限升2张随机, 更深档(-12/-24/-全血)慎入" },
            new EvOpt { Name="砸碎", Key="SMASH", Hp=20, Note="回20血" },
        },
        ["TANX"] = new[] { new EvOpt { Name="三选一近战遗物", AddRelic="CHOICE", Note="Key=遗物id动态" } },
        ["TEA_MASTER"] = new[] {
            // A级重排: 源码序 = [BONE_TEA(-50,±L), EMBER_TEA(-150,±L), TEA_OF_DISCOURTESY(免费)](原表轮转错位)
            new EvOpt { Name="骨茶", Key="BONE_TEA", Gold=-50, AddRelic="BONE_TEA", MinGold=50 },
            new EvOpt { Name="余烬茶", Key="EMBER_TEA", Gold=-150, AddRelic="EMBER_TEA", MinGold=150 },
            new EvOpt { Name="失礼之茶", Key="TEA_OF_DISCOURTESY", AddRelic="TEA_OF_DISCOURTESY", Note="免费" },
        },
        ["TEZCATARA"] = new[] { new EvOpt { Name="三池食物遗物三选一", AddRelic="CHOICE", Note="Key=遗物id动态" } },
        ["THE_ARCHITECT"] = new[] {
            // C级补条目: 通关结算对话事件(Combat布局), 选项只有对话推进+PROCEED→WinRun — 无策略价值, 条目仅作显式豁免
            new EvOpt { Name="对话推进→通关", Key="PROCEED", Note="WinRun, 无决策点" },
        },
        ["THE_FUTURE_OF_POTIONS"] = new[] {
            // 源码: 每瓶药水一个选项(≤3), TextKey全部相同=POTION → 按名匹配后再挑最稀有那瓶(第i选项=第i瓶)
            new EvOpt { Name="交易1药水", Key="POTION", AddCards=1, AddPotions=-1, RewardsSteps=6, Note="换同稀有度已升级卡3选1" },
        },
        ["THE_LANTERN_KEY"] = new[] {
            // A级重排: 源码序 = [RETURN_THE_KEY(+100), KEEP_THE_KEY(战斗)](原表倒序)
            new EvOpt { Name="还钥匙", Key="RETURN_THE_KEY", Gold=100 },
            new EvOpt { Name="留钥匙战斗", Key="KEEP_THE_KEY", AddCardEntry="lanternkey", AddCardType=0, Bonus=5.0, Note="后页FIGHT; 胜得钥匙卡→幕3 ChainActs 兑现历史书HistoryCourse(零费重放上回合末攻/技牌), 兑现时再+6" },
        },
        ["THE_LEGENDS_WERE_TRUE"] = new[] {
            new EvOpt { Name="夺图", Key="NAB_THE_MAP", AddCards=1, Note="战利品图卡(宝箱强化)" },
            new EvOpt { Name="慢慢找出口", Key="SLOWLY_FIND_AN_EXIT", Hp=-8, AddPotions=1, RewardsSteps=1 },
        },
        ["THIS_OR_THAT"] = new[] {
            // A级重排: 源码序 = [PLAIN(-6+54), ORNATE(遗物+Clumsy)](原表倒序)
            new EvOpt { Name="朴素", Key="PLAIN", Hp=-6, Gold=54, Note="金41~68, 54为期望(源码核准)" },
            new EvOpt { Name="华丽", Key="ORNATE", AddRelic="ROLL", AddCurse="CLUMSY", RewardsSteps=1 },
        },
        ["TINKER_TIME"] = new[] {
            // B级语义修正: 非全自选 — 页2从ATTACK/SKILL/POWER随机取2二选一, 页3词缀按类型从9种随机取2二选一 (TinkerTime)
            new EvOpt { Name="定制疯狂科学卡", Key="CHOOSE_CARD_TYPE", AddCards=1, Note="类型随机2选1+词缀随机2选1(非自选)" },
        },
        ["TRASH_HEAP"] = new[] {
            new EvOpt { Name="跳进去", Key="DIVE_IN", Hp=-8, AddRelic="ROLL", Note="5池随机1(暗石/捕梦网/手钻/血盆钱庄/长靴)" },
            new EvOpt { Name="捡一把", Key="GRAB", Gold=100, AddCards=1, Note="+100金但10池随机1卡强制入库" },
        },
        ["TRIAL"] = new[] {
            // B级语义修正 (Trial L116-126/L164-168): 审判类型1/3随机(MERCHANT|NOBLE|NONDESCRIPT), 但每种是GUILTY/INNOCENT玩家二选一(可控非随机);
            // NobleGuilty=回10血且无诅咒, 并非"全带诅咒"; REJECT不是离开 — 进入[再ACCEPT|DOUBLE_DOWN(弃档确认弹窗)]页, 本事件无干净离开路径
            new EvOpt { Name="接受审判(期望)", Key="ACCEPT", Gold=100, UpgradeCards=1, RewardsSteps=1,
                Note="类型1/3随机, 每种GUILTY/INNOCENT自选取优: 商人(2遗物/升2)|贵族(回10无诅咒/300金)|路人(2卡/转2), 期望化" },
            new EvOpt { Name="拒绝(会被逼回)", Key="REJECT", Note="进入[再接受|DOUBLE_DOWN=弃档弹窗]页, 无干净离开, 等效最终仍接受; 永不选DOUBLE_DOWN" },
        },
        ["UNREST_SITE"] = new[] {
            // A级重排: 源码序 = [REST(回满+PoorSleep), KILL(-8上限+遗物)](原表倒序)
            new EvOpt { Name="休息", Key="REST", HealPct=100, AddCurse="POOR_SLEEP", Note="血极低时的急救选项" },
            new EvOpt { Name="击杀", Key="KILL", MaxHp=-8, AddRelic="ROLL", RewardsSteps=1 },
        },
        ["VAKUU"] = new[] { new EvOpt { Name="三池遗物三选一", AddRelic="CHOICE", Note="Key=遗物id动态" } },
        ["WAR_HISTORIAN_REPY"] = new[] {
            // Key=UNLOCK_CAGE|UNLOCK_CHEST 可二连; 单条"全拿"抽象项覆盖两步, 按下标兜底
            new EvOpt { Name="开笼+开箱(单人全拿)", AddRelic="ROLL_X2", AddPotions=2, RemoveCards=1, RewardsSteps=4,
                Note="需LanternKey任务链; 历史课[S]遗物+2药水+2遗物roll; Key=UNLOCK_CAGE/UNLOCK_CHEST二连" },
        },
        ["WATERLOGGED_SCRIPTORIUM"] = new[] {
            // 源码: 血墨恒在; 两个付费附魔选项按金钱够不够换成LOCKED → 选项数1-3动态。
            // B级修正: 金额 6/55/99 全是静态常量(GoldVar, WaterloggedScriptorium L30, IsAllowed同源), 触手笔原表-35错, 应-55
            new EvOpt { Name="血墨(+上限)", Key="BLOODY_INK", MaxHp=6, Note="免费+6上限(静态)" },
            new EvOpt { Name="触手笔(付费1卡Steady)", Key="TENTACLE_QUILL", Gold=-55, MinGold=55, UpgradeCards=1, Note="1卡附魔Steady, -55金静态" },
            new EvOpt { Name="刺海绵(付费多卡Steady)", Key="PRICKLY_SPONGE", Gold=-99, MinGold=99, UpgradeCards=2, Note="2卡附魔Steady(CardsVar(2)), -99金静态" },
        },
        ["WELCOME_TO_WONGOS"] = new[] {
            // A级重排: 源码序 = [BARGAIN_BIN(-100,±L), FEATURED_ITEM(-200,±L), MYSTERY_BOX(±L), LEAVE](原表前两项互换)
            new EvOpt { Name="打折区", Key="BARGAIN_BIN", Gold=-100, AddRelic="ROLL", MinGold=100, Note="Common档" },
            new EvOpt { Name="主打商品", Key="FEATURED_ITEM", Gold=-200, AddRelic="ROLL", MinGold=200, Note="Rare档遗物" },
            new EvOpt { Name="神秘盒子", Key="MYSTERY_BOX", Gold=-300, AddRelic="WONGOS_MYSTERY_TICKET", MinGold=300, Note="5战后开3遗物" },
            new EvOpt { Name="离开", Key="LEAVE", UpgradeCards=-1, Note="惩罚: 随机降级1张已升级卡!" },
        },
        ["WELLSPRING"] = new[] {
            new EvOpt { Name="装瓶", Key="BOTTLE", AddPotions=1, RewardsSteps=1 },
            new EvOpt { Name="沐浴", Key="BATHE", RemoveCards=1, AddCurse="GUILTY", Note="移1自选但得Guilty" },
        },
        ["WHISPERING_HOLLOW"] = new[] {
            new EvOpt { Name="给钱", Key="GOLD", Gold=-35, AddPotions=2, RewardsSteps=2, MinGold=44, Note="费用35±9→26~44, MinGold=44保守" },
            new EvOpt { Name="拥抱", Key="HUG", Hp=-9, TransformCards=1 },
        },
        ["WOOD_CARVINGS"] = new[] {
            // 源码序 = [BIRD, SNAKE(±LOCKED), TORUS](原表BIRD|TORUS|SNAKE, 有Key按名可容错, 仍重排以保下标兜底正确)。鸟/环=自选1张基础卡转化(等效删1加1, 价值高)
            new EvOpt { Name="鸟(基础卡→啄击)", Key="BIRD", TransformCards=1, Bonus=1.0, Note="自选基础卡转Peck" },
            new EvOpt { Name="蛇(附魔Slither)", Key="SNAKE", UpgradeCards=1, Note="1卡附魔; 无可附魔卡时LOCKED" },
            new EvOpt { Name="环(基础卡→环形硬度)", Key="TORUS", TransformCards=1, Bonus=1.0, Note="自选基础卡转ToricToughness" },
        },
        ["ZEN_WEAVER"] = new[] {
            // A级重排: 源码序 = [BREATHING(-50), EMOTIONAL(-125), ARACHNID(-250)](原表整体倒序)。注意: 三项的锁定变体共用同一个"LOCKED" key
            new EvOpt { Name="呼吸法", Key="BREATHING_TECHNIQUES", Gold=-50, AddCards=2, MinGold=50, Note="2张Enlightenment" },
            new EvOpt { Name="情绪觉察", Key="EMOTIONAL_AWARENESS", Gold=-125, RemoveCards=1, MinGold=125 },
            new EvOpt { Name="蛛针灸", Key="ARACHNID_ACUPUNCTURE", Gold=-250, RemoveCards=2, MinGold=250 },
        },
    };

    public static EvOpt[] Get(string evId) =>
        evId != null && Table.TryGetValue(evId, out var v) ? v : null;
}
