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
                _log.LogInfo($"[Net] Client connected: {peer.EndPoint}");
                SendToPeer(peer, "Welcome to SimpleCoop!");
            };

            _listener.PeerDisconnectedEvent += (peer, info) =>
            {
                _log.LogInfo($"[Net] Client disconnected: {peer.EndPoint} ({info.Reason})");
            };

            _listener.NetworkReceiveEvent += OnReceive;

            _listener.NetworkErrorEvent += (endPoint, error) =>
            {
                _log.LogError($"[Net] Error {endPoint}: {error}");
            };

            if (_netManager.Start(_hostPort))
            {
                Role = NetRole.Host;
                _log.LogInfo($"[Net] Host started on port {_hostPort}");
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
                _log.LogInfo($"[Net] Connected to host: {peer.EndPoint}");
                SendToPeer(peer, "Hello from client!");
            };

            _listener.PeerDisconnectedEvent += (peer, info) =>
            {
                _log.LogInfo($"[Net] Disconnected from host: {info.Reason}");
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
                _log.LogInfo($"[Net] Connecting to {hostIp}:{_hostPort} (local port {_clientPort})...");
            }
            else
            {
                _log.LogError("[Net] Connect() returned null");
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

            _listener = null;
            Role = NetRole.None;
            _log.LogInfo("[Net] Stopped");
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
            _log.LogInfo($"[Net] Sent: {message}");
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
                    _log.LogInfo($"[Net] From {peer.EndPoint}: {msg}");
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
    }
}