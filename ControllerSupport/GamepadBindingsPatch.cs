using System;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace ControllerSupport;

// Postfix on InputManager.InitializeInputs: additively bind <Gamepad>/* controls onto the game's
// own InputActions. Keyboard/mouse bindings are left intact (purely additive).
[HarmonyPatch(typeof(InputManager), "InitializeInputs")]
internal static class GamepadBindingsPatch
{
    private static InputManager _injected; // idempotency: only inject once per InputManager instance

    [HarmonyPostfix]
    public static void Postfix(InputManager __instance)
    {
        if (!Plugin.Enabled.Value) return;
        if (ReferenceEquals(_injected, __instance)) return;
        _injected = __instance;

        try
        {
            // Sticks
            Bind(__instance, InputKey.Movement, "<Gamepad>/leftStick");
            // Face buttons (Xbox layout)
            Bind(__instance, InputKey.Jump,     "<Gamepad>/buttonSouth"); // A
            Bind(__instance, InputKey.Interact, "<Gamepad>/buttonWest");  // X
            Bind(__instance, InputKey.Tumble,   "<Gamepad>/buttonEast");  // B
            Bind(__instance, InputKey.Map,      "<Gamepad>/buttonNorth"); // Y
            // Stick clicks
            Bind(__instance, InputKey.Sprint,   "<Gamepad>/leftStickPress");  // L3
            Bind(__instance, InputKey.Crouch,   "<Gamepad>/rightStickPress"); // R3
            // Triggers
            Bind(__instance, InputKey.Grab,     "<Gamepad>/rightTrigger"); // RT
            Bind(__instance, InputKey.Rotate,   "<Gamepad>/leftTrigger");  // LT
            // Menu / chat
            Bind(__instance, InputKey.Menu,     "<Gamepad>/start");  // Start
            Bind(__instance, InputKey.Chat,     "<Gamepad>/select"); // Back/View
            // Inventory (discrete per-slot) on the D-pad
            Bind(__instance, InputKey.Inventory1, "<Gamepad>/dpad/left");  // D-pad Left
            Bind(__instance, InputKey.Inventory2, "<Gamepad>/dpad/up");    // D-pad Up
            Bind(__instance, InputKey.Inventory3, "<Gamepad>/dpad/right"); // D-pad Right
            // Push/Pull bumpers are added in Task 5.

            Plugin.Log.LogInfo("[Gamepad] Bindings injected.");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[Gamepad] Binding injection failed: {e}");
        }
    }

    // Add a gamepad binding to the action for `key`. Disables the action first because Unity
    // forbids AddBinding on an enabled action, then re-enables if it was enabled.
    internal static void Bind(InputManager im, InputKey key, string path, string processors = null)
    {
        var a = im.GetAction(key);
        if (a == null) { Plugin.Log.LogWarning($"[Gamepad] No action for {key}"); return; }
        bool wasEnabled = a.enabled;
        if (wasEnabled) a.Disable();
        if (string.IsNullOrEmpty(processors)) a.AddBinding(path);
        else a.AddBinding(path).WithProcessors(processors);
        if (wasEnabled) a.Enable();
    }
}
