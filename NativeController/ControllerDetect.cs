using UnityEngine;
using UnityEngine.InputSystem;

namespace NativeController;

// Detects which kind of controller is active (for correct button prompts/glyphs later, and logging).
// Updates on device add/remove. Classification is by device layout/product strings so it doesn't
// depend on optional Input System device modules (DualShock/Switch) being present.
internal static class ControllerDetect
{
    internal enum Kind { None, Xbox, PlayStation, Switch, Generic }

    internal static Kind Current { get; private set; } = Kind.None;

    // Last-input-wins "is the pad the active input" signal, valid EVERYWHERE (gameplay + menus).
    // NOTE: MenuNavigator.ControllerActive looks similar but is menu-scoped — its Update early-
    // returns when no menu is open, so it is permanently false in gameplay. Use THIS for any
    // gameplay-side gating (prompts, key tags). Starts true when a pad is present so pad players
    // get prompts immediately on load, before any input.
    internal static bool PadActive { get; private set; }
    private static bool _padActiveInit;

    // True once the player has ACTUALLY touched the pad this level (unlike PadActive, which
    // starts true when a pad is merely present). Used to delay teaching prompts until the
    // controller is in use. Reset per level by Plugin.OnSceneLoaded.
    internal static bool PadTouchedThisLevel { get; private set; }

    internal static void ResetLevelTouch()
    {
        PadTouchedThisLevel = false;
    }

    // Called once per frame (GrabPromptOverlay.Update) — cheap polling, no events needed.
    internal static void TrackActiveInput()
    {
        var gp = Gamepad.current;
        if (gp == null)
        {
            PadActive = false;
            _padActiveInit = true;
            return;
        }
        if (!_padActiveInit)
        {
            _padActiveInit = true;
            PadActive = true;
        }

        bool padInput =
            gp.leftStick.ReadValue().sqrMagnitude > 0.02f
            || gp.rightStick.ReadValue().sqrMagnitude > 0.02f
            || gp.dpad.ReadValue() != Vector2.zero
            || gp.buttonSouth.isPressed || gp.buttonEast.isPressed
            || gp.buttonWest.isPressed || gp.buttonNorth.isPressed
            || gp.leftTrigger.isPressed || gp.rightTrigger.isPressed
            || gp.leftShoulder.isPressed || gp.rightShoulder.isPressed
            || gp.startButton.isPressed || gp.selectButton.isPressed
            || gp.leftStickButton.isPressed || gp.rightStickButton.isPressed;
        if (padInput)
        {
            PadActive = true;
            PadTouchedThisLevel = true;
            return;
        }

        var mouse = Mouse.current;
        bool mouseInput = mouse != null
            && (mouse.delta.ReadValue().sqrMagnitude > 4f
                || mouse.leftButton.isPressed || mouse.rightButton.isPressed);
        var kb = Keyboard.current;
        bool kbInput = kb != null && kb.anyKey.isPressed;
        if (mouseInput || kbInput) PadActive = false;
        // neither input this frame: keep the last winner
    }

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
