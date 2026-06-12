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
// Segment 7 = mic mute toggle (vanilla keyboard B / InputKey.ToggleMute equivalent): flips
// DataDirector.toggleMute, which vanilla RPCs + displays itself. MP-only (greyed in solo).
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

    // DataDirector.toggleMute is internal — resolved guarded so a renamed toggleMute field
    // degrades just the mute slot to a warn-once no-op. (The three field initializers above
    // stay unguarded on purpose: they are load-bearing — the wheel is dead without them —
    // while mute is an optional slot.)
    private static readonly AccessTools.FieldRef<DataDirector, bool> ToggleMuteRef;

    static EmoteWheel()
    {
        try { ToggleMuteRef = AccessTools.FieldRefAccess<DataDirector, bool>("toggleMute"); }
        catch { ToggleMuteRef = null; } // warned on first pick
    }

    internal static bool Open; // read by LookPatch to suppress stick look + aim assist

    private const int SegmentCount = 7;     // 6 expressions + the mute slot
    private const int ExpressionCount = 6;  // vanilla expressions stay segments 1..6
    private const int MuteSegment = 7;
    private const float MinDeflection = 0.35f; // selection threshold floor (drift guard)

    // Chip placement, degrees clockwise from 12 o'clock (index = segment, [0] unused):
    // the 6 emotes fan down the RIGHT half (15..165, 30 deg apart), Mute sits ALONE on the
    // LEFT at 9 o'clock (user layout 2026-06-12). At radius 190 the tightest emote pair
    // (15/45 deg) is 49px apart vertically vs the 40px chip height — no overlap.
    private static readonly float[] SegmentAngles = { 0f, 15f, 45f, 75f, 105f, 135f, 165f, 270f };

    // Wheel-toggled expressions: index (1-based) -> seconds remaining (infinity when the
    // duration config is 0 = stay until re-picked). Static so Plugin.ResetState can clear
    // on scene load even though the GameObject survives scene changes.
    private static readonly Dictionary<int, float> Active = new Dictionary<int, float>();
    private static readonly List<int> Expired = new List<int>(); // scratch, avoids alloc per frame
    private static string[] _labels; // [1..6]; rebuilt per scene
    private static bool _warned;
    private static bool _muteWarned; // mute failures warn independently of expression failures

    private int _hovered; // 0 = none, 1..6 = expression segment (== expression index), 7 = mute
    private static Transform _previewCam; // camera filming the mini expression avatar
    private static bool _camMoved;        // camera is currently reframed by us
    private static Vector3 _camHomeLocal; // its cached vanilla localPosition
    private GUIStyle _title, _label, _labelHover, _labelActive, _labelDisabled;

    // Called by Plugin.OnSceneLoaded: forget toggled faces + refresh label cache.
    internal static void ResetState()
    {
        Active.Clear();
        _labels = null;
        Open = false;
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
            // Fast picks race the stick travel: hover is sampled only while the d-pad is
            // held, so a flick that finishes on the release frame would commit nothing
            // (playtest 2026-06-12: quick re-picks of Mute felt ignored). Sample once more
            // at release and prefer the live reading; deliberate center-to-cancel still
            // works because a stick centered BEFORE release zeroes both readings.
            int pick = HoveredSegment(gp.rightStick.ReadValue());
            if (pick == 0) pick = _hovered;
            Close();
            if (pick == MuteSegment) ToggleMute();
            else if (pick != 0) ToggleExpression(pick);
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
        // Hold the adjusted framing the entire time any wheel-emote is active — restoring
        // mid-emote caused visible perspective jumps. When the last emote expires the
        // preview hides anyway, so the restore is invisible.
        bool overridePos = Open || Active.Count > 0;
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

        // Count down each emote's lifetime; back to the default face when it runs out
        // (vanilla has no auto-timeout — keyboard players just release/re-press the key,
        // but re-opening a wheel to clear a face is clunky). Duration 0 = no timeout.
        Expired.Clear();
        if (Plugin.EmoteDurationSeconds.Value > 0f)
        {
            foreach (int index in new List<int>(Active.Keys))
            {
                Active[index] -= Time.deltaTime;
                if (Active[index] <= 0f) Expired.Add(index);
            }
            foreach (int index in Expired) Active.Remove(index);
            if (Active.Count == 0) return;
        }

        try
        {
            foreach (int index in Active.Keys)
            {
                // _playerInput: true every frame is exactly what vanilla's hold path does
                // (PlayerExpression.cs:316-318) — it keeps the preview widget at full size
                // via ShrinkReset, instead of letting it decay to the small idle look
                // mid-emote (which read as the preview "changing perspective").
                DoExpression(avatar.playerExpression, index, 100f, true);
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

    // Hover = nearest chip by angular distance (no dead wedges between chips, and the
    // asymmetric layout needs no slice math). 0 = none (stick inside threshold).
    // Stick up = +y; Atan2(x, y): 0deg = up, +90 = right.
    private int HoveredSegment(Vector2 stick)
    {
        float threshold = Mathf.Max(Plugin.StickDeadzone.Value, MinDeflection);
        if (stick.magnitude < threshold) return 0;
        float angle = Mathf.Atan2(stick.x, stick.y) * Mathf.Rad2Deg;
        int best = 0;
        float bestDist = float.MaxValue;
        for (int i = 1; i <= SegmentCount; i++)
        {
            float d = Mathf.Abs(Mathf.DeltaAngle(angle, SegmentAngles[i]));
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
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
                Plugin.Log.LogDebug($"[EmoteWheel] Expression {index} OFF");
            }
            else
            {
                // A new pick replaces whatever is currently playing (user preference
                // 2026-06-09 — no stacking/queueing): stop driving the old face(s) and
                // vanilla decays them out within 0.2s while the new one fades in.
                Active.Clear();
                DoExpression(avatar.playerExpression, index, 100f, true);
                float duration = Plugin.EmoteDurationSeconds.Value;
                Active[index] = duration > 0f ? duration : float.PositiveInfinity;
                Plugin.Log.LogDebug($"[EmoteWheel] Expression {index} ON");
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

    // Flip vanilla's mic mute. The one field IS the whole feature: PlayerVoiceChat polls it
    // per frame and on change RPCs ToggleMuteRPC (OthersBuffered) + drops TransmitEnabled
    // (PlayerVoiceChat.cs:511-524); ToggleMuteUI shows the on-screen icon. Same flag the
    // keyboard B key flips (DataDirector.cs:138-141), so both inputs stay in sync.
    private static void ToggleMute()
    {
        // Solo: vanilla force-resets the flag every frame (DataDirector.cs:143-146) — no-op.
        if (!SemiFunc.IsMultiplayer()) return;
        var dd = DataDirector.instance;
        if (dd == null) return;
        if (ToggleMuteRef == null)
        {
            if (!_muteWarned)
            {
                Plugin.Log.LogWarning("[EmoteWheel] DataDirector.toggleMute not found -- mute slot disabled.");
                _muteWarned = true;
            }
            return;
        }
        try
        {
            ref bool muted = ref ToggleMuteRef(dd);
            muted = !muted;
            Plugin.Log.LogDebug($"[EmoteWheel] Mic mute {(muted ? "ON" : "OFF")}");
        }
        catch (Exception e)
        {
            if (!_muteWarned)
            {
                Plugin.Log.LogWarning($"[EmoteWheel] Mute toggle failed: {e.Message}");
                _muteWarned = true;
            }
        }
    }

    // Live mute state for the wheel label; false on any failure (label just shows "Mute Mic").
    private static bool IsMuted()
    {
        var dd = DataDirector.instance;
        if (dd == null || ToggleMuteRef == null) return false;
        try { return ToggleMuteRef(dd); } catch { return false; }
    }

    private void OnGUI()
    {
        if (!Open) return;
        EnsureStyles();
        EnsureLabels();

        float cx = Screen.width / 2f, cy = Screen.height / 2f, radius = 190f;

        // Dim backdrop, lighter than the cheat sheet's 0.6 so gameplay stays visible.
        GUI.color = new Color(0f, 0f, 0f, 0.35f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUI.Label(new Rect(cx - 200f, cy - radius - 70f, 400f, 30f), "Emotes", _title);

        for (int i = 1; i <= SegmentCount; i++)
        {
            float rad = SegmentAngles[i] * Mathf.Deg2Rad;
            float x = cx + Mathf.Sin(rad) * radius;
            float y = cy - Mathf.Cos(rad) * radius;
            var rect = new Rect(x - 80f, y - 20f, 160f, 40f);

            bool hoverable = i != MuteSegment || SemiFunc.IsMultiplayer();
            GUI.color = i == _hovered && hoverable
                ? new Color(0.30f, 0.26f, 0.08f, 0.95f)   // hovered: dark gold chip
                : new Color(0.10f, 0.10f, 0.10f, 0.90f);  // normal: dark chip
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;

            if (i == MuteSegment)
            {
                // Live label/state: mute is runtime state (and MP-only), so it can't use the
                // per-scene label cache. String literals only — no per-frame allocation.
                bool mp = SemiFunc.IsMultiplayer();
                bool muted = mp && IsMuted();
                string muteLabel = !mp ? "Mute (MP only)" : (muted ? "Unmute Mic" : "Mute Mic");
                GUIStyle muteStyle = !mp ? _labelDisabled
                    : (i == _hovered ? _labelHover : (muted ? _labelActive : _label));
                GUI.Label(rect, muteLabel, muteStyle);
            }
            else
            {
                GUIStyle style = i == _hovered ? _labelHover : (Active.ContainsKey(i) ? _labelActive : _label);
                GUI.Label(rect, _labels[i], style);
            }
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
        for (int i = 1; i <= ExpressionCount; i++)
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
        _labelDisabled = new GUIStyle(_label);
        _labelDisabled.normal.textColor = new Color(0.5f, 0.5f, 0.5f); // greyed: solo, mute is MP-only
    }
}
