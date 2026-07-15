using System;
using System.IO;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace STS2MobileIos.Patches;

// Applies mobile-friendly default settings on first launch and fixes the VSync
// toggle label bug where the Off and On display values are swapped.
public static class SettingsPatches
{
    private static bool _mobileDefaultsChecked;

    // postfix on MegaCrit.Sts2.Core.Saves.SaveManager.InitSettingsData.
    // Applies mobile defaults once per install (marker file in the Godot user:// dir);
    // user preferences are respected after that.
    public static void InitSettingsDataPostfix()
    {
        if (_mobileDefaultsChecked)
            return;
        _mobileDefaultsChecked = true;

        var markerPath = Path.Combine(OS.GetUserDataDir(), ".mobile_defaults_applied");
        if (File.Exists(markerPath))
            return;

        try
        {
            var settings = SaveManager.Instance.SettingsSave;
            settings.VSync = VSyncType.On;
            settings.AspectRatioSetting = AspectRatioSetting.Auto;
            settings.Msaa = 0;
            SaveManager.Instance.SaveSettings();

            File.WriteAllText(markerPath, "1");
            PatchHelper.Log(
                "Applied mobile default settings (first launch): VSync=On, AspectRatio=Auto, Msaa=None"
            );
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"Failed to apply mobile defaults: {ex.Message}");
        }
    }

    // prefix on MegaCrit.Sts2.Core.Nodes.Screens.Settings.NVSyncPaginator.GetVSyncString
    // (static, returns string). Fixes swapped Off/On labels (upstream bug).
    // iOS port note: the target parameter is the VSyncType enum; the hook must declare
    // the exact enum type (the weaver passes arguments unboxed, unlike Harmony).
    public static bool GetVSyncStringPrefix(VSyncType vsyncType, ref string __result)
    {
        try
        {
            int val = (int)vsyncType;
            var sts2Asm = typeof(NGame).Assembly;
            var locStringType = sts2Asm.GetType("MegaCrit.Sts2.Core.Localization.LocString");
            var ctor = locStringType.GetConstructor(new[] { typeof(string), typeof(string) });
            var getTextMethod = locStringType.GetMethod("GetFormattedText", Type.EmptyTypes);

            string key = val switch
            {
                1 => "VSYNC_OFF",
                2 => "VSYNC_ON",
                3 => "VSYNC_ADAPTIVE",
                _ => "VSYNC_ADAPTIVE",
            };

            var locStr = ctor.Invoke(new object[] { "settings_ui", key });
            __result = (string)getTextMethod.Invoke(locStr, null);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"GetVSyncStringPrefix failed: {ex.Message}");
            __result = "On";
        }
        return false;
    }
}
