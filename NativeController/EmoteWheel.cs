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
    private const float PickLingerSeconds = 2f; // keep the face preview centered after a pick

    // Wheel-toggled expression indices (1-based). Static so Plugin.ResetState can clear
    // on scene load even though the GameObject survives scene changes.
    private static readonly HashSet<int> Active = new HashSet<int>();
    private static string[] _labels; // [1..6]; rebuilt per scene
    private static bool _warned;

    private int _hovered; // 0 = none, 1..6 = segment (== expression index)
    private static float _centerLinger;   // >0: preview keeps the wheel framing (post-pick feedback)
    private static Transform _previewCam; // camera filming the mini expression avatar
    private static bool _camMoved;        // camera is currently reframed by us
    private static Vector3 _camHomeLocal; // its cached vanilla localPosition
    private GUIStyle _title, _label, _labelHover, _labelActive;

    // Called by Plugin.OnSceneLoaded: forget toggled faces + refresh label cache.
    internal static void ResetState()
    {
        Active.Clear();
        _labels = null;
        Open = false;
        _centerLinger = 0f;
        _previewCam = null; // rig is freshly created per scene
        _camMoved = false;  // never restore stale coords across scenes
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

    // Recenter the game's face-preview widget (PlayerExpressionsUI) into the middle of the wheel
    // via the game's OWN repositioning seam. Screen-pixel / RectTransformUtility math does NOT
    // work here: REPO's HUD is a world-space 712x400-unit canvas filmed by an orthographic
    // camera into a RenderTexture (the retro look), so screen coordinates are ~2.7x too large
    // and there is no screen-facing camera to convert with. Vanilla moves SemiUI widgets with
    // SemiUIScoot(offset) — an additive canvas-local offset, re-armed every frame (0.2s
    // keep-alive), that eases back to the authored home automatically once we stop calling it.
    // While the wheel is open the widget is also force-shown (live mirror); after a pick it
    // lingers centered briefly so the face change is visible at the wheel, not half-clipped
    // down by the inventory.
    private void LateUpdate()
    {
        bool overridePos = Open || _centerLinger > 0f;
        if (_centerLinger > 0f) _centerLinger -= Time.deltaTime;
        if (!overridePos)
        {
            RestoreCamera();
            return;
        }

        var ui = PlayerExpressionsUI.instance;
        if (ui == null) return;
        // No force-Show here: the preview appears only when vanilla shows it (while a face
        // is active) — user preference 2026-06-09. We only adjust the camera framing so the
        // face is visible when it does pop up after a pick.

        // The preview stays at its vanilla spot (UI repositioning fought the prefab layout —
        // the widget is *designed* to peek over the screen's bottom edge). Instead, reframe
        // the dedicated camera that films the mini expression avatar (parked at world
        // (0,0,-1000), PlayerAvatarMenu.cs:51-54): dolly it back and drop it slightly so the
        // whole face sits in the upper part of the frame — the part that's actually on screen.
        // Both values are live-tunable configs. Applied fresh from the cached home every
        // frame, restored when the wheel closes and the linger ends.
        var cam = FindPreviewCamera(ui);
        if (cam == null) return;
        if (!_camMoved)
        {
            _camHomeLocal = cam.localPosition;
            _camMoved = true;
        }
        cam.localPosition = _camHomeLocal;
        cam.position += cam.forward * -Plugin.EmoteZoomOut.Value + cam.up * -Plugin.EmoteCameraLower.Value;
    }

    private static Transform FindPreviewCamera(PlayerExpressionsUI ui)
    {
        if (_previewCam != null) return _previewCam;
        if (ui.PlayerAvatarMenu == null) return null;
        var rig = ui.PlayerAvatarMenu.GetComponent<PlayerAvatarMenu>();
        if (rig == null || rig.cameraAndStuff == null) return null;
        var cam = rig.cameraAndStuff.GetComponentInChildren<Camera>(true);
        if (cam == null) return null;
        _previewCam = cam.transform;
        return _previewCam;
    }

    // Put the preview camera back to its vanilla framing once the override ends.
    private static void RestoreCamera()
    {
        if (!_camMoved) return;
        _camMoved = false;
        if (_previewCam != null)
        {
            _previewCam.localPosition = _camHomeLocal;
        }
    }

    private static void DriveActiveExpressions()
    {
        if (Active.Count == 0) return;
        var avatar = PlayerAvatar.instance;
        if (avatar == null || avatar.playerExpression == null) return;
        // The HUD face preview is a SEPARATE miniature avatar with its own PlayerExpression —
        // in vanilla, both components independently poll the keyboard keys, so driving only
        // the real avatar leaves the preview's face frozen. Drive the preview via its public
        // OverrideExpressionSet, the same way the game's Boombox does (ValuableBoombox.cs:97).
        var previewExpression = PlayerExpressionsUI.instance != null
            ? PlayerExpressionsUI.instance.playerExpression
            : null;
        try
        {
            foreach (int index in Active)
            {
                DoExpression(avatar.playerExpression, index, 100f, false);
                if (previewExpression != null)
                {
                    previewExpression.OverrideExpressionSet(index, 100f);
                }
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
            _centerLinger = PickLingerSeconds; // show the face change centered, not at the clipped vanilla spot
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
