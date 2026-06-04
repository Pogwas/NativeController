using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ControllerSupport;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
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

    private Harmony _harmony;

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        Log.LogInfo($"{PluginName} v{PluginVersion} loading...");

        Enabled = Config.Bind("Gamepad", "Enabled", true,
            "Master toggle for gamepad support. When off, the controller does nothing and keyboard/mouse are unaffected.");
        LookSpeedX = Config.Bind("Gamepad", "LookSpeedX", 1.5f,
            new ConfigDescription("Right-stick horizontal camera speed.", new AcceptableValueRange<float>(0.1f, 20f)));
        LookSpeedY = Config.Bind("Gamepad", "LookSpeedY", 1.5f,
            new ConfigDescription("Right-stick vertical camera speed.", new AcceptableValueRange<float>(0.1f, 20f)));
        InvertY = Config.Bind("Gamepad", "InvertY", false, "Invert right-stick vertical look.");
        StickDeadzone = Config.Bind("Gamepad", "StickDeadzone", 0.15f,
            new ConfigDescription("Right-stick deadzone (ignore small movements).", new AcceptableValueRange<float>(0f, 0.6f)));

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll();

        Log.LogInfo($"{PluginName} loaded.");
    }
}
