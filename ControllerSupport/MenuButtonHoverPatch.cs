using System.Reflection;
using HarmonyLib;

namespace ControllerSupport;

// While the controller is the active menu input, force the game to highlight the controller-selected
// button (and de-highlight the rest), using the game's OWN methods: set the hover flag, force the text
// colour, and call the public OnHovering() which positions the selection box on the button (the game
// centres it). This needs no cursor warping and no screen-coordinate math. When the controller is not
// the active input, this does nothing and the game's normal mouse hover applies.
[HarmonyPatch(typeof(MenuButton), "ProcessHover")]
internal static class MenuButtonHoverPatch
{
    private static readonly AccessTools.FieldRef<MenuButton, bool> HoveringRef =
        AccessTools.FieldRefAccess<MenuButton, bool>("hovering");
    private static readonly FieldInfo ButtonTextField = AccessTools.Field(typeof(MenuButton), "buttonText");
    private static PropertyInfo s_colorProp;

    [HarmonyPostfix]
    public static void Postfix(MenuButton __instance)
    {
        if (!MenuNavigator.ControllerActive) return;

        bool selected = ReferenceEquals(__instance, MenuNavigator.Selected);
        HoveringRef(__instance) = selected;

        // Override the text colour the game just set from mouse-hover.
        var txt = ButtonTextField != null ? ButtonTextField.GetValue(__instance) : null;
        if (txt != null)
        {
            if (s_colorProp == null) s_colorProp = txt.GetType().GetProperty("color");
            s_colorProp?.SetValue(txt, selected ? __instance.colorHover : __instance.colorNormal);
        }

        // Put the selection box on the selected button (game-positioned, always centred).
        if (selected) __instance.OnHovering();
    }
}
