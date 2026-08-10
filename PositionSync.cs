using LiteNetLib;
using LiteNetLib.Utils;
using System.Collections.Generic;
using UnityEngine;

namespace SimpleCoop
{
    public static class PositionSync
    {
        private const float SendInterval = 0.2f;
        private static float _timer;

        public static void Update(float deltaTime)
        {
            var net = NetworkManager.Current;
            if (net == null || !net.IsRunning) return;
            if (net.Role != NetworkManager.NetRole.Host) return;

            _timer += deltaTime;
            if (_timer < SendInterval) return;
            _timer = 0f;

            SendSnapshot(net);
        }

        private static void SendSnapshot(NetworkManager net)
        {
            if (DataHandler.mapCOs == null || DataHandler.mapCOs.Count == 0)
                return;

            var list = new List<(string name, float x, float y)>();

            foreach (CondOwner co in DataHandler.mapCOs.Values)
            {
                if (co == null || co.tf == null) continue;
                if (co.Pathfinder == null) continue;

                // только экипаж/персонажи, не все объекты с Pathfinder
                if (!co.HasCond("IsPerson") && !co.HasCond("IsPlayer") && !co.HasCond("IsAIManual"))
                    continue;

                Vector3 p = co.tf.position;
                list.Add((co.strName ?? co.strID ?? "", p.x, p.y));
            }

            if (list.Count == 0) return;

            var writer = new NetDataWriter();
            writer.Put("SNAP_POS");
            writer.Put(list.Count);

            foreach (var e in list)
            {
                writer.Put(e.name);
                writer.Put(e.x);
                writer.Put(e.y);
            }

            net.SendRaw(writer);
        }

        public static void ApplySnapshot(NetPacketReader reader)
        {
            int count = reader.GetInt();
            if (count < 0 || count > 256) return;

            GameLog.Info($"SNAP {count} entities");

            for (int i = 0; i < count; i++)
            {
                string name = reader.GetString(128);
                float x = reader.GetFloat();
                float y = reader.GetFloat();

                CondOwner co = FindByName(name);
                if (co == null || co.tf == null) continue;

                Vector3 p = co.tf.position;
                co.tf.position = new Vector3(x, y, p.z);
            }
        }

        private static CondOwner FindByName(string name)
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
}