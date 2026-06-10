using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NativeController;

// Adds right-stick camera movement on top of the game's mouse-delta look, AND injects the aim-assist
// correction as a bounded additive look-delta. The correction is in the SAME unit as __result (input
// units that CameraAim multiplies by AimSpeedMouse), is added to — never substituted for — the player's
// own input, and is clamped strictly below the player's own per-frame turn, so it cannot lock the view.
[HarmonyPatch(typeof(InputManager))]
internal static class LookPatch
{
    private const float AimSpeedFloor = 0.0001f; // avoid divide-by-zero on AimSpeedMouse

    [HarmonyPatch("GetMouseX")]
    [HarmonyPostfix]
    public static void GetMouseXPostfix(ref float __result)
    {
        if (!Plugin.Enabled.Value) return;
        if (EmoteWheel.Open) return; // wheel open: stick is selecting an emote, not looking

        // 1) Right-stick look (existing behaviour) — counts as the player's own input for the clamp.
        var gp = Gamepad.current;
        if (gp != null)
        {
            float x = Deadzone(gp.rightStick.ReadValue().x);
            if (Plugin.InvertX.Value) x = -x;
            __result += x * Plugin.LookSpeedX.Value;
        }

        // 2) Aim-assist yaw correction.
        if (!Plugin.AimAssistEnabled.Value || !AimAssist.HasTarget) return;
        var aim = CameraAim.Instance;
        var cam = Camera.main;
        if (aim == null || cam == null) return;

        Vector3 fwd   = cam.transform.forward;
        Vector3 toTgt = AimAssist.TargetPosition - cam.transform.position;

        // Gimbal guard: skip yaw when looking near-vertical (pitch resolves it).
        Vector3 fwdFlat = Vector3.ProjectOnPlane(fwd, Vector3.up);
        Vector3 tgtFlat = Vector3.ProjectOnPlane(toTgt, Vector3.up);
        if (fwdFlat.sqrMagnitude < 1e-4f || tgtFlat.sqrMagnitude < 1e-4f) return;

        float yawErrDeg = Vector3.SignedAngle(fwdFlat, tgtFlat, Vector3.up); // +ve = target right = +__result
        float totalErr  = Vector3.Angle(fwd, toTgt);

        __result += Correction(yawErrDeg, totalErr, __result, aim.AimSpeedMouse);
    }

    [HarmonyPatch("GetMouseY")]
    [HarmonyPostfix]
    public static void GetMouseYPostfix(ref float __result)
    {
        if (!Plugin.Enabled.Value) return;
        if (EmoteWheel.Open) return; // wheel open: stick is selecting an emote, not looking

        var gp = Gamepad.current;
        if (gp != null)
        {
            float y = Deadzone(gp.rightStick.ReadValue().y);
            if (Plugin.InvertY.Value) y = -y;
            __result += y * Plugin.LookSpeedY.Value;
        }

        if (!Plugin.AimAssistEnabled.Value || !AimAssist.HasTarget) return;
        var aim = CameraAim.Instance;
        var cam = Camera.main;
        if (aim == null || cam == null) return;

        Vector3 fwd   = cam.transform.forward;
        Vector3 toTgt = AimAssist.TargetPosition - cam.transform.position;

        // +__result on GetMouseY = look UP (CameraAim: aimVertical += 0 - vector.y).
        // pitchErr +ve when target is ABOVE forward → we want to look up → positive correction.
        float tgtElev  = Mathf.Asin(Mathf.Clamp(toTgt.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
        float fwdElev  = Mathf.Asin(Mathf.Clamp(fwd.normalized.y,   -1f, 1f)) * Mathf.Rad2Deg;
        float pitchErr = tgtElev - fwdElev;
        float totalErr = Vector3.Angle(fwd, toTgt);

        __result += Correction(pitchErr, totalErr, __result, aim.AimSpeedMouse);
    }

    // Shared bounded correction. Returns the value to ADD to __result (in input units).
    //   axisErrDeg : signed error on THIS axis (sign already matches +__result direction).
    //   totalErrDeg: total crosshair-to-target angle (drives the shared falloff).
    //   playerResult: __result so far this frame (player's own look incl. right stick), in input units.
    //   aimSpeed   : live CameraAim.AimSpeedMouse (degrees per input unit).
    private static float Correction(float axisErrDeg, float totalErrDeg, float playerResult, float aimSpeed)
    {
        aimSpeed = Mathf.Max(AimSpeedFloor, aimSpeed);

        float deadZone = Plugin.AimAssistDeadZone.Value;
        float maxAngle = Plugin.AimAssistMaxAngle.Value;

        // Near-target fade: 0 inside the dead zone, smoothstep up toward the cone edge. Kills settle/snap.
        float fall;
        if (totalErrDeg <= deadZone) fall = 0f;
        else if (totalErrDeg >= maxAngle) fall = 1f;
        else fall = Mathf.SmoothStep(0f, 1f, (totalErrDeg - deadZone) / Mathf.Max(0.001f, maxAngle - deadZone));
        if (fall <= 0f) return 0f;

        // Desired correction (degrees), signed toward target.
        float desiredDeg = Plugin.AimAssistGain.Value * axisErrDeg * fall;

        // Player's own per-frame turn on this axis, in degrees.
        float playerDeg = Mathf.Abs(playerResult) * aimSpeed;
        bool moving = playerDeg > Plugin.AimAssistActiveThreshold.Value;

        float capDeg;
        if (moving)
            // Strictly below the player's own turn (MaxFraction<1) + an absolute per-frame ceiling.
            capDeg = Mathf.Min(Plugin.AimAssistMaxFraction.Value * playerDeg, Plugin.AimAssistMaxDegPerFrame.Value);
        else
            // Tiny constant drift when stopped, itself faded by 'fall' so it can never fully home.
            capDeg = Mathf.Min(Plugin.AimAssistIdleDrift.Value * fall, Plugin.AimAssistMaxDegPerFrame.Value);

        float corrDeg = Mathf.Clamp(desiredDeg, -capDeg, capDeg);
        return corrDeg / aimSpeed; // degrees → input units
    }

    private static float Deadzone(float v)
    {
        float dz = Plugin.StickDeadzone.Value;
        return Mathf.Abs(v) < dz ? 0f : v;
    }
}
