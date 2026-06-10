# Native Controller

Full controller support for [R.E.P.O.](https://store.steampowered.com/app/3241660/REPO/) (Semiwork, 2025) — built the **native** way. Instead of faking keyboard and mouse presses like other controller mods, Native Controller binds your gamepad **directly into the game's own input system**, so the game treats the pad as a real controller. No emulation jank, no double-input.

Every setting is exposed as a config entry. Tune to taste.

## Why "native"?

Other R.E.P.O. controller mods *emulate* — under the hood they translate your gamepad into fake keyboard/mouse events. Native Controller doesn't: it registers the gamepad into R.E.P.O.'s own `InputManager` actions. So the game's own sensitivity applies to your look, analog inputs stay analog (e.g. **holdable** push/pull instead of discrete scroll ticks), and there's no desync with a real keyboard/mouse.

## Features

- **Native gamepad binding** — your controller plugs straight into the game's input, not a keyboard/mouse emulation layer.
- **Right-stick look** with configurable speed, invert, and deadzone.
- **Full menu navigation** — move with the D-pad / left stick, A to confirm, B to back. Works in the vanilla menus *and* REPOConfig's mod menu (sliders, scrolling lists, tabs).
- **Emote wheel** — hold D-pad Down for a radial wheel of the game's 6 expressions; right stick picks, release plays it. Emotes auto-clear after a few seconds (configurable).
- **On-screen chat keyboard** — open chat with the pad (Back/View) and a controller-navigable QWERTY keyboard appears: D-pad / left stick moves, A types, B deletes, X = space, Start sends. Your message goes through the game's own chat — other players see you type and the TTS voice speaks it, exactly like keyboard chat.
- **Toggle sprint & toggle grab** — press Sprint once to keep sprinting (ends when stamina empties or you stop moving, like vanilla); press Grab once to keep holding, press again to let go.
- **Button prompts** — vanilla-style hints flanking the inventory (GRAB / LET GO / ROTATE / CLIMB), and the game's own key tags show your controller's buttons (SHOTGUN [X] instead of [E]) while the pad is the active input.
- **Controller Layout overlay** — an in-game cheat-sheet of the button map, opened from a button added to the Settings menu (needs MenuLib).
- **Kind-aware button glyphs** — prompts show the right icons for Xbox, PlayStation, or Switch controllers (auto-detected).
- **Holdable push/pull on the bumpers** — LB/RB drive the grab beam's push/pull as a real held input.
- **Aim assist** — an optional, gentle nudge toward grabbable items (when close) and enemies (when a weapon or staff is in hand). It's a bounded correction added to your own look — it never overpowers your turn or locks on. Fully configurable, off-switchable.
- **Splash-screen skip** — a controller button skips the intro logos.

## Default controls

| Action | Xbox | PlayStation |
|---|---|---|
| Move | Left stick | Left stick |
| Look | Right stick | Right stick |
| Jump | A | ✕ |
| Interact | X | □ |
| Tumble | B | ○ |
| Map | Y | △ |
| Sprint (press to toggle — stops on empty stamina / standing still) | L3 (left stick click) | L3 (left stick click) |
| Crouch | R3 (right stick click) | R3 (right stick click) |
| Grab (press to toggle — press again to let go) | RT | R2 |
| Rotate held object | LT (hold) | L2 (hold) |
| Pull / Push (held) | LB / RB | L1 / R1 |
| Inventory slots 1 / 2 / 3 | ← ↑ → | ← ↑ → |
| Pause menu | Start | Options |
| Chat (opens the on-screen keyboard) | Back | Create |
| Emote wheel (right stick picks, release toggles) | ↓ (hold, in game) | ↓ (hold, in game) |
| Show the Controller Layout overlay | Settings menu | Settings menu |

## Installation

1. Install [BepInEx 5.4](https://thunderstore.io/c/repo/p/BepInEx/BepInExPack/) for R.E.P.O.
2. Drop `NativeController.dll` into `BepInEx/plugins/`.
3. *(Optional)* Install [MenuLib](https://thunderstore.io/c/repo/p/nickklmao/MenuLib/) to get the "Controller Layout" button in the Settings menu.
4. Launch the game once to generate `BepInEx/config/com.pogwas.nativecontroller.cfg`, then edit it to taste — or use [REPOConfig](https://thunderstore.io/c/repo/p/nickklmao/REPOConfig/) for an in-game UI.

> **Steam Input:** Native Controller reads the pad through the Unity Input System. If you launch R.E.P.O. through Steam Input, set its controller layout to a plain **Gamepad** template (not a keyboard/mouse layout) so the pad comes through as a raw gamepad.

## Configuration sections

| Section | What it controls |
|---|---|
| `Gamepad` | Master toggle, right-stick look speed (X/Y), invert-Y, stick deadzone, menu cursor speed |
| `Aim Assist` | Toggle + item/enemy toggles, and the bounded-nudge tuning: Gain, MaxFraction (the no-lock cap), MaxDegPerFrame, IdleDrift, ActiveThreshold, MaxAngle (cone), ItemRange, EnemyRange, DeadZone |
| `Chat Keyboard` | On-screen chat keyboard: toggle + panel size |

## Bug reports

Please open an [Issue](https://github.com/Pogwas/NativeController/issues) and include:

- R.E.P.O. game version
- Mod version
- Your `BepInEx/LogOutput.log` (or the relevant ~50 lines around the bug)
- Other plugins installed
- Steps to reproduce

## Changelog

### 0.4.0

- **On-screen chat keyboard** — pad players can finally chat/TTS: opening chat with Back/View shows a navigable QWERTY panel (D-pad / left stick moves, A types, B deletes, X space, Start sends, Back/View closes). Typing goes through vanilla chat, so live type-out, multiplayer sync, and the TTS voice all behave exactly like keyboard chat. Config: `[Chat Keyboard] Enabled` / `Scale`.

### 0.3.0

- **Button prompts** — vanilla-tooltip-style hints flank the inventory bar: GRAB / LET GO / CLIMB on its right, ROTATE on its left, shown exactly when the game's own crosshair says the action is available. Only while the controller is the active input. Config: `[Prompts] Enabled`.
- **Controller buttons in the game's own key hints** — item tooltips and other key tags show your pad's buttons (SHOTGUN [X] instead of [E]) while the pad is the active input; switches back live when you touch the mouse. Config: `[Prompts] ControllerKeyTags`.
- Toggle-sprint now ends **instantly** when you stop moving, matching vanilla (the `SprintStopGraceSeconds` config is removed).
- Key hints show button **symbols** (✕ ○ □ △, ← ↑ → ↓) instead of text names.

### 0.2.0

- **Emote wheel** — hold D-pad Down in-game for a radial wheel of the game's 6 expressions; right stick picks, release plays it. Emotes auto-clear after a few seconds (configurable), and the face preview is reframed so you can actually see your face. The Controller Layout overlay moved to the Settings menu (its old hold-D-pad-Down trigger now opens the wheel).
- **Toggle sprint** — press Sprint (L3) once to keep sprinting; stops when stamina empties, you stop moving, or you press it again. Configurable.
- **Toggle grab** — press Grab (RT) once to keep holding a grabbed object; press again to let go. Auto-releases if the grab breaks. Configurable.

### 0.1.0

- Initial release. Native gamepad binding, right-stick look, full button mapping, menu navigation (vanilla + REPOConfig), Controller Layout overlay, kind-aware glyphs (Xbox / PlayStation / Switch), holdable push/pull on the bumpers, configurable aim assist, and splash-screen skip.

## License

MIT — see [LICENSE](LICENSE).
