using System;
using BepInEx.Logging;
using LiteNetLib;
using LiteNetLib.Utils;

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

        public static NetworkManager? Current { get; private set; }
        public NetRole Role { get; private set; } = NetRole.None;
        public bool IsRunning => _netManager != null && _netManager.IsRunning;
        public int PeerCount => _netManager?.ConnectedPeersCount ?? 0;

        private NetManager? _netManager;
        private EventBasedNetListener? _listener;
        private readonly ManualLogSource _log;
        private readonly NetDataWriter _writer = new NetDataWriter();

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

        public void StartHost()
        {
            if (IsRunning)
            {
                _log.LogWarning("[Net] Already running!");
                return;
            }

            _listener = new EventBasedNetListener();
            _netManager = new NetManager(_listener);

            _listener.ConnectionRequestEvent += request =>
            {
                if (_netManager.ConnectedPeersCount < MaxClients)
                    request.AcceptIfKey(_connectionKey);
                else
                    request.Reject();
            };

            _listener.PeerConnectedEvent += peer =>
            {
                GameLog.Info($"[Net] Client connected: {peer.EndPoint}");
                SendToPeer(peer, "Welcome to SimpleCoop!");
            };

            _listener.PeerDisconnectedEvent += (peer, info) =>
            {
                GameLog.Info($"[Net] Client disconnected: {peer.EndPoint} ({info.Reason})");
            };

            _listener.NetworkReceiveEvent += OnReceive;

            _listener.NetworkErrorEvent += (endPoint, error) =>
            {
                _log.LogError($"[Net] Error {endPoint}: {error}");
            };

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
            _netManager = new NetManager(_listener);

            _listener.PeerConnectedEvent += peer =>
            {
                GameLog.Info($"[Net] Connected to host: {peer.EndPoint}");
                SendToPeer(peer, "Hello from client!");
            };

            _listener.PeerDisconnectedEvent += (peer, info) =>
            {
                GameLog.Info($"[Net] Disconnected from host: {info.Reason}");
                Role = NetRole.None;
                ActionSync.Reset();
                ItemSync.Clear();
                PositionSync.Clear();
            };

            _listener.NetworkReceiveEvent += OnReceive;

            _listener.NetworkErrorEvent += (endPoint, error) =>
            {
                _log.LogError($"[Net] Error {endPoint}: {error}");
            };

            if (!_netManager.Start(_clientPort))
            {
                _log.LogError($"[Net] Failed to bind client port {_clientPort}!");
                Stop();
                return;
            }

            var peer = _netManager.Connect(hostIp, _hostPort, _connectionKey);

            if (peer != null)
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

            Role = NetRole.None;
            Current = null;
            ActionSync.Reset();
            ItemSync.Clear();
            PositionSync.Clear();
            GameLog.Info("[Net] Stopped");
        }

        public void Update()
        {
            _netManager?.PollEvents();
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

        private void SendToPeer(NetPeer peer, string message)
        {
            _writer.Reset();
            _writer.Put("CHAT");
            _writer.Put(message);
            peer.Send(_writer, DeliveryMethod.ReliableOrdered);
        }

        private void OnReceive(NetPeer peer, NetPacketReader reader, DeliveryMethod method)
        {
            try
            {
                string type = reader.GetString(64);

                switch (type)
                {
                    case "CHAT":
                    {
                        string msg = reader.GetString(256);
                        GameLog.Info($"[Net] From {peer.EndPoint}: {msg}");
                        break;
                    }

                    case "CMD":
                    {
                        string cmd = reader.GetString(256);
                        GameLog.Info($"[Net] CMD from {peer.EndPoint}: {cmd}");
                        break;
                    }

                    // ---- client → host ----

                    case "CMD_MOVE":
                    {
                        string crewId = reader.GetString(128);
                        float x = reader.GetFloat();
                        float y = reader.GetFloat();

                        if (Role == NetRole.Host)
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

                        if (Role == NetRole.Host)
                            OrderSync.ApplyActionOrder(crewId, targetId, interactionName, thirdId, x, y);
                        break;
                    }

                    case ActionSync.MsgEffectRequest:
                    {
                        var fx = ActionSync.FxData.Read(reader);
                        if (Role == NetRole.Host)
                            ActionSync.OnEffectRequest(peer, fx);
                        break;
                    }

                    // ---- host → client ----

                    case ActionSync.MsgEffect:
                    {
                        var fx = ActionSync.FxData.Read(reader);
                        if (Role == NetRole.Client)
                            ActionSync.OnEffect(fx);
                        break;
                    }

                    // ---- в обе стороны ----

                    case ItemSync.Msg:
                        ItemSync.OnReceive(peer, reader, Role);
                        break;

                    case "SNAP_POS":
                    {
                        if (Role == NetRole.Client)
                            PositionSync.ApplySnapshot(reader);
                        break;
                    }

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

        public void SendCommand(string command)
        {
            if (!IsRunning) return;

            _writer.Reset();
            _writer.Put("CMD");
            _writer.Put(command);

            _netManager!.SendToAll(_writer, DeliveryMethod.ReliableOrdered);
            _log.LogInfo($"[Net] CMD sent: {command}");
        }

        public void SendRaw(NetDataWriter writer, DeliveryMethod method = DeliveryMethod.ReliableOrdered)
        {
            if (!IsRunning || _netManager == null) return;
            _netManager.SendToAll(writer, method);
        }
    }
}
