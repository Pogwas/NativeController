# Native Controller

Full controller support for [R.E.P.O.](https://store.steampowered.com/app/3241660/REPO/) (Semiwork, 2025) — built the **native** way. Instead of faking keyboard and mouse presses like other controller mods, Native Controller binds your gamepad **directly into the game's own input system**, so the game treats the pad as a real controller. No emulation jank, no double-input.

Every setting is exposed as a config entry. Tune to taste.

## Why "native"?

Other R.E.P.O. controller mods *emulate* — under the hood they translate your gamepad into fake keyboard/mouse events. Native Controller doesn't: it registers the gamepad into R.E.P.O.'s own `InputManager` actions. So the game's own sensitivity applies to your look, analog inputs stay analog (e.g. **holdable** push/pull instead of discrete scroll ticks), and there's no desync with a real keyboard/mouse.

## Features

- **Native gamepad binding** — the pad goes straight into the game's input system, not a keyboard/mouse emulation layer.
- **Right-stick look** — configurable speed, invert, deadzone.
- **Full menu navigation** — D-pad / left stick moves, A confirms, B backs. Covers the vanilla menus, server browser, saved games and REPOConfig's mod menu.
- **Emote wheel** — hold D-pad Down; right stick picks, release plays. Auto-clears (configurable); the wheel also has a Mute Mic slot (toggles the same mic mute as keyboard B — multiplayer only).
- **On-screen chat keyboard** — open chat with Back/View and type with the pad. Goes through vanilla chat: live type-out, multiplayer sync, TTS.
- **Menu text fields on pad** — lobby name, save rename, server search and password screens auto-open the keyboard; vanilla rules apply; hides itself on mouse.
- **Toggle sprint, grab & crouch** — one press holds the action, a second (or vanilla's own stop conditions) ends it.
- **Button prompts** — GRAB / LET GO / ROTATE / CLIMB hints by the inventory, and the game's key tags show pad buttons (SHOTGUN [X] instead of [E]).
- **Inventory D-pad arrows** — slots show ← ↑ → instead of 1 2 3 on pad.
- **Controller Layout overlay** — button-map cheat-sheet in the Settings menu (needs MenuLib).
- **Kind-aware glyphs** — Xbox / PlayStation / Switch icons, auto-detected with manual override.
- **Holdable push/pull** — LB/RB drive the grab beam as a real held input.
- **Aim assist** — gentle, bounded nudge toward items and enemies; never overpowers your turn or locks on. Fully configurable.
- **Splash-screen skip** — a pad button skips the intro logos.

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
| Crouch (press to toggle — press again to stand) | R3 (right stick click) | R3 (right stick click) |
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
| `Gamepad` | Master toggle, right-stick look speed (X/Y), invert X/Y, stick deadzone, menu cursor speed, toggle sprint/grab/crouch, glyph style override (Auto / Xbox / PlayStation / Switch) |
| `Aim Assist` | Toggle + item/enemy toggles, and the bounded-nudge tuning: Gain, MaxFraction (the no-lock cap), MaxDegPerFrame, IdleDrift, ActiveThreshold, MaxAngle (cone), ItemRange, EnemyRange, DeadZone |
| `Emote Wheel` | Toggle, emote duration, face-preview camera framing |
| `Prompts` | Crosshair button prompts, controller key tags, inventory D-pad arrows |
| `Chat Keyboard` | On-screen chat keyboard: toggle + panel size |
| `Menu Keyboard` | On-screen keyboard for menu text fields: toggle (size follows Chat Keyboard Scale) |

## Bug reports

Please open an [Issue](https://github.com/Pogwas/NativeController/issues) and include:

- R.E.P.O. game version
- Mod version
- Your `BepInEx/LogOutput.log` (or the relevant ~50 lines around the bug)
- Other plugins installed
- Steps to reproduce

## Changelog

### 0.5.0

- **Menu text fields on pad** — lobby name, save rename, server search and password screens auto-open an on-screen keyboard on controller: A types, B deletes, X space, Start/ENTER confirms, HIDE or Back/View tucks it away, auto-hides on mouse. Types through the game's own fields, so vanilla rules apply. Config: `[Menu Keyboard] Enabled` (size follows `[Chat Keyboard] Scale`).
- **Server browser on pad** — rows, page arrows and the join popup are controller-navigable.
- **Saved games on pad** — select, load, rename and delete saves; RIGHT from a save jumps to LOAD SAVE.
- **Smarter menu navigation** — presses respect panel layouts, staggered menus navigate cleanly, the selection box snaps on long hops.
- **Chat keyboard** — added a CLOSE key.

### 0.4.0

- **On-screen chat keyboard** — open chat with Back/View for a navigable QWERTY panel: A types, B deletes, X space, Start sends. Goes through vanilla chat, so live type-out, multiplayer sync and TTS are intact. Config: `[Chat Keyboard] Enabled` / `Scale`.
- **Inventory D-pad arrows** — slot labels become ← ↑ → while the pad is active. Config: `[Prompts] InventoryArrows`.
- **Menu hint gating** — the move/select/back hint only shows on controller.
- **Invert-X** — right-stick horizontal look invert. Config: `[Gamepad] InvertX`.
- **Toggle crouch** — press Crouch (R3) to stay crouched, again to stand. Config: `[Gamepad] ToggleCrouch`.
- **Button-name style override** — force Xbox / PlayStation / Switch names. Config: `[Gamepad] GlyphStyle`.
- **Menu navigation fixes** — horizontal presses prefer same-row targets; page detection falls back to the UI hierarchy (MODS button no longer skipped).

### 0.3.0

- **Button prompts** — GRAB / LET GO / CLIMB / ROTATE hints by the inventory bar, exactly when the game's crosshair allows the action. Config: `[Prompts] Enabled`.
- **Controller buttons in the game's key hints** — SHOTGUN [X] instead of [E] while the pad is active; switches back live on mouse. Config: `[Prompts] ControllerKeyTags`.
- Toggle-sprint ends instantly when you stop moving, matching vanilla (`SprintStopGraceSeconds` removed).
- Key hints show button symbols (✕ ○ □ △, ← ↑ → ↓) instead of text names.

### 0.2.0

- **Emote wheel** — hold D-pad Down for the game's 6 expressions; right stick picks, release plays. Auto-clears after a configurable delay. Controller Layout overlay moved to the Settings menu.
- **Toggle sprint** — press Sprint (L3) once to keep sprinting; stops on empty stamina, standing still, or a second press. Configurable.
- **Toggle grab** — press Grab (RT) once to keep holding; again to let go. Configurable.

### 0.1.0

- Initial release: native gamepad bindings, right-stick look, full button mapping, menu navigation (vanilla + REPOConfig), Controller Layout overlay, Xbox / PlayStation / Switch glyphs, bumper push/pull, configurable aim assist, splash-screen skip.

## License

MIT — see [LICENSE](LICENSE).
