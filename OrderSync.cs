using HarmonyLib;
using LiteNetLib.Utils;

namespace SimpleCoop
{
    /// <summary>
    /// Синхронизация приказов AIIssueOrder между клиентом и хостом.
    /// </summary>
    public static class OrderSync
    {
        public static bool ApplyingRemoteOrder;

        public static void SendMoveOrder(CondOwner crew, float x, float y)
        {
            var net = NetworkManager.Current;
            if (net == null || !net.IsRunning) return;
            if (net.Role != NetworkManager.NetRole.Client) return;

            var writer = new NetDataWriter();
            writer.Put("CMD_MOVE");
            writer.Put(crew != null ? crew.strName : "");
            writer.Put(x);
            writer.Put(y);

            net.SendRaw(writer);
            GameLog.Info($"[OrderSync] Sent MOVE for '{crew?.strName}' → ({x:F1}, {y:F1})");
        }

        public static void ApplyMoveOrder(string crewName, float x, float y)
        {
            CondOwner crew = FindCrew(crewName);
            if (crew == null)
            {
                GameLog.Warn($"[OrderSync] Crew not found: {crewName}");
                return;
            }

            if (crew.ship == null)
            {
                GameLog.Warn($"[OrderSync] Crew has no ship: {crewName}");
                return;
            }

            Tile tile = crew.ship.GetTileAtWorldCoords1(x, y, true, true);

            ApplyingRemoteOrder = true;
            try
            {
                bool ok = crew.AIIssueOrder(null, null, true, tile, x, y);
                GameLog.Info($"[OrderSync] Applied MOVE '{crewName}' → ({x:F1}, {y:F1}) ok={ok}");
            }
            finally
            {
                ApplyingRemoteOrder = false;
            }
        }

        private static CondOwner FindCrew(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            CondOwner selected = CrewSim.GetSelectedCrew();
            if (selected != null && selected.strName == name)
                return selected;

            if (CrewSim.objInstance != null)
            {
                // Если в CrewSim есть список — доработать позже
            }

            return selected;
        }
    }

    /// <summary>
    /// Harmony: на клиенте блокируем локальный AIIssueOrder и шлём на хост.
    /// </summary>
    [HarmonyPatch(typeof(CondOwner), nameof(CondOwner.AIIssueOrder))]
    public static class Patch_AIIssueOrder
    {
        static bool Prefix(
            CondOwner __instance,
            CondOwner coTarget,
            Interaction objInt,
            bool bPlayerOrdered,
            Tile til,
            float fPosX,
            float fPosY)
        {
            var net = NetworkManager.Current;

            // Только BepInEx — иначе спам в Debug Console при каждом шаге
            SimpleCoop.Logger.LogInfo(
                $"[OrderSync] AIIssueOrder | role={net?.Role} | crew={__instance?.strName} | " +
                $"playerOrdered={bPlayerOrdered} | target={coTarget?.strName} | int={objInt?.strName} | " +
                $"pos=({fPosX:F1},{fPosY:F1}) | remote={OrderSync.ApplyingRemoteOrder}");

            if (OrderSync.ApplyingRemoteOrder)
                return true;

            if (net == null || !net.IsRunning)
                return true;

            if (net.Role == NetworkManager.NetRole.Host)
                return true;

            if (net.Role == NetworkManager.NetRole.Client && bPlayerOrdered)
            {
                if (coTarget == null && objInt == null)
                {
                    OrderSync.SendMoveOrder(__instance, fPosX, fPosY);
                    return false;
                }

                GameLog.Info($"[OrderSync] Non-move order blocked on client: {objInt?.strName}");
                return false;
            }

            return true;
        }
    }
}