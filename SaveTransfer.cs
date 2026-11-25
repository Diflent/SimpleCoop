using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Ostranauts.Core;
using Ostranauts.Core.Models;
using Ostranauts.UI.Loading;
using UnityEngine;

namespace SimpleCoop
{
    /// Передача мира хоста клиенту через обычное сохранение игры.
    public static class SaveTransfer
    {
        public const string MsgBegin = "SAVE_BEGIN";
        public const string MsgChunk = "SAVE_CHUNK";
        public const string MsgEnd = "SAVE_END";
        public const string MsgLoaded = "LOADED";

        private const string HostSaveName = "SimpleCoop_Host";
        private const string RemoteSaveName = "SimpleCoop_Remote";

        // Имена файлов в протоколе. Из сети берём только эти два — никаких путей от удалённой стороны.
        private const string FileInfo = "saveInfo.json";
        private const string FileZip = "save.zip";

        private const int ChunkSize = 16 * 1024;
        private const int ChunksPerFrame = 4;      // ~64 КБ за кадр
        private const float TimeoutSeconds = 180f; // сохранение + передача + загрузка

        // LoadManager._loadedSave — «текущий слот» хоста. SaveGame переключает его на наш
        // служебный слот; возвращаем обратно, чтобы автосейвы хоста шли в его сохранение.
        private static readonly FieldInfo? LoadedSaveField =
            AccessTools.Field(typeof(LoadManager), "_loadedSave");

        // ===================================================================
        //                               ХОСТ
        // ===================================================================

        private enum HostState { Idle, Saving, Sending, WaitingLoaded }

        private static HostState _state = HostState.Idle;
        private static readonly List<NetPeer> _queue = new List<NetPeer>();
        private static NetPeer? _peer;
        private static readonly List<KeyValuePair<string, byte[]>> _files = new List<KeyValuePair<string, byte[]>>();
        private static int _fileIndex;
        private static int _offset;
        private static float _timer;

        private static bool _wasPaused;
        private static bool _wasPauseLocked;
        private static bool _timeLocked;
        private static object? _hostLoadedSave;

        ///Хост: клиент прошёл HELLO — поставить в очередь на получение мира.
        public static void Enqueue(NetPeer peer)
        {
            if (_peer == peer || _queue.Contains(peer)) return;

            _queue.Add(peer);
            GameLog.Info($"[Save] {peer.EndPoint} queued (position {_queue.Count})");

            if (_state == HostState.Idle)
                StartNext();
        }

        private static void StartNext()
        {
            _peer = null;
            while (_queue.Count > 0)
            {
                NetPeer p = _queue[0];
                _queue.RemoveAt(0);
                if (p.ConnectionState == ConnectionState.Connected)
                {
                    _peer = p;
                    break;
                }
            }

            if (_peer == null)
            {
                _state = HostState.Idle;
                return;
            }

            if (!NetworkManager.IsInGame())
            {
                Abort("хост не в игре");
                return;
            }

            LockTime();

            _hostLoadedSave = LoadedSaveField?.GetValue(null);
            _state = HostState.Saving;
            _timer = 0f;

            GameLog.Info($"[Save] Saving world for {_peer.EndPoint}...");

            LoadManager.OnSaveFinished.AddListener(OnSaveFinished);
            GUISaveMenu.OnCreateSave.Invoke(HostSaveName);
        }

        private static void OnSaveFinished()
        {
            LoadManager.OnSaveFinished.RemoveListener(OnSaveFinished);
            if (_state != HostState.Saving || _peer == null) return;

            LoadedSaveField?.SetValue(null, _hostLoadedSave);

            string folder = MonoSingleton<LoadManager>.Instance.SavesPath + HostSaveName + "/";
            string infoPath = folder + "saveInfo.json";
            string[] zips = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.zip") : new string[0];

            if (!File.Exists(infoPath) || zips.Length == 0)
            {
                Abort($"файлы сохранения не найдены в {folder}");
                return;
            }

            _files.Clear();
            _files.Add(new KeyValuePair<string, byte[]>(FileInfo, File.ReadAllBytes(infoPath)));
            _files.Add(new KeyValuePair<string, byte[]>(FileZip, File.ReadAllBytes(zips[0])));

            var w = new NetDataWriter();
            w.Put(MsgBegin);
            w.Put(_files.Count);
            long total = 0;
            foreach (var f in _files)
            {
                w.Put(f.Key);
                w.Put(f.Value.Length);
                total += f.Value.Length;
            }
            NetworkManager.Current?.SendToPeer(_peer, w);

            _fileIndex = 0;
            _offset = 0;
            _timer = 0f;
            _state = HostState.Sending;

            GameLog.Info($"[Save] Sending {total / 1024} KB to {_peer.EndPoint}");
        }

        public static void Update()
        {
            if (_state == HostState.Idle) return;

            _timer += Time.unscaledDeltaTime;
            if (_timer > TimeoutSeconds)
            {
                Abort("таймаут");
                return;
            }

            if (_state == HostState.Sending)
                PumpChunks();
        }

        private static void PumpChunks()
        {
            var net = NetworkManager.Current;
            if (net == null || _peer == null) return;

            for (int i = 0; i < ChunksPerFrame && _fileIndex < _files.Count; i++)
            {
                byte[] data = _files[_fileIndex].Value;
                int len = Math.Min(ChunkSize, data.Length - _offset);

                var w = new NetDataWriter();
                w.Put(MsgChunk);
                w.Put(_fileIndex);
                w.Put(_offset);
                w.PutBytesWithLength(data, _offset, len);
                net.SendToPeer(_peer, w);

                _offset += len;
                if (_offset >= data.Length)
                {
                    _fileIndex++;
                    _offset = 0;
                }
            }

            if (_fileIndex >= _files.Count)
            {
                var w = new NetDataWriter();
                w.Put(MsgEnd);
                net.SendToPeer(_peer, w);

                _files.Clear();
                _state = HostState.WaitingLoaded;
                GameLog.Info($"[Save] Sent, waiting for {_peer.EndPoint} to load...");
            }
        }

        ///Хост: клиент сообщил, что загрузил мир.
        public static void OnLoaded(NetPeer peer)
        {
            var net = NetworkManager.Current;
            if (net == null) return;

            if (peer != _peer || _state != HostState.WaitingLoaded)
            {
                GameLog.Warn($"[Save] Unexpected LOADED from {peer.EndPoint}");
                return;
            }

            net.MarkReady(peer);
            UnlockTime();
            TimeSync.SendTo(peer);

            GameLog.Info($"[Save] {peer.EndPoint} joined the game");

            _peer = null;
            _state = HostState.Idle;
            StartNext();
        }

        public static void OnPeerDisconnected(NetPeer peer)
        {
            _queue.Remove(peer);
            if (peer == _peer)
                Abort("клиент отключился");
        }

        private static void Abort(string reason)
        {
            GameLog.Warn($"[Save] Transfer aborted: {reason}");

            if (_state == HostState.Saving)
            {
                LoadManager.OnSaveFinished.RemoveListener(OnSaveFinished);
                LoadedSaveField?.SetValue(null, _hostLoadedSave);
            }

            NetPeer? peer = _peer;
            _peer = null;
            _files.Clear();
            _state = HostState.Idle;
            UnlockTime();

            if (peer != null && peer.ConnectionState == ConnectionState.Connected)
                NetworkManager.Current?.Reject(peer, "Не удалось передать мир: " + reason);

            StartNext();
        }

       
        /// Пауза на время передачи.
        /// пока он стоит, сеттер CrewSim.Paused ничего не меняет, и хост не снимет паузу случайно.
        private static void LockTime()
        {
            if (_timeLocked) return;

            _wasPaused = CrewSim.Paused;
            _wasPauseLocked = CrewSim.bPauseLock;

            CrewSim.bPauseLock = false;
            CrewSim.Paused = true;
            CrewSim.bPauseLock = true;
            _timeLocked = true;
        }

        private static void UnlockTime()
        {
            if (!_timeLocked) return;

            CrewSim.bPauseLock = false;
            CrewSim.Paused = _wasPaused;
            CrewSim.bPauseLock = _wasPauseLocked;
            _timeLocked = false;
        }

        // ===================================================================
        //                               КЛИЕНТ
        // ===================================================================

        private static string[]? _rxNames;
        private static byte[][]? _rxData;
        private static int[]? _rxReceived;
        private static int _rxLastPercent;
        private static bool _waitingForLoad;

        public static void OnBegin(NetPacketReader r)
        {
            int count = r.GetInt();
            if (count <= 0 || count > 8)
            {
                GameLog.Error($"[Save] Bad file count: {count}");
                return;
            }

            _rxNames = new string[count];
            _rxData = new byte[count][];
            _rxReceived = new int[count];
            _rxLastPercent = -1;

            long total = 0;
            for (int i = 0; i < count; i++)
            {
                _rxNames[i] = r.GetString(64);
                int size = r.GetInt();
                if (size < 0 || size > 512 * 1024 * 1024)
                {
                    GameLog.Error($"[Save] Bad file size: {size}");
                    _rxData = null;
                    return;
                }
                _rxData[i] = new byte[size];
                total += size;
            }

            GameLog.Info($"[Save] Receiving world from host: {total / 1024} KB");
        }

        public static void OnChunk(NetPacketReader r)
        {
            if (_rxData == null || _rxReceived == null) return;

            int index = r.GetInt();
            int offset = r.GetInt();
            byte[] data = r.GetBytesWithLength();

            if (index < 0 || index >= _rxData.Length ||
                offset < 0 || offset + data.Length > _rxData[index].Length)
            {
                GameLog.Warn($"[Save] Bad chunk: file={index} offset={offset} len={data.Length}");
                return;
            }

            Buffer.BlockCopy(data, 0, _rxData[index], offset, data.Length);
            _rxReceived[index] += data.Length;

            long got = 0, total = 0;
            for (int i = 0; i < _rxData.Length; i++)
            {
                got += _rxReceived[i];
                total += _rxData[i].Length;
            }
            int percent = total > 0 ? (int)(got * 100 / total) : 100;
            if (percent / 10 != _rxLastPercent / 10)
            {
                _rxLastPercent = percent;
                GameLog.Info($"[Save] {percent}%");
            }
        }

        public static void OnEnd()
        {
            if (_rxData == null || _rxNames == null || _rxReceived == null) return;

            for (int i = 0; i < _rxData.Length; i++)
            {
                if (_rxReceived[i] != _rxData[i].Length)
                {
                    GameLog.Error($"[Save] File {_rxNames[i]} incomplete: {_rxReceived[i]}/{_rxData[i].Length}");
                    return;
                }
            }

            try
            {
                LoadReceivedSave();
            }
            catch (Exception e)
            {
                GameLog.Error($"[Save] Load failed: {e}");
            }
            finally
            {
                _rxData = null;
                _rxNames = null;
                _rxReceived = null;
            }
        }

        private static void LoadReceivedSave()
        {
            LoadManager lm = MonoSingleton<LoadManager>.Instance;

            string folder = lm.SavesPath + RemoteSaveName;
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);

            for (int i = 0; i < _rxNames!.Length; i++)
            {
                string target;
                if (_rxNames[i] == FileInfo) target = "saveInfo.json";
                else if (_rxNames[i] == FileZip) target = RemoteSaveName + ".zip";
                else continue; // неизвестное имя — игнорируем

                File.WriteAllBytes(Path.Combine(folder, target), _rxData![i]);
            }

            // SaveInfo создаём так же, как игра при чтении списка сохранений
            var dict = new Dictionary<string, JsonSaveInfo>();
            DataHandler.JsonToData(folder + "/saveInfo.json", dict);

            JsonSaveInfo? json = null;
            foreach (JsonSaveInfo v in dict.Values)
            {
                json = v;
                break;
            }
            if (json == null)
                throw new Exception("saveInfo.json is empty");

            var info = new SaveInfo(json, folder);
            info.SaveName = RemoteSaveName; // иначе игра будет искать в Steam Cloud сохранение хоста

            GameLog.Info($"[Save] Loading world (player: {info.PlayerName})...");
            _waitingForLoad = true;
            GUILoadMenu.OnLoadSelected.Invoke(info);
        }

        public static void OnGameFinishedLoading()
        {
            if (!_waitingForLoad) return;
            _waitingForLoad = false;

            var net = NetworkManager.Current;
            if (net == null || net.Role != NetworkManager.NetRole.Client) return;

            ActionSync.Reset();
            ItemSync.Clear();
            PositionSync.Clear();
            PlayerRegistry.OnWorldLoaded();   // выделить своего персонажа, камера на него
            net.SetWorldReady();

            var w = new NetDataWriter();
            w.Put(MsgLoaded);
            net.SendControl(w);
        }

        public static void Reset()
        {
            if (_state == HostState.Saving)
                LoadManager.OnSaveFinished.RemoveListener(OnSaveFinished);

            UnlockTime();
            _queue.Clear();
            _peer = null;
            _files.Clear();
            _state = HostState.Idle;

            _rxData = null;
            _rxNames = null;
            _rxReceived = null;
            _waitingForLoad = false;
        }
    }
}