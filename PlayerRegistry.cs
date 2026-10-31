using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using LiteNetLib;
using LiteNetLib.Utils;

namespace SimpleCoop
{
    /// Кто каким персонажем управляет.
    ///
    /// Хост: при HELLO подбирает клиенту члена экипажа (или нанимает нового),
    ///       помнит закрепление по ключу игрока — при переподключении выдаст того же.
    /// Клиент: знает strID своего персонажа, после загрузки выделяет его.
    public static class PlayerRegistry
    {
        public const string MsgAssign = "ASSIGN"; // хост → клиент: strID вашего персонажа

        // ---------------- Настройки ----------------

        /// Ключ этого игрока (из конфига). По нему хост узнаёт вас при переподключении.
        public static string LocalKey = "";

        public static string LocalName = "";

        /// Хост: нанимать нового члена экипажа, если свободных нет.
        public static bool AutoCreateCrew = true;

        /// <summary>Хост: где хранить закрепления «ключ = strID» между запусками игры.</summary>
        private static ConfigEntry<string>? _assignedCfg;

        public static void Init(ConfigEntry<string> assignedCrew, string localKey, string localName, bool autoCreate)
        {
            _assignedCfg = assignedCrew;
            LocalKey = localKey;
            LocalName = localName;
            AutoCreateCrew = autoCreate;
            Load();
        }

        // ===================================================================
        //                               ХОСТ
        // ===================================================================

        private static readonly Dictionary<string, string> _byKey = new Dictionary<string, string>();

        // Только те, кто подключён.
        private static readonly Dictionary<int, string> _crewByPeer = new Dictionary<int, string>();
        private static readonly Dictionary<int, string> _keyByPeer = new Dictionary<int, string>();

        /// Подобрать персонажа для подключившегося клиента.
        public static string? AssignOnHost(NetPeer peer, string key, string name, out string reason)
        {
            reason = "";

            if (string.IsNullOrEmpty(key))
            {
                reason = "пустой ключ игрока (Player.Key в конфиге)";
                return null;
            }

            if (_keyByPeer.ContainsValue(key))
            {
                reason = "игрок с таким ключом уже подключён (скопировали конфиг вместе с игрой? очистите Player.Key)";
                return null;
            }

            CondOwner? crew = null;

            // Тот же персонаж, что был у этого игрока раньше
            if (_byKey.TryGetValue(key, out string oldId))
            {
                crew = CoLookup.Find(oldId);
                if (!IsUsable(crew) || IsTaken(crew!))
                    crew = null;
            }

            // Свободный член экипажа
            if (crew == null)
                crew = FindFreeCrew();

            // Нанять нового
            if (crew == null && AutoCreateCrew)
                crew = CreateCrew();

            if (crew == null)
            {
                reason = "нет свободного члена экипажа: наймите кого-нибудь или включите Host.AutoCreateCrew";
                return null;
            }

            string id = CoLookup.Id(crew);
            _byKey[key] = id;
            _crewByPeer[peer.Id] = id;
            _keyByPeer[peer.Id] = key;
            Save();

            // Ручное управление: ИИ хоста не будет сам раздавать этому персонажу задачи
            if (!crew.HasCond("IsAIManual"))
                crew.AddCondAmount("IsAIManual", 1.0, 0.0, 0f);

            GameLog.Info($"[Players] {name} → {crew.strName} ({id})");
            return id;
        }

        public static bool Owns(NetPeer peer, string crewId)
        {
            if (_crewByPeer.TryGetValue(peer.Id, out string id) && id == crewId)
                return true;

            GameLog.Warn($"[Players] {peer.EndPoint} tried to control {crewId} — not theirs");
            return false;
        }

        public static bool IsRemoteControlled(CondOwner co)
        {
            return co != null && _crewByPeer.ContainsValue(CoLookup.Id(co));
        }

        /// Хост: клиент отключился — персонаж возвращается под управление ИИ.
        public static void OnPeerDisconnected(NetPeer peer)
        {
            if (_crewByPeer.TryGetValue(peer.Id, out string id))
            {
                Release(id);
                _crewByPeer.Remove(peer.Id);
            }
            _keyByPeer.Remove(peer.Id);
        }

        private static void Release(string crewId)
        {
            CondOwner? co = CoLookup.Find(crewId);
            if (co != null && !co.bDestroyed)
            {
                co.ZeroCondAmount("IsAIManual");
                GameLog.Info($"[Players] {co.strName} is back under AI control");
            }
        }

        private static bool IsUsable(CondOwner? co)
        {
            return co != null
                && !co.bDestroyed
                && co.bAlive
                && co.HasCond("IsHuman")
                && co != CrewSim.coPlayer;
        }

        private static bool IsTaken(CondOwner co) => _crewByPeer.ContainsValue(CoLookup.Id(co));

        /// Член экипажа компании хоста, которого никто не занял и не «забронировал».
        private static CondOwner? FindFreeCrew()
        {
            var company = CrewSim.coPlayer?.Company;
            if (company?.mapRoster == null) return null;

            var reserved = new HashSet<string>(_byKey.Values); // закреплены за игроками

            foreach (string id in company.mapRoster.Keys)
            {
                if (reserved.Contains(id)) continue;

                CondOwner? co = CoLookup.Find(id);
                if (IsUsable(co) && !IsTaken(co!))
                    return co;
            }
            return null;
        }

        /// Нанять нового случайного члена экипажа рядом с хостом.
        private static CondOwner? CreateCrew()
        {
            if (CrewSim.objInstance == null || CrewSim.coPlayer == null) return null;

            Tile? near = CrewSim.coPlayer.Pathfinder != null ? CrewSim.coPlayer.Pathfinder.tilCurrent : null;

            CondOwner co = CrewSim.objInstance.GetRandomCrew(near);
            if (co == null) return null;

            CrewSim.objInstance.AddNpcToRoster(co);
            GameLog.Info($"[Players] Hired new crew member {co.strName} for a player");
            return co;
        }

        // ---- сохранение закреплений в конфиг: "ключ=strID;ключ=strID" ----

        private static void Load()
        {
            _byKey.Clear();
            if (_assignedCfg == null || string.IsNullOrEmpty(_assignedCfg.Value)) return;

            foreach (string pair in _assignedCfg.Value.Split(';'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0)
                    _byKey[pair.Substring(0, eq)] = pair.Substring(eq + 1);
            }
        }

        private static void Save()
        {
            if (_assignedCfg == null) return;
            _assignedCfg.Value = string.Join(";", _byKey.Select(kv => kv.Key + "=" + kv.Value));
        }

        // ===================================================================
        //                               КЛИЕНТ
        // ===================================================================

        /// Клиент: strID своего персонажа (приходит в ASSIGN до сохранения).
        public static string? OwnCrewId { get; private set; }

        public static CondOwner? OwnCrew => OwnCrewId == null ? null : CoLookup.Find(OwnCrewId);

        public static bool IsMine(CondOwner? co) =>
            co != null && OwnCrewId != null && CoLookup.Id(co) == OwnCrewId;

        public static void OnAssign(NetPacketReader r)
        {
            OwnCrewId = r.GetString(128);
            GameLog.Info($"[Players] Host assigned you character {OwnCrewId}");
        }

        /// Клиент: мир загружен — выделить своего персонажа и навести камеру.
        public static void OnWorldLoaded()
        {
            CondOwner? co = OwnCrew;
            if (co == null)
            {
                GameLog.Error($"[Players] Your character {OwnCrewId} not found in the world");
                return;
            }

            if (CrewSim.objInstance != null)
            {
                CrewSim.objInstance.SetBracketTarget(co.strID, false);
                CrewSim.objInstance.CamCenter(co);
            }

            GameLog.Info($"[Players] You are playing as {co.strName}");
        }

        // ===================================================================

        /// Остановка сети: отпустить всех персонажей клиентов.
        public static void Reset()
        {
            foreach (string id in _crewByPeer.Values)
                Release(id);

            _crewByPeer.Clear();
            _keyByPeer.Clear();
            OwnCrewId = null;
        }
    }
}