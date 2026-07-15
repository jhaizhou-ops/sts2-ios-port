using System;
using System.Collections.Generic;
using System.Linq;

namespace STS2MobileIos.Patches;

// Pure, engine-independent card-reward scoring core (the "advisor brain").
//
// Design: because we are woven INTO the game we can read the ENTIRE run from
// RunState (the whole deck + every relic), not just the 3 cards on screen like an
// external OCR tool. So archetype detection scans the full deck + all relics for
// the "key cards / key relics" that define an archetype (poison, shiv, strength,
// block, exhaust, frost, lightning, dark/orb), the same framework the community
// advisors (ebadon16/sts2-advisor, Shunrai's advisor) use, then weights each
// candidate by how well it fits the archetype(s) the run is already committed to.
//
// Kept free of any Godot/game types on purpose: the patch layer extracts plain
// CardInfo structs via reflection and hands them here, so this logic is simple to
// reason about and could be unit-tested in isolation. Scores are heuristic and
// deliberately coarse for the MVP — they point a direction, not a solved answer.
public static class CardAdvisor
{
    // Lightweight snapshot of a CardModel, extracted by the patch via reflection.
    public sealed class CardInfo
    {
        public string Entry = "";        // ModelId.Entry, normalised to lowercase, '_' stripped
        public int Type;                  // CardType: 1=Attack 2=Skill 3=Power 4=Status 5=Curse
        public int Rarity;                // CardRarity: 2=Common 3=Uncommon 4=Rare 5=Ancient ...
        public int Cost = -2;             // CanonicalEnergyCost; <0 means X / unknown
        public HashSet<string> Keywords = new();  // lowercase keyword names (exhaust, sly, retain...)
        public HashSet<string> Tags = new();      // lowercase tag names (shiv, minion, strike...)
        public string Display;            // 展示用中文名(可空; 不参与评分)
        public bool Removable = true;     // 是否可移除(进阶之灾等 Eternal 卡删不掉)
    }

    // An archetype defined by the "key" signals that identify it. Matching is done
    // on normalised entry substrings (card ids are snake_case like "deadly_poison",
    // normalised to "deadlypoison"), plus structural keyword/tag signals.
    private sealed class Archetype
    {
        public string Key = "";
        public string NameEn = "";
        public string NameZh = "";
        public string[] CardSignals = Array.Empty<string>();   // substrings in a card entry
        public string[] RelicSignals = Array.Empty<string>();  // substrings in a relic entry
        public string[] KeywordSignals = Array.Empty<string>();// card keyword/tag names
    }

    // Character detected from the deck's basic cards (strike_<char>/defend_<char>).
    // Archetype matching is scoped to this character, so e.g. Ironclad's "thunderclap"
    // never counts as Defect lightning, and broad mechanic words stay safe.
    private static readonly string[] Characters =
        { "ironclad", "silent", "defect", "regent", "necrobinder" };

    private static string DetectCharacter(IEnumerable<CardInfo> deck)
    {
        var votes = new Dictionary<string, int>();
        foreach (var c in deck)
            foreach (var ch in Characters)
                if (c.Entry.Contains(ch)) { votes[ch] = votes.TryGetValue(ch, out var v) ? v + 1 : 1; }
        return votes.Count == 0 ? null : votes.OrderByDescending(kv => kv.Value).First().Key;
    }

    // Which character each archetype belongs to (for scoping). Keeps the Archetype table
    // itself uncluttered.
    private static readonly Dictionary<string, string> ArchChar = new()
    {
        ["strength"] = "ironclad", ["block"] = "ironclad", ["exhaust"] = "ironclad", ["blood"] = "ironclad", ["vuln"] = "ironclad",
        ["poison"] = "silent", ["shiv"] = "silent", ["sly"] = "silent",
        ["lightning"] = "defect", ["frost"] = "defect", ["orb"] = "defect", ["claw"] = "defect",
        ["stars"] = "regent", ["forge"] = "regent",
        ["soul"] = "necrobinder", ["osty"] = "necrobinder", ["doom"] = "necrobinder", ["ethereal"] = "necrobinder",
    };

    // Archetype tables built from COMMUNITY archetype guides (mobalytics / stratgg /
    // outputlag, July 2026), not hand-guessed — every card id below was verified to exist
    // in the shipped pck. Matching is substring-on-normalised-id: the entries are mostly
    // full card names (precise) plus a few broad mechanic words (e.g. "poison", "star").
    // Broad words are chosen to avoid cross-archetype collisions (e.g. "glow" not "light",
    // so Defect's *lightning* never counts as Regent light — and note Regent has NO "light"
    // or "royalty" archetype: glow/glimmer/venerate are STAR cards, which was the earlier bug).
    private static readonly Archetype[] Archetypes =
    {
        // ---- Ironclad ----
        new()
        {
            Key = "strength", NameEn = "Strength", NameZh = "力量",
            CardSignals = new[] { "inflame", "demonform", "whirlwind", "twinstrike", "rupture", "brand", "limitbreak", "spotweakness" },
            RelicSignals = new[] { "girya", "brimstone" },
        },
        new()
        {
            Key = "block", NameEn = "Block", NameZh = "格挡",
            CardSignals = new[] { "bodyslam", "barricade", "shrugitoff", "impervious", "juggernaut", "bloodwall", "stonearmor", "unmovable", "demonicshield" },
            RelicSignals = new[] { "bronzescales" },
        },
        new()
        {
            Key = "exhaust", NameEn = "Exhaust", NameZh = "消耗",
            CardSignals = new[] { "corruption", "darkembrace", "feelnopain", "pactsend", "ashenstrike", "offering", "fiendfire", "secondwind" },
            RelicSignals = new[] { "charonsashes" },
            KeywordSignals = new[] { "exhaust" },
        },
        new()
        {
            Key = "blood", NameEn = "Self-Damage", NameZh = "自伤流血",
            CardSignals = new[] { "inferno", "breakthrough", "hemokinesis", "tearasunder", "crimsonmantle", "bloodletting", "feed", "hellraiser", "burningpact" },
        },
        new()
        {
            Key = "vuln", NameEn = "Vulnerability", NameZh = "易伤",
            CardSignals = new[] { "bash", "tremble", "bully", "cruelty", "dominate", "mangle" },
        },

        // ---- Silent ----
        new()
        {
            Key = "poison", NameEn = "Poison", NameZh = "中毒",
            CardSignals = new[] { "poison", "noxiousfumes", "accelerant", "bubblebubble", "burst", "corrosive", "envenom", "corpseexplosion", "bouncingflask", "snakebite", "malaise", "outbreak" },
            RelicSignals = new[] { "sneckoskull", "twistedfunnel" },
        },
        new()
        {
            Key = "shiv", NameEn = "Shiv/Blades", NameZh = "飞刀",
            CardSignals = new[] { "accuracy", "knifetrap", "infiniteblades", "afterimage", "bladedance", "phantomblades", "hiddendaggers", "upmysleeve", "cloakanddagger", "flechettes", "dagger", "fanofknives", "finisher", "stormofsteel" },
            RelicSignals = new[] { "baghnakh", "kunai", "shuriken", "ninjascroll", "nunchaku" },
            KeywordSignals = new[] { "shiv" },
        },
        new()
        {
            Key = "sly", NameEn = "Sly/Discard", NameZh = "诡诈弃牌",
            CardSignals = new[] { "toolsofthetrade", "tactician", "untouchable", "acrobatics", "masterplanner", "flickflack", "prepared" },
            RelicSignals = new[] { "kusarigama", "tingsha" },
            KeywordSignals = new[] { "sly" },
        },

        // ---- Defect ----
        new()
        {
            Key = "lightning", NameEn = "Lightning", NameZh = "闪电",
            CardSignals = new[] { "lightning", "zap", "thunder", "tempest", "tesla", "voltaic", "electro" },
            RelicSignals = new[] { "goldplatedcables" },
        },
        new()
        {
            Key = "frost", NameEn = "Frost", NameZh = "冰霜",
            CardSignals = new[] { "chill", "coldsnap", "coolheaded", "glacier", "hailstorm", "icelance", "coolant", "capacitor" },
            RelicSignals = new[] { "permafrost" },
        },
        new()
        {
            Key = "orb", NameEn = "Orb/Focus/Dark", NameZh = "充能球聚焦",
            CardSignals = new[] { "defragment", "multicast", "darkness", "consum", "focus", "hologram", "biased" },
            RelicSignals = new[] { "crackedcore", "datadisk", "emotionchip", "metronome" },
        },
        new()
        {
            Key = "claw", NameEn = "Claw/Zero-Cost", NameZh = "零费Claw",
            CardSignals = new[] { "claw", "allforone", "scrape", "feral" },
        },

        // ---- Regent (new STS2 character) — community archetypes: Stars + Sovereign Blade ----
        new()
        {
            Key = "stars", NameEn = "Star Engine", NameZh = "星辰",
            // Authoritative roster from slaythespire.wiki.gg "Stars" page (generation +
            // star-cost + synergy). Crescent Spear IS a star card (scales with 1-Star cards),
            // NOT forge — an earlier name-based misclassification.
            CardSignals = new[] {
                "glow", "genesis", "bigbang", "royalgamble", "hiddencache", "venerate",
                "reflect", "particlewall", "childofthestars", "cloakofstars", "iaminvincible",
                "comet", "radiate", "gammablast", "shiningstrike", "fallingstar", "solarstrike",
                "guidingstar", "convergence", "decisionsdecisions", "glimmer", "cosmicindifference",
                "begone", "voidform", "sealedthrone", "spectrumshift", "alignment",
                "crescentspear", "knockoutblow", "devastate", "resonance",
                // broad cosmic / light mechanic words (Regent-scoped, so safe)
                "star", "cosmic", "celestial", "astral", "quasar", "meteor", "supermassive",
                "nebula", "lunar", "solar", "stardust", "orbit", "pillar", "neutron", "paleblue",
                "blackhole", "bigbang", "glitter", "gather", "glow", "shining", "radiate", "spectrum",
            },
            RelicSignals = new[] { "divinedestiny", "pennib" },
        },
        new()
        {
            Key = "forge", NameEn = "Forge/Sovereign Blade", NameZh = "锻造(君主之刃)",
            // Only community-confirmed forge cards. The weapon-NAMED cards I'd guessed in
            // (sword_sage/arsenal/heavenly_drill/refine_blade/beat_into_shape/crescent_spear)
            // are removed — several were wrong (crescent_spear is a Star card). Correctness
            // over coverage: an unknown card scoring neutral beats a wrong archetype label.
            CardSignals = new[] {
                "seekingedge", "summonforth", "heirloomhammer", "smith", "furnace",
                "foregoneconclusion", "bulwark", "sovereignblade", "wroughtinwar", "hammer",
            },
        },

        // ---- Necrobinder (new STS2 character) — community archetypes: Soul/Osty/Doom/Ethereal ----
        new()
        {
            Key = "soul", NameEn = "Soul Cycle", NameZh = "灵魂",
            CardSignals = new[] { "haunt", "soulstorm", "dirge", "capturespirit", "deathmarch", "gravewarden", "severance", "reave", "soul" },
            RelicSignals = new[] { "josspaper" },
        },
        new()
        {
            Key = "osty", NameEn = "Osty/Summon", NameZh = "召唤(Osty)",
            CardSignals = new[] { "rattle", "fetch", "sicem", "pullaggro", "flatten", "boneshards", "unleash", "squeeze", "friendship", "legionofbone", "reanimate", "reaperform", "eidolon", "sentrymode", "bodyguard", "protector", "wisp", "righthandhand", "thescythe", "bone" },
            RelicSignals = new[] { "boneflute" },
            KeywordSignals = new[] { "minion" },
        },
        new()
        {
            Key = "doom", NameEn = "Doom", NameZh = "厄运处决",
            CardSignals = new[] { "scourge", "negativepulse", "deathsdoor", "timesup", "deathbringer", "noescape", "doom" },
        },
        new()
        {
            Key = "ethereal", NameEn = "Ethereal", NameZh = "虚无",
            CardSignals = new[] { "pullfrombelow", "defile", "veilpiercer", "bansheescry", "spiritofash" },
            KeywordSignals = new[] { "ethereal" },
        },
    };

    public sealed class DeckProfile
    {
        public int TotalCards;
        public int PlayableCards;      // excludes status/curse
        public int Attacks;
        public int Skills;
        public int Powers;
        public double AvgCost;         // over playable cards with a known cost
        public int HighCostCount;      // playable cards costing >= 3
        public Dictionary<string, double> ArchWeights = new();  // key -> KEY-weighted commitment
        public Dictionary<string, int> ArchCardCounts = new();  // key -> # deck cards matching
        public Dictionary<string, int> ArchKeyCounts = new();   // key -> # KEY cards among them
        public Dictionary<string, int> ArchRelicCounts = new(); // key -> # relics matching

        // Weights are KEY-driven (key card 3.0, support 0.75, relic 2.5): archetype
        // identity comes from its defining payoff/engine cards, not from how many cards
        // merely touch the mechanic. Regent star-cost fuel is ~half the class pool, so a
        // forge deck full of star SUPPORT still reads FORGE — 3 forge keys (9.0) beat
        // 8 star supports (6.0), matching how the community reads the deck.
        public string DominantArchKey =>
            ArchWeights.Count == 0 ? null : ArchWeights.OrderByDescending(kv => kv.Value).First().Key;
        public double DominantArchWeight =>
            DominantArchKey == null ? 0 : ArchWeights[DominantArchKey];
        public int KeyCount(string key) => key != null && ArchKeyCounts.TryGetValue(key, out var v) ? v : 0;
        public int CardCount(string key) => key != null && ArchCardCounts.TryGetValue(key, out var v) ? v : 0;
        public int RelicCount(string key) => key != null && ArchRelicCounts.TryGetValue(key, out var v) ? v : 0;
    }

    // Localised archetype name for a key (used by the direction banner).
    public static string ArchNameZh(string key)
    {
        foreach (var a in Archetypes) if (a.Key == key) return a.NameZh;
        return key ?? "";
    }
    public static string ArchNameEn(string key)
    {
        foreach (var a in Archetypes) if (a.Key == key) return a.NameEn;
        return key ?? "";
    }

    public sealed class ScoreResult
    {
        public double Score;
        public string Grade = "C";
        public string ReasonEn = "";
        public string ReasonZh = "";
        public bool Synergy;   // fits the run's committed archetype (for highlight)
    }

    // Build the run profile from the full deck and relic set.
    public static DeckProfile BuildProfile(IEnumerable<CardInfo> deck, IEnumerable<string> relicEntries)
    {
        var p = new DeckProfile();
        var costs = new List<int>();
        deck ??= Enumerable.Empty<CardInfo>();
        var deckList = deck.ToList();

        foreach (var c in deckList)
        {
            p.TotalCards++;
            bool playable = c.Type != 4 && c.Type != 5; // not Status/Curse
            if (playable)
            {
                p.PlayableCards++;
                if (c.Type == 1) p.Attacks++;
                else if (c.Type == 2) p.Skills++;
                else if (c.Type == 3) p.Powers++;
                if (c.Cost >= 0) costs.Add(c.Cost);
                if (c.Cost >= 3) p.HighCostCount++;
            }
        }
        p.AvgCost = costs.Count > 0 ? costs.Average() : 1.0;

        // Scope to the deck's character so an archetype from another class can never
        // match (e.g. Ironclad "thunderclap" won't register as Defect lightning). If the
        // character is unknown (no basic cards seen), fall back to all archetypes.
        string deckChar = DetectCharacter(deckList);
        bool InScope(Archetype a) =>
            deckChar == null || !ArchChar.TryGetValue(a.Key, out var ch) || ch == deckChar;
        var scoped = Archetypes.Where(InScope).ToArray();

        // Accumulate archetype commitment. Deck cards that carry a defining signal
        // add weight; relics commit harder (a key relic strongly signals intent).
        foreach (var a in scoped)
            p.ArchWeights[a.Key] = 0;

        foreach (var c in deckList)
        {
            // Starter cards (Rarity == Basic(1): strikes/defends AND each character's two
            // starting function cards — verified in IL: FallingStar/Venerate ctor rarity=1)
            // are GIVEN, not chosen, so they don't signal build intent. Same principle as
            // the starting relic. Skip them for archetype weighting only (deck stats above
            // still count them — deck size/curve are real).
            if (c.Rarity == 1) continue;
            foreach (var a in scoped)
            {
                if (CardMatchesArch(c, a))
                {
                    bool isKey = CardKeyData.IsKey(c.Entry, a.Key);
                    p.ArchWeights[a.Key] += isKey ? 3.0 : 0.75;
                    p.ArchCardCounts[a.Key] = p.CardCount(a.Key) + 1;
                    if (isKey) p.ArchKeyCounts[a.Key] = p.KeyCount(a.Key) + 1;
                }
            }
        }

        var relics = (relicEntries ?? Enumerable.Empty<string>()).Select(Normalize).ToList();
        foreach (var r in relics)
        {
            foreach (var a in scoped)
            {
                if (a.RelicSignals.Any(sig => r.Contains(sig)))
                {
                    p.ArchWeights[a.Key] += 2.5;
                    p.ArchRelicCounts[a.Key] = p.RelicCount(a.Key) + 1;
                }
            }
        }

        // Drop archetypes with no support to keep the dominant read clean.
        foreach (var key in p.ArchWeights.Keys.ToList())
            if (p.ArchWeights[key] <= 0) p.ArchWeights.Remove(key);

        return p;
    }

    // Score a single candidate against the run profile.
    public static ScoreResult Score(CardInfo card, DeckProfile deck)
    {
        var r = new ScoreResult();

        // Status / curse offered as a "card" (rare, e.g. certain events): reject.
        if (card.Type == 4 || card.Type == 5)
        {
            r.Score = -5;
            r.Grade = "D";
            r.ReasonEn = "Status/Curse";
            r.ReasonZh = "状态/诅咒牌";
            return r;
        }

        double score = 0;
        string reasonEn = "", reasonZh = "";
        double topReasonWeight = 0;

        void Consider(double weight, string en, string zh)
        {
            if (weight > topReasonWeight)
            {
                topReasonWeight = weight;
                reasonEn = en;
                reasonZh = zh;
            }
        }

        // 1) Base quality — the card's COMMUNITY TIER (real per-card strength), falling
        //    back to rarity only for cards the table doesn't know. Kept moderate so
        //    archetype fit can still out-vote a shiny off-theme card.
        string tier = CardData.GetTier(card.Entry);
        switch (tier)
        {
            case "S": score += 3.0; Consider(1.5, "Top-tier card", "社区T0强卡"); break;
            case "A": score += 2.0; Consider(1.0, "Strong card", "社区高分卡"); break;
            case "B": score += 1.0; break;
            case "C": score += 0.0; break;
            case "D": score -= 1.0; break;
            case "F": score -= 2.0; Consider(1.0, "Community-rated weak", "社区差评卡"); break;
            default: // unknown to the table → rarity as a weak proxy
                switch (card.Rarity)
                {
                    case 4: case 5: score += 2.0; Consider(0.8, "Rare", "稀有"); break;
                    case 3: score += 1.0; break;
                    case 2: score += 0.0; break;
                    default: score -= 0.5; break;
                }
                break;
        }

        // 2) Archetype synergy — the heart of it. Reward candidates that fit an archetype
        //    the run has already invested in; the bigger the commitment, the bigger the
        //    bonus. Fitting the run's DOMINANT archetype earns a decisive extra bonus so
        //    committing further always beats a shiny off-theme card.
        string domKey = deck.DominantArchKey;
        double domW = deck.DominantArchWeight;
        double bestSyn = 0; string synEn = "", synZh = "";
        foreach (var a in Archetypes)
        {
            if (!CardMatchesArch(card, a)) continue;
            double w = deck.ArchWeights.TryGetValue(a.Key, out var v) ? v : 0;
            bool isKey = CardKeyData.IsKey(card.Entry, a.Key);
            double syn;
            if (w <= 0)
            {
                // Introduces a brand-new archetype: exploratory value only — but a KEY
                // card opens a real build path, worth more than a stray support card.
                syn = isKey ? 1.0 : 0.3;
            }
            else
            {
                syn = Math.Min(6.0, w * 0.8);
                // Doubling down on the committed direction is worth more than rarity;
                // its KEY cards (payoffs/engines) are the single best pickups.
                if (a.Key == domKey && domW >= 2.0) syn += isKey ? 4.0 : 2.5;
            }
            if (syn > bestSyn)
            {
                bestSyn = syn;
                synEn = isKey ? $"KEY of {a.NameEn}" : $"Fits {a.NameEn}";
                synZh = isKey ? $"{a.NameZh}流Key卡" : $"契合{a.NameZh}流";
            }
        }
        if (bestSyn > 0)
        {
            score += bestSyn;
            r.Synergy = bestSyn >= 3.0;
            Consider(bestSyn, synEn, synZh);
        }

        // 3) Deck health — card-type needs. Damage matters most: if the deck is
        //    starved for attacks, boost attacks; if very few powers and the deck is
        //    young, powers scale; too many attacks -> value skills/utility.
        double attackRatio = deck.PlayableCards > 0 ? (double)deck.Attacks / deck.PlayableCards : 0.5;
        if (card.Type == 1) // Attack
        {
            if (attackRatio < 0.35) { score += 2.0; Consider(2.0, "Deck lacks damage", "牌组缺伤害"); }
            else if (attackRatio > 0.6) { score -= 1.0; }
        }
        else if (card.Type == 2) // Skill
        {
            if (attackRatio > 0.6) { score += 1.0; Consider(1.0, "Adds utility", "补充功能牌"); }
        }
        else if (card.Type == 3) // Power
        {
            // Powers scale; slightly favoured while the deck is still small.
            if (deck.TotalCards < 25) { score += 1.2; Consider(1.2, "Scaling power", "成长型能力牌"); }
            else { score += 0.4; }
        }

        // 4) Energy curve — if the deck is already top-heavy, discount expensive
        //    candidates and reward cheap ones (playability / more plays per turn).
        if (card.Cost >= 0)
        {
            if (deck.AvgCost >= 1.7 && card.Cost >= 3)
            {
                score -= 1.5;
                Consider(1.4, "Curve too heavy", "费用曲线偏高");
            }
            else if (card.Cost <= 1 && deck.AvgCost >= 1.6)
            {
                score += 0.6;
            }
        }

        // 5) Deck-size selectivity — a large deck dilutes draws; raise the bar so
        //    only genuinely good adds clear it (mediocre commons get discounted).
        if (deck.TotalCards >= 28 && card.Rarity <= 2 && bestSyn < 2.0)
        {
            score -= 1.2;
            Consider(1.3, "Deck bloated, be picky", "牌组偏大，宜精简");
        }

        r.Score = score;
        r.Grade = ToGrade(score);
        // If nothing stood out, give a neutral reason.
        if (string.IsNullOrEmpty(reasonEn))
        {
            reasonEn = card.Rarity == 2 ? "Filler" : "Playable";
            reasonZh = card.Rarity == 2 ? "填充" : "可用";
        }
        r.ReasonEn = reasonEn;
        r.ReasonZh = reasonZh;
        return r;
    }

    // Card→archetype: the community CardData table is authoritative (per-card, precise).
    // Substring/keyword signals only serve as fallback for cards the table doesn't know.
    private static bool CardMatchesArch(CardInfo c, Archetype a)
    {
        var known = CardData.GetArchetypes(c.Entry);
        if (known != null)
            return known.Contains(a.Key);
        return MatchesCard(c, a);
    }

    private static bool MatchesCard(CardInfo c, Archetype a)
    {
        var e = c.Entry; // already normalised
        if (a.CardSignals.Any(sig => e.Contains(sig)))
            return true;
        if (a.KeywordSignals.Length > 0)
        {
            foreach (var k in a.KeywordSignals)
                if (c.Keywords.Contains(k) || c.Tags.Contains(k))
                    return true;
        }
        return false;
    }

    private static string ToGrade(double score)
    {
        if (score >= 6.5) return "S";
        if (score >= 4.5) return "A";
        if (score >= 2.5) return "B";
        if (score >= 0.5) return "C";
        return "D";
    }

    // Score a card for UPGRADING (campfire/smith): "how much does the upgrade gain",
    // which is a different question from "should I pick this". Community upgrade
    // priority is authoritative; unknown cards fall back to community-agreed rules:
    // basics are never worth it, high-tier cards you actually play gain more, and
    // cards fitting the run's dominant archetype are upgraded first.
    public static ScoreResult ScoreUpgrade(CardInfo card, DeckProfile deck)
    {
        var r = new ScoreResult();

        // Status/Curse can't meaningfully upgrade.
        if (card.Type == 4 || card.Type == 5)
        {
            r.Score = -5; r.Grade = "D";
            r.ReasonEn = "Can't gain"; r.ReasonZh = "无升级价值";
            return r;
        }

        double score = 0;
        string reasonEn = "", reasonZh = "";

        string pri = CardUpgradeData.GetPriority(card.Entry);
        switch (pri)
        {
            case "S": score += 5.0; reasonEn = "Top upgrade"; reasonZh = "必敲·收益极大"; break;
            case "A": score += 3.5; reasonEn = "Strong upgrade"; reasonZh = "优先敲"; break;
            case "B": score += 2.0; reasonEn = "Decent upgrade"; reasonZh = "可敲"; break;
            case "C": score += 0.5; reasonEn = "Marginal"; reasonZh = "收益一般"; break;
            case "D": score -= 2.0; reasonEn = "Skip"; reasonZh = "不值得敲"; break;
            default:
                // Unknown: community rules as fallback.
                if (card.Rarity == 1)
                {
                    score -= 3.0; reasonEn = "Basic, skip"; reasonZh = "基础牌·不值得敲";
                }
                else
                {
                    string tier = CardData.GetTier(card.Entry);
                    if (tier == "S" || tier == "A") { score += 2.0; reasonEn = "Strong card gains"; reasonZh = "强卡·升级收益好"; }
                    else if (tier == "D" || tier == "F") { score -= 1.5; reasonEn = "Weak card"; reasonZh = "弱卡·先换不敲"; }
                    else { score += 0.5; reasonEn = "Average"; reasonZh = "普通收益"; }
                    if (card.Type == 3) { score += 1.0; if (reasonZh == "普通收益") { reasonEn = "Power scales"; reasonZh = "能力牌·全场生效"; } }
                }
                break;
        }

        // Archetype focus: upgrade the cards your run is actually built around first —
        // its KEY cards (payoffs/engines) above mere supports. Basics stay excluded —
        // a basic card doesn't become upgrade-worthy by matching the archetype.
        string domKey = deck.DominantArchKey;
        if (domKey != null && deck.DominantArchWeight >= 2.0 && !(pri == null && card.Rarity == 1))
        {
            foreach (var a in Archetypes)
            {
                if (a.Key != domKey || !CardMatchesArch(card, a)) continue;
                bool isKey = CardKeyData.IsKey(card.Entry, a.Key);
                score += isKey ? 2.5 : 1.0;
                if (pri == null || isKey)
                {
                    reasonEn = isKey ? $"KEY of {a.NameEn}" : reasonEn;
                    reasonZh = isKey ? $"主流派Key卡·优先敲" : (pri == null ? "主流派核心·优先" : reasonZh);
                    if (pri == null && !isKey) reasonEn = $"Core of {a.NameEn}";
                }
                break;
            }
        }

        r.Score = score;
        r.Grade = ToGrade(score);
        r.ReasonEn = reasonEn; r.ReasonZh = reasonZh;
        return r;
    }

    public static string Normalize(string s) =>
        string.IsNullOrEmpty(s) ? "" : s.ToLowerInvariant().Replace("_", "").Replace(" ", "");
}
