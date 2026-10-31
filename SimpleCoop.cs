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

        // Игрок / хост
        private ConfigEntry<string> _playerName = null!;
        private ConfigEntry<string> _playerKey = null!;
        private ConfigEntry<bool> _autoCreateCrew = null!;
        private ConfigEntry<string> _assignedCrew = null!;

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

            // --- Игрок ---
            _playerName = Config.Bind(
                "Player",
                "Name",
                System.Environment.UserName,
                "Ваше имя — показывается в логе хоста");

            _playerKey = Config.Bind(
                "Player",
                "Key",
                "",
                "Уникальный ключ: по нему хост возвращает вам того же персонажа. " +
                "Заполняется автоматически. Если копируете игру для второго клиента — очистите.");

            if (string.IsNullOrEmpty(_playerKey.Value))
                _playerKey.Value = System.Guid.NewGuid().ToString("N"); // BepInEx сам запишет в .cfg

            // --- Хост ---
            _autoCreateCrew = Config.Bind(
                "Host",
                "AutoCreateCrew",
                true,
                "Нанимать нового члена экипажа для игрока, если свободных нет");

            _assignedCrew = Config.Bind(
                "Host",
                "AssignedCrew",
                "",
                "Служебное: какой персонаж закреплён за каким игроком (ключ=strID;...)");

            PlayerRegistry.Init(_assignedCrew, _playerKey.Value, _playerName.Value, _autoCreateCrew.Value);

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

            // --- Синхронизация мира и времени ---
            TimeSync.Init();
            CrewSim.OnGameFinishedLoading.AddListener(SaveTransfer.OnGameFinishedLoading);
            CrewSim.OnGameEnd.AddListener(OnGameEnd);
        }

        private void Update()
        {
            _net?.Update();

            SaveTransfer.Update();
            TimeSync.Update();

            // Host: snapshot from Update. Client: interpolation in LateUpdate (on top of game movement).
            if (NetworkManager.Current?.Role == NetworkManager.NetRole.Host)
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
            else if (UnityEngine.Input.GetKeyDown(KeyCode.F11))
            {
                ActionSync.Verbose = !ActionSync.Verbose;
                GameLog.Info($"[ActionSync] Verbose = {ActionSync.Verbose}");
            }
        }

        private void LateUpdate()
        {
            if (NetworkManager.Current?.Role == NetworkManager.NetRole.Client)
                PositionSync.Update(Time.deltaTime);

            // Inventory changes made this frame (both roles)
            ItemSync.Flush();
        }

        /// Игра выгружает мир (выход в меню / загрузка другого сохранения).
        private void OnGameEnd()
        {
            var net = NetworkManager.Current;
            if (net == null || !net.IsRunning) return;

            // У клиента OnGameEnd срабатывает и при загрузке мира хоста — тогда он ещё не «готов»
            if (net.Role == NetworkManager.NetRole.Host ||
                (net.Role == NetworkManager.NetRole.Client && net.IsWorldReady))
            {
                GameLog.Info("[Net] World unloaded, disconnecting");
                net.Stop();
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
        public const string PLUGIN_VERSION = "0.5.1";
    }
}