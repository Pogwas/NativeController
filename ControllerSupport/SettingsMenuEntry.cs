using MenuLib;
using UnityEngine;

namespace ControllerSupport;

// Isolated so MenuLib types are only JIT-resolved when MenuLib is actually loaded — Plugin guards the
// Register() call behind a Chainloader presence check, so when MenuLib is absent this type is never
// touched and the mod loads normally. Adds a "Controller Layout" button to the game's Settings menu,
// shown only when a controller is connected; clicking toggles the overlay.
internal static class SettingsMenuEntry
{
    internal static void Register()
    {
        MenuAPI.AddElementToSettingsMenu(parent =>
        {
            if (ControllerDetect.Current == ControllerDetect.Kind.None) return;
            MenuAPI.CreateREPOButton("Controller Layout", ControllerLayoutOverlay.Toggle, parent, new Vector2(180f, 30f));
        });
    }
}
