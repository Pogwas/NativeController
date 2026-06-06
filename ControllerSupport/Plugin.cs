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

    private Harmony _harmony;
    private static GameObject _menuNavGO;
    private static GameObject _overlayGO;

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
    }
}
