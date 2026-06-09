using HarmonyLib;
using UnityEngine.InputSystem;

namespace NativeController;

// The game reads Push/Pull as a 2D scroll axis (InputKey.Push/Pull default to <Mouse>/scroll/y and are
// read via ReadValue<Vector2>().y), so a plain gamepad button binding can't drive them. Instead inject the
// bumper state straight into InputManager.KeyPullAndPush() — the signed value PhysGrabber uses to move a
// grabbed object (>0 push, <0 pull). RB = push, LB = pull. PhysGrabber already gates the actual movement
// on !InputHold(Rotate) (LT), so this composes with the Rotate binding.
[HarmonyPatch(typeof(InputManager), "KeyPullAndPush")]
internal static class PushPullPatch
{
    [HarmonyPostfix]
    public static void Postfix(ref float __result)
    {
        if (!Plugin.Enabled.Value) return;
        var gp = Gamepad.current;
        if (gp == null) return;
        if (gp.rightShoulder.isPressed) __result = 1f;       // RB push
        else if (gp.leftShoulder.isPressed) __result = -1f;  // LB pull
    }
}
