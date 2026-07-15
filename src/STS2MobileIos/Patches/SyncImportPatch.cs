using System;
using System.IO;
using System.Linq;
using Godot;

namespace STS2MobileIos.Patches;

// Newest-wins save sync, phone side. The Mac-side syncer (ios-export/sts2_save_sync.sh)
// pushes the desktop save tree to user://sync_inbox/<unixts>/1/** whenever the desktop
// copy is newer. At the earliest boot point (before any save is read) we compare that
// timestamp against the LOCAL save tree's newest mtime and import only if the inbox
// really is newer — the newest-wins rule is enforced on-device too, so a stale push can
// never clobber fresher phone progress. The replaced tree is kept as a timestamped
// backup (last 2 retained). Import is an atomic directory swap (same discipline as
// SnapshotPatch): no partial states.
public static class SyncImportPatch
{
    public static void TryImport()
    {
        try
        {
            string root = OS.GetUserDataDir();                    // = Documents
            string inbox = Path.Combine(root, "sync_inbox");
            if (!Directory.Exists(inbox)) return;

            // Pushes are timestamp-named subdirs; pick the newest, ignore strays.
            var pushes = Directory.GetDirectories(inbox)
                .Select(d => new { Dir = d, Ts = ParseTs(Path.GetFileName(d)) })
                .Where(x => x.Ts > 0 && Directory.Exists(Path.Combine(x.Dir, "1")))
                .OrderByDescending(x => x.Ts)
                .ToList();
            if (pushes.Count == 0) { Directory.Delete(inbox, true); return; }

            var best = pushes[0];
            string defaultDir = Path.Combine(root, "default");
            long localNewest = NewestMtimeUnix(Path.Combine(defaultDir, "1"));

            if (best.Ts <= localNewest)
            {
                // Phone progressed past this push — stale, drop it.
                Log($"inbox stale (push {best.Ts} <= local {localNewest}), discarded");
                Directory.Delete(inbox, true);
                return;
            }

            // Import: swap default aside as a backup, move the pushed tree in.
            string backup = Path.Combine(root, $"sync_replaced_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
            if (Directory.Exists(defaultDir))
                Directory.Move(defaultDir, backup);
            Directory.CreateDirectory(defaultDir);
            Directory.Move(Path.Combine(best.Dir, "1"), Path.Combine(defaultDir, "1"));
            Directory.Delete(inbox, true);
            PruneBackups(root, keep: 2);
            Log($"imported desktop save (push {best.Ts} > local {localNewest}); old tree kept at {Path.GetFileName(backup)}");
        }
        catch (Exception e)
        {
            Log($"import failed: {e.Message}");
        }
    }

    private static long ParseTs(string name) => long.TryParse(name, out var v) ? v : 0;

    private static long NewestMtimeUnix(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long newest = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var t = new DateTimeOffset(File.GetLastWriteTimeUtc(f)).ToUnixTimeSeconds();
            if (t > newest) newest = t;
        }
        return newest;
    }

    private static void PruneBackups(string root, int keep)
    {
        var backs = Directory.GetDirectories(root, "sync_replaced_*").OrderByDescending(d => d).ToList();
        foreach (var d in backs.Skip(keep))
            Directory.Delete(d, true);
    }

    private static void Log(string msg)
    {
        PatchHelper.Log($"[SyncImport] {msg}");
        try { File.AppendAllText(Path.Combine(OS.GetUserDataDir(), "sync_log.txt"), $"[import] {msg}\n"); }
        catch { }
    }
}
