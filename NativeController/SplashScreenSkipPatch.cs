using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace NativeController;

// The opening SplashScreen skips on Input.anyKeyDown (legacy input), which doesn't see gamepad buttons
// under the new Input System. This postfix lets a controller button skip it too, mirroring the game's
// own gate (only once the logos are showing, i.e. past the initial Wait state).
[HarmonyPatch(typeof(SplashScreen), "Update")]
internal static class SplashScreenSkipPatch
{
    private static readonly FieldInfo StateField = AccessTools.Field(typeof(SplashScreen), "state");
    private static readonly MethodInfo StateSetMethod = AccessTools.Method(typeof(SplashScreen), "StateSet");
    private static readonly Type StateType = AccessTools.Inner(typeof(SplashScreen), "State");
    // State enum order: Wait=0, Semiwork=1, Warning=2, Done=3

    [HarmonyPostfix]
    public static void Postfix(SplashScreen __instance)
    {
        if (!Plugin.Enabled.Value) return;
        var gp = Gamepad.current;
        if (gp == null) return;

        int state = Convert.ToInt32(StateField.GetValue(__instance));
        if (state <= 0 || state >= 3) return; // only while logos are showing (Semiwork/Warning)

        if (!AnyButtonDown(gp)) return;

        try
        {
            StateSetMethod.Invoke(__instance, new[] { Enum.ToObject(StateType, 3) }); // -> Done
            Plugin.Log.LogInfo("[Gamepad] Splash screen skipped via controller.");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[Gamepad] Splash skip failed: {e}");
        }
    }

    private static bool AnyButtonDown(Gamepad gp) =>
        gp.buttonSouth.wasPressedThisFrame || gp.buttonEast.wasPressedThisFrame ||
        gp.buttonWest.wasPressedThisFrame || gp.buttonNorth.wasPressedThisFrame ||
        gp.startButton.wasPressedThisFrame || gp.selectButton.wasPressedThisFrame;
}
