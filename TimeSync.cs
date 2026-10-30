using System;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace SimpleCoop
{
    public static class TimeSync
    {
        public const string Msg = "TIME";            // хост → клиенты
        public const string MsgRequest = "TIME_REQ"; // клиент → хост

        public enum Req : byte { Pause = 1, Mult = 2, Reset = 3 }

        /// Может ли клиент менять скорость. Паузу может ставить любой.
        public static bool ClientsMayChangeSpeed = false;

        private const float HeartbeatInterval = 1f;

        /// Допустимое расхождение игровых часов (с) при скорости ×1. Подобрать на тесте.
        private const double EpochTolerance = 2.0;

        internal static bool ApplyingRemote;
        private static float _beatTimer;

        public static void Init()
        {
            CrewSim.OnTimeScaleUpdated.AddListener(OnTimeChanged);
        }

        // ---------------- Хост ----------------

        private static void OnTimeChanged()
        {
            var net = NetworkManager.Current;
            if (net == null || !net.IsRunning || net.Role != NetworkManager.NetRole.Host) return;
            if (net.PeerCount == 0) return;

            net.SendRaw(Build(), DeliveryMethod.ReliableOrdered);
        }

        public static void Update()
        {
            var net = NetworkManager.Current;
            if (net == null || !net.IsRunning || net.Role != NetworkManager.NetRole.Host) return;

            _beatTimer += Time.unscaledDeltaTime;
            if (_beatTimer < HeartbeatInterval) return;
            _beatTimer = 0f;

            if (net.PeerCount > 0)
                net.SendRaw(Build(), DeliveryMethod.Sequenced);
        }

        public static void SendTo(NetPeer peer)
        {
            NetworkManager.Current?.SendToPeer(peer, Build());
        }

        private static NetDataWriter Build()
        {
            var w = new NetDataWriter();
            w.Put(Msg);
            w.Put(CrewSim.Paused);
            w.Put(Time.timeScale);
            w.Put(StarSystem.fEpoch);
            return w;
        }

        public static void OnRequest(NetPeer peer, NetPacketReader r)
        {
            var req = (Req)r.GetByte();
            float arg = r.GetFloat();

            switch (req)
            {
                case Req.Pause:
                    GameLog.Info($"[Time] {peer.EndPoint}: pause={arg > 0.5f}");
                    CrewSim.Paused = arg > 0.5f;
                    break;

                case Req.Mult:
                case Req.Reset:
                    if (!ClientsMayChangeSpeed)
                    {
                        GameLog.Info($"[Time] {peer.EndPoint}: speed change ignored (host only)");
                        SendTo(peer); // вернуть клиенту актуальное значение
                        return;
                    }

                    if (req == Req.Mult) CrewSim.TimeScaleMult(arg);
                    else CrewSim.ResetTimeScale();
                    break;
            }
        }

        // ---------------- Клиент ----------------

        public static void OnTime(NetPacketReader r)
        {
            bool paused = r.GetBool();
            float scale = r.GetFloat();
            double epoch = r.GetDouble();

            ApplyingRemote = true;
            try
            {
                if (CrewSim.Paused != paused)
                    CrewSim.Paused = paused; // сам выключит аниматоры и обновит UI

                // ПОСЛЕ Paused: его сеттер зажимает timeScale в [0.25; 16]
                if (Mathf.Abs(Time.timeScale - scale) > 0.001f)
                {
                    Time.timeScale = scale;
                    CrewSim.OnTimeScaleUpdated.Invoke();
                }

                double tolerance = EpochTolerance * Math.Max(1f, scale);
                double diff = epoch - StarSystem.fEpoch;
                if (Math.Abs(diff) > tolerance)
                {
                    StarSystem.fEpoch = epoch;
                    if (ActionSync.Verbose)
                        GameLog.Info($"[Time] epoch corrected by {diff:F1}s");
                }
            }
            finally
            {
                ApplyingRemote = false;
            }
        }

        /// Prefix для методов времени на клиенте.
        /// true — выполнить оригинал, false — пропустить (вместо этого — запрос хосту).
        internal static bool ClientIntercept(Req req, float arg)
        {
            var net = NetworkManager.Current;
            if (net == null || !net.IsRunning) return true;
            if (net.Role != NetworkManager.NetRole.Client) return true;
            if (!net.IsWorldReady) return true;   // меню/загрузка — игра управляет временем сама
            if (ApplyingRemote) return true;      // применяем TIME от хоста

            // Повтор эффекта хоста (bPause внутри ApplyEffects) — хост уже сам поставил паузу
            if (ActionSync.ApplyingRemote) return false;

            var w = new NetDataWriter();
            w.Put(MsgRequest);
            w.Put((byte)req);
            w.Put(arg);
            net.SendRaw(w);
            return false;
        }

        internal static bool IsClientInWorld()
        {
            var net = NetworkManager.Current;
            return net != null && net.IsRunning
                && net.Role == NetworkManager.NetRole.Client
                && net.IsWorldReady;
        }
    }

    [HarmonyPatch(typeof(CrewSim), nameof(CrewSim.Paused), MethodType.Setter)]
    public static class Patch_CrewSim_Paused
    {
        static bool Prefix(bool value) =>
            TimeSync.ClientIntercept(TimeSync.Req.Pause, value ? 1f : 0f);
    }

    [HarmonyPatch(typeof(CrewSim), nameof(CrewSim.TimeScaleMult))]
    public static class Patch_CrewSim_TimeScaleMult
    {
        static bool Prefix(float fMultiplier) =>
            TimeSync.ClientIntercept(TimeSync.Req.Mult, fMultiplier);
    }

    [HarmonyPatch(typeof(CrewSim), nameof(CrewSim.ResetTimeScale))]
    public static class Patch_CrewSim_ResetTimeScale
    {
        static bool Prefix() =>
            TimeSync.ClientIntercept(TimeSync.Req.Reset, 0f);
    }

    /// Перемотка (окно FFWD) прыгает по fEpoch своим механизмом — пока только у хоста.
    /// Если хост перематывает, клиента догонит поправка часов в TIME.
    [HarmonyPatch(typeof(CrewSim), nameof(CrewSim.ToggleSFF))]
    public static class Patch_CrewSim_ToggleSFF
    {
        static bool Prefix()
        {
            if (!TimeSync.IsClientInWorld()) return true;
            GameLog.Warn("[Time] Перемотка времени пока доступна только хосту");
            return false;
        }
    }
}