using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;

namespace STS2MobileIos.Patches;

// Save-state snapshots / time-travel rollback for the mobile port ("free SL"):
// adds six buttons to the in-game pause menu, wired in from QuickRestartPatch.ReadyPostfix
// (one _Ready hook adds Restart Room + these six). Three fixed save slots, each with an
// independent Save and Load button:
//   存档位1/2/3 (Save Slot 1/2/3) and 读档位1/2/3 (Load Slot 1/2/3).
//
// Design — purely file-level, does NOT touch the game's save format or any game value.
// Each slot is a folder user://snapshots/slot_<N>/ holding a full recursive copy of the
// game's on-disk save tree (user://default, which holds every profile, the active run,
// progression and run history).
//   Save Slot N: copy user://default into slot_<N>/ (temp dir + swap, overwriting any
//                previous contents of that slot). Other slots are untouched.
//   Load Slot N: copy slot_<N>/ back over user://default (temp dir + atomic Move so the
//                live save is never left half-restored), then reload through
//                QuickRestartPatch.ReloadCurrentRunAsync (the game's own
//                quit-to-menu -> Continue path, already proven on device).
//
// Because slots are fixed and independent, you can Save Slot 1, play on, Save Slot 2,
// then Load Slot 1 to jump back exactly to slot 1's point — the multi-slot selection the
// previous incremental "save many / load latest only" version could not do.
//
// Because we snapshot what is on disk, a slot captures the game's most recent autosave
// point (the game autosaves on room entry etc.) — the same granularity the "Restart Room"
// button relies on.
public static class SnapshotPatch
{
    private const int SlotCount = 3;
    private const string SaveRootFolder = "default";     // game save tree under user://
    private const string SnapshotsFolder = "snapshots";  // our slots live under user://
    private const string SlotPrefix = "slot_";           // fixed dirs slot_1 / slot_2 / slot_3

    // Called from QuickRestartPatch.ReadyPostfix. `template` is the native
    // "Save and Quit" button, duplicated so layout/style/script match automatically.
    public static void AddButtons(Control container, Node template)
    {
        try
        {
            if (container == null || template == null)
            {
                PatchHelper.Log("[Snapshot] container/template button not available");
                return;
            }

            // Guard against a second _Ready adding duplicates.
            if (container.GetNodeOrNull(SaveButtonName(1)) != null)
                return;

            // "Restart Room" was inserted at index 1; place our six right below it.
            // Grouped: Save 1/2/3 first, then Load 1/2/3 (indices 2..7).
            int index = 2;
            for (int n = 1; n <= SlotCount; n++)
            {
                int slot = n; // capture per-iteration to avoid the shared-closure bug
                bool occ = SlotOccupied(slot);
                int? floor = GetSlotFloor(slot);
                AddButton(container, template, SaveButtonName(slot), SaveLabelText(slot, occ, floor),
                    index++, b => OnSaveSlot(slot, b));
            }
            for (int n = 1; n <= SlotCount; n++)
            {
                int slot = n;
                bool occ = SlotOccupied(slot);
                int? floor = GetSlotFloor(slot);
                AddButton(container, template, LoadButtonName(slot), LoadLabelText(slot, occ, floor),
                    index++, b => OnLoadSlot(slot, b));
            }

            PatchHelper.Log("[Snapshot] Added 3-slot Save/Load buttons to pause menu");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Snapshot] AddButtons failed: {ex}");
        }
    }

    private static Node AddButton(Control container, Node template, string name, string text,
        int index, NClickableControl.ReleasedEventHandler handler)
    {
        // Duplicate flags 14 (groups|scripts|instancing, no signals) — same as
        // QuickRestartPatch, so the template's own Released connection is not copied.
        var dup = template.Duplicate(14);
        dup.Name = name;

        var label = dup.GetNodeOrNull<MegaLabel>("Label");
        if (label != null)
            label.SetTextAutoSize(text);

        container.AddChild(dup);
        container.MoveChild(dup, index);

        ((NClickableControl)dup).Released += handler;
        return dup;
    }

    // ---- Save ------------------------------------------------------------------

    private static void OnSaveSlot(int slot, NClickableControl button)
    {
        try
        {
            var source = SaveRootPath();
            if (!Directory.Exists(source))
            {
                PatchHelper.Log($"[Snapshot] save tree not found at {source}");
                return;
            }

            Directory.CreateDirectory(SnapshotsRootPath());
            var dest = SlotPath(slot);

            // Copy into a temp dir first, then swap into place, so a failure mid-copy
            // never leaves a half-written slot that Load could pick up. Only this slot's
            // folder is replaced; the other two slots are untouched.
            var tmp = dest + ".writing";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            CopyDirectory(source, tmp);
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.Move(tmp, dest);

            // Record the run's current floor at save time so the button can show it
            // (e.g. "存档位1 [第12层]"). Kept in a sibling meta file — never inside the
            // slot tree — so Load does not copy it back into the live save.
            SetSlotFloor(slot, GetCurrentFloor());

            PatchHelper.Log($"[Snapshot] saved slot {slot} -> {dest}");

            // Reflect the new occupancy on this slot's Save + Load buttons.
            RefreshLabels((Node)button);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Snapshot] OnSaveSlot({slot}) failed: {ex}");
        }
    }

    // ---- Load ------------------------------------------------------------------

    private static void OnLoadSlot(int slot, NClickableControl _)
    {
        TaskHelper.RunSafely(LoadSlotAsync(slot));
    }

    private static async Task LoadSlotAsync(int slot)
    {
        try
        {
            var src = SlotPath(slot);
            if (!Directory.Exists(src) || !Directory.EnumerateFileSystemEntries(src).Any())
            {
                // Empty slot: do nothing (the button label already shows [空]/[Empty]).
                PatchHelper.Log($"[Snapshot] slot {slot} is empty, nothing to load");
                return;
            }

            var target = SaveRootPath();

            // Rebuild the live save tree from the slot via a temp dir + swap, so the
            // user's on-disk save is never left in a partially-restored state.
            var tmp = target + ".restoring";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            CopyDirectory(src, tmp);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(tmp, target);

            PatchHelper.Log($"[Snapshot] restored save tree from slot {slot}, reloading run");

            // Reload the run through the game's own, on-device-proven path.
            await QuickRestartPatch.ReloadCurrentRunAsync();
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Snapshot] LoadSlotAsync({slot}) failed: {ex}");
        }
    }

    // ---- New run: clear all slots ---------------------------------------------

    // postfix on MegaCrit.Sts2.Core.Runs.RunManager.SetUpNewSingleplayer (single overload,
    // so the weaver resolves it unambiguously). Fires only when a brand-new singleplayer
    // run starts — continuing a saved run goes through SetUpSavedSingleplayer, which we do
    // NOT hook, so loading a save never wipes the slots. Deletes the three slot folders and
    // their floor meta files so the pause-menu buttons read [空] again for the new run.
    // (If the pause menu is already open its labels won't update until reopened — acceptable;
    // ReadyPostfix re-labels from disk every time the menu is opened.)
    public static void OnNewRun()
    {
        try
        {
            for (int n = 1; n <= SlotCount; n++)
            {
                var dir = SlotPath(n);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);

                var writing = dir + ".writing";
                if (Directory.Exists(writing)) Directory.Delete(writing, true);

                var meta = SlotMetaPath(n);
                if (File.Exists(meta)) File.Delete(meta);
            }
            PatchHelper.Log("[Snapshot] new run started, cleared all 3 slots");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Snapshot] OnNewRun clear failed: {ex}");
        }
    }

    // ---- Slot state ------------------------------------------------------------

    private static bool SlotOccupied(int slot)
    {
        try
        {
            var path = SlotPath(slot);
            return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch { return false; }
    }

    // Re-label all six buttons from current on-disk slot occupancy. Given any one of our
    // buttons, walk up to the shared container and update siblings by name.
    private static void RefreshLabels(Node anyButton)
    {
        try
        {
            var container = anyButton?.GetParent();
            if (container == null) return;
            for (int n = 1; n <= SlotCount; n++)
            {
                bool occ = SlotOccupied(n);
                int? floor = GetSlotFloor(n);
                SetLabel(container.GetNodeOrNull(SaveButtonName(n)), SaveLabelText(n, occ, floor));
                SetLabel(container.GetNodeOrNull(LoadButtonName(n)), LoadLabelText(n, occ, floor));
            }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Snapshot] RefreshLabels failed: {ex.Message}");
        }
    }

    private static void SetLabel(Node button, string text)
    {
        var label = button?.GetNodeOrNull<MegaLabel>("Label");
        if (label != null)
            label.SetTextAutoSize(text);
    }

    // ---- Paths -----------------------------------------------------------------

    private static string SaveRootPath() => Path.Combine(OS.GetUserDataDir(), SaveRootFolder);
    private static string SnapshotsRootPath() => Path.Combine(OS.GetUserDataDir(), SnapshotsFolder);
    private static string SlotPath(int slot) => Path.Combine(SnapshotsRootPath(), SlotPrefix + slot);
    // Floor meta lives as a sibling file (slot_<N>.floor), never inside the slot tree, so
    // restoring a slot into user://default never carries it into the live save.
    private static string SlotMetaPath(int slot) => Path.Combine(SnapshotsRootPath(), SlotPrefix + slot + ".floor");

    // ---- Floor number ----------------------------------------------------------

    // The run's current floor, read live from the game. Null when not in a run
    // (State is null on the main menu) or if anything is unavailable — never throws.
    // RunManager.Instance is public, but State/ActFloor are not, so they are read via
    // reflection (getter first, backing field as fallback) — the same reflection path
    // QuickRestartPatch already relies on and that survives this AOT build (sts2 is the
    // rooted main assembly, so its metadata is preserved).
    private static int? GetCurrentFloor()
    {
        try
        {
            var rm = RunManager.Instance;
            if (rm == null) return null;

            var state = PatchHelper.Method(rm.GetType(), "get_State")?.Invoke(rm, null)
                        ?? PatchHelper.Field(rm.GetType(), "<State>k__BackingField")?.GetValue(rm);
            if (state == null) return null;

            var floor = PatchHelper.Method(state.GetType(), "get_ActFloor")?.Invoke(state, null)
                        ?? PatchHelper.Field(state.GetType(), "<ActFloor>k__BackingField")?.GetValue(state);
            return floor is int i ? i : (int?)null;
        }
        catch { return null; }
    }

    private static void SetSlotFloor(int slot, int? floor)
    {
        try
        {
            var meta = SlotMetaPath(slot);
            if (floor.HasValue)
                File.WriteAllText(meta, floor.Value.ToString());
            else if (File.Exists(meta))
                File.Delete(meta); // unknown floor -> label degrades to [已存]/[Saved]
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Snapshot] SetSlotFloor({slot}) failed: {ex.Message}");
        }
    }

    private static int? GetSlotFloor(int slot)
    {
        try
        {
            var meta = SlotMetaPath(slot);
            if (!File.Exists(meta)) return null;
            return int.TryParse(File.ReadAllText(meta).Trim(), out var f) ? f : (int?)null;
        }
        catch { return null; }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    // ---- Node names & localized labels -----------------------------------------

    private static string SaveButtonName(int slot) => "SaveSlot" + slot;
    private static string LoadButtonName(int slot) => "LoadSlot" + slot;

    private static bool IsChinese()
    {
        try
        {
            var locale = TranslationServer.GetLocale();
            return !string.IsNullOrEmpty(locale) && locale.StartsWith("zh");
        }
        catch { return false; }
    }

    private static string SaveLabelText(int slot, bool occupied, int? floor)
    {
        if (!occupied)
            return IsChinese() ? $"存档位{slot}" : $"Save Slot {slot}";
        return IsChinese() ? $"存档位{slot} {FloorTag(floor)}" : $"Save Slot {slot} {FloorTag(floor)}";
    }

    private static string LoadLabelText(int slot, bool occupied, int? floor)
    {
        if (!occupied)
            return IsChinese() ? $"读档位{slot} [空]" : $"Load Slot {slot} [Empty]";
        return IsChinese() ? $"读档位{slot} {FloorTag(floor)}" : $"Load Slot {slot} {FloorTag(floor)}";
    }

    // Floor suffix for an occupied slot: "[第12层]"/"[Floor 12]" when the floor was
    // captured, degrading to "[已存]"/"[Saved]" when it could not be read at save time.
    private static string FloorTag(int? floor)
    {
        if (floor.HasValue && floor.Value >= 0)
            return IsChinese() ? $"[第{floor.Value}层]" : $"[Floor {floor.Value}]";
        return IsChinese() ? "[已存]" : "[Saved]";
    }
}
