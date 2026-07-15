using System.Collections.Generic;

namespace STS2MobileIos.Patches;

// KEY cards per archetype — the cards that DEFINE a build (payoffs, engines, core
// enablers), as opposed to support cards that merely touch the mechanic. Curated from
// the community archetype guides gathered July 2026 (outputlag Regent guide's key-card
// sections, stratgg per-archetype lists, sts2companion) — these guides list curated
// cores, not exhaustive members. Every id verified against the shipped pck.
//
// Why keys matter: archetype identity comes from key cards. Regent star-cost cards are
// ~half the class pool (generic fuel), so counting raw members calls every deck "stars";
// three forge payoffs are a deliberate build statement. Key = weight 3.0, support = 0.75.
public static class CardKeyData
{
    public static readonly Dictionary<string, string[]> KeyCards = new()
    {
        // ---- Regent ----
        ["stars"] = new[] { "glow", "genesis", "bigbang", "royalgamble", "hiddencache",
            "spectrumshift", "thesealedthrone", "particlewall", "alignment", "reflect",
            "childofthestars", "voidform" },
        ["forge"] = new[] { "conqueror", "seekingedge", "summonforth", "heirloomhammer",
            "thesmith", "furnace", "foregoneconclusion", "refineblade", "wroughtinwar", "bulwark" },

        // ---- Necrobinder ----
        ["soul"] = new[] { "dirge", "capturespirit", "soulstorm", "haunt", "severance", "gravewarden" },
        ["osty"] = new[] { "rattle", "fetch", "sicem", "boneshards", "necromastery",
            "reanimate", "legionofbone", "protector" },
        ["doom"] = new[] { "noescape", "deathsdoor", "timesup", "deathbringer", "scourge", "endofdays" },
        ["ethereal"] = new[] { "demesne", "eidolon", "spiritofash", "bansheescry", "pagestorm", "parse" },

        // ---- Ironclad ----
        ["strength"] = new[] { "inflame", "demonform", "brand", "limitbreak" },
        ["block"] = new[] { "barricade", "bodyslam", "juggernaut", "impervious", "colossus" },
        ["exhaust"] = new[] { "corruption", "darkembrace", "feelnopain", "fiendfire", "pactsend", "offering" },
        ["blood"] = new[] { "feed", "offering", "rupture", "crimsonmantle", "hemokinesis" },
        ["vuln"] = new[] { "bash", "dominate" },

        // ---- Silent ----
        ["poison"] = new[] { "noxiousfumes", "deadlypoison", "accelerant", "corrosivewave",
            "envenom", "serpentform" },
        ["shiv"] = new[] { "accuracy", "knifetrap", "infiniteblades", "bladedance",
            "phantomblades", "hiddendaggers", "afterimage", "stormofsteel" },
        ["sly"] = new[] { "toolsofthetrade", "tactician", "untouchable", "masterplanner",
            "bullettime", "acrobatics" },

        // ---- Defect ----
        ["lightning"] = new[] { "voltaic", "hyperbeam", "storm", "tempest", "lightningrod", "thunder" },
        ["frost"] = new[] { "glacier", "icelance", "coolant", "chill" },
        ["orb"] = new[] { "defragment", "multicast", "biasedcognition", "fusion" },
        ["claw"] = new[] { "claw", "allforone", "scrape", "feral", "hologram" },
    };

    private static Dictionary<string, HashSet<string>> _sets;
    public static bool IsKey(string entry, string archKey)
    {
        if (_sets == null)
        {
            _sets = new Dictionary<string, HashSet<string>>();
            foreach (var kv in KeyCards)
                _sets[kv.Key] = new HashSet<string>(kv.Value);
        }
        return _sets.TryGetValue(archKey, out var s) && s.Contains(entry);
    }
}
