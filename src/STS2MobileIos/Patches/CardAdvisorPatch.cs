using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Godot;
using MegaCrit.Sts2.Core.Runs;

namespace STS2MobileIos.Patches;

// In-game card-reward advisor (the mobile-native answer to the Steam "STS2 Adviser"
// overlay). Because we run INSIDE the game we read the whole run straight from
// RunState — every card in the deck plus every relic — and score the offered
// candidates against the archetype(s) the run is committed to, then draw a small
// grade + reason above each candidate card. No game value is ever modified: this
// only READS card/relic data and ADDS a label node.
//
// Hook: postfix on NCardRewardSelectionScreen.ShowScreen and .RefreshOptions
// (RefreshOptions also fires on reroll, so both entry points are covered; a
// duplicate-guard makes a double call idempotent — it updates the existing label).
public static class CardAdvisorPatch
{
    // postfix on NCardRewardSelectionScreen.RefreshOptions (populates the reward card
    // row; fires on initial display and reroll).
    public static void RefreshOptionsPostfix(object __instance) => ScheduleRefresh(__instance);

    // postfix on NMerchantInventory.Initialize (the shop; FillSlot populates the
    // NMerchantCard slots). Same advisor, scoring the cards for sale.
    public static void MerchantInitPostfix(object __instance) => ScheduleRefresh(__instance);

    // postfix on NChooseACardSelectionScreen.ShowScreen — EVENT card choices (choose 1
    // of 5, choose 2 of 8, ...). Uses NCardHolder-subclass holders, so the same
    // collect/score/label pipeline applies unchanged.
    public static void ChooseACardPostfix(object __instance) => ScheduleRefresh(__instance);

    // postfix on NChooseABundleSelectionScreen.ShowScreen — bundle (card package) choices.
    public static void ChooseABundlePostfix(object __instance) => ScheduleRefresh(__instance);

    // postfix on NCardGridSelectionScreen._Ready — the generic N-of-M card GRID (event
    // "choose 1 of 5 / 2 of 8" pickers). Scores also help on remove-style grids (drop
    // the low-scoring card).
    public static void GridSelectPostfix(object __instance) => ScheduleRefresh(__instance);

    // postfix on NOverlayStack.Push(IOverlayScreen screen) — the UNIVERSAL entry: every
    // overlay the game opens passes through here. Any overlay that contains card holders
    // gets advisor labels; card-less overlays no-op after one cheap walk. This covers
    // every current and future card-choice surface without hunting screen classes one by
    // one (the per-screen hooks above remain for their extra refresh timing, e.g. reroll).
    public static void OverlayPushPostfix(object screen)
    {
        try { if (screen != null) Trace($"overlay: {screen.GetType().Name}"); } catch { }
        ScheduleRefresh(screen);
    }

    // Run Refresh now, then keep refreshing on a persistent Timer for as long as the
    // screen lives. Why persistent (not a fixed burst): cards deal/animate in, the shop
    // has an entrance animation and hover/scale tweens, and cards can be purchased —
    // a fixed 3s burst stops maintaining labels and they "flash then vanish". A repeating
    // Timer (child of the screen, so it dies with it) keeps every label correct the whole
    // time. Refresh reuses/hides labels, so re-running is cheap and never flickers.
    private static void ScheduleRefresh(object screenObj)
    {
        Refresh(screenObj);
        try
        {
            if (screenObj is Node screen && screen.IsInsideTree())
            {
                if (screen.GetNodeOrNull<Godot.Timer>("AdvisorTimer") != null) return; // already running
                var timer = new Godot.Timer { Name = "AdvisorTimer", WaitTime = 0.25, OneShot = false };
                screen.AddChild(timer);
                timer.Timeout += () =>
                {
                    if (GodotObject.IsInstanceValid(screen) && screen.IsInsideTree())
                        Refresh(screen);
                };
                timer.Start();
            }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Advisor] timer setup failed: {ex.Message}");
        }
    }

    // Per-screen-type holder-count trace (bounded: writes only when the count CHANGES).
    // Diagnoses "hook fires but no labels": trace shows holders=0, and for Choose screens
    // a one-shot child-tree dump reveals where the card nodes actually live.
    private static readonly Dictionary<string, int> _lastHolderCount = new();
    private static void Trace(string line)
    {
        try { File.AppendAllText(Path.Combine(OS.GetUserDataDir(), "advisor_trace.txt"), line + "\n"); }
        catch { /* diagnostics must never hurt the game */ }
    }

    private static void Refresh(object screenObj)
    {
        try
        {
            var screen = screenObj as Node;
            if (screen == null) return;

            // 1) Collect the on-screen candidate card holders (and their CardModels).
            var holders = new List<(Node holder, Node card, object model)>();
            CollectHolders(screen, holders);

            string st = screen.GetType().Name;
            if (!_lastHolderCount.TryGetValue(st, out var last) || last != holders.Count)
            {
                _lastHolderCount[st] = holders.Count;
                Trace($"{st} holders={holders.Count}");
                // First time any hooked screen comes up empty, dump its child tree once —
                // shows where its card nodes actually live.
                if (holders.Count == 0)
                    DumpTree(screen);
            }

            if (holders.Count == 0)
                return;

            // 2) Read the full run: deck + relics.
            var deckInfos = ReadDeck(out var relicEntries);
            // The character's STARTING relic is always relic[0] — auto-granted, present
            // every run, never a build choice. Counting it would lock the recommended
            // direction to its archetype from turn 1. Exclude it from archetype weighting;
            // only ACQUIRED relics (chosen/found) signal build intent. Full list still
            // logged for verification.
            var acquiredRelics = relicEntries.Count > 1
                ? relicEntries.GetRange(1, relicEntries.Count - 1)
                : new List<string>();
            var profile = CardAdvisor.BuildProfile(deckInfos, acquiredRelics);

            // Screen context: on the deck-UPGRADE screen (campfire smith / events) the
            // question is "which card gains most from upgrading", a different metric
            // from "which card to pick" — switch the scorer accordingly.
            bool upgradeMode = st == "NDeckUpgradeSelectScreen";

            // 3) Score every candidate, remember the best for a "★ pick" highlight.
            var scored = new List<(Node holder, Node card, CardAdvisor.CardInfo info, CardAdvisor.ScoreResult res)>();
            foreach (var (holder, card, model) in holders)
            {
                var info = ExtractCardInfo(model);
                if (info == null) continue;
                var res = upgradeMode
                    ? CardAdvisor.ScoreUpgrade(info, profile)
                    : CardAdvisor.Score(info, profile);
                scored.Add((holder, card, info, res));
            }
            if (scored.Count == 0) return;

            int bestIdx = 0;
            for (int i = 1; i < scored.Count; i++)
                if (scored[i].res.Score > scored[bestIdx].res.Score) bestIdx = i;

            bool zh = IsChinese();

            // 4) Draw / update a grade label above each candidate. Labels live under a
            //    layer parented to the SCREEN — NOT the card holders. NGridCardHolders
            //    are pooled and reused (e.g. by a later event's card-enchant screen), so
            //    a label parented to a holder would linger as a ghost on the next screen.
            //    Tied to the screen, labels die when the reward screen closes.
            var layer = GetAdvisorLayer(screen);
            for (int i = 0; i < scored.Count; i++)
            {
                var (holder, card, _, res) = scored[i];
                bool isBest = i == bestIdx && scored.Count > 1;
                DrawLabel(layer, i, holder as Control, res, isBest, zh);
            }
            // Hide (don't free) labels beyond the current count — e.g. a shop card that
            // was just purchased, or a smaller reward on a reused screen. Hiding + reusing
            // avoids create/destroy churn (which flickers on a 0.25s refresh).
            for (int i = scored.Count; ; i++)
            {
                var extra = layer.GetNodeOrNull<Label>($"AdvisorScore_{i}");
                if (extra == null) break;
                extra.Visible = false;
            }

            // 5) Top-of-screen archetype DIRECTION banner — reads all signals (deck+relics),
            //    so a run-defining starting relic points a direction from the first pick.
            float cxSum = 0; int cxN = 0;
            foreach (var s in scored)
                if (s.holder is Control h) { cxSum += h.GlobalPosition.X; cxN++; }
            DrawBanner(layer, cxN > 0 ? cxSum / cxN : 1000f, profile, zh);

            WriteDebugSnapshot(profile, scored, bestIdx, zh, relicEntries, deckInfos);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Advisor] Refresh failed: {ex}");
        }
    }

    // Recursively find NCardHolder nodes under the screen and pair each with its
    // card node (holder.CardNode) and CardModel (cardNode._model). The card node is
    // kept so the label can be positioned against the card's actual on-screen rect.
    private static void CollectHolders(Node node, List<(Node holder, Node card, object model)> outList)
    {
        foreach (var child in node.GetChildren())
        {
            // Reward screen: NGridCardHolder (subclass of NCardHolder) — the card node is
            // <CardNode>k__BackingField. Shop: NMerchantCard (a Control slot) — the card
            // node is the _cardNode field. Both end at NCard._model.
            object cardNode = null;
            var ct = child.GetType();
            if (IsCardHolder(ct))
                cardNode = PatchHelper.Field(ct, "<CardNode>k__BackingField")?.GetValue(child);
            else if (ct.Name == "NMerchantCard")
                cardNode = PatchHelper.Field(ct, "_cardNode")?.GetValue(child);

            if (cardNode != null)
            {
                var model = PatchHelper.Field(cardNode.GetType(), "_model")?.GetValue(cardNode);
                if (model != null)
                    outList.Add((child, cardNode as Node, model));
            }
            CollectHolders(child, outList);
        }
    }

    // One-shot dump of a screen's child tree (type names, 3 levels, capped) so we can
    // see where its card nodes actually live when CollectHolders finds none.
    private static readonly HashSet<string> _dumped = new();
    private static void DumpTree(Node screen)
    {
        string st = screen.GetType().Name;
        if (!_dumped.Add(st)) return;
        var sb = new System.Text.StringBuilder($"tree {st}:\n");
        int budget = 60;
        void Walk(Node n, int depth)
        {
            if (depth > 3 || budget <= 0) return;
            foreach (var ch in n.GetChildren())
            {
                if (budget-- <= 0) return;
                sb.Append(new string(' ', depth * 2)).Append(ch.GetType().Name)
                  .Append(' ').Append(ch.Name).Append('\n');
                Walk(ch, depth + 1);
            }
        }
        Walk(screen, 0);
        Trace(sb.ToString());
    }

    // True if the node's type is NCardHolder or any subclass of it (e.g.
    // NGridCardHolder, used by the card-reward screen).
    private static bool IsCardHolder(Type t)
    {
        for (var cur = t; cur != null; cur = cur.BaseType)
            if (cur.Name == "NCardHolder") return true;
        return false;
    }

    // Read the full deck as CardInfo plus the flat list of relic entry ids, from
    // RunManager.Instance.State (the exact path QuickRestart/Snapshot use: Instance
    // is public/typed, State is non-public so getter-first then backing-field).
    private static List<CardAdvisor.CardInfo> ReadDeck(out List<string> relicEntries)
    {
        var infos = new List<CardAdvisor.CardInfo>();
        relicEntries = new List<string>();
        try
        {
            var rm = RunManager.Instance;
            if (rm == null) return infos;
            var state = PatchHelper.Method(rm.GetType(), "get_State")?.Invoke(rm, null)
                        ?? PatchHelper.Field(rm.GetType(), "<State>k__BackingField")?.GetValue(rm);
            if (state == null) return infos;

            // Deck: RunState._players[*].Deck (CardPile) ._cards — the player's ACTUAL
            // deck. NOT RunState._allCards: that is the run-wide registry of every card
            // model ever instantiated (reward candidates, merchant stock, tokens...), so
            // reading it inflated the count as the player browsed screens (verified via
            // deckCards log: 11 real cards + candidates counted twice + 7 shop cards).
            // Relics: RunState._players[*]._relics[*].Id.Entry
            if (PatchHelper.Field(state.GetType(), "_players")?.GetValue(state) is IEnumerable players)
                foreach (var pl in players)
                {
                    if (pl == null) continue;

                    var deckPile = PatchHelper.Method(pl.GetType(), "get_Deck")?.Invoke(pl, null)
                                   ?? PatchHelper.Field(pl.GetType(), "<Deck>k__BackingField")?.GetValue(pl);
                    if (deckPile != null &&
                        PatchHelper.Field(deckPile.GetType(), "_cards")?.GetValue(deckPile) is IEnumerable cards)
                        foreach (var c in cards)
                        {
                            var info = ExtractCardInfo(c);
                            if (info != null) infos.Add(info);
                        }

                    if (PatchHelper.Field(pl.GetType(), "_relics")?.GetValue(pl) is IEnumerable relics)
                        foreach (var r in relics)
                        {
                            var entry = ReadEntry(r);
                            if (!string.IsNullOrEmpty(entry)) relicEntries.Add(entry);
                        }
                }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Advisor] ReadDeck failed: {ex.Message}");
        }
        return infos;
    }

    // Reflection-extract a CardModel into a plain CardInfo.
    private static CardAdvisor.CardInfo ExtractCardInfo(object model)
    {
        try
        {
            if (model == null) return null;
            var t = model.GetType();
            var info = new CardAdvisor.CardInfo
            {
                Entry = CardAdvisor.Normalize(ReadEntry(model)),
                Type = ReadEnumInt(model, "<Type>k__BackingField"),
                Rarity = ReadEnumInt(model, "<Rarity>k__BackingField"),
                Cost = ReadIntSafe(model, "<CanonicalEnergyCost>k__BackingField", -2),
            };
            AddEnumNames(model, "_keywords", info.Keywords);
            AddEnumNames(model, "_tags", info.Tags);
            return info;
        }
        catch
        {
            return null;
        }
    }

    // Read ModelId.Entry off an AbstractModel (card or relic).
    private static string ReadEntry(object model)
    {
        var id = PatchHelper.Field(model.GetType(), "<Id>k__BackingField")?.GetValue(model);
        if (id == null) return "";
        return PatchHelper.Field(id.GetType(), "<Entry>k__BackingField")?.GetValue(id) as string ?? "";
    }

    private static int ReadEnumInt(object obj, string field)
    {
        var v = PatchHelper.Field(obj.GetType(), field)?.GetValue(obj);
        return v == null ? 0 : Convert.ToInt32(v);
    }

    private static int ReadIntSafe(object obj, string field, int fallback)
    {
        var v = PatchHelper.Field(obj.GetType(), field)?.GetValue(obj);
        try { return v == null ? fallback : Convert.ToInt32(v); }
        catch { return fallback; }
    }

    private static void AddEnumNames(object obj, string field, HashSet<string> outSet)
    {
        if (PatchHelper.Field(obj.GetType(), field)?.GetValue(obj) is IEnumerable set)
            foreach (var e in set)
                if (e != null) outSet.Add(e.ToString().ToLowerInvariant());
    }

    // The game's Simplified-Chinese UI font. A bare Label uses the default theme
    // font, which has no CJK glyphs → tofu boxes; loading the game's own font makes
    // the reason text render and matches the game's look. Loaded once, then cached.
    private static Font _cjkFont;
    private static bool _fontTried;
    private static Font GetCjkFont()
    {
        if (_fontTried) return _cjkFont;
        _fontTried = true;
        try { _cjkFont = ResourceLoader.Load("res://fonts/zhs/SourceHanSerifSC-Bold.otf") as Font; }
        catch (Exception e) { PatchHelper.Log($"[Advisor] font load failed: {e.Message}"); }
        if (_cjkFont == null) PatchHelper.Log("[Advisor] CJK font missing, reason text may be tofu");
        return _cjkFont;
    }

    // A layer parented to the reward SCREEN that holds all advisor labels. Kept off the
    // pooled card holders so labels can never leak onto a later screen that reuses them.
    private static Control GetAdvisorLayer(Node screen)
    {
        var layer = screen.GetNodeOrNull<Control>("AdvisorLayer");
        if (layer == null)
        {
            layer = new Control { Name = "AdvisorLayer" };
            layer.MouseFilter = Control.MouseFilterEnum.Ignore;
            screen.AddChild(layer);
        }
        return layer;
    }

    // Top-of-screen archetype DIRECTION banner. Uses the whole run's signal (deck cards
    // + relics), so even a run-defining starting relic gives a direction from pick #1.
    // Commitment tiers: emerging → recommended → committed.
    private static void DrawBanner(Control layer, float centerX, CardAdvisor.DeckProfile profile, bool zh)
    {
        var label = layer.GetNodeOrNull<Label>("AdvisorBanner") ?? new Label { Name = "AdvisorBanner" };

        string dom = profile.DominantArchKey;
        double w = profile.DominantArchWeight;
        string text;
        Color color;
        // Commitment tier is KEY-driven: archetype identity comes from its defining
        // payoff/engine cards. Supports alone (or the starter deck's baseline cards)
        // don't set a direction; a directing ACQUIRED relic can point one early.
        int cards = profile.CardCount(dom), relics = profile.RelicCount(dom);
        int keys = profile.KeyCount(dom);
        if (dom == null || (keys == 0 && cards < 4 && relics == 0))
        {
            text = zh ? "起手期 · 暂无明确方向，按强度选" : "Early — no clear direction yet";
            color = new Color(0.82f, 0.82f, 0.82f);
        }
        else
        {
            string name = zh ? CardAdvisor.ArchNameZh(dom) : CardAdvisor.ArchNameEn(dom);
            string cnt = zh
                ? $"{keys}张Key+{cards - keys}辅助" + (relics > 0 ? $"+{relics}遗物" : "")
                : $"{keys} key + {cards - keys} support" + (relics > 0 ? $"+{relics} relics" : "");
            if (keys >= 3) { text = zh ? $"★ 已成型：{name}流（{cnt}）" : $"★ Committed: {name} ({cnt})"; color = new Color(1f, 0.84f, 0.2f); }
            else { text = zh ? $"推荐方向：{name}流（{cnt}）" : $"Direction: {name} ({cnt})"; color = new Color(0.45f, 0.9f, 0.45f); }
        }

        label.Text = text;
        label.Visible = true;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        label.TopLevel = true;
        label.ZIndex = 400;

        var font = GetCjkFont();
        if (font != null) label.AddThemeFontOverride("font", font);
        label.AddThemeFontSizeOverride("font_size", 42);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 1));
        label.AddThemeConstantOverride("outline_size", 12);

        if (label.GetParent() == null) layer.AddChild(label);
        const float bw = 1000f, bh = 60f;
        label.Size = new Vector2(bw, bh);
        label.Position = new Vector2(centerX - bw * 0.5f, 60f);
    }

    // Create (or refresh) the grade + reason label above a card. The label lives under
    // the screen's AdvisorLayer (named per index) and positions itself in global space
    // via TopLevel, anchored to the holder's global position (= card centre).
    private static void DrawLabel(Control layer, int index, Control holder, CardAdvisor.ScoreResult res, bool isBest, bool zh)
    {
        if (layer == null || holder == null) return;

        string name = $"AdvisorScore_{index}";
        var existing = layer.GetNodeOrNull<Label>(name);
        var label = existing ?? new Label { Name = name };

        string reason = zh ? res.ReasonZh : res.ReasonEn;
        string star = isBest ? "★ " : "";
        label.Text = $"{star}{res.Grade}  {reason}";

        label.Visible = true;                               // re-show a reused/hidden label
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Bottom;
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.MouseFilter = Control.MouseFilterEnum.Ignore; // never steal card clicks
        label.TopLevel = true;                              // use global coords directly
        label.ZIndex = 400;

        var font = GetCjkFont();
        if (font != null) label.AddThemeFontOverride("font", font);
        label.AddThemeFontSizeOverride("font_size", 38);
        label.AddThemeColorOverride("font_color", GradeColor(res.Grade, isBest));
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 1));
        label.AddThemeConstantOverride("outline_size", 12);

        if (existing == null)
            layer.AddChild(label);

        // Two holder shapes:
        //  • Reward: NGridCardHolder is a ZERO-size node whose global position is the card
        //    CENTRE, so offset up by the card's half-height (a constant in the game's fixed
        //    render-canvas space — the card is a fixed-size asset).
        //  • Shop: NMerchantCard is a real sized Control slot, so read its rect directly.
        var hrect = holder.GetGlobalRect();
        float cx, cardTop;
        if (hrect.Size.Y > 20f)                     // sized slot (shop)
        {
            cx = hrect.Position.X + hrect.Size.X * 0.5f;
            cardTop = hrect.Position.Y;
        }
        else                                        // zero-size holder (reward)
        {
            var center = holder.GlobalPosition;
            cx = center.X;
            cardTop = center.Y - 178f;              // card half-height, canvas units
        }
        const float h = 92f, w = 300f, gap = 2f;
        label.Size = new Vector2(w, h);
        // VerticalAlignment.Bottom hugs the text to the box bottom, placed just above
        // the card's top edge.
        label.Position = new Vector2(cx - w * 0.5f, cardTop - gap - h);
    }

    // Union of the on-screen rects of a node's sized descendants — the card's real
    // visual box, independent of the zero-size NCard wrapper. controlsOnly=true keeps
    // only Control nodes (frame/text) and skips Sprite2D (glow/aura), so the box is the
    // card frame, not the inflated glow. Reflection-free (direct type checks; AOT-safe).
    private static Rect2 FindVisualRect(Node root, bool controlsOnly)
    {
        Rect2 acc = new Rect2();
        bool has = false;
        void Walk(Node n)
        {
            foreach (var ch in n.GetChildren())
            {
                Rect2 r = default;
                bool ok = false;
                if (ch is Control c && c.Size.X > 2f && c.Size.Y > 2f)
                {
                    r = c.GetGlobalRect(); ok = true;
                }
                else if (!controlsOnly && ch is Sprite2D s && s.Texture != null)
                {
                    var lr = s.GetRect();
                    var g = s.GetGlobalTransform();
                    r = new Rect2(g * lr.Position, lr.Size * g.Scale);
                    ok = true;
                }
                if (ok && r.Size.X > 2f && r.Size.Y > 2f)
                {
                    acc = has ? acc.Merge(r) : r; has = true;
                }
                Walk(ch);
            }
        }
        Walk(root);
        return has ? acc : new Rect2();
    }

    private static Color GradeColor(string grade, bool isBest)
    {
        if (isBest) return new Color(1f, 0.84f, 0.2f);      // gold for the pick
        return grade switch
        {
            "S" => new Color(1f, 0.84f, 0.2f),               // gold
            "A" => new Color(0.45f, 0.9f, 0.45f),            // green
            "B" => new Color(0.45f, 0.8f, 1f),               // blue
            "C" => new Color(0.9f, 0.9f, 0.9f),              // white
            _ => new Color(0.7f, 0.55f, 0.55f),              // muted red-grey
        };
    }

    private static bool IsChinese()
    {
        try
        {
            var locale = TranslationServer.GetLocale();
            return !string.IsNullOrEmpty(locale) && locale.StartsWith("zh");
        }
        catch { return false; }
    }

    // Dump the read data + scores to user://advisor_last.txt so the data+scoring
    // chain can be verified off-device without needing to see the UI.
    private static void WriteDebugSnapshot(
        CardAdvisor.DeckProfile profile,
        List<(Node holder, Node card, CardAdvisor.CardInfo info, CardAdvisor.ScoreResult res)> scored,
        int bestIdx,
        bool zh,
        List<string> relicEntries,
        List<CardAdvisor.CardInfo> deck)
    {
        try
        {
            var path = Path.Combine(OS.GetUserDataDir(), "advisor_last.txt");
            using var w = new StreamWriter(path, append: false);
            w.WriteLine($"[advisor] deck={profile.TotalCards} playable={profile.PlayableCards} " +
                        $"atk={profile.Attacks} skl={profile.Skills} pwr={profile.Powers} " +
                        $"avgCost={profile.AvgCost:F2}");
            // Log every card ReadDeck returned — to catch a stale/phantom deck read
            // (e.g. count doesn't match what's actually in the player's deck).
            w.WriteLine("[advisor] deckCards: " +
                        (deck == null ? "(null)" : string.Join(", ", deck.Select(c => c.Entry))));
            w.WriteLine($"[advisor] archetypes: " +
                        string.Join(", ", ArchWeightsString(profile)));
            // Log relics IN ORDER with index — to confirm whether relic[0] is the
            // character's auto-granted starting relic (which should not bias direction).
            w.WriteLine("[advisor] relics(顺序): " +
                        (relicEntries == null || relicEntries.Count == 0
                            ? "(无)"
                            : string.Join(", ", relicEntries.Select((r, i) => $"[{i}]{r}"))));
            for (int i = 0; i < scored.Count; i++)
            {
                var (holder, card, info, res) = scored[i];
                string mark = i == bestIdx ? " <== PICK" : "";
                // Log the geometry anchors so reward vs shop positioning can be tuned
                // off a single screenshot: holder type, holder rect + position, card pos.
                string geo = "";
                if (holder is Control hc)
                {
                    geo = $" htype={holder.GetType().Name} hrect={hc.GetGlobalRect()} hpos={hc.GlobalPosition}";
                    if (card is Control cc) geo += $" cardpos={cc.GlobalPosition}";
                }
                w.WriteLine($"  #{i + 1} {info.Entry} type={info.Type} rar={info.Rarity} " +
                            $"cost={info.Cost} => {res.Grade} {res.Score:F1} " +
                            $"({res.ReasonEn}){mark}{geo}");
            }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Advisor] WriteDebugSnapshot failed: {ex.Message}");
        }
    }

    private static IEnumerable<string> ArchWeightsString(CardAdvisor.DeckProfile p)
    {
        foreach (var kv in p.ArchWeights)
            yield return $"{kv.Key}={kv.Value:F1}";
    }
}
