using HarmonyLib;
using UnityEngine;

namespace NativeController;

// REPO only scrolls a MenuScrollBox while the MOUSE is hovering it: MenuScrollBox.Update recomputes
// scrollBoxActive from menuElementHover.isHovering each frame and early-returns if it's false, so the
// list never moves. Controller nav (MenuNavigator.ScrollIntoView) calls ScrollToChild, which sets the
// correct scroll target, but the game's Update bails before applying it unless the cursor is over the
// box. That's why d-pad scrolling "worked only once" (single-scroll-box pages keep scrollBoxActive's
// default true; multi-scroll-box pages flip it false with no mouse hover).
//
// This postfix runs AFTER MenuScrollBox.Update (so the hover recompute can't clobber it) and, when the
// controller owns the selection inside this box and the game skipped its scroll, applies the pending
// scroll toward the target ScrollToChild already computed — reusing the game's own math, no coordinate
// duplication. It deliberately does nothing when scrollBoxActive is true (mouse hovering, or a single
// scroll-box page) so it never fights the game's native scrolling.
[HarmonyPatch(typeof(MenuScrollBox), "Update")]
internal static class MenuScrollBoxScrollPatch
{
    private static readonly AccessTools.FieldRef<MenuScrollBox, bool> ActiveRef =
        AccessTools.FieldRefAccess<MenuScrollBox, bool>("scrollBoxActive");
    private static readonly AccessTools.FieldRef<MenuScrollBox, float> HandleTargetRef =
        AccessTools.FieldRefAccess<MenuScrollBox, float>("scrollHandleTargetPosition");
    private static readonly AccessTools.FieldRef<MenuScrollBox, float> ScrollerStartRef =
        AccessTools.FieldRefAccess<MenuScrollBox, float>("scrollerStartPosition");
    private static readonly AccessTools.FieldRef<MenuScrollBox, float> ScrollerEndRef =
        AccessTools.FieldRefAccess<MenuScrollBox, float>("scrollerEndPosition");
    private static readonly AccessTools.FieldRef<MenuScrollBox, float> ScrollAmountRef =
        AccessTools.FieldRefAccess<MenuScrollBox, float>("scrollAmount");

    private static void Postfix(MenuScrollBox __instance)
    {
        if (!MenuNavigator.ControllerActive) return;

        var sel = MenuNavigator.Selected;
        if (sel == null) return;
        if (sel.GetComponentInParent<MenuScrollBox>() != __instance) return;

        // Game already scrolled this frame (mouse hovering, or single-box page) — don't fight it.
        if (ActiveRef(__instance)) return;
        if (__instance.scrollBar == null || !__instance.scrollBar.activeSelf) return;

        var handle = __instance.scrollHandle;
        var bg = __instance.scrollBarBackground;
        var scroller = __instance.scroller;
        if (handle == null || bg == null || scroller == null) return;

        // Drive the scroll handle toward the target ScrollToChild set, then mirror the game's mapping of
        // handle position -> scrollAmount -> scroller position (MenuScrollBox.Update lines 207-220).
        float target = HandleTargetRef(__instance);
        float newHandleY = Mathf.Lerp(handle.localPosition.y, target, Time.deltaTime * 20f);
        handle.localPosition = new Vector3(handle.localPosition.x, newHandleY, handle.localPosition.z);

        float min = handle.sizeDelta.y / 2f;
        float max = bg.rect.height - handle.sizeDelta.y / 2f;
        float amt = Mathf.Clamp01(Mathf.InverseLerp(min, max, handle.localPosition.y));
        ScrollAmountRef(__instance) = amt;
        scroller.localPosition = new Vector3(
            scroller.localPosition.x,
            Mathf.Lerp(ScrollerStartRef(__instance), ScrollerEndRef(__instance), amt),
            scroller.localPosition.z);
    }
}
