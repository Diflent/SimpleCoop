using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SimpleCoop
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class SimpleCoop : BaseUnityPlugin
    {
        internal static new ManualLogSource Logger = null!;
        public static SimpleCoop Instance { get; private set; } = null!;
        public NetworkManager? Net => _net;
        private Harmony? _harmony;
        private NetworkManager? _net;

        // Конфиг
        private ConfigEntry<string> _clientIp = null!;
        private ConfigEntry<int> _hostPort = null!;
        private ConfigEntry<int> _clientPort = null!;
        private ConfigEntry<string> _connectionKey = null!;

        private void Awake()
        {
            Logger = base.Logger;
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");

            // --- Конфиг ---
            _clientIp = Config.Bind(
                "Network",
                "HostIP",
                "26.0.0.1",
                "IP хоста из Radmin VPN (для клиента)");

            _hostPort = Config.Bind(
                "Network",
                "HostPort",
                7777,
                "Порт сервера (хост)");

            _clientPort = Config.Bind(
                "Network",
                "ClientPort",
                7779,
                "Локальный порт клиента");

            _connectionKey = Config.Bind(
                "Network",
                "ConnectionKey",
                "SimpleCoopKey",
                "Ключ подключения (должен совпадать у хоста и клиента)");

            // --- Harmony ---
            _harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            _harmony.PatchAll(typeof(SimpleCoop).Assembly);

            // --- Сеть ---
            _net = new NetworkManager(
                Logger,
                _hostPort.Value,
                _clientPort.Value,
                _connectionKey.Value);

            Logger.LogInfo("Harmony patches applied successfully!");
            Logger.LogInfo("[SimpleCoop] Keys: F5 = Host | F6 = Client | F7 = Stop | F8 = Send test message");
            Logger.LogInfo($"[SimpleCoop] Config: HostIP={_clientIp.Value}, HostPort={_hostPort.Value}, ClientPort={_clientPort.Value}");

            // UI оверлей мода
            var go = new GameObject("SimpleCoopOverlay");
            DontDestroyOnLoad(go);
            go.AddComponent<ModOverlay>();

            GameLog.Info("Overlay ready (F10 — hide/show)");
        }

        private void Update()
        {
            _net?.Update();
            PositionSync.Update(Time.deltaTime);

            if (UnityEngine.Input.GetKeyDown(KeyCode.F5))
            {
                _net?.StartHost();
            }
            else if (UnityEngine.Input.GetKeyDown(KeyCode.F6))
            {
                _net?.StartClient(_clientIp.Value);
            }
            else if (UnityEngine.Input.GetKeyDown(KeyCode.F7))
            {
                _net?.Stop();
            }
            else if (UnityEngine.Input.GetKeyDown(KeyCode.F8))
            {
                _net?.SendChat($"Test from {_net?.Role} at {Time.time:F1}");
            }
            else if (UnityEngine.Input.GetKeyDown(KeyCode.F9))
            {
                _net?.SendCommand("TEST_ACTION");
            }
        }

        private void OnDestroy()
        {
            _net?.Stop();
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