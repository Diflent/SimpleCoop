using System;
using System.Collections.Generic;
using BepInEx.Logging;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine.SceneManagement;

namespace SimpleCoop
{
    public class NetworkManager
    {
        public enum NetRole
        {
            None,
            Host,
            Client
        }

        /// <summary>До 4 игроков: хост + 3 клиента.</summary>
        public const int MaxClients = 3;

        /// <summary>Увеличивать при КАЖДОМ изменении формата любого пакета.</summary>
        public const int ProtocolVersion = 3;

        public static NetworkManager? Current { get; private set; }
        public NetRole Role { get; private set; } = NetRole.None;
        public bool IsRunning => _netManager != null && _netManager.IsRunning;

        /// <summary>Клиент: мир хоста загружен, можно обмениваться мировыми пакетами.</summary>
        public bool IsWorldReady { get; private set; }

        /// <summary>
        /// Сколько получателей у «мировых» пакетов (ACT_FX, ITEM_LOC, SNAP_POS, TIME...).
        /// Хост — число загрузившихся клиентов; клиент — 1, если мир загружен.
        /// </summary>
        public int PeerCount =>
            Role == NetRole.Host ? _readyPeers.Count :
            Role == NetRole.Client && IsWorldReady ? 1 : 0;

        private NetManager? _netManager;
        private EventBasedNetListener? _listener;
        private readonly ManualLogSource _log;
        private readonly NetDataWriter _writer = new NetDataWriter();
        private readonly HashSet<int> _readyPeers = new HashSet<int>();

        private readonly int _hostPort;
        private readonly int _clientPort;
        private readonly string _connectionKey;

        public NetworkManager(ManualLogSource log, int hostPort, int clientPort, string connectionKey)
        {
            _log = log;
            _hostPort = hostPort;
            _clientPort = clientPort;
            _connectionKey = connectionKey;
        }

        /// <summary>Загружен ли игровой мир (а не главное меню / экран загрузки).</summary>
        public static bool IsInGame()
        {
            return CrewSim.objInstance != null
                && CrewSim.coPlayer != null
                && SceneManager.GetActiveScene().name == "CrewSim";
        }

        // ================= Запуск / остановка =================

        public void StartHost()
        {
            if (IsRunning)
            {
                _log.LogWarning("[Net] Already running!");
                return;
            }

            if (!IsInGame())
            {
                GameLog.Warn("[Net] Сначала загрузите сохранение: сервер запускается только из игры");
                return;
            }

            _listener = new EventBasedNetListener();
            _netManager = new NetManager(_listener) { DisconnectTimeout = 30000 };

            _listener.ConnectionRequestEvent += request =>
            {
                if (_netManager.ConnectedPeersCount < MaxClients)
                    request.AcceptIfKey(_connectionKey);
                else
                    request.Reject();
            };

            _listener.PeerConnectedEvent += peer =>
            {
                // Ждём HELLO — до него клиент считается «не готов»
                GameLog.Info($"[Net] Client connected: {peer.EndPoint}, waiting for HELLO");
            };

            _listener.PeerDisconnectedEvent += (peer, info) =>
            {
                _readyPeers.Remove(peer.Id);
                SaveTransfer.OnPeerDisconnected(peer);
                PlayerRegistry.OnPeerDisconnected(peer);   // персонаж клиента — снова под ИИ
                GameLog.Info($"[Net] Client disconnected: {peer.EndPoint} ({info.Reason})");
            };

            _listener.NetworkReceiveEvent += OnReceive;
            _listener.NetworkErrorEvent += (endPoint, error) =>
                _log.LogError($"[Net] Error {endPoint}: {error}");

            if (_netManager.Start(_hostPort))
            {
                Role = NetRole.Host;
                Current = this;
                GameLog.Info($"[Net] Host started on port {_hostPort}");
            }
            else
            {
                _log.LogError($"[Net] Failed to start host on port {_hostPort}!");
                Stop();
            }
        }

        public void StartClient(string hostIp)
        {
            if (IsRunning)
            {
                _log.LogWarning("[Net] Already running!");
                return;
            }

            if (string.IsNullOrWhiteSpace(hostIp))
            {
                _log.LogError("[Net] IP is empty!");
                return;
            }

            _listener = new EventBasedNetListener();
            _netManager = new NetManager(_listener) { DisconnectTimeout = 30000 };

            _listener.PeerConnectedEvent += peer =>
            {
                GameLog.Info($"[Net] Connected to host: {peer.EndPoint}, sending HELLO");

                var w = new NetDataWriter();
                w.Put("HELLO");
                w.Put(ProtocolVersion);
                w.Put(PluginInfo.PLUGIN_VERSION);
                w.Put(DataHandler.strBuild ?? "");
                w.Put(PlayerRegistry.LocalKey);    // кто я: по ключу хост вернёт того же персонажа
                w.Put(PlayerRegistry.LocalName);   // имя для логов хоста
                peer.Send(w, DeliveryMethod.ReliableOrdered);
            };

            _listener.PeerDisconnectedEvent += (peer, info) =>
            {
                string reason = info.Reason.ToString();
                if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0)
                    reason = info.AdditionalData.GetString(256);

                GameLog.Warn($"[Net] Disconnected from host: {reason}");

                Role = NetRole.None;
                IsWorldReady = false;
                ActionSync.Reset();
                ItemSync.Clear();
                PositionSync.Clear();
                SaveTransfer.Reset();
            };

            _listener.NetworkReceiveEvent += OnReceive;
            _listener.NetworkErrorEvent += (endPoint, error) =>
                _log.LogError($"[Net] Error {endPoint}: {error}");

            if (!_netManager.Start(_clientPort))
            {
                _log.LogError($"[Net] Failed to bind client port {_clientPort}!");
                Stop();
                return;
            }

            var hostPeer = _netManager.Connect(hostIp, _hostPort, _connectionKey);

            if (hostPeer != null)
            {
                Role = NetRole.Client;
                Current = this;
                GameLog.Info($"[Net] Connecting to {hostIp}:{_hostPort} (local port {_clientPort})...");
            }
            else
            {
                GameLog.Info("[Net] Connect() returned null");
                Stop();
            }
        }

        public void Stop()
        {
            if (_netManager != null)
            {
                _netManager.Stop();
                _netManager = null;
            }

            PlayerRegistry.Reset();   // отпустить персонажей клиентов, пока мир ещё на месте

            Role = NetRole.None;
            Current = null;
            IsWorldReady = false;
            _readyPeers.Clear();

            ActionSync.Reset();
            ItemSync.Clear();
            PositionSync.Clear();
            SaveTransfer.Reset();

            GameLog.Info("[Net] Stopped");
        }

        public void Update()
        {
            _netManager?.PollEvents();
        }

        // ================= Готовность =================

        public bool IsReady(NetPeer peer) => _readyPeers.Contains(peer.Id);

        /// <summary>Хост: клиент загрузил мир — начинаем слать ему мировые пакеты.</summary>
        public void MarkReady(NetPeer peer)
        {
            _readyPeers.Add(peer.Id);
            GameLog.Info($"[Net] {peer.EndPoint} is in the world ({_readyPeers.Count} ready)");
        }

        /// <summary>Клиент: мир хоста загружен.</summary>
        public void SetWorldReady()
        {
            IsWorldReady = true;
            GameLog.Info("[Net] World loaded, sync started");
        }

        /// <summary>Хост: отключить клиента с причиной (клиент увидит её в логе).</summary>
        public void Reject(NetPeer peer, string reason)
        {
            GameLog.Warn($"[Net] Reject {peer.EndPoint}: {reason}");
            var w = new NetDataWriter();
            w.Put(reason);
            _netManager?.DisconnectPeer(peer, w);
        }

        // ================= Отправка =================

        /// <summary>
        /// «Мировой» пакет. Хост — всем ГОТОВЫМ клиентам; клиент — хосту, только если мир загружен.
        /// </summary>
        public void SendRaw(NetDataWriter writer, DeliveryMethod method = DeliveryMethod.ReliableOrdered)
        {
            if (!IsRunning || _netManager == null) return;

            if (Role == NetRole.Host)
            {
                foreach (NetPeer peer in _netManager.ConnectedPeerList)
                {
                    if (_readyPeers.Contains(peer.Id))
                        peer.Send(writer, method);
                }
            }
            else if (Role == NetRole.Client && IsWorldReady)
            {
                _netManager.SendToAll(writer, method);
            }
        }

        /// <summary>Служебный пакет конкретному клиенту (не зависит от готовности).</summary>
        public void SendToPeer(NetPeer peer, NetDataWriter writer, DeliveryMethod method = DeliveryMethod.ReliableOrdered)
        {
            peer.Send(writer, method);
        }

        /// <summary>Клиент: служебный пакет хосту (HELLO, LOADED) — не зависит от готовности.</summary>
        public void SendControl(NetDataWriter writer)
        {
            _netManager?.SendToAll(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendChat(string message)
        {
            if (!IsRunning) return;

            _writer.Reset();
            _writer.Put("CHAT");
            _writer.Put(message);

            _netManager!.SendToAll(_writer, DeliveryMethod.ReliableOrdered);
            GameLog.Info($"[Net] Sent: {message}");
        }

        public void SendCommand(string command)
        {
            if (!IsRunning) return;

            _writer.Reset();
            _writer.Put("CMD");
            _writer.Put(command);

            _netManager!.SendToAll(_writer, DeliveryMethod.ReliableOrdered);
            _log.LogInfo($"[Net] CMD sent: {command}");
        }

        // ================= Приём =================

        private void OnReceive(NetPeer peer, NetPacketReader reader, DeliveryMethod method)
        {
            try
            {
                string type = reader.GetString(64);

                bool host = Role == NetRole.Host;
                bool client = Role == NetRole.Client;
                bool peerReady = host && IsReady(peer);   // хост: этот клиент уже в мире
                bool worldReady = client && IsWorldReady;  // клиент: мир хоста загружен

                switch (type)
                {
                    case "CHAT":
                        GameLog.Info($"[Net] From {peer.EndPoint}: {reader.GetString(256)}");
                        break;

                    case "CMD":
                        GameLog.Info($"[Net] CMD from {peer.EndPoint}: {reader.GetString(256)}");
                        break;

                    // ---------- рукопожатие и загрузка мира ----------

                    case "HELLO":
                        {
                            int version = reader.GetInt();
                            string modVersion = reader.GetString(32);
                            string build = reader.GetString(64);
                            string playerKey = reader.GetString(64);
                            string playerName = reader.GetString(64);
                            if (!host) break;

                            GameLog.Info($"[Net] HELLO from {peer.EndPoint} ({playerName}): protocol={version} mod={modVersion} game={build}");

                            if (version != ProtocolVersion)
                            {
                                Reject(peer, $"Разные версии мода: хост {ProtocolVersion}, клиент {version}");
                                break;
                            }
                            if (build != (DataHandler.strBuild ?? ""))
                            {
                                Reject(peer, $"Разные версии игры: хост {DataHandler.strBuild}, клиент {build}");
                                break;
                            }
                            if (!IsInGame())
                            {
                                Reject(peer, "Хост сейчас не в игре");
                                break;
                            }

                            // Назначить персонажа ДО сохранения — тогда новый член экипажа попадёт в сохранение
                            string? crewId = PlayerRegistry.AssignOnHost(peer, playerKey, playerName, out string why);
                            if (crewId == null)
                            {
                                Reject(peer, why);
                                break;
                            }

                            var assign = new NetDataWriter();
                            assign.Put(PlayerRegistry.MsgAssign);
                            assign.Put(crewId);
                            SendToPeer(peer, assign);

                            SaveTransfer.Enqueue(peer);
                            break;
                        }

                    case SaveTransfer.MsgBegin:
                        if (client) SaveTransfer.OnBegin(reader);
                        break;

                    case SaveTransfer.MsgChunk:
                        if (client) SaveTransfer.OnChunk(reader);
                        break;

                    case SaveTransfer.MsgEnd:
                        if (client) SaveTransfer.OnEnd();
                        break;

                    case SaveTransfer.MsgLoaded:
                        if (host) SaveTransfer.OnLoaded(peer);
                        break;

                    case PlayerRegistry.MsgAssign:
                        if (client) PlayerRegistry.OnAssign(reader);
                        break;

                    // ---------- клиент → хост (только от загрузившихся) ----------

                    case "CMD_MOVE":
                        {
                            string crewId = reader.GetString(128);
                            float x = reader.GetFloat();
                            float y = reader.GetFloat();
                            if (peerReady && PlayerRegistry.Owns(peer, crewId))
                                OrderSync.ApplyMoveOrder(crewId, x, y);
                            break;
                        }

                    case "CMD_ACT":
                        {
                            string crewId = reader.GetString(128);
                            string targetId = reader.GetString(128);
                            string interactionName = reader.GetString(128);
                            string thirdId = reader.GetString(128);
                            float x = reader.GetFloat();
                            float y = reader.GetFloat();

                            GameLog.Info($"[Net] CMD_ACT {crewId} → {interactionName} on {targetId}");
                            if (peerReady && PlayerRegistry.Owns(peer, crewId))
                                OrderSync.ApplyActionOrder(crewId, targetId, interactionName, thirdId, x, y);
                            break;
                        }

                    case ActionSync.MsgEffectRequest:
                        {
                            var fx = ActionSync.FxData.Read(reader);
                            if (peerReady && PlayerRegistry.Owns(peer, fx.Us))
                                ActionSync.OnEffectRequest(peer, fx);
                            break;
                        }

                    case TimeSync.MsgRequest:
                        if (peerReady) TimeSync.OnRequest(peer, reader);
                        break;

                    // ---------- хост → клиент (только когда мир загружен) ----------

                    case ActionSync.MsgEffect:
                        {
                            var fx = ActionSync.FxData.Read(reader);
                            if (worldReady) ActionSync.OnEffect(fx);
                            break;
                        }

                    case "SNAP_POS":
                        if (worldReady) PositionSync.ApplySnapshot(reader);
                        break;

                    case TimeSync.Msg:
                        if (worldReady) TimeSync.OnTime(reader);
                        break;

                    // ---------- в обе стороны ----------

                    case ItemSync.Msg:
                        if (peerReady || worldReady)
                            ItemSync.OnReceive(peer, reader, Role);
                        break;

                    default:
                        _log.LogWarning($"[Net] Unknown packet '{type}' from {peer.EndPoint}");
                        break;
                }
            }
            catch (Exception e)
            {
                _log.LogError($"[Net] Receive error: {e}");
            }
            finally
            {
                reader.Recycle();
            }
        }
    }
}