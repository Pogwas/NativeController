using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NativeController;

// Shows CONTROLLER button names inside the game's own key tags ("SHOTGUN [E]" -> "SHOTGUN [X]")
// while the controller is the active input. Single seam: InputManager.InputDisplayGet is what
// InputDisplayReplaceTags calls per tag (InputManager.cs:691-710), which feeds item tooltips
// (ItemAttributes.ShowingInfo), tutorial text, and every [interact]/[grab]/... tag.
// Menu-gated so the keybind-settings UI keeps showing real keyboard keys.
// ItemAttributes CACHES its tag once (itemTag=="" guard, ItemAttributes.cs:329) — when the
// active input flips we clear every cached itemTag so tooltips rebuild with the new input.
[HarmonyPatch]
internal static class InputDisplayPatch
{
    private static readonly AccessTools.FieldRef<MenuManager, int> MenuStateRef =
        AccessTools.FieldRefAccess<MenuManager, int>("currentMenuState");
    private static readonly AccessTools.FieldRef<ItemAttributes, string> ItemTagRef =
        AccessTools.FieldRefAccess<ItemAttributes, string>("itemTag");

    private static bool _lastControllerActive;

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.InputDisplayGet))]
    [HarmonyPostfix]
    private static void InputDisplayGetPostfix(InputKey _inputKey, MenuKeybind.KeyType _keyType, MovementDirection _movementDirection, ref string __result)
    {
        if (!Plugin.Enabled.Value || !Plugin.ControllerKeyTags.Value) return;
        if (Gamepad.current == null || !ControllerDetect.PadActive) return;
        var mm = MenuManager.instance;
        if (mm != null && MenuStateRef(mm) == (int)MenuManager.MenuState.Open) return; // keybind menu keeps keyboard names

        if (_keyType == MenuKeybind.KeyType.MovementKey)
        {
            // ReplaceTags concatenates FOUR direction calls for [move]; emit the stick once.
            __result = _movementDirection == MovementDirection.Up ? "L-STICK" : "";
            return;
        }
        string name = NameFor(_inputKey, ControllerDetect.Current);
        if (name != null) __result = name.ToUpper();
    }

    // Our actual gamepad bindings (GamepadBindingsPatch + PushPullPatch + EmoteWheel).
    // null = not bound on the pad -> keep the keyboard name.
    private static string NameFor(InputKey key, ControllerDetect.Kind kind)
    {
        switch (key)
        {
            case InputKey.Jump: return B(ButtonNames.Control.South, kind);
            case InputKey.Interact: return B(ButtonNames.Control.West, kind);
            case InputKey.Tumble: return B(ButtonNames.Control.East, kind);
            case InputKey.Map: return B(ButtonNames.Control.North, kind);
            case InputKey.Sprint: return "L3";
            case InputKey.Crouch: return "R3";
            case InputKey.Grab: return B(ButtonNames.Control.RT, kind);
            case InputKey.Rotate: return B(ButtonNames.Control.LT, kind);
            case InputKey.Menu: return B(ButtonNames.Control.Start, kind);
            case InputKey.Chat: return B(ButtonNames.Control.Select, kind);
            case InputKey.Inventory1: return B(ButtonNames.Control.DpadLeft, kind);
            case InputKey.Inventory2: return B(ButtonNames.Control.DpadUp, kind);
            case InputKey.Inventory3: return B(ButtonNames.Control.DpadRight, kind);
            case InputKey.Push: return B(ButtonNames.Control.RB, kind);
            case InputKey.Pull: return B(ButtonNames.Control.LB, kind);
            case InputKey.Expression1:
            case InputKey.Expression2:
            case InputKey.Expression3:
            case InputKey.Expression4:
            case InputKey.Expression5:
            case InputKey.Expression6:
                return B(ButtonNames.Control.DpadDown, kind) + " (wheel)";
            default:
                return null;
        }
    }

    // Vanilla key text renders through TextMeshPro, whose font may lack the PS face symbols
    // (✕○□△) and arrow glyphs — use TMP-safe text there. Our own IMGUI overlays keep using
    // ButtonNames symbols (the OS symbol font renders them fine).
    private static string B(ButtonNames.Control c, ControllerDetect.Kind kind)
    {
        if (kind == ControllerDetect.Kind.PlayStation)
        {
            switch (c)
            {
                case ButtonNames.Control.South: return "Cross";
                case ButtonNames.Control.East: return "Circle";
                case ButtonNames.Control.West: return "Square";
                case ButtonNames.Control.North: return "Triangle";
            }
        }
        switch (c)
        {
            case ButtonNames.Control.DpadLeft: return "D-Pad Left";
            case ButtonNames.Control.DpadUp: return "D-Pad Up";
            case ButtonNames.Control.DpadRight: return "D-Pad Right";
            case ButtonNames.Control.DpadDown: return "D-Pad Down";
        }
        return ButtonNames.Of(c, kind);
    }

    // Ticked per-frame from GrabPromptOverlay.Update: when the active input flips
    // (pad <-> mouse/keyboard), clear every ItemAttributes cached tag so the next
    // ShowingInfo rebuilds the tooltip with the new input's name.
    internal static void TickCacheInvalidation()
    {
        if (!Plugin.Enabled.Value || !Plugin.ControllerKeyTags.Value) return;
        bool active = ControllerDetect.PadActive && Gamepad.current != null;
        if (active == _lastControllerActive) return;
        _lastControllerActive = active;
        try
        {
            foreach (var item in UnityEngine.Object.FindObjectsOfType<ItemAttributes>())
            {
                ItemTagRef(item) = "";
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"[Prompts] item tag cache clear failed: {e.Message}");
        }
    }
}
