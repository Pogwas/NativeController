using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NativeController;

// Shared on-screen keyboard engine used by ChatKeyboard (chat/TTS) and MenuKeyboard (menu
// text fields): grid model, cursor + key-repeat navigation, A/X/Start dispatch, IMGUI
// rendering, glyph hint row. Pure input + pixels -- never touches game state. The owning
// front-end wires OnChar/OnConfirm and keeps all open/close logic. B and Select are
// intentionally NOT handled here: backspace is vanilla's ChatDelete binding in chat vs a
// direct front-end handler in menus, and Select close semantics are front-end-specific
// (chat's close-on-release arming; menu's dismiss latch).
internal class PadKeyboardCore
{
    private const float RepeatDelay = 0.4f; // held-direction initial delay (standard OSK feel)
    private const float RepeatRate = 0.12f; // held-direction repeat interval

    // Rows 0-3 are one char per key (displayed as-is, typed lowercase); row 4 is the special row.
    private static readonly string[] CharRows = { "1234567890", "QWERTYUIOP", "ASDFGHJKL'", "ZXCVBNM,.?" };
    private const int SpecialRow = 4;

    // Per-key label cache (OnGUI runs 2+ times per frame -- no per-event allocations).
    private static readonly string[][] KeyLabels = BuildKeyLabels();

    internal Action<string> OnChar;  // "a".."z", "0".."9", "'", ",", ".", "?", "!", " "
    internal Action OnConfirm;       // the SEND/ENTER grid key and the Start button
    internal Action OnClose;         // the optional HIDE grid key (front-ends with hasHide)

    private readonly bool _hasSpace;
    private readonly string[] _specialKeys; // "!" + optional SPACE + optional hide key + confirmLabel
    private readonly string _hideLabel;     // null = no hide key on the grid
    private readonly Func<ControllerDetect.Kind, string> _extraHint; // null = no extra hint

    private int _row, _col;                 // cursor
    private int _heldX, _heldY;             // current held nav direction
    private float _repeatTimer;
    private GUIStyle _key, _keyHover, _hint;
    private float _styleScale = -1f;        // rebuild styles when the Scale config changes
    private string _hints;                  // cached hint row text
    private ControllerDetect.Kind _hintsKind;
    private bool _hintsBuilt;

    internal float LastPanelTop { get; private set; } // screen-space y of the panel's top edge, set each Draw

    internal PadKeyboardCore(bool hasSpace, string confirmLabel, string hideLabel = null,
                             Func<ControllerDetect.Kind, string> extraHint = null)
    {
        _hasSpace = hasSpace;
        _hideLabel = hideLabel;
        var keys = new System.Collections.Generic.List<string> { "!" };
        if (hasSpace) keys.Add("SPACE");
        if (hideLabel != null) keys.Add(hideLabel); // navigable close key (playtest: the hint row alone wasn't discoverable)
        keys.Add(confirmLabel);
        _specialKeys = keys.ToArray();
        _extraHint = extraHint;
    }

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

    internal void Reset()
    {
        _row = 1; _col = 0; // start on Q
        _heldX = 0; _heldY = 0; _repeatTimer = 0f;
        _hintsBuilt = false;
    }

    // Call from the front-end's Update while its panel is open.
    internal void HandleInput(Gamepad gp)
    {
        HandleNavigation(gp);
        if (gp.buttonSouth.wasPressedThisFrame) PressHovered();                  // A: type hovered key
        if (_hasSpace && gp.buttonWest.wasPressedThisFrame) OnChar?.Invoke(" "); // X: space
        if (gp.startButton.wasPressedThisFrame) OnConfirm?.Invoke();             // Start: send/confirm
    }

    private void PressHovered()
    {
        if (_row == SpecialRow)
        {
            string k = _specialKeys[_col];
            if (k == "SPACE") OnChar?.Invoke(" ");
            else if (k == "!") OnChar?.Invoke("!");
            else if (_hideLabel != null && k == _hideLabel) OnClose?.Invoke();
            else OnConfirm?.Invoke();
        }
        else
        {
            OnChar?.Invoke(char.ToLowerInvariant(CharRows[_row][_col]).ToString());
        }
    }

    // D-pad and left stick both move the highlight: first press steps immediately,
    // holding repeats after RepeatDelay at RepeatRate.
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
            // Crossing into/out of the short special row: keep the cursor's horizontal
            // position proportionally (e.g. col 9 of 10 lands on the confirm key, col 0 on '!').
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

    private int ColsIn(int row) => row == SpecialRow ? _specialKeys.Length : CharRows[row].Length;

    // Call from the front-end's OnGUI while its panel is open.
    internal void Draw(float s)
    {
        EnsureStyles(s);

        float keyW = 34f * s, gap = 4f * s, pad = 10f * s;
        float gridW = 10 * keyW + 9 * gap;
        float panelW = gridW + 2 * pad;
        float hintH = 22f * s;
        float panelH = 5 * (keyW + gap) - gap + 2 * pad + hintH;
        float x0 = (Screen.width - panelW) / 2f;
        // Sit just above the bottom edge; chat text / menu fields render higher up the screen.
        float y0 = Screen.height - panelH - 40f * s;
        LastPanelTop = y0;

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

        // Special row: "!" small + the remaining keys splitting the rest of the grid width
        // evenly (2-4 keys depending on hasSpace/hasHide; chat's 3-key layout is unchanged).
        float yS = y0 + pad + SpecialRow * (keyW + gap);
        int nKeys = _specialKeys.Length;
        float wide = (gridW - keyW - (nKeys - 1) * gap) / (nKeys - 1);
        DrawKey(new Rect(x0 + pad, yS, keyW, keyW), "!", _row == SpecialRow && _col == 0);
        for (int i = 1; i < nKeys; i++)
        {
            DrawKey(new Rect(x0 + pad + keyW + gap + (i - 1) * (wide + gap), yS, wide, keyW),
                    _specialKeys[i], _row == SpecialRow && _col == i);
        }

        var kind = ControllerDetect.Current;
        if (!_hintsBuilt || kind != _hintsKind)
        {
            _hintsBuilt = true;
            _hintsKind = kind;
            string extra = _extraHint?.Invoke(kind);
            // Start/Select still work (send/close) but get no hint line: the grid's own
            // SEND/CLOSE keys already show those verbs (user feedback 2026-06-12 -- the
            // extra words overflowed the row).
            _hints =
                ButtonNames.Of(ButtonNames.Control.South, kind) + " type    " +
                ButtonNames.Of(ButtonNames.Control.East, kind) + " backspace    " +
                (_hasSpace ? ButtonNames.Of(ButtonNames.Control.West, kind) + " space" : "") +
                (string.IsNullOrEmpty(extra) ? "" : "    " + extra);
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
