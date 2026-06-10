using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NativeController;

// On-screen chat keyboard: when the PAD opens vanilla chat (Select), draw a compact QWERTY
// grid bottom-center. D-pad / left stick move the highlight, A types the hovered key into
// vanilla's live message (ChatManager.AddLetterToChat), X = space, Start = send
// (ForceConfirmChat -> vanilla network sync + TTS), Select = close. B = backspace via the
// ChatDelete gamepad binding (GamepadBindingsPatch) -- vanilla consumes it natively, one
// char per press like vanilla's keyboard backspace.
// Vanilla owns ALL message state; this class is purely an input device + panel renderer.
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
    private const float RepeatDelay = 0.4f; // held-direction initial delay (standard OSK feel)
    private const float RepeatRate = 0.12f; // held-direction repeat interval
    private const float PadOpenWindow = 0.25f; // Select-press recency that counts as "pad opened chat"

    // Rows 0-3 are one char per key (displayed as-is, typed lowercase); row 4 is ! / SPACE / SEND.
    private static readonly string[] CharRows = { "1234567890", "QWERTYUIOP", "ASDFGHJKL'", "ZXCVBNM,.?" };
    private const int SpecialRow = 4;
    private static readonly string[] SpecialKeys = { "!", "SPACE", "SEND" };

    private int _row, _col;                 // cursor
    private bool _selectArmed;              // ignore the Select press that opened chat
    private float _lastSelectPress = -10f;  // pad-open detection (Update-order safe)
    private bool _prevChatActive;
    private int _heldX, _heldY;             // current held nav direction
    private float _repeatTimer;
    private GUIStyle _key, _keyHover, _hint;
    private float _styleScale = -1f;        // rebuild styles when the Scale config changes
    private static bool _warned;

    // Per-key label cache (OnGUI runs 2+ times per frame -- no per-event allocations).
    private static readonly string[][] KeyLabels = BuildKeyLabels();
    private string _hints;                       // cached hint row text
    private ControllerDetect.Kind _hintsKind;    // rebuilt when the glyph kind changes
    private bool _hintsBuilt;

    private static string[][] BuildKeyLabels()
    {
        var labels = new string[CharRows.Length][];
        for (int r = 0; r < CharRows.Length; r++)
        {
            labels[r] = new string[CharRows[r].Length];
            for (int c = 0; c < CharRows[r].Length; c++) labels[r][c] = CharRows[r][c].ToString();
        }
        return labels;
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
                _row = 1; _col = 0; // start on Q
                _selectArmed = false;
                _heldX = 0; _heldY = 0; _repeatTimer = 0f;
            }
        }
        if (!Open || gp == null) return;

        // Select-to-close arms only once the opening press is fully released (the
        // release frame itself must not arm, or the opening press's own release
        // would close immediately).
        if (!gp.selectButton.isPressed && !gp.selectButton.wasReleasedThisFrame) _selectArmed = true;

        try
        {
            HandleNavigation(gp);
            HandleButtons(chat, gp);
        }
        catch (Exception e) { WarnOnce(e); }
    }

    // D-pad and left stick both move the highlight: first press steps immediately,
    // holding repeats after RepeatDelay at RepeatRate. Both inputs are vanilla-gated
    // during chat (InputDisableMovement), so nothing leaks into gameplay.
    private void HandleNavigation(Gamepad gp)
    {
        Vector2 stick = gp.leftStick.ReadValue();
        float dz = Mathf.Max(Plugin.StickDeadzone.Value, 0.35f);
        int sx = Mathf.Abs(stick.x) > dz ? Math.Sign(stick.x) : 0;
        int sy = Mathf.Abs(stick.y) > dz ? -Math.Sign(stick.y) : 0; // stick up = previous row
        int dx = (gp.dpad.left.isPressed ? -1 : 0) + (gp.dpad.right.isPressed ? 1 : 0);
        int dy = (gp.dpad.up.isPressed ? -1 : 0) + (gp.dpad.down.isPressed ? 1 : 0);
        if (dx == 0) dx = sx;
        if (dy == 0) dy = sy;

        if (dx == 0 && dy == 0)
        {
            _heldX = 0; _heldY = 0;
            return;
        }
        if (dx != _heldX || dy != _heldY)
        {
            _heldX = dx; _heldY = dy;
            _repeatTimer = RepeatDelay;
            MoveCursor(dx, dy);
            return;
        }
        _repeatTimer -= Time.unscaledDeltaTime;
        if (_repeatTimer <= 0f)
        {
            _repeatTimer = RepeatRate;
            MoveCursor(dx, dy);
        }
    }

    private void MoveCursor(int dx, int dy)
    {
        const int rows = SpecialRow + 1;
        if (dy != 0)
        {
            // Crossing into/out of the 3-key special row: keep the cursor's horizontal
            // position proportionally (e.g. col 9 of 10 lands on SEND, col 0 on '!').
            int oldCols = ColsIn(_row);
            float frac = oldCols > 1 ? (float)_col / (oldCols - 1) : 0f;
            _row = (_row + dy + rows) % rows;
            int newCols = ColsIn(_row);
            _col = Mathf.Clamp(Mathf.RoundToInt(frac * (newCols - 1)), 0, newCols - 1);
        }
        if (dx != 0)
        {
            int cols = ColsIn(_row);
            _col = (_col + dx + cols) % cols;
        }
    }

    private static int ColsIn(int row) => row == SpecialRow ? SpecialKeys.Length : CharRows[row].Length;

    private void HandleButtons(ChatManager chat, Gamepad gp)
    {
        if (gp.buttonSouth.wasPressedThisFrame) // A: type the hovered key
        {
            if (_row == SpecialRow)
            {
                if (_col == 1) TypeChar(chat, " ");
                else if (_col == 2) SendOrClose(chat);
                else TypeChar(chat, "!");
            }
            else
            {
                TypeChar(chat, char.ToLowerInvariant(CharRows[_row][_col]).ToString());
            }
        }
        if (gp.buttonWest.wasPressedThisFrame) TypeChar(chat, " ");      // X: space
        if (gp.startButton.wasPressedThisFrame) SendOrClose(chat);       // Start: send / close-if-empty
        // Select: close on RELEASE, not press -- Select is also InputKey.Chat, and a
        // press is visible to vanilla's StateInactive for the whole frame (either
        // Update order), which would instantly REOPEN the chat we just closed.
        if (_selectArmed && gp.selectButton.wasReleasedThisFrame) Cancel(chat);
        // B (backspace): vanilla consumes the ChatDelete binding in StateActive -- nothing here.
    }

    private void TypeChar(ChatManager chat, string s)
    {
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
    private void SendOrClose(ChatManager chat)
    {
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
    private void Cancel(ChatManager chat)
    {
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
        float s = Plugin.ChatKeyboardScale.Value;
        EnsureStyles(s);

        float keyW = 34f * s, gap = 4f * s, pad = 10f * s;
        float gridW = 10 * keyW + 9 * gap;
        float panelW = gridW + 2 * pad;
        float hintH = 22f * s;
        float panelH = 5 * (keyW + gap) - gap + 2 * pad + hintH;
        float x0 = (Screen.width - panelW) / 2f;
        // Sit just above the bottom edge; vanilla's chat text renders higher up the screen.
        float y0 = Screen.height - panelH - 40f * s;

        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(new Rect(x0, y0, panelW, panelH), Texture2D.whiteTexture);
        GUI.color = Color.white;

        for (int r = 0; r < SpecialRow; r++)
        {
            for (int c = 0; c < CharRows[r].Length; c++)
            {
                DrawKey(new Rect(x0 + pad + c * (keyW + gap), y0 + pad + r * (keyW + gap), keyW, keyW),
                        KeyLabels[r][c], r == _row && c == _col);
            }
        }

        // Special row: ! | SPACE (wide) | SEND (wide) -- the two wide keys split the rest of the grid width.
        float yS = y0 + pad + SpecialRow * (keyW + gap);
        float wide = (gridW - keyW - 2 * gap) / 2f;
        DrawKey(new Rect(x0 + pad, yS, keyW, keyW), "!", _row == SpecialRow && _col == 0);
        DrawKey(new Rect(x0 + pad + keyW + gap, yS, wide, keyW), "SPACE", _row == SpecialRow && _col == 1);
        DrawKey(new Rect(x0 + pad + keyW + gap + wide + gap, yS, wide, keyW), "SEND", _row == SpecialRow && _col == 2);

        var kind = ControllerDetect.Current;
        if (!_hintsBuilt || kind != _hintsKind)
        {
            _hintsBuilt = true;
            _hintsKind = kind;
            _hints =
                ButtonNames.Of(ButtonNames.Control.South, kind) + " type    " +
                ButtonNames.Of(ButtonNames.Control.East, kind) + " backspace    " +
                ButtonNames.Of(ButtonNames.Control.West, kind) + " space    " +
                ButtonNames.Of(ButtonNames.Control.Start, kind) + " send    " +
                ButtonNames.Of(ButtonNames.Control.Select, kind) + " close";
        }
        GUI.Label(new Rect(x0, yS + keyW + gap, panelW, hintH), _hints, _hint);
    }

    private void DrawKey(Rect rect, string label, bool hovered)
    {
        GUI.color = hovered
            ? new Color(0.30f, 0.26f, 0.08f, 0.95f)   // hovered: dark gold chip (matches EmoteWheel)
            : new Color(0.10f, 0.10f, 0.10f, 0.90f);  // normal: dark chip
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(rect, label, hovered ? _keyHover : _key);
    }

    private void EnsureStyles(float scale)
    {
        if (_key != null && Mathf.Approximately(_styleScale, scale)) return;
        _styleScale = scale;
        _key = new GUIStyle(GUI.skin.label) { fontSize = (int)(15 * scale), alignment = TextAnchor.MiddleCenter };
        _key.normal.textColor = Color.white;
        _keyHover = new GUIStyle(_key) { fontStyle = FontStyle.Bold };
        _keyHover.normal.textColor = new Color(1f, 0.85f, 0.3f); // gold (matches EmoteWheel hover)
        _hint = new GUIStyle(GUI.skin.label) { fontSize = (int)(12 * scale), alignment = TextAnchor.MiddleCenter };
        _hint.normal.textColor = new Color(0.75f, 0.77f, 0.85f);
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
