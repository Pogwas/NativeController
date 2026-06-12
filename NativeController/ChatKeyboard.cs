using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NativeController;

// On-screen chat keyboard front-end: when the PAD opens vanilla chat (Select), show the
// shared OSK panel (PadKeyboardCore) over vanilla's live message. A types the hovered key
// into vanilla (ChatManager.AddLetterToChat), X = space, Start = send (ForceConfirmChat ->
// vanilla network sync + TTS), Select = close. B = backspace via the ChatDelete gamepad
// binding (GamepadBindingsPatch) -- vanilla consumes it natively, one char per press like
// vanilla's keyboard backspace.
// Vanilla owns ALL message state; this class is purely an input device + panel owner.
// While chat is active, vanilla's own InputDisableMovement gate already swallows the
// A/B/X/d-pad gameplay actions (InputManager.cs:358) and GameDirector blocks the Esc menu
// (GameDirector.cs:320); the only leak is Map on Y -- suppressed by ChatKeyboardMapGuard
// below. Chat opened from the physical keyboard never shows this panel.
// Recreated per scene by Plugin (REPO wipes DontDestroyOnLoad at boot).
internal class ChatKeyboard : MonoBehaviour
{
    private static readonly AccessTools.FieldRef<ChatManager, bool> ChatActiveRef =
        AccessTools.FieldRefAccess<ChatManager, bool>("chatActive");
    private static readonly AccessTools.FieldRef<ChatManager, string> ChatMessageRef =
        AccessTools.FieldRefAccess<ChatManager, string>("chatMessage");
    // ChatState is a public nested enum; StateSet is private -> open instance delegate.
    private static readonly Action<ChatManager, ChatManager.ChatState> StateSet =
        AccessTools.MethodDelegate<Action<ChatManager, ChatManager.ChatState>>(
            AccessTools.Method(typeof(ChatManager), "StateSet"));

    internal static bool Open; // read by ChatKeyboardMapGuard

    private const int MaxChatLength = 50;   // vanilla cap (ChatManager.cs:428)
    private const float PadOpenWindow = 0.25f; // Select-press recency that counts as "pad opened chat"

    private readonly PadKeyboardCore _core = new PadKeyboardCore(
        hasSpace: true, confirmLabel: "SEND", confirmVerb: "send", closeVerb: "close",
        hideLabel: "CLOSE"); // navigable close key (playtest 2026-06-11: Select-to-close alone wasn't discoverable)

    private bool _selectArmed;              // ignore the Select press that opened chat
    private float _lastSelectPress = -10f;  // pad-open detection (Update-order safe)
    private bool _prevChatActive;
    private static bool _warned;

    private void Awake()
    {
        _core.OnChar = TypeChar;
        _core.OnConfirm = SendOrClose;
        _core.OnClose = Cancel; // CLOSE grid key = the pad's Esc, same as Select-release
    }

    // Called by Plugin.OnSceneLoaded.
    internal static void ResetState()
    {
        Open = false;
    }

    private void Update()
    {
        var chat = ChatManager.instance;
        var gp = Gamepad.current;
        if (chat == null || !Plugin.Enabled.Value || !Plugin.ChatKeyboardEnabled.Value)
        {
            Open = false;
            return;
        }

        bool chatActive;
        try { chatActive = ChatActiveRef(chat); }
        catch (Exception e) { WarnOnce(e); Open = false; return; }

        // Remember recent Select presses BEFORE the edge check: ChatManager may flip
        // chatActive in the same frame as the press, in either Update order.
        if (gp != null && gp.selectButton.wasPressedThisFrame)
        {
            _lastSelectPress = Time.unscaledTime;
        }

        if (!chatActive)
        {
            Open = false;
            _prevChatActive = false;
            return;
        }

        if (!_prevChatActive)
        {
            _prevChatActive = true;
            // Pad-opened only: chat became active right after a Select press.
            // Keyboard-opened chat (T) shows nothing.
            if (gp != null && Time.unscaledTime - _lastSelectPress < PadOpenWindow)
            {
                Open = true;
                _core.Reset();
                _selectArmed = false;
            }
        }
        if (!Open || gp == null) return;

        // Select-to-close arms only once the opening press is fully released (the
        // release frame itself must not arm, or the opening press's own release
        // would close immediately).
        if (!gp.selectButton.isPressed && !gp.selectButton.wasReleasedThisFrame) _selectArmed = true;

        try
        {
            _core.HandleInput(gp);
            // Select: close on RELEASE, not press -- Select is also InputKey.Chat, and a
            // press is visible to vanilla's StateInactive for the whole frame (either
            // Update order), which would instantly REOPEN the chat we just closed.
            if (_selectArmed && gp.selectButton.wasReleasedThisFrame) Cancel();
            // B (backspace): vanilla consumes the ChatDelete binding in StateActive -- nothing here.
        }
        catch (Exception e) { WarnOnce(e); }
    }

    private void TypeChar(string s)
    {
        var chat = ChatManager.instance;
        if (chat == null) return;
        string msg = ChatMessageRef(chat) ?? "";
        if (msg.Length >= MaxChatLength)
        {
            // Vanilla's own at-cap deny feedback (ChatManager.cs:430-433).
            ChatUI.instance.SemiUITextFlashColor(Color.red, 0.2f);
            ChatUI.instance.SemiUISpringShakeX(10f, 10f, 0.3f);
            ChatUI.instance.SemiUISpringScale(0.05f, 5f, 0.2f);
            MenuManager.instance.MenuEffectClick(MenuManager.MenuClickEffectType.Deny, null, 1f, 1f, soundOnly: true);
            return;
        }
        chat.AddLetterToChat(s);
        // Vanilla's per-keystroke TypeEffect is private -- mirror its three public calls
        // (ChatManager.cs:323-328) so pad typing feels identical to keyboard typing.
        ChatUI.instance.SemiUITextFlashColor(Color.yellow, 0.2f);
        ChatUI.instance.SemiUISpringShakeY(2f, 5f, 0.2f);
        MenuManager.instance.MenuEffectClick(MenuManager.MenuClickEffectType.Tick, null, 2f, 0.2f, soundOnly: true);
    }

    // Mirror vanilla Confirm semantics (ChatManager.cs:460-468): non-empty -> Send;
    // empty -> close SILENTLY (the Deny shake/sound is vanilla's Esc path, not Confirm's).
    private void SendOrClose()
    {
        var chat = ChatManager.instance;
        if (chat == null) return;
        string msg = ChatMessageRef(chat) ?? "";
        if (msg != "")
        {
            chat.ForceConfirmChat();
        }
        else
        {
            StateSet(chat, ChatManager.ChatState.Inactive);
            Open = false;
        }
    }

    // Pad equivalent of vanilla's Esc/Back cancel, including its feedback (ChatManager.cs:368-374).
    private void Cancel()
    {
        var chat = ChatManager.instance;
        if (chat == null) return;
        StateSet(chat, ChatManager.ChatState.Inactive);
        ChatUI.instance.SemiUISpringShakeX(10f, 10f, 0.3f);
        ChatUI.instance.SemiUISpringScale(0.05f, 5f, 0.2f);
        MenuManager.instance.MenuEffectClick(MenuManager.MenuClickEffectType.Deny, null, 1f, 1f, soundOnly: true);
        Open = false; // don't wait a frame for the chatActive watch
    }

    private static void WarnOnce(Exception e)
    {
        if (_warned) return;
        _warned = true;
        Plugin.Log.LogWarning($"[ChatKeyboard] Error (further warnings suppressed): {e.Message}");
    }

    private void OnGUI()
    {
        if (!Open) return;
        _core.Draw(Plugin.ChatKeyboardScale.Value);
    }
}

// While the OSK is open, swallow Map (Y) -- the one pad action vanilla's chat
// movement-disable gate does not cover (InputManager.cs:358 list has no Map).
// Class-level [HarmonyPatch] required for PatchAll to see the per-method attributes
// (multi-method patch class, runtime quirk 7).
[HarmonyPatch]
internal static class ChatKeyboardMapGuard
{
    [HarmonyPatch(typeof(InputManager), nameof(InputManager.KeyDown))]
    [HarmonyPostfix]
    private static void KeyDownPostfix(InputKey key, ref bool __result)
    {
        if (__result && key == InputKey.Map && ChatKeyboard.Open) __result = false;
    }

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.KeyHold))]
    [HarmonyPostfix]
    private static void KeyHoldPostfix(InputKey key, ref bool __result)
    {
        if (__result && key == InputKey.Map && ChatKeyboard.Open) __result = false;
    }
}
