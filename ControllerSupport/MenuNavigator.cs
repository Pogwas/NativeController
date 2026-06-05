using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace ControllerSupport;

// Standard console-style discrete menu navigation. Tracks a Selected button (D-pad / left stick move it
// to the spatially-nearest button on the current page). It does NOT move the cursor — instead, while the
// controller is the active input, MenuButtonHoverPatch forces the game to highlight Selected directly
// (the game positions its own selection box, so it's always centred — no coordinate math). The mouse
// still works: any mouse movement flips ControllerActive off and the game's native hover takes over.
// A selects (reflection-invokes MenuButton.OnSelect); B backs. Recreated per scene load by Plugin.
internal class MenuNavigator : MonoBehaviour
{
    private static readonly AccessTools.FieldRef<MenuManager, int> StateRef =
        AccessTools.FieldRefAccess<MenuManager, int>("currentMenuState");
    private static readonly AccessTools.FieldRef<MenuManager, List<MenuButton>> ButtonsRef =
        AccessTools.FieldRefAccess<MenuManager, List<MenuButton>>("allMenuButtons");
    private static readonly AccessTools.FieldRef<MenuPage, bool> AddedOnTopRef =
        AccessTools.FieldRefAccess<MenuPage, bool>("addedPageOnTop");
    private static readonly AccessTools.FieldRef<MenuButton, MenuPage> ParentPageRef =
        AccessTools.FieldRefAccess<MenuButton, MenuPage>("parentPage");
    private static readonly AccessTools.FieldRef<MenuButton, bool> DisabledRef =
        AccessTools.FieldRefAccess<MenuButton, bool>("disabled");
    private static readonly AccessTools.FieldRef<MenuButton, bool> HoveringRef =
        AccessTools.FieldRefAccess<MenuButton, bool>("hovering");
    private static readonly AccessTools.FieldRef<MenuPage, MenuPage.PageState> PageStateRef =
        AccessTools.FieldRefAccess<MenuPage, MenuPage.PageState>("currentPageState");
    private static readonly MethodInfo OnSelectMethod = AccessTools.Method(typeof(MenuButton), "OnSelect");

    private static readonly Vector3[] s_corners = new Vector3[4];

    // Read by MenuButtonHoverPatch.
    internal static MenuButton Selected;
    internal static bool ControllerActive;

    private MenuButton _selected;
    private bool _active;
    private float _navCooldown;
    private int _lastCount = -1;
    private GUIStyle _style;

    private bool MenuOpen(out MenuManager mm)
    {
        mm = MenuManager.instance;
        return mm != null && StateRef(mm) == (int)MenuManager.MenuState.Open;
    }

    private void Update()
    {
        ControllerActive = false;
        Selected = null;

        if (!Plugin.Enabled.Value) return;
        var gp = Gamepad.current;
        if (gp == null) return;
        if (!MenuOpen(out var mm)) { _selected = null; return; }

        var candidates = Candidates(mm);
        if (candidates.Count != _lastCount)
        {
            _lastCount = candidates.Count;
            Plugin.Log.LogInfo($"[GamepadDiag] candidates ({candidates.Count}): {string.Join(", ", candidates.Select(b => b.buttonTextString))}");
        }
        if (candidates.Count == 0) { _selected = null; return; }

        // Last-input-wins: controller input claims control; mouse movement hands it back to the mouse.
        if (AnyControllerInput(gp)) _active = true;
        if (Mouse.current != null && Mouse.current.delta.ReadValue().sqrMagnitude > 4f) _active = false;

        if (_active)
        {
            if (_selected == null || !candidates.Contains(_selected)) _selected = Topmost(candidates);
            HandleNavigation(gp, candidates);

            if (gp.buttonSouth.wasPressedThisFrame) Select(_selected);
            if (gp.buttonEast.wasPressedThisFrame) Back(candidates);

            Selected = _selected;
            ControllerActive = true;
        }
        else
        {
            // Mouse mode: keep _selected synced to the moused-over button for a smooth handover back.
            var hovered = candidates.FirstOrDefault(b => HoveringRef(b));
            if (hovered != null) _selected = hovered;
        }
    }

    private static bool AnyControllerInput(Gamepad gp) =>
        gp.dpad.up.isPressed || gp.dpad.down.isPressed || gp.dpad.left.isPressed || gp.dpad.right.isPressed
        || gp.leftStick.ReadValue().sqrMagnitude > 0.2f
        || gp.buttonSouth.wasPressedThisFrame || gp.buttonEast.wasPressedThisFrame
        || gp.selectButton.wasPressedThisFrame;

    private List<MenuButton> Candidates(MenuManager mm)
    {
        var all = ButtonsRef(mm);
        var list = new List<MenuButton>();
        if (all == null) return list;
        foreach (var b in all)
        {
            if (!Usable(b)) continue;
            var page = ParentPageRef(b);
            // Mirror the game's own "live button" condition (MenuManager.Update) so side panels and
            // added-on-top pages (e.g. the mod config menu) are included, but inactive/closing pages aren't.
            if (page == null || AddedOnTopRef(page) || IsActivePage(page)) list.Add(b);
        }
        return list;
    }

    private static bool Usable(MenuButton b) =>
        b != null && !DisabledRef(b) && b.rectTransformSelection != null;

    private static bool IsActivePage(MenuPage p)
    {
        if (p == null) return true;
        var st = PageStateRef(p);
        return st != MenuPage.PageState.Inactive && st != MenuPage.PageState.Closing;
    }

    private static Vector2 ScreenPos(MenuButton b)
    {
        var rt = b.transform as RectTransform;
        if (rt != null) { rt.GetWorldCorners(s_corners); var c = (s_corners[0] + s_corners[2]) * 0.5f; return new Vector2(c.x, c.y); }
        var p = b.transform.position;
        return new Vector2(p.x, p.y);
    }

    private static MenuButton Topmost(List<MenuButton> c) =>
        c.OrderByDescending(b => ScreenPos(b).y).ThenBy(b => ScreenPos(b).x).First();

    private void HandleNavigation(Gamepad gp, List<MenuButton> candidates)
    {
        if (_navCooldown > 0f) _navCooldown -= Time.deltaTime;

        Vector2 d = new Vector2(
            (gp.dpad.right.isPressed ? 1f : 0f) - (gp.dpad.left.isPressed ? 1f : 0f),
            (gp.dpad.up.isPressed ? 1f : 0f) - (gp.dpad.down.isPressed ? 1f : 0f));
        Vector2 nav = d + gp.leftStick.ReadValue();

        if (nav.magnitude < 0.4f) { _navCooldown = 0f; return; }
        if (_navCooldown > 0f) return;
        _navCooldown = 0.22f;

        Vector2 dir = Mathf.Abs(nav.x) > Mathf.Abs(nav.y)
            ? new Vector2(Mathf.Sign(nav.x), 0f)
            : new Vector2(0f, Mathf.Sign(nav.y));
        var next = NearestInDirection(candidates, _selected, dir);
        if (next != null && next != _selected)
        {
            _selected = next;
            if (MenuManager.instance != null) MenuManager.instance.MenuEffectHover();
        }
    }

    private static MenuButton NearestInDirection(List<MenuButton> candidates, MenuButton from, Vector2 dir)
    {
        Vector2 origin = ScreenPos(from);
        MenuButton best = null;
        float bestCost = float.MaxValue;
        foreach (var c in candidates)
        {
            if (c == from) continue;
            Vector2 delta = ScreenPos(c) - origin;
            float along = delta.x * dir.x + delta.y * dir.y;
            if (along <= 0.5f) continue;
            float perp = Mathf.Abs(delta.x * dir.y - delta.y * dir.x);
            float cost = along + perp * 2f;
            if (cost < bestCost) { bestCost = cost; best = c; }
        }
        return best;
    }

    private static void Select(MenuButton b)
    {
        if (b == null) return;
        try
        {
            OnSelectMethod.Invoke(b, null);
            Plugin.Log.LogInfo($"[Gamepad] Menu select '{b.buttonTextString}'");
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogError($"[Gamepad] Menu select failed: {e}");
        }
    }

    private static void Back(List<MenuButton> candidates)
    {
        var back = candidates.FirstOrDefault(b => IsBackLabel(b.buttonTextString));
        if (back != null) { Select(back); return; }

        // Fallback: synthesize Escape, the universal back/close for any REPO menu (incl. mod menus).
        var kb = Keyboard.current;
        if (kb != null)
        {
            InputSystem.QueueStateEvent(kb, new KeyboardState(Key.Escape));
            InputSystem.QueueStateEvent(kb, new KeyboardState());
            Plugin.Log.LogInfo("[Gamepad] B -> Escape (no labeled Back button on page).");
        }
    }

    private static bool IsBackLabel(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        s = s.ToLowerInvariant();
        return s.Contains("back") || s.Contains("cancel") || s.Contains("close")
            || s.Contains("return") || s.Contains("resume");
    }

    private void OnGUI()
    {
        if (!Plugin.Enabled.Value || Gamepad.current == null) return;
        if (!MenuOpen(out _)) return;
        if (_style == null) _style = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };

        string text = "D-Pad: Move    A: Select    B: Back";
        var rect = new Rect(20, Screen.height - 40, 700, 30);
        _style.normal.textColor = Color.black;
        GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, _style);
        _style.normal.textColor = Color.white;
        GUI.Label(rect, text, _style);
    }
}
