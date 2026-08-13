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

        public static NetworkManager? Current { get; private set; }
        public NetRole Role { get; private set; } = NetRole.None;
        public bool IsRunning => _netManager != null && _netManager.IsRunning;

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
                if (_netManager.ConnectedPeersCount < 1)
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

            _netManager.SendToAll(_writer, DeliveryMethod.ReliableOrdered);
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

                if (type == "CHAT")
                {
                    string msg = reader.GetString(256);
                    GameLog.Info($"[Net] From {peer.EndPoint}: {msg}");
                }
                else if (type == "CMD")
                {
                    string cmd = reader.GetString(256);
                    GameLog.Info($"[Net] CMD from {peer.EndPoint}: {cmd}");

                    if (Role == NetRole.Host)
                    {
                        //заглушка
                    }
                }
                else if (type == "CMD_MOVE")
                {
                    string crewName = reader.GetString(128);
                    float x = reader.GetFloat();
                    float y = reader.GetFloat();

                    GameLog.Info($"[Net] CMD_MOVE from {peer.EndPoint}: {crewName} ({x:F1},{y:F1})");

                    if (Role == NetRole.Host)
                    {
                        OrderSync.ApplyMoveOrder(crewName, x, y);
                    }
                }
                else if (type == "CMD_ACT")
                {
                    string crewName = reader.GetString(128);
                    string targetName = reader.GetString(128);
                    string interactionName = reader.GetString(128);
                    float x = reader.GetFloat();
                    float y = reader.GetFloat();

                    GameLog.Info($"[Net] CMD_ACT {crewName} → {interactionName} on {targetName}");

                    if (Role == NetRole.Host)
                        OrderSync.ApplyActionOrder(crewName, targetName, interactionName, x, y);
                }
                else if (type == "SNAP_POS")
                {
                    if (Role == NetRole.Client)
                    {
                        PositionSync.ApplySnapshot(reader);
                    }
                }
            }
            catch (Exception e)
            {
                _log.LogError($"[Net] Receive error: {e.Message}");
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

            _netManager.SendToAll(_writer, DeliveryMethod.ReliableOrdered);
            _log.LogInfo($"[Net] CMD sent: {command}");
        }

        public void SendRaw(NetDataWriter writer)
        {
            if (!IsRunning || _netManager == null) return;
            _netManager.SendToAll(writer, DeliveryMethod.ReliableOrdered);
        }
    }
}