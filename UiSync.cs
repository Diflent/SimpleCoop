using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Ostranauts.InputControl;
using Ostranauts.Inventory;

namespace SimpleCoop
{
    /// <summary>
    /// Окна, которые игра открывает по ходу взаимодействия (strRaiseUI: инвентарь,
    /// контейнер, терминал...). Взаимодействия персонажей клиентов выполняются на хосте,
    /// и без перехвата CrewSim.RaiseUI переключал бы выделение хоста на персонажа клиента
    /// (CrewSim.CycleCrew). Поэтому такое окно открывается у клиента, а не у хоста.
    /// </summary>
    public static class UiSync
    {
        public const string MsgRaiseUI = "RAISE_UI"; // хост → клиент: key, coSelfId, targetId

        /// <summary>
        /// Кто «открывает» окно. Повторяет логику CrewSim.RaiseUI: если окно — у предмета
        /// (контейнер), то открывающий — второй участник текущего взаимодействия предмета.
        /// </summary>
        internal static CondOwner? Actor(CondOwner coSelf)
        {
            if (coSelf == null) return null;
            if (coSelf.IsHumanOrRobot) return coSelf;

            Interaction? ia = coSelf.GetInteractionCurrent();
            return ia?.objThem;
        }

        /// <summary>
        /// Хост: цель текущего взаимодействия (для окна инвентаря — открываемый контейнер).
        /// У клиента этого взаимодействия нет (оно выполняется на хосте), поэтому
        /// coSelf.GetInteractionCurrent() там пустой — цель передаём явно.
        /// </summary>
        internal static string TargetId(CondOwner coSelf)
        {
            Interaction? ia = coSelf != null ? coSelf.GetInteractionCurrent() : null;
            return CoLookup.Id(ia?.objThem);
        }

        /// <summary>Клиент: хост просит открыть окно.</summary>
        public static void OnRaiseUI(NetPacketReader r)
        {
            string key = r.GetString(64);
            string id = r.GetString(128);
            string targetId = r.GetString(128);

            CondOwner? co = CoLookup.Find(id);
            if (co == null)
            {
                GameLog.Warn($"[UI] RAISE_UI {key}: object {id} not found");
                return;
            }

            if (key == "Inventory")
            {
                OpenContainer(co, CoLookup.Find(targetId), targetId);
                return;
            }

            // Остальные окна (зеркало раковины, терминалы...) сами читают
            // coSelf.GetInteractionCurrent().objThem. У клиента этого взаимодействия нет —
            // на время, пока окно открыто, подставляем его (см. Patch_GetInteractionCurrent).
            CondOwner? target = CoLookup.Find(targetId);
            if (target != null)
                SetInteractionOverride(co, target, true);

            CrewSim.RaiseUI(key, co);

            if (CrewSim.goUI == null)
                ClearInteractionOverride(); // окно не открылось
        }

        // ===================================================================
        //       Подмена «текущего взаимодействия» для окон у клиента
        // ===================================================================

        private static CondOwner? _overrideCo;
        private static CondOwner? _overrideThem;
        private static bool _overrideAlways;
        private static Interaction? _dummy;

        /// <summary>
        /// Пока окно открыто, co.GetInteractionCurrent() вернёт взаимодействие «co → them».
        /// always=false — только если у co своего текущего взаимодействия нет.
        /// </summary>
        internal static void SetInteractionOverride(CondOwner co, CondOwner them, bool always)
        {
            _overrideCo = co;
            _overrideThem = them;
            _overrideAlways = always;
        }

        internal static void ClearInteractionOverride()
        {
            _overrideCo = null;
            _overrideThem = null;
        }

        internal static void TryOverride(CondOwner instance, ref Interaction result)
        {
            // (object) — обычное сравнение ссылок: метод вызывается очень часто,
            // а Unity-шный == для объектов заметно дороже
            if ((object?)_overrideCo == null || !ReferenceEquals(instance, _overrideCo)) return;
            if (result != null && !_overrideAlways) return;

            _dummy ??= DataHandler.GetInteraction("Wait", null, false);
            if (_dummy == null) return;

            _dummy.objUs = _overrideCo!;
            _dummy.objThem = _overrideThem!;
            result = _dummy;
        }

        /// <summary>
        /// Повторяет ветку strCOGUIKey == "Inventory" из CrewSim.RaiseUI, но контейнер
        /// берёт из пакета, а не из coSelf.GetInteractionCurrent().objThem.
        /// </summary>
        private static void OpenContainer(CondOwner crew, CondOwner? container, string containerId)
        {
            if (container == null)
            {
                GameLog.Warn($"[UI] Container {containerId} not found");
                return;
            }

            // Окно инвентаря показывается для выделенного персонажа
            if (crew != CrewSim.GetSelectedCrew() && CrewSim.objInstance != null)
                CrewSim.objInstance.SetBracketTarget(crew.strID, false);

            if (CrewSim.inventoryGUI == null) return;

            if (!CrewSim.inventoryGUI.IsOpen)
                CommandInventory.ToggleInventory(crew, false);

            CrewSim.inventoryGUI.SpawnInventoryWindow(container, InventoryWindowType.Container, null, null);
        }
    }

    [HarmonyPatch(typeof(CondOwner), nameof(CondOwner.GetInteractionCurrent))]
    public static class Patch_GetInteractionCurrent
    {
        static void Postfix(CondOwner __instance, ref Interaction __result)
        {
            UiSync.TryOverride(__instance, ref __result);
        }
    }

    /// <summary>Окно закрыто — подмена больше не нужна.</summary>
    [HarmonyPatch(typeof(CrewSim), nameof(CrewSim.LowerUI))]
    public static class Patch_CrewSim_LowerUI
    {
        static void Postfix()
        {
            if (CrewSim.goUI == null)
                UiSync.ClearInteractionOverride();
        }
    }

    [HarmonyPatch(typeof(CrewSim), nameof(CrewSim.RaiseUI))]
    public static class Patch_CrewSim_RaiseUI
    {
        static bool Prefix(string strCOGUIKey, CondOwner coSelf)
        {
            var net = NetworkManager.Current;
            if (net == null || !net.IsRunning || net.Role != NetworkManager.NetRole.Host)
                return true;

            CondOwner? actor = UiSync.Actor(coSelf);
            if (actor == null || !PlayerRegistry.IsRemoteControlled(actor))
                return true; // окно открывает хост или NPC — как обычно

            // Окно открывает персонаж клиента: показываем его клиенту, хоста не трогаем
            var w = new NetDataWriter();
            w.Put(UiSync.MsgRaiseUI);
            w.Put(strCOGUIKey);
            w.Put(CoLookup.Id(coSelf));
            w.Put(UiSync.TargetId(coSelf));   // что открываем (контейнер) — у клиента этого взаимодействия нет
            net.SendToPeerId(PlayerRegistry.PeerIdOf(actor), w);
            return false;
        }
    }

    /// <summary>
    /// Игра при расстыковке «сворачивает» (SaveToShallow) все корабли, не пристыкованные
    /// к кораблю игрока, и персонажи на них перестают существовать в сцене.
    /// Поэтому хост может улететь, только когда все игроки у него на борту.
    /// </summary>
    public static class ShipGuard
    {
        internal static bool CanUndock(Ship shipUs, Ship objShipThem)
        {
            var net = NetworkManager.Current;
            if (net == null || !net.IsRunning) return true;

            if (net.Role == NetworkManager.NetRole.Client)
            {
                if (!net.IsWorldReady) return true;
                GameLog.Warn("[Ship] Управлять кораблём пока может только хост");
                return false;
            }

            // Хост: касается только расстыковки корабля, на котором находится хост.
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
    }

    [HarmonyPatch(typeof(CrewSim), nameof(CrewSim.UndockShip))]
    public static class Patch_CrewSim_UndockShip
    {
        static bool Prefix(Ship shipUs, Ship objShipThem) => ShipGuard.CanUndock(shipUs, objShipThem);
    }

    [HarmonyPatch(typeof(CrewSim), nameof(CrewSim.UnMoorShip))]
    public static class Patch_CrewSim_UnMoorShip
    {
        static bool Prefix(Ship shipUs, Ship objShipThem) => ShipGuard.CanUndock(shipUs, objShipThem);
    }
}
