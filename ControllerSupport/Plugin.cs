using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ControllerSupport;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("nickklmao.menulib", BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.pogwas.controllersupport";
    public const string PluginName = "Controller Support";
    public const string PluginVersion = "0.1.0";

    internal static Plugin Instance;
    internal static ManualLogSource Log;

    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> LookSpeedX;
    internal static ConfigEntry<float> LookSpeedY;
    internal static ConfigEntry<bool> InvertY;
    internal static ConfigEntry<float> StickDeadzone;
    internal static ConfigEntry<float> MenuCursorSpeed;

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
            _menuNavGO = new GameObject("ControllerSupport.MenuNav", typeof(MenuNavigator));
            DontDestroyOnLoad(_menuNavGO);
            Log.LogDebug("[Gamepad] MenuNavigator (re)created.");
        }
        if (_overlayGO == null)
        {
            _overlayGO = new GameObject("ControllerSupport.LayoutOverlay", typeof(ControllerLayoutOverlay));
            DontDestroyOnLoad(_overlayGO);
            Log.LogDebug("[Gamepad] ControllerLayoutOverlay (re)created.");
        }
        if (_aimAssistGO == null)
        {
            _aimAssistGO = new GameObject("ControllerSupport.AimAssist", typeof(AimAssist));
            DontDestroyOnLoad(_aimAssistGO);
            Log.LogDebug("[Gamepad] AimAssist (re)created.");
        }
    }
}
