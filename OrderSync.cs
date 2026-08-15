using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace SimpleCoop
{
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
            writer.Put(crew != null ? (crew.strID ?? crew.strName ?? "") : "");
            writer.Put(x);
            writer.Put(y);

            net.SendRaw(writer);
            GameLog.Info($"[OrderSync] Sent MOVE for '{crew?.strName}' → ({x:F1}, {y:F1})");
        }

        public static void ApplyMoveOrder(string crewName, float x, float y)
        {
            CondOwner crew = FindByName(crewName);
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

        public static void SendActionOrder(
            CondOwner crew,
            CondOwner? target,
            Interaction? interaction,
            float x,
            float y)
        {
            var net = NetworkManager.Current;
            if (net == null || !net.IsRunning) return;
            if (net.Role != NetworkManager.NetRole.Client) return;

            var writer = new NetDataWriter();
            writer.Put("CMD_ACT");
            writer.Put(crew != null ? (crew.strID ?? crew.strName ?? "") : "");
            writer.Put(target != null ? (target.strID ?? target.strName ?? "") : "");
            writer.Put(interaction != null ? (interaction.strName ?? "") : "");
            writer.Put(x);
            writer.Put(y);

            net.SendRaw(writer);
            GameLog.Info(
                $"[OrderSync] Sent ACT '{crew?.strName}' → '{interaction?.strName}' on '{target?.strName}'");
        }

        public static void ApplyActionOrder(
            string crewName,
            string targetName,
            string interactionName,
            float x,
            float y)
        {
            CondOwner crew = FindByName(crewName);
            if (crew == null)
            {
                GameLog.Warn($"[OrderSync] ACT crew not found: {crewName}");
                return;
            }

            CondOwner? target = null;
            if (!string.IsNullOrEmpty(targetName))
            {
                target = FindByName(targetName);
                if (target == null)
                    GameLog.Warn($"[OrderSync] ACT target not found: {targetName}");
            }

            Interaction? interaction = null;
            if (!string.IsNullOrEmpty(interactionName))
            {
                interaction = DataHandler.GetInteraction(interactionName, null, false);
                if (interaction == null)
                    GameLog.Warn($"[OrderSync] ACT interaction not found: {interactionName}");
            }

            if (interaction == null && target == null)
            {
                GameLog.Warn("[OrderSync] ACT aborted: no interaction and no target");
                return;
            }

            ApplyingRemoteOrder = true;
            try
            {
                bool ok = crew.AIIssueOrder(target, interaction, true, null, 0f, 0f);
                GameLog.Info(
                    $"[OrderSync] Applied ACT '{crewName}' int='{interactionName}' " +
                    $"target='{targetName}' foundT={(target != null)} foundI={(interaction != null)} ok={ok}");
            }
            finally
            {
                ApplyingRemoteOrder = false;
            }
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