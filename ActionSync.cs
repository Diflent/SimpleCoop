using System;
using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;

namespace SimpleCoop
{
    /// <summary>
    /// Синхронизация РЕЗУЛЬТАТОВ действий (host-authoritative).
    ///
    /// Хост:   каждый верхнеуровневый вызов Interaction.ApplyEffects → пакет ACT_FX всем клиентам.
    /// Клиент: собственные ApplyEffects НЕ выполняются (мир симулирует хост);
    ///         пришедший ACT_FX воспроизводится локально тем же ApplyEffects.
    ///         Ручные действия игрока клиента, выполненные мимо очереди (GUI: терминалы,
    ///         замки, диалоги), пересылаются хосту как CMD_FX.
    ///
    /// Ограничение: ApplyEffects содержит случайность (MathUtils.Rand) и спавн лута
    /// с новыми strID — такие исходы у клиента могут разойтись. Это закрывается
    /// следующим шагом: синхронизацией состояния (conds / предметов).
    /// </summary>
    public static class ActionSync
    {
        public const string MsgEffect = "ACT_FX";        // host → clients
        public const string MsgEffectRequest = "CMD_FX"; // client → host

        /// <summary>true, пока клиент воспроизводит эффект, пришедший с хоста.</summary>
        internal static bool ApplyingRemote;

        /// <summary>Глубина вложенных ApplyEffects на хосте (ApplyEffects → ApplyChain → ApplyEffects).</summary>
        internal static int Depth;

        /// <summary>Логировать каждый эффект в оверлей (шумно).</summary>
        public static bool Verbose = false;

        /// <summary>Не рассылать: позиции и так идут через PositionSync.</summary>
        private static readonly HashSet<string> SkipBroadcast = new HashSet<string> { "Walk" };

        public sealed class FxData
        {
            public string Name = "";
            public string Us = "";
            public string Them = "";
            public string Third = "";
            public bool Cancel;
            public bool Manual;

            // «Контракты» — конкретные предметы, которые игра выбрала в Interaction.Triggered().
            // ApplyEffects работает ТОЛЬКО с этими списками (подбор, экипировка, выброс, еда...).
            public string[] Give = Empty;    // aLootItemGiveContract:   us отдаёт them
            public string[] Use = Empty;     // aLootItemUseContract:    расходуются
            public string[] Remove = Empty;  // aLootItemRemoveContract: выбрасываются / уничтожаются
            public string[] Take = Empty;    // aLootItemTakeContract:   us забирает у them

            private static readonly string[] Empty = new string[0];

            public void Write(NetDataWriter w)
            {
                w.Put(Name);
                w.Put(Us);
                w.Put(Them);
                w.Put(Third);
                w.Put(Cancel);
                w.Put(Manual);
                PutIds(w, Give);
                PutIds(w, Use);
                PutIds(w, Remove);
                PutIds(w, Take);
            }

            private static void PutIds(NetDataWriter w, string[] ids)
            {
                w.Put(ids.Length);
                foreach (string id in ids)
                    w.Put(id);
            }

            private static string[] GetIds(NetPacketReader r)
            {
                int n = r.GetInt();
                if (n <= 0 || n > 256) return Empty;
                var ids = new string[n];
                for (int i = 0; i < n; i++)
                    ids[i] = r.GetString(128);
                return ids;
            }

            public static FxData Read(NetPacketReader r)
            {
                return new FxData
                {
                    Name = r.GetString(128),
                    Us = r.GetString(128),
                    Them = r.GetString(128),
                    Third = r.GetString(128),
                    Cancel = r.GetBool(),
                    Manual = r.GetBool(),
                    Give = GetIds(r),
                    Use = GetIds(r),
                    Remove = GetIds(r),
                    Take = GetIds(r)
                };
            }

            public override string ToString() =>
                $"{Name} us={Us} them={Them}" + (Third.Length > 0 ? $" 3rd={Third}" : "") +
                (Cancel ? " [cancel]" : "") + (Manual ? " [manual]" : "") +
                List("give", Give) + List("use", Use) + List("remove", Remove) + List("take", Take);

            private static string List(string label, string[] ids) =>
                ids.Length == 0 ? "" : $" {label}=[{string.Join(",", ids)}]";
        }

        /// <summary>Снимаем данные ДО выполнения: после ApplyEffects цель может быть уничтожена.</summary>
        internal static FxData? Capture(Interaction ia, bool isCancel)
        {
            if (ia == null || string.IsNullOrEmpty(ia.strName)) return null;

            CondOwner? us = ia.objUs;
            CondOwner? them = ia.objThem;
            if (us == null || them == null) return null;

            return new FxData
            {
                Name = ia.strName,
                Us = CoLookup.Id(us),
                Them = CoLookup.Id(them),
                Third = CoLookup.Id(ia.obj3rd),
                Cancel = isCancel,
                Manual = ia.bManual,
                Give = Ids(ia.aLootItemGiveContract),
                Use = Ids(ia.aLootItemUseContract),
                Remove = Ids(ia.aLootItemRemoveContract),
                Take = Ids(ia.aLootItemTakeContract)
            };
        }

        private static string[] Ids(List<CondOwner>? list)
        {
            if (list == null || list.Count == 0) return new string[0];

            var ids = new List<string>(list.Count);
            foreach (CondOwner co in list)
            {
                if (co != null)
                    ids.Add(CoLookup.Id(co));
            }
            return ids.ToArray();
        }

        private static List<CondOwner>? Cos(string[] ids, string what, string iaName)
        {
            if (ids.Length == 0) return null;

            var list = new List<CondOwner>(ids.Length);
            foreach (string id in ids)
            {
                CondOwner? co = CoLookup.Find(id);
                if (co != null)
                    list.Add(co);
                else
                    GameLog.Warn($"[ActionSync] {iaName}: {what} item not found ({id})");
            }
            return list.Count > 0 ? list : null;
        }

        // ---------------- Host ----------------

        internal static void Broadcast(FxData d)
        {
            var net = NetworkManager.Current;
            if (net == null || !net.IsRunning || net.Role != NetworkManager.NetRole.Host) return;
            if (net.PeerCount == 0) return;
            if (SkipBroadcast.Contains(d.Name)) return;

            var w = new NetDataWriter();
            w.Put(MsgEffect);
            d.Write(w);
            net.SendRaw(w, DeliveryMethod.ReliableOrdered);

            if (Verbose)
                GameLog.Info($"[ActionSync] → FX {d}");
        }

        /// <summary>Хост получил от клиента запрос на эффект (GUI-действие игрока клиента).</summary>
        public static void OnEffectRequest(NetPeer peer, FxData d)
        {
            GameLog.Info($"[ActionSync] CMD_FX from {peer.EndPoint}: {d}");

            Interaction? ia = Build(d);
            if (ia == null) return;

            // Выполняем как обычный эффект хоста — патч сам разошлёт ACT_FX всем (включая отправителя).
            ia.ApplyEffects(null, d.Cancel);
        }

        // ---------------- Client ----------------

        /// <summary>Клиент получил результат действия с хоста.</summary>
        public static void OnEffect(FxData d)
        {
            if (Verbose)
                GameLog.Info($"[ActionSync] ← FX {d}");

            Interaction? ia = Build(d);
            if (ia == null) return;

            ApplyingRemote = true;
            try
            {
                ia.ApplyEffects(null, d.Cancel);
            }
            catch (Exception e)
            {
                GameLog.Error($"[ActionSync] Replay {d.Name} failed: {e.Message}");
            }
            finally
            {
                ApplyingRemote = false;
            }
        }

        /// <summary>
        /// Клиент пытается локально применить эффект. Локально — никогда;
        /// но если это ручное действие своего персонажа — просим хост.
        /// </summary>
        internal static void OnClientLocalEffect(Interaction ia, bool isCancel)
        {
            if (isCancel || ia == null || !ia.bManual) return;

            CondOwner me = CrewSim.coPlayer;
            if (me == null || ia.objUs != me) return;

            FxData? d = Capture(ia, false);
            if (d == null) return;

            var net = NetworkManager.Current;
            if (net == null || !net.IsRunning) return;

            var w = new NetDataWriter();
            w.Put(MsgEffectRequest);
            d.Write(w);
            net.SendRaw(w, DeliveryMethod.ReliableOrdered);

            GameLog.Info($"[ActionSync] Sent CMD_FX {d}");
        }

        // ---------------- Common ----------------

        private static Interaction? Build(FxData d)
        {
            Interaction ia = DataHandler.GetInteraction(d.Name, null, false);
            if (ia == null)
            {
                GameLog.Warn($"[ActionSync] Unknown interaction: {d.Name}");
                return null;
            }

            CondOwner? us = CoLookup.Find(d.Us);
            CondOwner? them = CoLookup.Find(d.Them);
            if (us == null || them == null)
            {
                // Чаще всего — предмет, заспавненный у хоста с новым strID (см. ограничение выше)
                GameLog.Warn($"[ActionSync] {d.Name}: missing CO us={(us != null)} them={(them != null)} ({d.Us} / {d.Them})");
                return null;
            }

            ia.objUs = us;
            ia.objThem = them;

            if (!string.IsNullOrEmpty(d.Third))
            {
                CondOwner? third = CoLookup.Find(d.Third);
                if (third != null)
                    ia.obj3rd = third;
                else
                    GameLog.Warn($"[ActionSync] {d.Name}: 3rd not found ({d.Third})");
            }

            ia.bManual = d.Manual;

            // Без этого Give/Take/Remove/Use у клиента пусты и предметы никуда не двигаются
            ia.aLootItemGiveContract = Cos(d.Give, "give", d.Name);
            ia.aLootItemUseContract = Cos(d.Use, "use", d.Name);
            ia.aLootItemRemoveContract = Cos(d.Remove, "remove", d.Name);
            ia.aLootItemTakeContract = Cos(d.Take, "take", d.Name);

            return ia;
        }

        public static void Reset()
        {
            ApplyingRemote = false;
            Depth = 0;
        }
    }

    [HarmonyPatch(typeof(Interaction), nameof(Interaction.ApplyEffects))]
    public static class Patch_ApplyEffects
    {
        public sealed class State
        {
            public bool Counted;
            public ActionSync.FxData? Data;
        }

        [HarmonyPriority(Priority.First)]
        static bool Prefix(Interaction __instance, bool isCancelIa, out State? __state)
        {
            __state = null;

            var net = NetworkManager.Current;
            if (net == null || !net.IsRunning)
                return true;

            if (net.Role == NetworkManager.NetRole.Client)
            {
                if (!net.IsWorldReady)
                    return true;     // меню / загрузка — игра работает сама

                if (ActionSync.ApplyingRemote)
                    return true;

                ActionSync.OnClientLocalEffect(__instance, isCancelIa);
                return false;
            }

            if (net.Role == NetworkManager.NetRole.Host)
            {
                __state = new State { Counted = true };
                if (ActionSync.Depth == 0)
                    __state.Data = ActionSync.Capture(__instance, isCancelIa);
                ActionSync.Depth++;
            }

            return true;
        }

        // Finalizer выполняется и при исключении — глубина не «залипнет».
        static void Finalizer(Exception? __exception, State? __state)
        {
            if (__state == null || !__state.Counted) return;

            ActionSync.Depth--;
            if (ActionSync.Depth < 0) ActionSync.Depth = 0;

            // Рассылаем только верхний уровень: вложенные ApplyChain клиент повторит сам.
            if (__exception == null && __state.Data != null)
                ActionSync.Broadcast(__state.Data);
        }
    }
}
