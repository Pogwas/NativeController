using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace ControllerSupport;

// Standard console-style discrete menu navigation. Tracks a Selected button (D-pad / left stick move it
// to the spatially-nearest button on the FOCUSED page). It does NOT move the cursor — instead, while the
// controller is the active input, MenuButtonHoverPatch forces the game to highlight Selected directly
// (the game positions its own selection box, so it's always centred — no coordinate math). The mouse
// still works: any mouse movement flips ControllerActive off and the game's native hover takes over.
// A selects (reflection-invokes MenuButton.OnSelect); B backs. Recreated per scene load by Plugin.
//
// Candidates are scoped to ONE focused page (the topmost added-on-top page, else currentMenuPage). This
// is required because MenuManager.allMenuButtons is a global flat list that is never page-scoped, and
// MenuLib opens its pages on top WITHOUT inactivating the page underneath — so without scoping, the main
// menu's buttons stay mixed into the mod-menu candidate set. The REPOConfig menu is two pages (left mod
// list = currentMenuPage, right settings = an added-on-top page); horizontal nav that finds no in-page
// target switches the focused page (LEFT -> under page, RIGHT -> top page) so the panels are reachable.
internal class MenuNavigator : MonoBehaviour
{
    private static readonly AccessTools.FieldRef<MenuManager, int> StateRef =
        AccessTools.FieldRefAccess<MenuManager, int>("currentMenuState");
    private static readonly AccessTools.FieldRef<MenuManager, List<MenuButton>> ButtonsRef =
        AccessTools.FieldRefAccess<MenuManager, List<MenuButton>>("allMenuButtons");
    private static readonly AccessTools.FieldRef<MenuManager, MenuPage> CurrentPageRef =
        AccessTools.FieldRefAccess<MenuManager, MenuPage>("currentMenuPage");
    private static readonly AccessTools.FieldRef<MenuManager, List<MenuPage>> AddedPagesRef =
        AccessTools.FieldRefAccess<MenuManager, List<MenuPage>>("addedPagesOnTop");
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
    // The REAL visible label lives on the live TMP component MenuButton.buttonText. buttonTextString is a
    // baked template field that MenuLib never updates (its buttons are clones of "Menu Button - Quit game"),
    // so it reads "Quit game"/"BUTTON" for every MenuLib button. Read buttonText untyped (no TMPro ref).
    private static readonly FieldInfo ButtonTextField = AccessTools.Field(typeof(MenuButton), "buttonText");
    private static PropertyInfo s_textProp;

    private static readonly Vector3[] s_corners = new Vector3[4];

    // Read by MenuButtonHoverPatch.
    internal static MenuButton Selected;
    internal static bool ControllerActive;

    private MenuButton _selected;
    private bool _active;
    private float _navCooldown;
    private GUIStyle _style;

    // Focus tracking. _focusPage is the page nav is scoped to this frame. _focusOverride is a user-chosen
    // panel (set by cross-panel LEFT/RIGHT); it is cleared whenever the page set changes (_lastTop differs)
    // so drilling into / out of a mod returns focus to the default (topmost) page.
    private MenuPage _focusPage;
    private MenuPage _focusOverride;
    private MenuPage _lastTop;
    // Per-page memory of the last selected button: backing out of a mod's settings returns the selection to
    // the mod row you opened, and switching panels restores each panel's last position. Cleared on close.
    private readonly Dictionary<MenuPage, MenuButton> _lastByPage = new Dictionary<MenuPage, MenuButton>();

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
        if (!MenuOpen(out var mm)) { _selected = null; _focusOverride = null; _focusPage = null; _lastTop = null; _lastByPage.Clear(); return; }

        ResolveFocus(mm);

        var candidates = Candidates(mm, _focusPage);
        if (candidates.Count == 0) { _selected = null; return; }

        // Last-input-wins: controller input claims control; mouse movement hands it back to the mouse.
        if (AnyControllerInput(gp)) _active = true;
        if (Mouse.current != null && Mouse.current.delta.ReadValue().sqrMagnitude > 4f) _active = false;

        if (_active)
        {
            if (_selected == null || !candidates.Contains(_selected)) _selected = Reseed(candidates);
            HandleNavigation(gp, candidates);

            if (gp.buttonSouth.wasPressedThisFrame) Select(_selected);
            if (gp.buttonEast.wasPressedThisFrame) Back(candidates);

            if (_focusPage != null && _selected != null) _lastByPage[_focusPage] = _selected;
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

    // The page nav should be scoped to: the topmost still-live added-on-top page, else currentMenuPage.
    private MenuPage DefaultFocus(MenuManager mm)
    {
        var added = AddedPagesRef(mm);
        if (added != null)
            for (int i = added.Count - 1; i >= 0; i--)
                if (IsFocusableAdded(added[i])) return added[i];
        return CurrentPageRef(mm); // may be null during boot/teardown
    }

    private bool IsLive(MenuManager mm, MenuPage p)
    {
        if (p == null) return false;
        if (p == CurrentPageRef(mm)) return IsActivePage(p);
        var added = AddedPagesRef(mm);
        return added != null && added.Contains(p) && IsFocusableAdded(p);
    }

    private void ResolveFocus(MenuManager mm)
    {
        var top = DefaultFocus(mm);
        if (top != _lastTop) { _focusOverride = null; _lastTop = top; } // page set changed -> back to default
        if (_focusOverride != null && !IsLive(mm, _focusOverride)) _focusOverride = null;
        var focus = _focusOverride ?? top;
        if (focus != _focusPage) { _focusPage = focus; _selected = null; } // reseed on focus change
    }

    private List<MenuButton> Candidates(MenuManager mm, MenuPage focus)
    {
        var all = ButtonsRef(mm);
        var list = new List<MenuButton>();
        if (all == null) return list;

        // Primary: scope to the one focused page. This drops foreign-page buttons (the main menu staying
        // live under the mod menu) so DOWN walks in-page rows and never jumps to a wrong-page button.
        if (focus != null)
        {
            foreach (var b in all)
                if (Usable(b) && ParentPageRef(b) == focus) list.Add(b);
            if (list.Count > 0) { AddCulledScrollRows(list); return list; }
            // Scoped set empty (e.g. a page whose buttons don't back-reference it) -> permissive fallback.
        }

        // Fallback (vanilla single-page menus, or focus unresolved during boot/teardown): the game's own
        // "live button" predicate.
        foreach (var b in all)
        {
            if (!Usable(b)) continue;
            var page = ParentPageRef(b);
            if (page == null || AddedOnTopRef(page) || IsActivePage(page)) list.Add(b);
        }
        return list;
    }

    // REPOScrollView/MenuScrollBox SetActive(false) on off-mask rows, which removes them from
    // allMenuButtons — so long lists (e.g. a mod's settings) can't be navigated/scrolled past the visible
    // rows. Pull the scroll content's children directly (including inactive) back into the candidate set, so
    // nav can target a culled row; selecting it calls ScrollToChild, which scrolls it into view and the view
    // re-activates it. (No-op for short lists / pages with no scroll box.)
    private static void AddCulledScrollRows(List<MenuButton> list)
    {
        MenuScrollBox box = null;
        for (int i = 0; i < list.Count; i++) { box = list[i].GetComponentInParent<MenuScrollBox>(); if (box != null) break; }
        if (box == null || box.scroller == null) return;
        var children = box.scroller.GetComponentsInChildren<MenuButton>(true); // true = include inactive
        foreach (var cb in children)
            if (Usable(cb) && !list.Contains(cb)) list.Add(cb);
    }

    private List<MenuButton> PageButtons(MenuManager mm, MenuPage page)
    {
        var all = ButtonsRef(mm);
        var list = new List<MenuButton>();
        if (all == null) return list;
        foreach (var b in all)
            if (Usable(b) && ParentPageRef(b) == page) list.Add(b);
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

    // An added-on-top page is still focusable if it's open in ANY form — including Inactive. Vanilla
    // deactivates a settings sub-page that isn't the "current" page (MenuPage.StateActive sets Inactive when
    // currentMenuPageIndex != its own), but the page stays fully interactable (mouse can click it); only
    // Closing/Closed mean it's actually going away. MenuLib popups dodge this by all sharing index -1.
    private static bool IsFocusableAdded(MenuPage p)
    {
        if (p == null) return false;
        var st = PageStateRef(p);
        return st != MenuPage.PageState.Closing && st != MenuPage.PageState.Closed;
    }

    // The real visible label (live TMP text), with the GameObject name as a fallback. Used for Back
    // detection and diagnostics; NOT used for positioning (that stays world-corner based).
    private static string Label(MenuButton b)
    {
        if (b == null) return "<null>";
        var txt = ButtonTextField != null ? ButtonTextField.GetValue(b) : null;
        if (txt != null)
        {
            if (s_textProp == null) s_textProp = txt.GetType().GetProperty("text");
            if (s_textProp?.GetValue(txt) is string s && !string.IsNullOrEmpty(s)) return s;
        }
        return b.gameObject.name;
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

    // Restore this page's last-selected button if it's still a valid candidate, else the topmost.
    private MenuButton Reseed(List<MenuButton> candidates)
    {
        if (_focusPage != null && _lastByPage.TryGetValue(_focusPage, out var remembered)
            && remembered != null && candidates.Contains(remembered))
            return remembered;
        return Topmost(candidates);
    }

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
        // Inside a scroll list, stay within the list until its rows are exhausted, so a deep (scrolled-off)
        // row isn't skipped in favour of a fixed button outside the box (e.g. "Back") that's spatially closer.
        // Falling back to the full set only when no same-box target exists lets nav reach Back/footer normally.
        var box = _selected != null ? _selected.GetComponentInParent<MenuScrollBox>() : null;
        MenuButton next = null;
        if (box != null)
        {
            var sameBox = candidates.Where(b => b.GetComponentInParent<MenuScrollBox>() == box).ToList();
            next = NearestInDirection(sameBox, _selected, dir);
        }
        if (next == null) next = NearestInDirection(candidates, _selected, dir);

        // No in-page target on a horizontal press -> try crossing to the other panel/page.
        if (next == null && Mathf.Abs(dir.x) > 0.5f && TrySwitchPanel(dir.x < 0 ? -1 : 1))
            return;

        if (next != null && next != _selected)
        {
            _selected = next;
            ScrollIntoView(next);
            if (MenuManager.instance != null) MenuManager.instance.MenuEffectHover();
        }
    }

    // Cross-panel focus switch. The REPOConfig menu is two pages: the under/current page (left mod list)
    // and a top added-on-top page (right settings). sign<0 = LEFT (go to the under page), sign>0 = RIGHT
    // (go to the top page). Returns true if focus moved.
    private bool TrySwitchPanel(int sign)
    {
        var mm = MenuManager.instance;
        if (mm == null) return false;
        var cur = CurrentPageRef(mm);
        var top = DefaultFocus(mm);
        if (top == null || cur == null || top == cur) return false; // only one page -> nothing to cross to

        MenuPage target = sign < 0
            ? (_focusPage == top ? cur : null)   // LEFT: only from the top page back to the under page
            : (_focusPage != top ? top : null);  // RIGHT: only from the under page to the top page
        if (target == null) return false;

        var btns = PageButtons(mm, target);
        if (btns.Count == 0) return false;

        _focusOverride = target;
        _focusPage = target;
        _selected = Reseed(btns);
        ScrollIntoView(_selected);
        if (MenuManager.instance != null) MenuManager.instance.MenuEffectHover();
        return true;
    }

    // If the selected button lives inside a scroll box, scroll it into view (the game animates it).
    private static void ScrollIntoView(MenuButton b)
    {
        if (b == null) return;
        var box = b.GetComponentInParent<MenuScrollBox>();
        if (box == null || box.scroller == null) return;
        // ScrollToChild reads child.localPosition.y as the content offset, so it needs a DIRECT child of the
        // scroller (the per-row container). The button itself is nested inside that row, with a row-relative
        // localPosition (~0) that would barely scroll. Walk up to the scroller's direct child and scroll that.
        Transform row = b.transform;
        while (row != null && row.parent != (Transform)box.scroller) row = row.parent;
        var target = (row as RectTransform) ?? (b.transform as RectTransform);
        box.ScrollToChild(target);
    }

    // Row-based directional nav: advance to the NEAREST step in the press direction, then pick the
    // laterally-closest control in that band. A simple "along + k*perp" cost skips a control that sits
    // offset sideways in the very next row (e.g. a slider's < >) in favour of an x-aligned button further
    // away (the next toggle) — picking the nearest row first prevents that.
    private static MenuButton NearestInDirection(List<MenuButton> candidates, MenuButton from, Vector2 dir)
    {
        Vector2 origin = ScreenPos(from);

        // Pass 1: smallest forward distance (the nearest row/step).
        float minAlong = float.MaxValue;
        foreach (var c in candidates)
        {
            if (c == from) continue;
            Vector2 delta = ScreenPos(c) - origin;
            float along = delta.x * dir.x + delta.y * dir.y;
            if (along > 0.5f && along < minAlong) minAlong = along;
        }
        if (minAlong == float.MaxValue) return null;

        // Pass 2: among candidates within a band of that nearest step, pick min lateral offset (then nearest).
        float band = minAlong * 1.6f + 1f;
        MenuButton best = null;
        float bestScore = float.MaxValue;
        foreach (var c in candidates)
        {
            if (c == from) continue;
            Vector2 delta = ScreenPos(c) - origin;
            float along = delta.x * dir.x + delta.y * dir.y;
            if (along <= 0.5f || along > band) continue;
            float perp = Mathf.Abs(delta.x * dir.y - delta.y * dir.x);
            float score = perp * 10000f + along; // lateral offset dominates; along breaks ties
            if (score < bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    private static void Select(MenuButton b)
    {
        if (b == null) return;
        try
        {
            OnSelectMethod.Invoke(b, null);
            Plugin.Log.LogDebug($"[Gamepad] Menu select '{Label(b)}'");
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogError($"[Gamepad] Menu select failed: {e}");
        }
    }

    private void Back(List<MenuButton> candidates)
    {
        // If a popup/settings page is open on top (e.g. a mod's settings over the mod list), close JUST that
        // top page and return to the page underneath. A synthesized global Escape is seen by BOTH the popup
        // AND the page beneath in the same frame, so it cascades all the way out to the main menu.
        var mm = MenuManager.instance;
        if (mm != null)
        {
            var added = AddedPagesRef(mm);
            if (added != null && added.Any(p => IsFocusableAdded(p)))
            {
                mm.PageCloseAllAddedOnTop();
                ResetRepoConfigGuard();
                Plugin.Log.LogDebug("[Gamepad] B -> close added-on-top page (back one level).");
                return;
            }
        }

        // Else prefer a labeled Back button on the current page (e.g. the mod list's "Back").
        var back = candidates.FirstOrDefault(b => IsBackLabel(Label(b)));
        if (back != null) { Select(back); return; }

        // Else synthesize Escape, the universal close for a base page with no Back button.
        var kb = Keyboard.current;
        if (kb != null)
        {
            InputSystem.QueueStateEvent(kb, new KeyboardState(Key.Escape));
            InputSystem.QueueStateEvent(kb, new KeyboardState());
            Plugin.Log.LogDebug("[Gamepad] B -> Escape (no labeled Back / added page).");
        }
    }

    // REPOConfig keeps the settings panel as a side panel and guards re-opening a mod with
    // `lastClickedModButton != modButton`, only nulling it when the mod LIST is rebuilt. Closing the panel
    // ourselves doesn't rebuild the list, so without this the just-backed-out mod can't be reopened (its
    // button still == lastClickedModButton). Null it via reflection so any mod (incl. the same one) reopens.
    // Looked up once; no-op when REPOConfig isn't installed.
    private static FieldInfo s_repoLastClicked;
    private static bool s_repoLookupDone;
    private static void ResetRepoConfigGuard()
    {
        if (!s_repoLookupDone)
        {
            s_repoLookupDone = true;
            var t = AccessTools.TypeByName("REPOConfig.ConfigMenu");
            if (t != null) s_repoLastClicked = AccessTools.Field(t, "lastClickedModButton");
        }
        s_repoLastClicked?.SetValue(null, null);
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
