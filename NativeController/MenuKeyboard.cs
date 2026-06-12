using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NativeController;

// On-screen keyboard for vanilla MENU text fields (lobby name, save rename, server search,
// lobby password). Auto-opens when a text-input page is on screen (MenuManager.
// textInputActive, re-armed every frame by any live field) while the controller is the
// active input (MenuNavigator.ControllerActive) -- the console convention. Writes straight
// into the vanilla string field via FieldRef; vanilla's own per-frame Update then does the
// max-length cap + deny, casing, tick/shake feedback and cursor, so pad typing feels
// identical to a physical keyboard. Start (or the grid's ENTER key) invokes the page's own
// public confirm method -- exactly what keyboard Enter triggers, so deny/confirm/page-close
// behavior is vanilla's. Select dismisses the panel for this page entry (to reach Show/Copy
// on the password page, or B-back out); leaving and re-entering the page re-opens it.
// MenuNavigator yields d-pad/stick/A/B while Open (one input owner at a time); the
// physical keyboard keeps working throughout (vanilla inputString path untouched).
// Recreated per scene by Plugin (REPO wipes DontDestroyOnLoad at boot).
internal class MenuKeyboard : MonoBehaviour
{
    private static readonly AccessTools.FieldRef<MenuManager, bool> TextInputActiveRef =
        AccessTools.FieldRefAccess<MenuManager, bool>("textInputActive");
    private static readonly AccessTools.FieldRef<MenuTextInput, string> TextCurrentRef =
        AccessTools.FieldRefAccess<MenuTextInput, string>("textCurrent");
    private static readonly AccessTools.FieldRef<MenuPagePassword, string> PasswordRef =
        AccessTools.FieldRefAccess<MenuPagePassword, string>("password");

    internal static bool Open; // read by MenuNavigator (yield gate)

    private PadKeyboardCore _core;
    private bool _coreHasSpace;
    private MenuTextInput _textInput;   // standard target (lobby name / save rename / search)
    private MenuPagePassword _password; // password target (modal popup, takes precedence)
    private bool _dismissed;            // Select latch; cleared when textInputActive drops
    private bool _selectArmed;          // dismiss on release only after a clean press
    private static bool _warned;

    // Called by Plugin.OnSceneLoaded.
    internal static void ResetState()
    {
        Open = false;
    }

    private void Update()
    {
        var mm = MenuManager.instance;
        var gp = Gamepad.current;
        if (mm == null || gp == null || !Plugin.Enabled.Value || !Plugin.MenuKeyboardEnabled.Value)
        {
            Open = false;
            return;
        }

        bool fieldOnScreen;
        try { fieldOnScreen = TextInputActiveRef(mm); }
        catch (Exception e) { WarnOnce(e); Open = false; return; }

        if (!fieldOnScreen)
        {
            // Page closed (confirm, back, mouse click, Esc) -- 0.1 s watchdog decay.
            Open = false;
            _dismissed = false;
            _textInput = null;
            _password = null;
            return;
        }

        // Level-triggered open (not edge-triggered): also catches "entered the page with
        // the mouse, then picked up the pad" -- the first pad input flips ControllerActive
        // and the panel appears.
        if (!Open && !_dismissed && MenuNavigator.ControllerActive)
        {
            try { if (AcquireTarget()) OpenPanel(); }
            catch (Exception e) { WarnOnce(e); return; }
        }
        if (!Open) return;

        // Mouse takeover: hide WITHOUT latching _dismissed -- the panel sits over the lower
        // screen (server rows etc.) and must never cover mouse browsing. MenuNavigator flips
        // ControllerActive off on mouse movement; the next pad input flips it back on and the
        // level-trigger above reopens the panel. (Playtest finding 2026-06-11.)
        if (!MenuNavigator.ControllerActive)
        {
            Open = false;
            return;
        }

        // Target page swapped/deactivated under us: close now; if another field is live,
        // the level-trigger above reopens on the new target next frame.
        if ((_password != null && !_password.isActiveAndEnabled) ||
            (_password == null && (_textInput == null || !_textInput.isActiveAndEnabled)))
        {
            Open = false;
            _textInput = null;
            _password = null;
            return;
        }

        // Select-to-dismiss arms only once Select is seen fully released (same pattern as
        // ChatKeyboard, in case a Select press is what got us here).
        if (!gp.selectButton.isPressed && !gp.selectButton.wasReleasedThisFrame) _selectArmed = true;

        try
        {
            _core.HandleInput(gp);
            if (gp.buttonEast.wasPressedThisFrame) Backspace(); // B: one char per press (vanilla-timing rule: no hold-repeat)
            if (_selectArmed && gp.selectButton.wasReleasedThisFrame) Dismiss();
        }
        catch (Exception e) { WarnOnce(e); }
    }

    private bool AcquireTarget()
    {
        _password = null;
        _textInput = null;
        foreach (var p in FindObjectsOfType<MenuPagePassword>())
            if (p.isActiveAndEnabled) { _password = p; break; }
        if (_password == null)
            foreach (var t in FindObjectsOfType<MenuTextInput>())
                if (t.isActiveAndEnabled) { _textInput = t; break; }
        return _password != null || _textInput != null;
    }

    private void OpenPanel()
    {
        bool hasSpace = _password == null; // vanilla strips spaces from passwords
        if (_core == null || _coreHasSpace != hasSpace)
        {
            _core = new PadKeyboardCore(hasSpace, confirmLabel: "ENTER",
                                        confirmVerb: "confirm", closeVerb: "hide",
                                        hideLabel: "HIDE"); // navigable HIDE key (playtest: hint row alone wasn't discoverable)
            _core.OnChar = TypeChar;
            _core.OnConfirm = Confirm;
            _core.OnClose = Dismiss;
            _coreHasSpace = hasSpace;
        }
        _core.Reset();
        _selectArmed = false;
        Open = true;
    }

    // Append into the vanilla string field. Vanilla's own Update does the rest next frame:
    // max-length cap + deny shake (MenuTextInput), upperOnly casing, password uppercasing,
    // and the per-keystroke tick/shake feedback off its old-vs-new diff.
    private void TypeChar(string s)
    {
        if (_password != null)
        {
            if (s == " ") return; // defensive: no SPACE key in password mode anyway
            PasswordRef(_password) = (PasswordRef(_password) ?? "") + s;
        }
        else if (_textInput != null)
        {
            string cur = TextCurrentRef(_textInput) ?? "";
            if (cur == "\b") cur = ""; // vanilla's clear-sentinel: treat as empty
            TextCurrentRef(_textInput) = cur + s;
        }
    }

    private void Backspace()
    {
        if (_password != null)
        {
            string cur = PasswordRef(_password) ?? "";
            if (cur.Length > 0) PasswordRef(_password) = cur.Remove(cur.Length - 1);
        }
        else if (_textInput != null)
        {
            string cur = TextCurrentRef(_textInput) ?? "";
            if (cur == "\b" || cur.Length == 0) return;
            TextCurrentRef(_textInput) = cur.Remove(cur.Length - 1);
        }
    }

    // The page's own public confirm -- what keyboard Enter triggers. Vanilla owns the
    // empty-text deny (SavesRename), the empty-password "Skip", feedback, and page close.
    private void Confirm()
    {
        if (_password != null)
        {
            _password.ConfirmButton();
            return;
        }
        if (_textInput == null) return;
        // Vanilla never shows more than one text-input page at a time, so first-match-wins is safe.
        var create = FindPage<MenuPageServerListCreateNew>();
        if (create != null) { create.ButtonConfirm(); return; }
        var rename = FindPage<MenuPageSavesRename>();
        if (rename != null) { rename.ButtonConfirm(); return; }
        var search = FindPage<MenuPageServerListSearch>();
        if (search != null) { search.ButtonConfirm(); return; }
        WarnOnce(new Exception("active MenuTextInput has no known confirm owner"));
    }

    // The page script usually sits on an ancestor of its MenuTextInput; scene-scan as a
    // fallback in case the hierarchy says otherwise.
    private T FindPage<T>() where T : MonoBehaviour
    {
        if (_textInput == null) return null;
        var onParent = _textInput.GetComponentInParent<T>();
        if (onParent != null) return onParent;
        foreach (var c in FindObjectsOfType<T>())
            if (c.isActiveAndEnabled) return c;
        return null;
    }

    private void Dismiss()
    {
        Open = false;
        _dismissed = true; // until this page closes (textInputActive drops)
    }

    private static void WarnOnce(Exception e)
    {
        if (_warned) return;
        _warned = true;
        Plugin.Log.LogWarning($"[MenuKeyboard] Error (further warnings suppressed): {e.Message}");
    }

    private void OnGUI()
    {
        if (!Open || _core == null) return;
        _core.Draw(Plugin.ChatKeyboardScale.Value); // shared scale: both keyboards stay visually identical
    }
}
