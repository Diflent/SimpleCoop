using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

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
            SimpleCoop.Logger.LogInfo($"[OrderSync] Sent MOVE for '{crew?.strName}' → ({x:F1}, {y:F1})");
        }

        public static void ApplyMoveOrder(string crewName, float x, float y)
        {
            CondOwner crew = FindCrew(crewName);
            if (crew == null)
            {
                SimpleCoop.Logger.LogWarning($"[OrderSync] Crew not found: {crewName}");
                return;
            }

            if (crew.ship == null)
            {
                SimpleCoop.Logger.LogWarning($"[OrderSync] Crew has no ship: {crewName}");
                return;
            }

            Tile tile = crew.ship.GetTileAtWorldCoords1(x, y, true, true);

            ApplyingRemoteOrder = true;
            try
            {
                bool ok = crew.AIIssueOrder(null, null, true, tile, x, y);
                SimpleCoop.Logger.LogInfo($"[OrderSync] Applied MOVE '{crewName}' → ({x:F1}, {y:F1}) ok={ok}");
            }
            finally
            {
                ApplyingRemoteOrder = false;
            }
        }

        
        public static void ApplyActionOrder(
            string crewName,
            string targetName,
            string interactionName,
            float x,
            float y)
        {
            CondOwner crew = FindCrew(crewName);
            if (crew == null)
            {
                GameLog.Warn($"[OrderSync] ACT crew not found: {crewName}");
                return;
            }

            CondOwner? target = null;
            if (!string.IsNullOrEmpty(targetName))
                target = FindCrew(targetName);

            Interaction? interaction = null;
            if (!string.IsNullOrEmpty(interactionName))
                interaction = DataHandler.GetInteraction(interactionName, null, false);

            Tile? tile = null;
            if (crew.ship != null)
                tile = crew.ship.GetTileAtWorldCoords1(x, y, true, true);

            ApplyingRemoteOrder = true;
            try
            {
                bool ok = crew.AIIssueOrder(target, interaction, true, tile, x, y);
                GameLog.Info($"[OrderSync] Applied ACT '{crewName}' {interactionName} on '{targetName}' ok={ok}");
            }
            finally
            {
                ApplyingRemoteOrder = false;
            }
        }

        private static CondOwner? FindCrew(string name)
        {
            return FindByName(name);
        }

        private static CondOwner? FindByName(string name)
        {
            if (string.IsNullOrEmpty(name) || DataHandler.mapCOs == null)
                return null;

            foreach (CondOwner co in DataHandler.mapCOs.Values)
            {
                if (co == null) continue;
                if (co.strName == name || co.strID == name)
                    return co;
            }

            return null;
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
            string role = net != null ? net.Role.ToString() : "null";

            SimpleCoop.Logger.LogInfo(
                $"[OrderSync] AIIssueOrder called | role={role} | crew={__instance?.strName} | " +
                $"playerOrdered={bPlayerOrdered} | target={coTarget?.strName} | int={objInt?.strName} | " +
                $"pos=({fPosX:F1},{fPosY:F1}) | applyingRemote={OrderSync.ApplyingRemoteOrder}");

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

                OrderSync.SendActionOrder(__instance, coTarget, objInt, fPosX, fPosY);
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(CondOwner), nameof(CondOwner.RefreshAnim))]
    public static class Patch_RefreshAnim
    {
        static void Postfix(CondOwner __instance)
        {
            var net = NetworkManager.Current;
            if (net == null || net.Role != NetworkManager.NetRole.Client)
                return;

            if (PositionSync.IsMoving(__instance))
                PositionSync.ForceWalkAnim(__instance);
        }
    }
}