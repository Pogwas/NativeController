using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ControllerSupport;

// Adds right-stick camera movement on top of the game's mouse-delta look. The stick is a held
// position (not a delta), so it produces continuous camera motion while deflected, scaled by a
// configurable look speed independent of the game's mouseSensitivity.
[HarmonyPatch(typeof(InputManager))]
internal static class LookPatch
{
    [HarmonyPatch("GetMouseX")]
    [HarmonyPostfix]
    public static void GetMouseXPostfix(ref float __result)
    {
        if (!Plugin.Enabled.Value) return;
        var gp = Gamepad.current;
        if (gp == null) return;
        float x = Deadzone(gp.rightStick.ReadValue().x);
        __result += x * Plugin.LookSpeedX.Value;
    }

    [HarmonyPatch("GetMouseY")]
    [HarmonyPostfix]
    public static void GetMouseYPostfix(ref float __result)
    {
        if (!Plugin.Enabled.Value) return;
        var gp = Gamepad.current;
        if (gp == null) return;
        float y = Deadzone(gp.rightStick.ReadValue().y);
        if (Plugin.InvertY.Value) y = -y;
        __result += y * Plugin.LookSpeedY.Value;
    }

    private static float Deadzone(float v)
    {
        float dz = Plugin.StickDeadzone.Value;
        return Mathf.Abs(v) < dz ? 0f : v;
    }
}
