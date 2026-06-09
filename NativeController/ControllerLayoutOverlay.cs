using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NativeController;

// On-screen controller button reference. Shown via the Settings-menu button (SettingsMenuEntry,
// MenuLib), closed with B / any button. No longer self-shows in gameplay — D-pad Down now opens
// the EmoteWheel instead. Recreated per scene by Plugin (REPO wipes DontDestroyOnLoad at boot).
internal class ControllerLayoutOverlay : MonoBehaviour
{
    private static readonly AccessTools.FieldRef<MenuManager, int> StateRef =
        AccessTools.FieldRefAccess<MenuManager, int>("currentMenuState");

    internal static bool Visible;
    private static int _openFrame;
    private GUIStyle _title, _row, _key;

    internal static void Toggle()
    {
        Visible = !Visible;
        if (Visible) _openFrame = Time.frameCount;
    }

    private static bool MenuOpen()
    {
        var mm = MenuManager.instance;
        return mm != null && StateRef(mm) == (int)MenuManager.MenuState.Open;
    }

    private void Update()
    {
        if (!Plugin.Enabled.Value) { Visible = false; return; }
        var gp = Gamepad.current;
        if (gp == null) { Visible = false; return; }

        // Toggled on by the Settings button; dismiss on ANY controller button or a mouse click (1-frame
        // grace so the press/click that opened it doesn't immediately close it). No in-game self-show:
        // D-pad Down belongs to the EmoteWheel now.
        if (Visible && Time.frameCount > _openFrame + 1 && (AnyButtonPressed(gp) || MouseClicked())) Visible = false;
        if (!MenuOpen()) Visible = false;
    }

    private static bool MouseClicked()
    {
        var m = Mouse.current;
        return m != null && (m.leftButton.wasPressedThisFrame || m.rightButton.wasPressedThisFrame);
    }

    private static bool AnyButtonPressed(Gamepad gp) =>
        gp.buttonSouth.wasPressedThisFrame || gp.buttonEast.wasPressedThisFrame
        || gp.buttonWest.wasPressedThisFrame || gp.buttonNorth.wasPressedThisFrame
        || gp.leftShoulder.wasPressedThisFrame || gp.rightShoulder.wasPressedThisFrame
        || gp.leftTrigger.wasPressedThisFrame || gp.rightTrigger.wasPressedThisFrame
        || gp.dpad.up.wasPressedThisFrame || gp.dpad.down.wasPressedThisFrame
        || gp.dpad.left.wasPressedThisFrame || gp.dpad.right.wasPressedThisFrame
        || gp.startButton.wasPressedThisFrame || gp.selectButton.wasPressedThisFrame
        || gp.leftStickButton.wasPressedThisFrame || gp.rightStickButton.wasPressedThisFrame;

    private void OnGUI()
    {
        if (!Visible) return;
        EnsureStyles();
        var rows = Rows(ControllerDetect.Current);

        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);

        float w = 520f, rowH = 26f, h = 60f + rows.Count * rowH;
        float x = (Screen.width - w) / 2f, y = (Screen.height - h) / 2f;
        GUI.color = new Color(0.1f, 0.1f, 0.1f, 0.95f);
        GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUI.Label(new Rect(x, y + 12f, w, 30f), "Controller Layout", _title);
        float ry = y + 50f;
        foreach (var pair in rows)
        {
            GUI.Label(new Rect(x + 24f, ry, w * 0.55f, rowH), pair.Key, _row);
            ControllerGlyphs.DrawLabel(new Rect(x + w * 0.55f, ry, w * 0.4f, rowH), pair.Value, _key);
            ry += rowH;
        }
    }

    private static List<KeyValuePair<string, string>> Rows(ControllerDetect.Kind k)
    {
        string N(ButtonNames.Control c) => ButtonNames.Of(c, k);
        KeyValuePair<string, string> R(string a, string b) => new KeyValuePair<string, string>(a, b);
        return new List<KeyValuePair<string, string>>
        {
            R("Move / Look", N(ButtonNames.Control.LStick) + " / " + N(ButtonNames.Control.RStick)),
            R("Jump", N(ButtonNames.Control.South)),
            R("Interact", N(ButtonNames.Control.West)),
            R("Tumble", N(ButtonNames.Control.East)),
            R("Map", N(ButtonNames.Control.North)),
            R("Sprint / Crouch", N(ButtonNames.Control.L3) + " / " + N(ButtonNames.Control.R3)),
            R("Grab / Rotate", N(ButtonNames.Control.RT) + " / " + N(ButtonNames.Control.LT)),
            R("Push / Pull", N(ButtonNames.Control.RB) + " / " + N(ButtonNames.Control.LB)),
            R("Pause / Chat", N(ButtonNames.Control.Start) + " / " + N(ButtonNames.Control.Select)),
            R("Inventory 1 / 2 / 3", N(ButtonNames.Control.DpadLeft) + " / " + N(ButtonNames.Control.DpadUp) + " / " + N(ButtonNames.Control.DpadRight)),
            R("Emotes", "hold " + N(ButtonNames.Control.DpadDown)),
            R("View this layout", "Settings menu"),
            R("Menu: Select / Back", N(ButtonNames.Control.South) + " / " + N(ButtonNames.Control.East)),
        };
    }

    private void EnsureStyles()
    {
        if (_title != null) return;
        _title = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        _title.normal.textColor = Color.white;
        _row = new GUIStyle(GUI.skin.label) { fontSize = 16 };
        _row.normal.textColor = Color.white;
        _key = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
        _key.normal.textColor = new Color(1f, 0.85f, 0.3f);
    }
}
