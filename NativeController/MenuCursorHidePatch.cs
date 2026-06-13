using HarmonyLib;

namespace NativeController;

// Hide the game's menu mouse pointer while the controller is the active input (user
// 2026-06-12 -- the idle pointer just sits over pad-navigated menus). MenuCursor's mesh only
// stays visible while Show() is re-armed every frame (CursorManager.Update keep-alive,
// MenuCursor.cs); skipping Show() lets vanilla's own scale-out branch retire the pointer
// gracefully, and the first real mouse movement flips MenuNavigator.ControllerActive off so
// the pointer pops straight back. MenuNavigator itself never uses MenuCursor (verified) --
// pad navigation is hover/selection-box driven.
[HarmonyPatch(typeof(MenuCursor), nameof(MenuCursor.Show))]
internal static class MenuCursorHidePatch
{
    [HarmonyPrefix]
    private static bool Prefix()
    {
        if (!Plugin.Enabled.Value || !Plugin.HideMouseCursorOnPad.Value) return true;
        return !MenuNavigator.ControllerActive;
    }
}
