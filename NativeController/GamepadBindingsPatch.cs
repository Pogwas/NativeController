using System;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace NativeController;

// User-facing pad buttons for one-off bindable configs (controller-agnostic names; South =
// A/Cross, Select = Back/View). None = unbound.
internal enum PadButton
{
    None, South, East, West, North, LB, RB, LT, RT, L3, R3,
    DpadLeft, DpadUp, DpadRight, DpadDown, Start, Select,
}

// Postfix on InputManager.InitializeInputs: additively bind <Gamepad>/* controls onto the game's
// own InputActions. Keyboard/mouse bindings are left intact (purely additive).
[HarmonyPatch]
internal static class GamepadBindingsPatch
{
    private static InputManager _injected; // idempotency: only inject once per InputManager instance
    private static string _pttPath; // the <Gamepad> path we added to PushToTalk (null = none)

    private static string PathOf(PadButton b) => b switch
    {
        PadButton.South => "<Gamepad>/buttonSouth",
        PadButton.East => "<Gamepad>/buttonEast",
        PadButton.West => "<Gamepad>/buttonWest",
        PadButton.North => "<Gamepad>/buttonNorth",
        PadButton.LB => "<Gamepad>/leftShoulder",
        PadButton.RB => "<Gamepad>/rightShoulder",
        PadButton.LT => "<Gamepad>/leftTrigger",
        PadButton.RT => "<Gamepad>/rightTrigger",
        PadButton.L3 => "<Gamepad>/leftStickPress",
        PadButton.R3 => "<Gamepad>/rightStickPress",
        PadButton.DpadLeft => "<Gamepad>/dpad/left",
        PadButton.DpadUp => "<Gamepad>/dpad/up",
        PadButton.DpadRight => "<Gamepad>/dpad/right",
        PadButton.DpadDown => "<Gamepad>/dpad/down",
        PadButton.Start => "<Gamepad>/start",
        PadButton.Select => "<Gamepad>/select",
        _ => null,
    };

    [HarmonyPatch(typeof(InputManager), "InitializeInputs")]
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
            Bind(__instance, InputKey.ChatDelete, "<Gamepad>/buttonEast"); // B = backspace while chat is open (action is only read inside chat -- inert otherwise; B-as-Tumble is vanilla-gated during chat)
            // Inventory (discrete per-slot) on the D-pad
            Bind(__instance, InputKey.Inventory1, "<Gamepad>/dpad/left");  // D-pad Left
            Bind(__instance, InputKey.Inventory2, "<Gamepad>/dpad/up");    // D-pad Up
            Bind(__instance, InputKey.Inventory3, "<Gamepad>/dpad/right"); // D-pad Right
            // Optional push-to-talk binding ([Gamepad] PushToTalkButton, default None). Additive
            // onto vanilla's real PushToTalk action — PlayerVoiceChat's InputHold sees the pad
            // button exactly like keyboard V (PlayerVoiceChat.cs:514). The chosen button keeps
            // its normal function too; that trade is the user's call.
            if (Plugin.PushToTalkButton.Value != PadButton.None)
            {
                _pttPath = PathOf(Plugin.PushToTalkButton.Value);
                Bind(__instance, InputKey.PushToTalk, _pttPath);
            }
            else
            {
                _pttPath = null;
            }
            // Push/Pull (RB/LB) can't be done with a binding: the game reads InputKey.Push/Pull as a 2D
            // scroll axis (ReadValue<Vector2>().y), which a button can't drive. Handled instead by
            // PushPullPatch (a postfix on InputManager.KeyPullAndPush).

            Plugin.Log.LogInfo("[Gamepad] Bindings injected.");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[Gamepad] Binding injection failed: {e}");
        }
    }

    // Vanilla snapshots ALL bindings by index (SaveCurrentKeyBindings, run when leaving the
    // Controls settings page) and re-applies them on boot (Start -> LoadKeyBindings ->
    // ApplyBindingOverride). If the PTT config changed between sessions, that stale snapshot
    // would silently override the fresh binding — re-assert ours after vanilla loads.
    [HarmonyPatch(typeof(InputManager), "LoadKeyBindings")]
    [HarmonyPostfix]
    public static void LoadKeyBindingsPostfix()
    {
        if (Plugin.Enabled.Value) RebindPushToTalk();
    }

    // Same family: vanilla's per-key reset re-applies its boot-time snapshot of OUR binding.
    [HarmonyPatch(typeof(InputManager), nameof(InputManager.ResetKeyToDefault))]
    [HarmonyPostfix]
    public static void ResetKeyToDefaultPostfix(InputKey key)
    {
        if (key == InputKey.PushToTalk && Plugin.Enabled.Value) RebindPushToTalk();
    }

    // Live rebind for [Gamepad] PushToTalkButton — REPOConfig dropdown changes apply without a
    // restart. Erases the previously added pad binding by path, then adds the new one. A failed
    // rebind must not break the existing bindings (worst case the old one stays until restart).
    internal static void RebindPushToTalk()
    {
        if (_injected == null) return; // pre-init change: the initial bind reads the config
        try
        {
            var a = _injected.GetAction(InputKey.PushToTalk);
            if (a == null) return;
            bool wasEnabled = a.enabled;
            if (wasEnabled) a.Disable();
            try
            {
                if (_pttPath != null)
                {
                    for (int i = 0; i < a.bindings.Count; i++)
                    {
                        if (a.bindings[i].path == _pttPath)
                        {
                            a.ChangeBinding(i).Erase();
                            break;
                        }
                    }
                    _pttPath = null;
                }
                PadButton button = Plugin.PushToTalkButton.Value;
                if (button != PadButton.None)
                {
                    _pttPath = PathOf(button);
                    a.AddBinding(_pttPath);
                }
            }
            finally
            {
                if (wasEnabled) a.Enable();
            }
            Plugin.Log.LogInfo($"[Gamepad] PushToTalk binding -> {Plugin.PushToTalkButton.Value}.");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[Gamepad] PushToTalk rebind failed: {e}");
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
