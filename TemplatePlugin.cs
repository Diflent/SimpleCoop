using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace SimpleCoop
{
    /// <summary>
    /// Template mod plugin - copy this to create new mods
    /// </summary>
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class SimpleCoop : BaseUnityPlugin
    {
        internal static new ManualLogSource Logger;
        private Harmony _harmony;

        private void Awake()
        {
            Logger = base.Logger;
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");

            // Apply Harmony patches
            _harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            _harmony.PatchAll(typeof(SimpleCoop).Assembly);
            
            Logger.LogInfo("Harmony patches applied successfully!");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID = "com.Diflent.ostranauts.SimpleCoop";
        public const string PLUGIN_NAME = "SimpleCoop";
        public const string PLUGIN_VERSION = "0.0.1";
    }
}


