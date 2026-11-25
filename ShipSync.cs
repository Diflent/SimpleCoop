using System.Linq;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Ostranauts.Core.Models;
using UnityEngine;

namespace SimpleCoop
{
    /// <summary>
    /// Корабль игроков: стыковка, швартовка и полёт.
    ///
    /// Пилотирует хост. Всё, что происходит с группой кораблей хоста (его корабль и всё,
    /// что к нему пристыковано), хост рассылает клиентам:
    ///   SHIP_EV  — стыковка / расстыковка / швартовка / отшвартовка. Клиент вызывает
    ///              у себя тот же метод CrewSim — игра сама переставит и «свернёт» корабли.
    ///   SHIP_POS — положение и скорость «якоря» группы (ShipSitu) несколько раз в секунду.
    ///              Тягу и автопилот клиент у себя не считает точно, поэтому его копию
    ///              корабля постоянно поправляем.
    ///
    /// Корабли ИИ вдали от игроков стыкуются у каждого сами, как и раньше.
    /// </summary>
    public static class ShipSync
    {
        public const string MsgEvent = "SHIP_EV";  // хост → клиенты
        public const string MsgPos = "SHIP_POS";   // хост → клиенты

        public enum Ev : byte { Dock = 1, Undock = 2, Moor = 3, UnMoor = 4 }

        private const float SendInterval = 0.2f;

        /// <summary>Догонять позицию по часам не дальше этого (с игрового времени) — дальше это уже рассинхрон часов.</summary>
        private const double MaxExtrapolation = 5.0;

        /// <summary>Клиент: сейчас применяется событие хоста — свои проверки не мешают.</summary>
        internal static bool ApplyingRemote;

        private static float _timer;

        // ===================================================================
        //                        Группа кораблей хоста
        // ===================================================================

        /// <summary>
        /// Касается ли корабль группы игроков: корабль персонажа хоста и всё, что к нему пристыковано.
        /// У клиента CrewSim.coPlayer — тот же персонаж хоста, так что группа у всех одна.
        /// </summary>
        internal static bool InPlayerGroup(Ship? ship)
        {
            Ship? home = CrewSim.coPlayer != null ? CrewSim.coPlayer.ship : null;
            if (ship == null || home == null) return false;
            if (ship == home) return true;

            return home.GetAllDockedShips(null).Any(s => s != null && s.strRegID == ship.strRegID);
        }

        internal static bool InPlayerGroup(string? regId) =>
            !string.IsNullOrEmpty(regId) && InPlayerGroup(CrewSim.system?.GetShipByRegID(regId));

        private static Ship? Find(string regId) =>
            string.IsNullOrEmpty(regId) ? null : CrewSim.system?.GetShipByRegID(regId);

        /// <summary>Сеть запущена и роль совпадает (клиент — только с загруженным миром).</summary>
        private static bool Is(NetworkManager.NetRole role)
        {
            var net = NetworkManager.Current;
            if (net == null || !net.IsRunning || net.Role != role) return false;
            return role != NetworkManager.NetRole.Client || net.IsWorldReady;
        }

        // ===================================================================
        //                       Проверки перед действием
        // ===================================================================

        /// <summary>
        /// Клиент не стыкует и не расстыковывает группу игроков сам — это приходит от хоста.
        /// </summary>
        internal static bool ClientMayChange(bool touchesGroup)
        {
            if (ApplyingRemote || !touchesGroup || !Is(NetworkManager.NetRole.Client))
                return true;

            GameLog.Warn("[Ship] Управлять кораблём пока может только хост");
            return false;
        }

        /// <summary>
        /// Игра при расстыковке «сворачивает» (SaveToShallow) все корабли, не пристыкованные
        /// к кораблю хоста, и персонажи на них перестают существовать в сцене.
        /// Поэтому хост может улететь, только когда все игроки у него на борту.
        /// </summary>
        internal static bool HostMayUndock(Ship shipUs, Ship objShipThem)
        {
            if (!Is(NetworkManager.NetRole.Host)) return true;

            // Касается только корабля, на котором находится хост.
            // Корабли ИИ (Ostranauts/Ships/Commands/Undock.cs) расстыковываются как обычно.
            Ship? hostShip = CrewSim.coPlayer != null ? CrewSim.coPlayer.ship : null;
            if (hostShip == null || (shipUs != hostShip && objShipThem != hostShip))
                return true;

            foreach (CondOwner crew in PlayerRegistry.RemoteCrews())
            {
                if (crew.ship != hostShip)
                {
                    GameLog.Warn($"[Ship] {crew.strName} не на борту — сначала заберите всех игроков");
                    return false;
                }
            }
            return true;
        }

        // ===================================================================
        //                       Хост → клиенты: события
        // ===================================================================

        internal static void SendEvent(Ev ev, string usId, string themId, string portUs = "", string portThem = "", bool flag = false)
        {
            if (!Is(NetworkManager.NetRole.Host)) return;

            var w = new NetDataWriter();
            w.Put(MsgEvent);
            w.Put((byte)ev);
            w.Put(usId ?? "");
            w.Put(themId ?? "");
            w.Put(portUs ?? "");
            w.Put(portThem ?? "");
            w.Put(flag);
            NetworkManager.Current!.SendRaw(w, DeliveryMethod.ReliableOrdered);

            GameLog.Info($"[Ship] {ev} {usId} ↔ {themId}");

            // Сразу и положение — после расстыковки корабль получает толчок (pushback)
            _timer = SendInterval;
        }

        /// <summary>Клиент: повторить у себя то, что сделал хост.</summary>
        public static void OnEvent(NetPacketReader r)
        {
            var ev = (Ev)r.GetByte();
            string usId = r.GetString(64);
            string themId = r.GetString(64);
            string portUs = r.GetString(128);
            string portThem = r.GetString(128);
            bool flag = r.GetBool();

            Ship? us = Find(usId);
            Ship? them = Find(themId);
            if (us == null || (them == null && ev != Ev.Dock && ev != Ev.Moor))
            {
                GameLog.Warn($"[Ship] {ev}: ship {usId} / {themId} not found");
                return;
            }

            ApplyingRemote = true;
            try
            {
                switch (ev)
                {
                    case Ev.Dock:
                        // Те же порты, что выбрал хост: иначе корабли встанут к другим шлюзам
                        CrewSim.DockShip(us, themId,
                            string.IsNullOrEmpty(portThem) ? null : portThem,
                            string.IsNullOrEmpty(portUs) ? null : portUs, null);
                        break;

                    case Ev.Undock:
                        CrewSim.UndockShip(us, them!, flag, false);
                        break;

                    case Ev.Moor:
                        // Порты швартовки игра создаёт заново с новыми id — пусть клиент создаст свои
                        CrewSim.MoorShip(new DockingPortDTO(us, null),
                            new DockingPortDTO { RegID = themId, Ship = them! }, flag);
                        break;

                    case Ev.UnMoor:
                        CrewSim.UnMoorShip(us, them!);
                        break;
                }
            }
            finally
            {
                ApplyingRemote = false;
            }

            GameLog.Info($"[Ship] Host: {ev} {usId} ↔ {themId}");
        }

        // ===================================================================
        //                     Хост → клиенты: положение
        // ===================================================================

        /// <summary>
        /// Положение группы задаёт её «якорь» (DockGroup.Anchor): остальные корабли
        /// группы игра ставит относительно него (DockGroup.TimeAdvance).
        /// </summary>
        private static Ship? AnchorShip(Ship home)
        {
            ShipSitu? anchor = home.objSS?._dockGroup?.Anchor;
            if (anchor == null || anchor == home.objSS) return home;

            foreach (Ship s in home.GetAllDockedShips(null))
            {
                if (s != null && s.objSS == anchor)
                    return s;
            }
            return home;
        }

        /// <summary>Хост: вызывать каждый кадр.</summary>
        public static void Update()
        {
            if (!Is(NetworkManager.NetRole.Host)) return;

            _timer += Time.unscaledDeltaTime;
            if (_timer < SendInterval) return;
            _timer = 0f;

            var net = NetworkManager.Current!;
            if (net.PeerCount == 0) return;

            Ship? home = CrewSim.coPlayer != null ? CrewSim.coPlayer.ship : null;
            if (home == null || home.bDestroyed) return;

            Ship? ship = AnchorShip(home);
            ShipSitu? ss = ship?.objSS;
            if (ship == null || ss == null || ss.bIsBO) return;

            var w = new NetDataWriter();
            w.Put(MsgPos);
            w.Put(ship.strRegID ?? "");
            w.Put(StarSystem.fEpoch);
            w.Put(ss.vPosx);
            w.Put(ss.vPosy);
            w.Put(ss.vVelX);
            w.Put(ss.vVelY);
            w.Put(ss.vBOOffsetx);
            w.Put(ss.vBOOffsety);
            w.Put(ss.vAccIn.x); w.Put(ss.vAccIn.y);
            w.Put(ss.vAccRCS.x); w.Put(ss.vAccRCS.y);
            w.Put(ss.vAccEx.x); w.Put(ss.vAccEx.y);
            w.Put(ss.fRot);
            w.Put(ss.fW);
            w.Put(ss.fA);
            w.Put(ss.bBOLocked);
            w.Put(ss.bOrbitLocked);
            w.Put(ss.strBOPORShip ?? "");

            // Устаревает через 0.2 с — переотправка не нужна, важен только порядок
            net.SendRaw(w, DeliveryMethod.Sequenced);
        }

        /// <summary>Клиент: поставить корабль туда, где он у хоста.</summary>
        public static void OnPos(NetPacketReader r)
        {
            string regId = r.GetString(64);
            double epoch = r.GetDouble();
            double px = r.GetDouble(), py = r.GetDouble();
            double vx = r.GetDouble(), vy = r.GetDouble();
            double ox = r.GetDouble(), oy = r.GetDouble();
            var accIn = new Vector2(r.GetFloat(), r.GetFloat());
            var accRcs = new Vector2(r.GetFloat(), r.GetFloat());
            var accEx = new Vector2(r.GetFloat(), r.GetFloat());
            float rot = r.GetFloat(), w = r.GetFloat(), a = r.GetFloat();
            bool boLocked = r.GetBool(), orbitLocked = r.GetBool();
            string bo = r.GetString(64);

            Ship? ship = Find(regId);
            ShipSitu? ss = ship?.objSS;
            if (ship == null || ss == null || ship.bDestroyed) return;

            // Пакет шёл по сети — догнать по скорости до своих часов (TimeSync держит их рядом)
            double dt = StarSystem.fEpoch - epoch;
            if (dt < 0.0 || dt > MaxExtrapolation) dt = 0.0;

            ss.vPosx = px + vx * dt;
            ss.vPosy = py + vy * dt;
            ss.vVelX = vx;
            ss.vVelY = vy;
            ss.vBOOffsetx = ox;
            ss.vBOOffsety = oy;
            ss.vAccIn = accIn;
            ss.vAccRCS = accRcs;
            ss.vAccEx = accEx;
            ss.fRot = rot;
            ss.fW = w;
            ss.fA = a;
            ss.bBOLocked = boLocked;
            ss.bOrbitLocked = orbitLocked;
            ss.strBOPORShip = string.IsNullOrEmpty(bo) ? null : bo;

            // Пристыкованные корабли — следом за якорем
            if (ss._dockGroup != null && ss._dockGroup.Anchor == ss)
                ss._dockGroup.TimeAdvance(0.0);
        }

        public static void Reset()
        {
            _timer = 0f;
            ApplyingRemote = false;
        }
    }

    // ---------------- Патчи ----------------

    [HarmonyPatch(typeof(CrewSim), nameof(CrewSim.UndockShip))]
    public static class Patch_CrewSim_UndockShip
    {
        static bool Prefix(Ship shipUs, Ship objShipThem, out bool __state)
        {
            __state = ShipSync.InPlayerGroup(shipUs) || ShipSync.InPlayerGroup(objShipThem);
            return ShipSync.ClientMayChange(__state) && ShipSync.HostMayUndock(shipUs, objShipThem);
        }

        static void Postfix(Ship shipUs, Ship objShipThem, bool bPushback, bool __state, bool __runOriginal)
        {
            if (__runOriginal && __state)
                ShipSync.SendEvent(ShipSync.Ev.Undock, shipUs.strRegID, objShipThem.strRegID, flag: bPushback);
        }
    }

    [HarmonyPatch(typeof(CrewSim), nameof(CrewSim.UnMoorShip))]
    public static class Patch_CrewSim_UnMoorShip
    {
        static bool Prefix(Ship shipUs, Ship objShipThem, out bool __state)
        {
            __state = ShipSync.InPlayerGroup(shipUs) || ShipSync.InPlayerGroup(objShipThem);
            return ShipSync.ClientMayChange(__state) && ShipSync.HostMayUndock(shipUs, objShipThem);
        }

        static void Postfix(Ship shipUs, Ship objShipThem, bool __state, bool __runOriginal)
        {
            if (__runOriginal && __state)
                ShipSync.SendEvent(ShipSync.Ev.UnMoor, shipUs.strRegID, objShipThem.strRegID);
        }
    }

    [HarmonyPatch(typeof(CrewSim), nameof(CrewSim.DockShip))]
    public static class Patch_CrewSim_DockShip
    {
        static bool Prefix(Ship shipUs, string strRegID, out bool __state)
        {
            __state = ShipSync.InPlayerGroup(shipUs) || ShipSync.InPlayerGroup(strRegID);
            return ShipSync.ClientMayChange(__state);
        }

        static void Postfix(Ship shipUs, Ship __result, bool __state, bool __runOriginal)
        {
            if (!__runOriginal || !__state || __result == null || shipUs == null) return;

            // Порты, которые выбрала игра, — клиент встанет к тем же
            string portUs = shipUs.GetPortIdForDockedShip(__result.strRegID) ?? "";
            string portThem = __result.GetPortIdForDockedShip(shipUs.strRegID) ?? "";
            ShipSync.SendEvent(ShipSync.Ev.Dock, shipUs.strRegID, __result.strRegID, portUs, portThem);
        }
    }

    /// <summary>
    /// Оба варианта MoorShip сходятся сюда (MoorShip(Ship, Ship, bool) создаёт порты и вызывает этот).
    /// </summary>
    [HarmonyPatch(typeof(CrewSim), nameof(CrewSim.MoorShip),
        new[] { typeof(DockingPortDTO), typeof(DockingPortDTO), typeof(bool) })]
    public static class Patch_CrewSim_MoorShip
    {
        static bool Prefix(DockingPortDTO loaded, DockingPortDTO incoming, out bool __state)
        {
            __state = ShipSync.InPlayerGroup(loaded?.Ship) || ShipSync.InPlayerGroup(incoming?.RegID);
            return ShipSync.ClientMayChange(__state);
        }

        static void Postfix(DockingPortDTO loaded, DockingPortDTO incoming, bool moorLoadedToIncoming,
            Ship __result, bool __state, bool __runOriginal)
        {
            if (!__runOriginal || !__state || __result == null || loaded?.Ship == null) return;
            ShipSync.SendEvent(ShipSync.Ev.Moor, loaded.Ship.strRegID, __result.strRegID, flag: moorLoadedToIncoming);
        }
    }
}
