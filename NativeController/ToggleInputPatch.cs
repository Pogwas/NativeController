using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NativeController;

// Press-to-toggle Sprint and Grab when playing on a gamepad. Instead of re-implementing
// toggle logic, force the game's OWN per-input toggle mode on for those two inputs
// (InputManager.InputToggleGet postfix) and let vanilla handle the semantics:
//  - sprint toggle auto-cancels on empty stamina (PlayerController.cs:647-650)
//  - grab toggle latches only while something is actually grabbed and auto-drops when the
//    grab breaks (PhysGrabber.cs:1390-1397)
// One vanilla gap: toggle sprint does NOT cancel when the player stops moving (it resumes
// on the next step) — the FixedUpdate postfix below adds that, per the requested behavior.
[HarmonyPatch]
internal static class ToggleInputPatch
{
    private static readonly AccessTools.FieldRef<PlayerController, bool> ToggleSprintRef =
        AccessTools.FieldRefAccess<PlayerController, bool>("toggleSprint");

    private static bool _movedSinceToggle; // don't cancel before the sprint actually started

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.InputToggleGet))]
    [HarmonyPostfix]
    private static void InputToggleGetPostfix(InputKey key, ref bool __result)
    {
        if (__result || !Plugin.Enabled.Value || Gamepad.current == null) return;
        if (key == InputKey.Sprint && Plugin.SprintToggle.Value) __result = true;
        else if (key == InputKey.Grab && Plugin.GrabToggle.Value) __result = true;
    }

    // Cancel the sprint toggle the moment the player stops moving. 'moving' is the game's
    // own velocity-based flag (PlayerController.cs:863-876) and already carries a built-in
    // 0.1s debounce, so no extra grace is needed (user removed it 2026-06-09). Cancellation
    // only arms after the player has actually moved while toggled, so pressing Sprint while
    // standing still doesn't immediately untoggle before they take their first step.
    [HarmonyPatch(typeof(PlayerController), "FixedUpdate")]
    [HarmonyPostfix]
    private static void FixedUpdatePostfix(PlayerController __instance)
    {
        if (!Plugin.Enabled.Value || !Plugin.SprintToggle.Value || Gamepad.current == null) return;
        if (!ToggleSprintRef(__instance))
        {
            _movedSinceToggle = false;
            return;
        }

        if (__instance.moving)
        {
            _movedSinceToggle = true;
            return;
        }
        if (!_movedSinceToggle) return;

        ToggleSprintRef(__instance) = false;
        _movedSinceToggle = false;
    }
}
