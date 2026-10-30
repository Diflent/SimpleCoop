using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace SimpleCoop
{
    /// <summary>
    /// Синхронизация РАСПОЛОЖЕНИЯ предметов (state-based, а не event-based).
    ///
    /// Окно инвентаря (Ostranauts/Inventory/GUIInventoryItem.cs) двигает предметы напрямую
    /// через Slots.SlotItem / Container.AddCOSimple / Ship.AddCO, минуя Interaction.ApplyEffects,
    /// поэтому ActionSync такие перемещения не видит.
    ///
    /// Идея: ловим три «точки входа», куда предмет может попасть, помечаем предмет «грязным»,
    /// а в конце кадра отправляем, ГДЕ он теперь лежит:
    ///   Slot      — у родителя в слоте (надет / в руках)
    ///   Container — у родителя в контейнере, ячейка (x, y)
    ///   Ground    — на полу корабля, координаты (x, y)
    ///
    /// Клиент → хост: изменения, сделанные игроком в своём окне инвентаря.
    /// Хост → все:    любые изменения (в т.ч. применённые запросы клиентов) — «истина».
    /// Пакет идемпотентен: если предмет уже там — ничего не делаем, поэтому эхо безопасно.
    /// </summary>
    public static class ItemSync
    {
        public const string Msg = "ITEM_LOC";
        private const int MaxPerFrame = 64;

        public enum Kind : byte { None = 0, Slot = 1, Container = 2, Ground = 3 }

        public struct Loc
        {
            public Kind Kind;
            public string ParentId; // Slot, Container
            public string SlotName; // Slot
            public string ShipId;   // Ground (Ship.strRegID)
            public float X, Y;      // Container: ячейка, Ground: мировые координаты

            public override string ToString()
            {
                switch (Kind)
                {
                    case Kind.Slot: return $"slot {ParentId}/{SlotName}";
                    case Kind.Container: return $"container {ParentId} ({X},{Y})";
                    case Kind.Ground: return $"ground {ShipId} ({X:F1},{Y:F1})";
                    default: return "none";
                }
            }
        }

        /// <summary>true, пока применяем пакет — свои патчи не должны помечать предмет.</summary>
        internal static bool ApplyingRemote;

        private static readonly Dictionary<string, CondOwner> _dirty = new Dictionary<string, CondOwner>();
        private static readonly List<string> _flushKeys = new List<string>();

        // ---------------- Отслеживание ----------------

        internal static void MarkDirty(CondOwner co)
        {
            var net = NetworkManager.Current;
            if (net == null || !net.IsRunning || net.PeerCount == 0) return;

            if (net.Role == NetworkManager.NetRole.Client)
            {
                // Изменения, пришедшие с хоста, обратно не отправляем
                if (ApplyingRemote || ActionSync.ApplyingRemote) return;
            }

            if (!IsTrackable(co)) return;

            string id = CoLookup.Id(co);
            if (id.Length > 0)
                _dirty[id] = co;
        }

        private static bool IsTrackable(CondOwner co)
        {
            if (co == null || co.bDestroyed) return false;
            if (co.coStackHead != null) return false;   // в стеке — место хранит голова стека
            if (co.Pathfinder != null) return false;    // персонажи — это PositionSync
            if (co.Item == null) return false;          // не предмет
            if (co.HasCond("IsInstalled")) return false; // установленное оборудование — отдельная тема
            return true;
        }

        /// <summary>Вызывать раз в кадр (LateUpdate): отправить положение «грязных» предметов.</summary>
        public static void Flush()
        {
            if (_dirty.Count == 0) return;

            var net = NetworkManager.Current;
            if (net == null || !net.IsRunning)
            {
                _dirty.Clear();
                return;
            }

            _flushKeys.Clear();
            foreach (string key in _dirty.Keys)
            {
                _flushKeys.Add(key);
                if (_flushKeys.Count >= MaxPerFrame) break; // остальное — в следующем кадре
            }

            foreach (string id in _flushKeys)
            {
                CondOwner co = _dirty[id];
                _dirty.Remove(id);

                if (!IsTrackable(co)) continue;

                Loc loc = Locate(co);
                if (loc.Kind == Kind.None) continue; // «в руке курсора» и т.п. — ждём, пока положат

                Send(net, id, loc);
            }
        }

        private static void Send(NetworkManager net, string id, Loc loc)
        {
            var w = new NetDataWriter();
            w.Put(Msg);
            w.Put(id);
            w.Put((byte)loc.Kind);
            w.Put(loc.ParentId ?? "");
            w.Put(loc.SlotName ?? "");
            w.Put(loc.ShipId ?? "");
            w.Put(loc.X);
            w.Put(loc.Y);
            net.SendRaw(w, DeliveryMethod.ReliableOrdered);

            if (ActionSync.Verbose)
                GameLog.Info($"[ItemSync] → {id}: {loc}");
        }

        // ---------------- Где лежит предмет ----------------

        public static Loc Locate(CondOwner co)
        {
            var loc = new Loc { Kind = Kind.None };
            CondOwner parent = co.objCOParent;

            if (parent != null)
            {
                if (co.slotNow != null && !string.IsNullOrEmpty(co.slotNow.strName))
                {
                    loc.Kind = Kind.Slot;
                    loc.ParentId = CoLookup.Id(parent);
                    loc.SlotName = co.slotNow.strName;
                }
                else if (parent.objContainer != null && co.pairInventoryXY.IsValid())
                {
                    loc.Kind = Kind.Container;
                    loc.ParentId = CoLookup.Id(parent);
                    loc.X = co.pairInventoryXY.x;
                    loc.Y = co.pairInventoryXY.y;
                }
            }
            else if (co.ship != null && co.tf != null)
            {
                loc.Kind = Kind.Ground;
                loc.ShipId = co.ship.strRegID;
                loc.X = co.tf.position.x;
                loc.Y = co.tf.position.y;
            }

            return loc;
        }

        private static bool Same(Loc a, Loc b)
        {
            if (a.Kind != b.Kind) return false;
            switch (a.Kind)
            {
                case Kind.Slot:
                    return a.ParentId == b.ParentId && a.SlotName == b.SlotName;
                case Kind.Container:
                    return a.ParentId == b.ParentId && (int)a.X == (int)b.X && (int)a.Y == (int)b.Y;
                case Kind.Ground:
                    return a.ShipId == b.ShipId && Mathf.Abs(a.X - b.X) < 0.05f && Mathf.Abs(a.Y - b.Y) < 0.05f;
                default:
                    return true;
            }
        }

        // ---------------- Приём ----------------

        public static void OnReceive(NetPeer peer, NetPacketReader r, NetworkManager.NetRole role)
        {
            string id = r.GetString(128);
            var loc = new Loc
            {
                Kind = (Kind)r.GetByte(),
                ParentId = r.GetString(128),
                SlotName = r.GetString(64),
                ShipId = r.GetString(64),
                X = r.GetFloat(),
                Y = r.GetFloat()
            };

            if (ActionSync.Verbose)
                GameLog.Info($"[ItemSync] ← {id}: {loc}");

            CondOwner? co = CoLookup.Find(id);
            if (co == null || co.bDestroyed)
            {
                if (ActionSync.Verbose)
                    GameLog.Warn($"[ItemSync] item not found: {id}");
                return;
            }

            if (!Same(Locate(co), loc))
            {
                bool ok;
                ApplyingRemote = true;
                try
                {
                    ok = Apply(co, loc);
                }
                finally
                {
                    ApplyingRemote = false;
                }

                if (!ok)
                    GameLog.Warn($"[ItemSync] can't place {co.strName} ({id}) → {loc}");
            }

            // Хост всегда рассылает фактическое положение: если запрос клиента
            // не удалось выполнить, клиент получит «правду» и откатится.
            if (role == NetworkManager.NetRole.Host)
                _dirty[id] = co;
        }

        private static bool Apply(CondOwner co, Loc loc)
        {
            switch (loc.Kind)
            {
                case Kind.Slot:
                {
                    CondOwner? parent = CoLookup.Find(loc.ParentId);
                    if (parent == null || parent.compSlots == null) return false;

                    co.RemoveFromCurrentHome(false);
                    if (parent.compSlots.SlotItem(loc.SlotName, co, false))
                        return true;

                    // Слот занят/не подходит — не теряем предмет, кладём родителю куда влезет
                    PutSomewhere(co, parent);
                    return false;
                }

                case Kind.Container:
                {
                    CondOwner? parent = CoLookup.Find(loc.ParentId);
                    if (parent == null || parent.objContainer == null) return false;

                    co.RemoveFromCurrentHome(false);
                    parent.objContainer.AddCOSimple(co, new PairXY((int)loc.X, (int)loc.Y));
                    Container.Redraw(parent.objContainer);
                    return true;
                }

                case Kind.Ground:
                {
                    Ship? ship = null;
                    if (CrewSim.system != null && CrewSim.system.dictShips != null)
                        CrewSim.system.dictShips.TryGetValue(loc.ShipId, out ship);
                    if (ship == null) return false;

                    Container? oldContainer = co.objCOParent != null ? co.objCOParent.objContainer : null;

                    co.RemoveFromCurrentHome(false);
                    co.tf.position = new Vector3(loc.X, loc.Y, co.tf.position.z);
                    ship.AddCO(co, true);           // так же, как GUIInventoryItem.MoveToGround
                    if (co.Item != null)
                        co.Item.ResetTransforms(loc.X, loc.Y);
                    co.UpdateAppearance();
                    Container.Redraw(oldContainer);
                    return true;
                }
            }

            return false;
        }

        private static void PutSomewhere(CondOwner co, CondOwner parent)
        {
            CondOwner rest = parent.AddCO(co, false, true, true);
            if (rest != null && parent.ship != null)
                parent.ship.AddCO(rest, true);
        }

        public static void Clear()
        {
            _dirty.Clear();
            ApplyingRemote = false;
        }
    }

    // ---------------- Патчи: куда предмет может попасть ----------------

    [HarmonyPatch(typeof(Slots), nameof(Slots.SlotItem))]
    public static class Patch_Slots_SlotItem
    {
        static void Postfix(bool __result, CondOwner co)
        {
            if (__result) ItemSync.MarkDirty(co);
        }
    }

    [HarmonyPatch(typeof(Container), nameof(Container.AddCOSimple))]
    public static class Patch_Container_AddCOSimple
    {
        static void Postfix(CondOwner objCO)
        {
            ItemSync.MarkDirty(objCO);
        }
    }

    // У Ship.AddCO две перегрузки (публичная на 2 аргумента и приватная на 3) — указываем типы явно
    [HarmonyPatch(typeof(Ship), nameof(Ship.AddCO), new[] { typeof(CondOwner), typeof(bool) })]
    public static class Patch_Ship_AddCO
    {
        static void Postfix(CondOwner objICO)
        {
            ItemSync.MarkDirty(objICO);
        }
    }
}
