using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NativeController;

// Emote wheel: hold D-pad Down in gameplay -> radial wheel of the game's 6 expressions
// (vanilla keyboard keys 5-0). Right stick hovers a segment; releasing D-pad Down toggles
// that expression on/off via the public RPC-synced PlayerAvatar expression API (vanilla
// toggle semantics). Neutral-stick release = no change (wheel convention: center = cancel).
// Recreated per scene by Plugin (REPO wipes DontDestroyOnLoad at boot); Plugin also calls
// ResetState() on scene load so no stale faces carry across levels.
internal class EmoteWheel : MonoBehaviour
{
    private static readonly AccessTools.FieldRef<MenuManager, int> MenuStateRef =
        AccessTools.FieldRefAccess<MenuManager, int>("currentMenuState");
    private static readonly AccessTools.FieldRef<ChatManager, bool> ChatActiveRef =
        AccessTools.FieldRefAccess<ChatManager, bool>("chatActive");

    // Vanilla's local-side expression driver. The public PlayerAvatar.PlayerExpressionSet API only
    // feeds the REMOTE-avatar dict (PlayerExpression.Update reads it in the !isLocal branch), so it
    // is invisible to yourself — especially in solo. DoExpression is the path the keyboard keys use:
    // it animates the local face, pops the on-screen face-preview UI, and syncs to MP internally.
    // Signature: DoExpression(int _index, float _percent, bool _playerInput = false)
    private static readonly Action<PlayerExpression, int, float, bool> DoExpression =
        AccessTools.MethodDelegate<Action<PlayerExpression, int, float, bool>>(
            AccessTools.Method(typeof(PlayerExpression), "DoExpression"));

    internal static bool Open; // read by LookPatch to suppress stick look + aim assist

    private const int SegmentCount = 6;
    private const float MinDeflection = 0.35f; // selection threshold floor (drift guard)

    // Wheel-toggled expression indices (1-based). Static so Plugin.ResetState can clear
    // on scene load even though the GameObject survives scene changes.
    private static readonly HashSet<int> Active = new HashSet<int>();
    private static string[] _labels; // [1..6]; rebuilt per scene
    private static bool _warned;

    private int _hovered; // 0 = none, 1..6 = segment (== expression index)
    private GUIStyle _title, _label, _labelHover, _labelActive;

    // Called by Plugin.OnSceneLoaded: forget toggled faces + refresh label cache.
    internal static void ResetState()
    {
        Active.Clear();
        _labels = null;
        Open = false;
    }

    private void Update()
    {
        if (!Plugin.Enabled.Value || !Plugin.EmoteWheelEnabled.Value) { Close(); return; }
        var gp = Gamepad.current;
        if (gp == null || !InGameplay()) { Close(); return; }

        if (gp.dpad.down.isPressed)
        {
            Open = true;
            _hovered = HoveredSegment(gp.rightStick.ReadValue());
        }
        else if (Open)
        {
            int pick = _hovered;
            Close();
            if (pick != 0) ToggleExpression(pick);
        }

        // Vanilla toggle semantics: actively-toggled expressions must be driven EVERY frame
        // (each DoExpression call only keeps the face alive for 0.2s; when we stop calling,
        // vanilla auto-resets the face and syncs the stop to MP). Same loop vanilla runs for
        // its own toggle list (PlayerExpression.cs:488-494).
        DriveActiveExpressions();
    }

    private static void DriveActiveExpressions()
    {
        if (Active.Count == 0) return;
        var avatar = PlayerAvatar.instance;
        if (avatar == null || avatar.playerExpression == null) return;
        try
        {
            foreach (int index in Active)
            {
                DoExpression(avatar.playerExpression, index, 100f, false);
            }
        }
        catch (Exception e)
        {
            if (!_warned)
            {
                Plugin.Log.LogWarning($"[EmoteWheel] DoExpression failed: {e.Message}");
                _warned = true;
            }
        }
    }

    private void Close()
    {
        Open = false;
        _hovered = 0;
    }

    private static bool InGameplay()
    {
        var mm = MenuManager.instance;
        if (mm != null && MenuStateRef(mm) == (int)MenuManager.MenuState.Open) return false;
        var chat = ChatManager.instance;
        if (chat != null && ChatActiveRef(chat)) return false;
        return PlayerAvatar.instance != null;
    }

    // Segment 1 at 12 o'clock, clockwise (1=up, 2=upper-right, ... 6=upper-left).
    // 0 = none (stick inside threshold). Stick up = +y; Atan2(x, y): 0deg = up, +90 = right.
    private int HoveredSegment(Vector2 stick)
    {
        float threshold = Mathf.Max(Plugin.StickDeadzone.Value, MinDeflection);
        if (stick.magnitude < threshold) return 0;
        float angle = Mathf.Atan2(stick.x, stick.y) * Mathf.Rad2Deg;
        if (angle < 0f) angle += 360f;
        return Mathf.FloorToInt(((angle + 30f) % 360f) / 60f) + 1;
    }

    private static void ToggleExpression(int index)
    {
        var avatar = PlayerAvatar.instance;
        if (avatar == null || avatar.playerExpression == null) return;
        try
        {
            if (Active.Remove(index))
            {
                // Just stop driving it — vanilla auto-resets the face 0.2s after the last
                // DoExpression call and syncs the stop to MP itself (ResetExpressions).
                Plugin.Log.LogInfo($"[EmoteWheel] Expression {index} OFF"); // TODO: downgrade to LogDebug after playtest
            }
            else
            {
                // _playerInput: true on the first call pops the face-preview UI wide open
                // (ShrinkReset), exactly like a vanilla keyboard press. Steady-state frames
                // are driven by DriveActiveExpressions with _playerInput: false.
                DoExpression(avatar.playerExpression, index, 100f, true);
                Active.Add(index);
                Plugin.Log.LogInfo($"[EmoteWheel] Expression {index} ON"); // TODO: downgrade to LogDebug after playtest
            }
        }
        catch (Exception e)
        {
            // One-time warning, not per-call spam (quiet-mode policy).
            if (!_warned)
            {
                Plugin.Log.LogWarning($"[EmoteWheel] Expression call failed: {e.Message}");
                _warned = true;
            }
        }
    }

    private void OnGUI()
    {
        if (!Open) return;
        EnsureStyles();
        EnsureLabels();

        float cx = Screen.width / 2f, cy = Screen.height / 2f, radius = 170f;

        // Dim backdrop, lighter than the cheat sheet's 0.6 so gameplay stays visible.
        GUI.color = new Color(0f, 0f, 0f, 0.35f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUI.Label(new Rect(cx - 200f, cy - radius - 70f, 400f, 30f), "Emotes", _title);

        for (int i = 1; i <= SegmentCount; i++)
        {
            float rad = (i - 1) * 60f * Mathf.Deg2Rad;
            float x = cx + Mathf.Sin(rad) * radius;
            float y = cy - Mathf.Cos(rad) * radius;
            var rect = new Rect(x - 80f, y - 20f, 160f, 40f);

            GUI.color = i == _hovered
                ? new Color(0.30f, 0.26f, 0.08f, 0.95f)   // hovered: dark gold chip
                : new Color(0.10f, 0.10f, 0.10f, 0.90f);  // normal: dark chip
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;

            GUIStyle style = i == _hovered ? _labelHover : (Active.Contains(i) ? _labelActive : _label);
            GUI.Label(rect, _labels[i], style);
        }
    }

    private static void EnsureLabels()
    {
        if (_labels != null) return;
        _labels = new string[SegmentCount + 1];
        List<ExpressionSettings> expressions = null;
        var avatar = PlayerAvatar.instance;
        if (avatar != null && avatar.playerExpression != null)
        {
            expressions = avatar.playerExpression.expressions;
        }
        for (int i = 1; i <= SegmentCount; i++)
        {
            string name = null;
            if (expressions != null && i < expressions.Count && expressions[i] != null)
            {
                name = expressions[i].expressionName;
            }
            _labels[i] = string.IsNullOrEmpty(name) ? $"Expression {i}" : name;
        }
    }

    private void EnsureStyles()
    {
        if (_title != null) return;
        _title = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        _title.normal.textColor = Color.white;
        _label = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
        _label.normal.textColor = Color.white;
        _labelHover = new GUIStyle(_label) { fontStyle = FontStyle.Bold };
        _labelHover.normal.textColor = new Color(1f, 0.85f, 0.3f);   // gold (matches overlay _key)
        _labelActive = new GUIStyle(_label) { fontStyle = FontStyle.Bold };
        _labelActive.normal.textColor = new Color(0.55f, 0.8f, 1f);  // light blue = currently active
    }
}
