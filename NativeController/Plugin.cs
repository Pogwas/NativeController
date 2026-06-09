using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NativeController;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("nickklmao.menulib", BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.pogwas.nativecontroller";
    public const string PluginName = "Native Controller";
    public const string PluginVersion = "0.2.0";

    internal static Plugin Instance;
    internal static ManualLogSource Log;

    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> LookSpeedX;
    internal static ConfigEntry<float> LookSpeedY;
    internal static ConfigEntry<bool> InvertY;
    internal static ConfigEntry<float> StickDeadzone;
    internal static ConfigEntry<float> MenuCursorSpeed;
    internal static ConfigEntry<bool> EmoteWheelEnabled;
    internal static ConfigEntry<float> EmoteZoomOut;
    internal static ConfigEntry<float> EmoteCameraLower;
    internal static ConfigEntry<float> EmoteDurationSeconds;
    internal static ConfigEntry<bool> PromptsEnabled;
    internal static ConfigEntry<bool> ControllerKeyTags;
    internal static ConfigEntry<bool> SprintToggle;
    internal static ConfigEntry<bool> GrabToggle;
    internal static ConfigEntry<float> SprintStopGraceSeconds;

    internal static ConfigEntry<bool> AimAssistEnabled;
    internal static ConfigEntry<bool> AimAssistItems;
    internal static ConfigEntry<bool> AimAssistEnemies;
    internal static ConfigEntry<float> AimAssistGain;
    internal static ConfigEntry<float> AimAssistMaxFraction;
    internal static ConfigEntry<float> AimAssistMaxDegPerFrame;
    internal static ConfigEntry<float> AimAssistIdleDrift;
    internal static ConfigEntry<float> AimAssistActiveThreshold;
    internal static ConfigEntry<float> AimAssistDeadZone;
    internal static ConfigEntry<float> AimAssistItemRange;
    internal static ConfigEntry<float> AimAssistEnemyRange;
    internal static ConfigEntry<float> AimAssistMaxAngle;

    private Harmony _harmony;
    private static GameObject _menuNavGO;
    private static GameObject _overlayGO;
    private static GameObject _aimAssistGO;
    private static GameObject _emoteWheelGO;
    private static GameObject _grabPromptsGO;

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        Log.LogInfo($"{PluginName} v{PluginVersion} loading...");

        Enabled = Config.Bind("Gamepad", "Enabled", true,
            "Master toggle for gamepad support. When off, the controller does nothing and keyboard/mouse are unaffected.");
        LookSpeedX = Config.Bind("Gamepad", "LookSpeedX", 1.0f,
            new ConfigDescription("Right-stick horizontal camera speed.", new AcceptableValueRange<float>(0.1f, 20f)));
        LookSpeedY = Config.Bind("Gamepad", "LookSpeedY", 1.0f,
            new ConfigDescription("Right-stick vertical camera speed.", new AcceptableValueRange<float>(0.1f, 20f)));
        InvertY = Config.Bind("Gamepad", "InvertY", false, "Invert right-stick vertical look.");
        StickDeadzone = Config.Bind("Gamepad", "StickDeadzone", 0.15f,
            new ConfigDescription("Right-stick deadzone (ignore small movements).", new AcceptableValueRange<float>(0f, 0.6f)));
        MenuCursorSpeed = Config.Bind("Gamepad", "MenuCursorSpeed", 12f,
            new ConfigDescription("Left-stick cursor speed when navigating menus.", new AcceptableValueRange<float>(1f, 50f)));
        EmoteWheelEnabled = Config.Bind("Emote Wheel", "Enabled", true,
            "Hold D-pad Down in-game to open an emote wheel (right stick to pick, release to toggle the expression on/off).");
        EmoteZoomOut = Config.Bind("Emote Wheel", "PreviewZoomOut", 0.5f,
            new ConfigDescription("How far the face-preview camera pulls back while the wheel is open (metres). Tune live in the in-game mod settings until the whole face is visible.", new AcceptableValueRange<float>(0f, 3f)));
        EmoteCameraLower = Config.Bind("Emote Wheel", "PreviewCameraLower", 0.25f,
            new ConfigDescription("How far the face-preview camera drops while the wheel is open (metres) — raises the head in the picture. Tune live alongside PreviewZoomOut.", new AcceptableValueRange<float>(-1f, 1f)));
        EmoteDurationSeconds = Config.Bind("Emote Wheel", "EmoteDurationSeconds", 5f,
            new ConfigDescription("How long a picked emote stays on your face before returning to normal. 0 = it stays until you pick it again on the wheel.", new AcceptableValueRange<float>(0f, 30f)));
        PromptsEnabled = Config.Bind("Prompts", "Enabled", true,
            "Show controller prompts near the crosshair (Grab when aiming at a grabbable, Let go / Rotate while holding, Climb when tumbling at a wall). Only shown while the controller is the active input.");
        ControllerKeyTags = Config.Bind("Prompts", "ControllerKeyTags", true,
            "Show controller buttons inside the game's own key hints (e.g. SHOTGUN [X] instead of [E]) while the controller is the active input.");
        SprintToggle = Config.Bind("Gamepad", "ToggleSprint", true,
            "Press Sprint (L3) once to keep sprinting; it stops when stamina runs out, you stop moving, or you press it again. Applies while a gamepad is connected (also affects keyboard Sprint).");
        GrabToggle = Config.Bind("Gamepad", "ToggleGrab", true,
            "Press Grab (RT) once to keep holding a grabbed object; press again to let go. Auto-releases if the grab breaks. Applies while a gamepad is connected (also affects keyboard/mouse Grab).");
        SprintStopGraceSeconds = Config.Bind("Gamepad", "SprintStopGraceSeconds", 0.35f,
            new ConfigDescription("How long you can be stationary before toggle-sprint switches off.", new AcceptableValueRange<float>(0f, 2f)));

        AimAssistEnabled = Config.Bind("Aim Assist", "Enabled", true,
            "Master toggle for aim assist (gently nudges your view toward items, and toward enemies when a weapon/staff is held).");
        AimAssistItems = Config.Bind("Aim Assist", "AssistItems", true,
            "Pull your view slightly toward a nearby grabbable item near your crosshair.");
        AimAssistEnemies = Config.Bind("Aim Assist", "AssistEnemies", true,
            "Pull your view slightly toward an enemy when you hold a weapon or magic staff and aim near one.");
        AimAssistGain = Config.Bind("Aim Assist", "Gain", 0.07f,
            new ConfigDescription("Fraction of the remaining angle the nudge closes per frame (before clamping). Higher = quicker settle. Kept tiny on purpose.", new AcceptableValueRange<float>(0f, 0.5f)));
        AimAssistMaxFraction = Config.Bind("Aim Assist", "MaxFraction", 0.30f,
            new ConfigDescription("While you are turning, the assist is capped at this fraction of YOUR OWN look this frame. Below 1.0 it can never out-turn you — this is the no-lock guarantee.", new AcceptableValueRange<float>(0f, 0.9f)));
        AimAssistMaxDegPerFrame = Config.Bind("Aim Assist", "MaxDegPerFrame", 0.5f,
            new ConfigDescription("Absolute ceiling (degrees/frame) on the nudge, so a fast flick is never visibly curved.", new AcceptableValueRange<float>(0.05f, 3f)));
        AimAssistIdleDrift = Config.Bind("Aim Assist", "IdleDrift", 0.10f,
            new ConfigDescription("Gentle drift (degrees/frame) toward the target ONLY when you are not moving the view; faded near the target so a parked camera never fully homes.", new AcceptableValueRange<float>(0f, 0.5f)));
        AimAssistActiveThreshold = Config.Bind("Aim Assist", "ActiveThreshold", 0.05f,
            new ConfigDescription("Per-frame look (degrees) above which you count as 'turning' (uses the MaxFraction cap) vs 'stopped' (uses IdleDrift).", new AcceptableValueRange<float>(0f, 1f)));
        AimAssistDeadZone = Config.Bind("Aim Assist", "DeadZone", 1.5f,
            new ConfigDescription("Inside this angle (degrees) of the target the assist does nothing — removes the hard settle/snap and lets your own aim finish the last bit.", new AcceptableValueRange<float>(0f, 8f)));
        AimAssistItemRange = Config.Bind("Aim Assist", "ItemRange", 5f,
            new ConfigDescription("Max distance (metres) to assist toward an item.", new AcceptableValueRange<float>(0f, 15f)));
        AimAssistEnemyRange = Config.Bind("Aim Assist", "EnemyRange", 25f,
            new ConfigDescription("Max distance (metres) to assist toward an enemy.", new AcceptableValueRange<float>(0f, 60f)));
        AimAssistMaxAngle = Config.Bind("Aim Assist", "MaxAngle", 12f,
            new ConfigDescription("Cone (degrees from your crosshair) a target must be within to be assisted.", new AcceptableValueRange<float>(1f, 45f)));

        ControllerDetect.Init();

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll();

        // Soft dependency: only touch MenuLib (via SettingsMenuEntry) when it's actually loaded.
        if (Chainloader.PluginInfos.ContainsKey("nickklmao.menulib"))
        {
            SettingsMenuEntry.Register();
            Log.LogInfo("[Gamepad] MenuLib detected — added Controller Layout to the Settings menu.");
        }

        SceneManager.sceneLoaded += OnSceneLoaded;

        Log.LogInfo($"{PluginName} loaded.");
    }

    // REPO destroys DontDestroyOnLoad objects at boot, so (re)create the menu navigator each scene load.
    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (_menuNavGO == null)
        {
            _menuNavGO = new GameObject("NativeController.MenuNav", typeof(MenuNavigator));
            DontDestroyOnLoad(_menuNavGO);
            Log.LogDebug("[Gamepad] MenuNavigator (re)created.");
        }
        if (_overlayGO == null)
        {
            _overlayGO = new GameObject("NativeController.LayoutOverlay", typeof(ControllerLayoutOverlay));
            DontDestroyOnLoad(_overlayGO);
            Log.LogDebug("[Gamepad] ControllerLayoutOverlay (re)created.");
        }
        if (_aimAssistGO == null)
        {
            _aimAssistGO = new GameObject("NativeController.AimAssist", typeof(AimAssist));
            DontDestroyOnLoad(_aimAssistGO);
            Log.LogDebug("[Gamepad] AimAssist (re)created.");
        }
        if (_emoteWheelGO == null)
        {
            _emoteWheelGO = new GameObject("NativeController.EmoteWheel", typeof(EmoteWheel));
            DontDestroyOnLoad(_emoteWheelGO);
            Log.LogDebug("[Gamepad] EmoteWheel (re)created.");
        }
        if (_grabPromptsGO == null)
        {
            _grabPromptsGO = new GameObject("NativeController.GrabPrompts", typeof(GrabPromptOverlay));
            DontDestroyOnLoad(_grabPromptsGO);
            Log.LogDebug("[Gamepad] GrabPromptOverlay (re)created.");
        }
        EmoteWheel.ResetState(); // every scene load: forget toggled faces, refresh labels
    }
}
