using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace SimpleCoop
{
    /// <summary>
    /// Приказы игрока клиента (клик по полу / клик по объекту) → хост.
    /// Результат действия возвращается клиенту через ActionSync (ACT_FX),
    /// перемещение — через PositionSync.
    /// </summary>
    public static class OrderSync
    {
        public static bool ApplyingRemoteOrder;

        // ---------------- Client → Host ----------------

        public static void SendMoveOrder(CondOwner crew, float x, float y)
        {
            var net = NetworkManager.Current;
            if (net == null || !net.IsRunning) return;
            if (net.Role != NetworkManager.NetRole.Client) return;

            var writer = new NetDataWriter();
            writer.Put("CMD_MOVE");
            writer.Put(CoLookup.Id(crew));
            writer.Put(x);
            writer.Put(y);

            net.SendRaw(writer);
            GameLog.Info($"[OrderSync] Sent MOVE for '{crew?.strName}' → ({x:F1}, {y:F1})");
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
            writer.Put(CoLookup.Id(crew));
            writer.Put(CoLookup.Id(target));
            writer.Put(interaction?.strName ?? "");
            writer.Put(CoLookup.Id(interaction?.obj3rd));
            writer.Put(x);
            writer.Put(y);

            net.SendRaw(writer);
            GameLog.Info(
                $"[OrderSync] Sent ACT '{crew?.strName}' → '{interaction?.strName}' on '{target?.strName}'");
        }

        // ---------------- Host ----------------

        public static void ApplyMoveOrder(string crewId, float x, float y)
        {
            CondOwner? crew = CoLookup.Find(crewId);
            if (crew == null)
            {
                GameLog.Warn($"[OrderSync] Crew not found: {crewId}");
                return;
            }

            if (crew.ship == null || crew.Pathfinder == null)
            {
                GameLog.Warn($"[OrderSync] Crew has no ship/pathfinder: {crewId}");
                return;
            }

            Tile tile = crew.ship.GetTileAtWorldCoords1(x, y, true, true);
            if (tile == null)
            {
                GameLog.Warn($"[OrderSync] No tile at ({x:F1},{y:F1})");
                return;
            }

            ApplyingRemoteOrder = true;
            try
            {
                bool ok = IssueWalk(crew, tile, x, y);
                GameLog.Info($"[OrderSync] Applied MOVE '{crew.strName}' → ({x:F1}, {y:F1}) ok={ok}");
            }
            finally
            {
                ApplyingRemoteOrder = false;
            }
        }

        /// <summary>
        /// Копия ветки "til != null" из CondOwner.AIIssueOrder, но БЕЗ проверки IsPlayer.
        /// Оригинал для персонажа без IsPlayer (а персонаж клиента на хосте именно такой)
        /// начисляет FreeWillPenalty и ставит SeekSocialDeny вместо ходьбы,
        /// а также рисует путь и туториалы на экране хоста.
        /// </summary>
        private static bool IssueWalk(CondOwner crew, Tile tile, float x, float y)
        {
            crew.AICancelAll(null);

            bool standingUp = false;
            CondTrigger? prone = DataHandler.GetCondTrigger("TIsProneAwake");
            if (prone != null && prone.Triggered(crew, null, true))
            {
                Interaction standUp = DataHandler.GetInteraction("ACTStandUp", null, false);
                if (standUp != null)
                {
                    standUp.bManual = true;
                    standingUp = crew.QueueInteraction(crew, standUp, false);
                }
            }

            CondTrigger? canWalk = DataHandler.GetCondTrigger("TCanWalk");
            if (canWalk != null && !canWalk.Triggered(crew, null, true) && !standingUp)
            {
                GameLog.Warn($"[OrderSync] '{crew.strName}' can't walk: {canWalk.strFailReasonLast}");
                return false;
            }

            PathResult path = crew.Pathfinder.SetGoal2(tile, 0f, null, x, y, crew.HasAirlockPermission(true));
            if (!path.HasPath)
            {
                GameLog.Warn($"[OrderSync] '{crew.strName}' no path: {path.FailReason(crew)}");
                return false;
            }

            Interaction walk = DataHandler.GetInteraction("Walk", null, false);
            if (walk == null) return false;

            walk.bManual = true;
            if (!crew.QueueInteraction(crew, walk, false))
                return false;

            walk.objThem = tile.coProps;
            walk.strTargetPoint = "use";
            walk.fTargetPointRange = 0f;
            return true;
        }

        public static void ApplyActionOrder(
            string crewId,
            string targetId,
            string interactionName,
            string thirdId,
            float x,
            float y)
        {
            CondOwner? crew = CoLookup.Find(crewId);
            if (crew == null)
            {
                GameLog.Warn($"[OrderSync] ACT crew not found: {crewId}");
                return;
            }

            CondOwner? target = CoLookup.Find(targetId);
            Interaction? interaction = string.IsNullOrEmpty(interactionName)
                ? null
                : DataHandler.GetInteraction(interactionName, null, false);

            if (interaction == null || target == null)
            {
                GameLog.Warn(
                    $"[OrderSync] ACT aborted: crew={crewId} int={interactionName} ({interaction != null}) " +
                    $"target={targetId} ({target != null})");
                return;
            }

            ApplyingRemoteOrder = true;
            try
            {
                // Как AIIssueOrder при клике, но без ветки FreeWill/SeekSocialDeny (см. IssueWalk)
                interaction.bManual = true;
                interaction.objUs = crew;
                interaction.objThem = target;

                if (!string.IsNullOrEmpty(thirdId))
                {
                    CondOwner? third = CoLookup.Find(thirdId);
                    if (third != null) interaction.obj3rd = third;
                    else GameLog.Warn($"[OrderSync] ACT 3rd not found: {thirdId}");
                }

                crew.AICancelAll(null);
                if (crew.Pathfinder != null)
                    crew.Pathfinder.Reset();

                bool ok = crew.QueueInteraction(target, interaction, false);

                GameLog.Info(
                    $"[OrderSync] Queued '{interactionName}' for '{crew.strName}' on '{target.strName}' ok={ok}");
            }
            finally
            {
                ApplyingRemoteOrder = false;
            }
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
            if (OrderSync.ApplyingRemoteOrder)
                return true;

            var net = NetworkManager.Current;
            if (net == null || !net.IsRunning)
                return true;

            // Приказы ИИ (не игрока) не трогаем
            if (!bPlayerOrdered)
                return true;

            if (net.Role == NetworkManager.NetRole.Host)
            {
                // Хост не может командовать персонажем, которым управляет клиент
                if (PlayerRegistry.IsRemoteControlled(__instance))
                {
                    GameLog.Warn($"[Order] {__instance.strName} is controlled by another player");
                    return false;
                }
                return true;
            }

            if (net.Role == NetworkManager.NetRole.Client)
            {
                if (!net.IsWorldReady)
                    return true; // свой мир / загрузка — игра работает как обычно

                // Приказывать можно только своему персонажу (назначен хостом)
                if (!PlayerRegistry.IsMine(__instance))
                {
                    GameLog.Warn($"[Order] {__instance.strName} is not your character");
                    return false;
                }

                if (coTarget == null && objInt == null)
                    OrderSync.SendMoveOrder(__instance, fPosX, fPosY);
                else
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
