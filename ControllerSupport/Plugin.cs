using BepInEx;
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

    private Harmony _harmony;

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        Log.LogInfo($"{PluginName} v{PluginVersion} loading...");

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll();

        Log.LogInfo($"{PluginName} loaded.");
    }
}
