using UnityEngine.InputSystem;

namespace ControllerSupport;

// Detects which kind of controller is active (for correct button prompts/glyphs later, and logging).
// Updates on device add/remove. Classification is by device layout/product strings so it doesn't
// depend on optional Input System device modules (DualShock/Switch) being present.
internal static class ControllerDetect
{
    internal enum Kind { None, Xbox, PlayStation, Switch, Generic }

    internal static Kind Current { get; private set; } = Kind.None;

    internal static void Init()
    {
        Detect();
        InputSystem.onDeviceChange += (device, change) =>
        {
            if (device is Gamepad) Detect();
        };
    }

    private static void Detect()
    {
        var gp = Gamepad.current;
        var kind = Classify(gp);
        if (kind == Current) return;
        Current = kind;
        string product = gp == null ? "none" : $"{gp.displayName} (layout={gp.layout})";
        Plugin.Log.LogInfo($"[Gamepad] Detected controller: {kind} — {product}");
    }

    private static Kind Classify(Gamepad gp)
    {
        if (gp == null) return Kind.None;
        var d = gp.description;
        string s = $"{d.manufacturer} {d.product} {gp.layout} {gp.name}".ToLowerInvariant();

        if (s.Contains("dualsense") || s.Contains("dualshock") || s.Contains("sony")
            || s.Contains("playstation") || s.Contains("wireless controller"))
            return Kind.PlayStation;
        if (s.Contains("switch") || s.Contains("pro controller") || s.Contains("nintendo") || s.Contains("joy-con"))
            return Kind.Switch;
        if (s.Contains("xinput") || s.Contains("xbox"))
            return Kind.Xbox;
        return Kind.Generic;
    }
}
