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

    private ControllerDetect.Kind _cachedKind = (ControllerDetect.Kind)(-1);
    private string _grab, _letGo, _rotate, _climb;
    private readonly string[] _lines = new string[2];
    private int _lineCount;
    private GUIStyle _style;

    private void Update()
    {
        _lineCount = 0;
        if (!Plugin.Enabled.Value || !Plugin.PromptsEnabled.Value) return;
        if (Gamepad.current == null || !MenuNavigator.ControllerActive) return;
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
                _lines[_lineCount++] = _grab;
                break;
            case Aim.State.Grab:
            case Aim.State.Rotate:
                // "Let go" only teaches the toggle-grab behavior; without the toggle,
                // releasing the trigger lets go and the line would be wrong.
                if (Plugin.GrabToggle.Value) _lines[_lineCount++] = _letGo;
                _lines[_lineCount++] = _rotate;
                break;
            case Aim.State.Climbable:
                _lines[_lineCount++] = _climb;
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
        if (kind == _cachedKind && _grab != null) return;
        _cachedKind = kind;
        string rt = ButtonNames.Of(ButtonNames.Control.RT, kind);
        string lt = ButtonNames.Of(ButtonNames.Control.LT, kind);
        _grab = rt + "  Grab";
        _letGo = rt + "  Let go";
        _rotate = lt + "  Rotate";
        _climb = rt + "  Climb";
    }

    private void OnGUI()
    {
        if (_lineCount == 0) return;
        EnsureStyles();
        float cx = Screen.width / 2f;
        float y = Screen.height / 2f + 35f; // just below the crosshair
        for (int i = 0; i < _lineCount; i++)
        {
            Vector2 size = _style.CalcSize(new GUIContent(_lines[i]));
            var rect = new Rect(cx - (size.x + 18f) / 2f, y, size.x + 18f, 24f);
            GUI.color = new Color(0.08f, 0.08f, 0.08f, 0.85f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            ControllerGlyphs.DrawLabel(rect, _lines[i], _style);
            y += 28f;
        }
    }

    private void EnsureStyles()
    {
        if (_style != null) return;
        _style = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        _style.normal.textColor = new Color(1f, 0.85f, 0.3f); // gold, matches overlay/wheel accents
    }
}
