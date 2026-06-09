using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NativeController;

// Controller prompts near the crosshair: "RT Grab" when aiming at a grabbable, "RT Let go"
// (toggle-grab only) + "LT Rotate" while holding, "RT Climb" when tumbling at a climbable
// wall. Zero raycasting and zero Harmony patches — PhysGrabber already raycasts every frame
// and publishes the result to the crosshair via Aim.SetState; we just read Aim.currentState,
// so the prompt is exactly as accurate as the game's own crosshair. Shown only while the
// controller is the active input. Recreated per scene by Plugin (REPO wipes
// DontDestroyOnLoad at boot).
internal class GrabPromptOverlay : MonoBehaviour
{
    private static readonly AccessTools.FieldRef<MenuManager, int> MenuStateRef =
        AccessTools.FieldRefAccess<MenuManager, int>("currentMenuState");

    private static AccessTools.FieldRef<Aim, Aim.State> _aimStateRef;
    private static bool _aimRefFailed; // one-time warn + permanent disable (quiet-mode policy)

    // Vanilla item-tooltip look: action word in vanilla-orange, button in white brackets.
    private struct Prompt
    {
        public string Rich;  // drawn (IMGUI rich text)
        public string Plain; // measured (CalcSize counts markup characters)
    }

    private ControllerDetect.Kind _cachedKind = (ControllerDetect.Kind)(-1);
    private Prompt _grab, _letGo, _rotate, _climb;
    private Prompt _rightLine; // RT family (GRAB / LET GO / CLIMB) — right of the inventory bar
    private Prompt _leftLine;  // LT (ROTATE) — left of the inventory bar
    private GUIStyle _style;
    private InventorySpot[] _spots; // the 3 hotbar slots; located lazily, revalidated when destroyed
    private readonly Vector3[] _corners = new Vector3[4]; // scratch

    private void Update()
    {
        ControllerDetect.TrackActiveInput();       // gameplay-valid last-input-wins signal
        InputDisplayPatch.TickCacheInvalidation(); // vanilla [E]-tag cache follows input flips
        _rightLine = default;
        _leftLine = default;
        if (!Plugin.Enabled.Value || !Plugin.PromptsEnabled.Value) return;
        if (Gamepad.current == null || !ControllerDetect.PadActive) return;
        if (EmoteWheel.Open) return;
        var mm = MenuManager.instance;
        if (mm != null && MenuStateRef(mm) == (int)MenuManager.MenuState.Open) return;
        var aim = Aim.instance;
        if (aim == null) return;
        if (!EnsureAimRef()) return;

        EnsureNames();
        switch (_aimStateRef(aim))
        {
            case Aim.State.Grabbable:
                _rightLine = _grab;
                break;
            case Aim.State.Grab:
            case Aim.State.Rotate:
                // "Let go" only teaches the toggle-grab behavior; without the toggle,
                // releasing the trigger lets go and the line would be wrong.
                if (Plugin.GrabToggle.Value) _rightLine = _letGo;
                _leftLine = _rotate;
                break;
            case Aim.State.Climbable:
                _rightLine = _climb;
                break;
        }
    }

    private static bool EnsureAimRef()
    {
        if (_aimStateRef != null) return true;
        if (_aimRefFailed) return false;
        try
        {
            _aimStateRef = AccessTools.FieldRefAccess<Aim, Aim.State>("currentState");
            return true;
        }
        catch (Exception e)
        {
            _aimRefFailed = true;
            Plugin.Log.LogWarning($"[Prompts] Aim.currentState unavailable — prompts disabled: {e.Message}");
            return false;
        }
    }

    private void EnsureNames()
    {
        var kind = ControllerDetect.Current;
        if (kind == _cachedKind && _grab.Plain != null) return;
        _cachedKind = kind;
        string rt = ButtonNames.Of(ButtonNames.Control.RT, kind);
        string lt = ButtonNames.Of(ButtonNames.Control.LT, kind);
        _grab = Make("GRAB", rt);
        _letGo = Make("LET GO", rt);
        _rotate = Make("ROTATE", lt);
        _climb = Make("CLIMB", rt);
    }

    private static Prompt Make(string action, string button)
    {
        return new Prompt
        {
            // #FFA21C ~ the vanilla tooltip orange ("SHOTGUN [E]" style)
            Rich = $"<color=#FFA21C>{action}</color> <color=#FFFFFF>[{button}]</color>",
            Plain = $"{action} [{button}]",
        };
    }

    private void OnGUI()
    {
        if (_rightLine.Plain == null && _leftLine.Plain == null) return;
        EnsureStyles();

        // Flank the inventory bar (user request 2026-06-09): RT prompts on its right,
        // ROTATE on its left. Falls back to below-crosshair if the HUD pipeline isn't up.
        if (TryInventoryScreenBounds(out float minX, out float maxX, out float centerY))
        {
            float guiY = Screen.height - centerY; // UI coords are y-up; IMGUI is y-down
            if (_rightLine.Plain != null) DrawChip(_rightLine, maxX + 14f, guiY, rightSide: true);
            if (_leftLine.Plain != null) DrawChip(_leftLine, minX - 14f, guiY, rightSide: false);
            return;
        }

        float cx = Screen.width / 2f;
        float y = Screen.height / 2f + 35f;
        if (_rightLine.Plain != null) { DrawChipCentered(_rightLine, cx, y); y += 28f; }
        if (_leftLine.Plain != null) DrawChipCentered(_leftLine, cx, y);
    }

    private void DrawChip(Prompt p, float anchorX, float guiCenterY, bool rightSide)
    {
        Vector2 size = _style.CalcSize(new GUIContent(p.Plain));
        float w = size.x + 18f;
        var rect = new Rect(rightSide ? anchorX : anchorX - w, guiCenterY - 12f, w, 24f);
        GUI.color = new Color(0.08f, 0.08f, 0.08f, 0.85f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(rect, p.Rich, _style);
    }

    private void DrawChipCentered(Prompt p, float cx, float y)
    {
        Vector2 size = _style.CalcSize(new GUIContent(p.Plain));
        float w = size.x + 18f;
        var rect = new Rect(cx - w / 2f, y, w, 24f);
        GUI.color = new Color(0.08f, 0.08f, 0.08f, 0.85f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(rect, p.Rich, _style);
    }

    // Screen-pixel bounds of the 3 inventory hotbar slots. HUD elements live on the
    // world-space 712x400 HUD canvas (rendered through a RenderTexture — see Quirk 8), so
    // the conversion is: HUD world position -> overlay camera viewport -> position within
    // the composited overlay RawImage, whose corners ARE screen pixels (it sits on the
    // final Screen Space - Overlay canvas).
    private bool TryInventoryScreenBounds(out float minX, out float maxX, out float centerY)
    {
        minX = float.MaxValue;
        maxX = float.MinValue;
        centerY = 0f;

        var overlay = CameraOverlay.instance;
        var rtMain = RenderTextureMain.instance;
        if (overlay == null || rtMain == null || rtMain.overlayRawImage == null) return false;
        // CameraOverlay.overlayCamera is internal — same component, same GameObject.
        var overlayCam = overlay.GetComponent<Camera>();
        if (overlayCam == null) return false;

        if (_spots == null || _spots.Length == 0 || _spots[0] == null)
        {
            _spots = FindObjectsOfType<InventorySpot>();
            if (_spots.Length == 0) return false;
        }

        rtMain.overlayRawImage.rectTransform.GetWorldCorners(_corners);
        Vector2 displayMin = _corners[0]; // overlay canvas is Screen Space - Overlay: world == px
        Vector2 displayMax = _corners[2];

        float ySum = 0f;
        int found = 0;
        foreach (var spot in _spots)
        {
            if (spot == null) continue;
            var rt = spot.transform as RectTransform;
            if (rt == null) continue;
            rt.GetWorldCorners(_corners);
            for (int c = 0; c < 4; c++)
            {
                Vector3 vp = overlayCam.WorldToViewportPoint(_corners[c]);
                float sx = Mathf.Lerp(displayMin.x, displayMax.x, vp.x);
                float sy = Mathf.Lerp(displayMin.y, displayMax.y, vp.y);
                if (sx < minX) minX = sx;
                if (sx > maxX) maxX = sx;
                ySum += sy;
            }
            found++;
        }
        if (found == 0) return false;
        centerY = ySum / (found * 4);
        return true;
    }

    private void EnsureStyles()
    {
        if (_style != null) return;
        _style = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            richText = true, // vanilla-tooltip look: orange action + white [button]
        };
        _style.normal.textColor = Color.white;
    }
}
