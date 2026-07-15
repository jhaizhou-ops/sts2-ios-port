using System;
using Godot;

namespace STS2MobileIos.Patches;

// Fixes a crash when closing the rewards screen caused by a tween race condition.
// Kills the fade tween and stops processing before QueueFree so _Process doesn't
// fire after the node is removed from the tree.
public static class CardRewardPatches
{
    // prefix (void) on MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen.AfterOverlayClosed
    public static void RewardsScreenClosedPrefix(object __instance)
    {
        try
        {
            var node = (Node)__instance;
            node.SetProcess(false);

            var field = PatchHelper.Field(__instance.GetType(), "_fadeTween");
            var tween = field?.GetValue(__instance) as Tween;
            if (tween != null && tween.IsValid())
            {
                tween.Kill();
                field.SetValue(__instance, null);
            }
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"RewardsScreenClosedPrefix failed: {ex.Message}");
        }
    }
}
