using LiteNetLib;
using LiteNetLib.Utils;
using System.Collections.Generic;
using UnityEngine;
using System.Reflection;

namespace SimpleCoop
{
    public static class PositionSync
    {
        private const float SendInterval = 0.15f;
        private const float InterpSpeed = 12f;
        private const float SnapDistance = 3f;
        private const int MaxEntries = 32;

        private static float _timer;
        // ключ — strID персонажа
        private static readonly Dictionary<string, Vector2> _targets = new Dictionary<string, Vector2>();
        private static readonly int AnimStateHash = Animator.StringToHash("AnimState");
        private static FieldInfo? _animField;

        /// <summary>Хост: вызывать из Update. Клиент: из LateUpdate (поверх движения игры).</summary>
        public static void Update(float deltaTime)
        {
            var net = NetworkManager.Current;
            if (net == null || !net.IsRunning) return;

            if (net.Role == NetworkManager.NetRole.Host)
            {
                _timer += deltaTime;
                if (_timer >= SendInterval)
                {
                    _timer = 0f;
                    if (net.PeerCount > 0)
                        SendSnapshot(net);
                }
            }
            else if (net.Role == NetworkManager.NetRole.Client)
            {
                ApplyInterpolation(deltaTime);
            }
        }

        private static void SendSnapshot(NetworkManager net)
        {
            if (DataHandler.mapCOs == null || DataHandler.mapCOs.Count == 0)
                return;

            var list = new List<(string id, float x, float y)>();
            var added = new HashSet<string>();

            void TryAdd(CondOwner co)
            {
                if (co == null || co.tf == null) return;
                if (co.Pathfinder == null) return;

                string id = CoLookup.Id(co);
                if (string.IsNullOrEmpty(id) || added.Contains(id)) return;

                Vector3 p = co.tf.position;
                list.Add((id, p.x, p.y));
                added.Add(id);
            }

            TryAdd(CrewSim.coPlayer);
            TryAdd(CrewSim.GetSelectedCrew());

            if (CrewSim.aSelected != null)
            {
                foreach (var co in CrewSim.aSelected)
                    TryAdd(co);
            }

            foreach (CondOwner co in DataHandler.mapCOs.Values)
            {
                if (list.Count >= MaxEntries) break;
                if (co == null || co.tf == null || co.Pathfinder == null) continue;

                bool isPerson =
                    co.HasCond("IsPerson") ||
                    co.HasCond("IsPlayer") ||
                    co.HasCond("IsAIManual");

                if (isPerson)
                    TryAdd(co);
            }

            if (list.Count == 0) return;

            var writer = new NetDataWriter();
            writer.Put("SNAP_POS");
            writer.Put(list.Count);

            foreach (var e in list)
            {
                writer.Put(e.id);
                writer.Put(e.x);
                writer.Put(e.y);
            }

            // Позиции устаревают за 150 мс — переотправка не нужна, важен только порядок.
            net.SendRaw(writer, DeliveryMethod.Sequenced);
        }

        public static void ApplySnapshot(NetPacketReader reader)
        {
            int count = reader.GetInt();
            if (count < 0 || count > 256) return;

            for (int i = 0; i < count; i++)
            {
                string id = reader.GetString(128);
                float x = reader.GetFloat();
                float y = reader.GetFloat();

                if (string.IsNullOrEmpty(id)) continue;
                _targets[id] = new Vector2(x, y);
            }
        }

        private static void ApplyInterpolation(float deltaTime)
        {
            if (_targets.Count == 0) return;
            if (DataHandler.mapCOs == null) return;

            foreach (var kv in _targets)
            {
                CondOwner? co = CoLookup.Find(kv.Key);
                if (co == null || co.tf == null) continue;

                Vector3 pos = co.tf.position;
                Vector2 target = kv.Value;
                Vector2 current = new Vector2(pos.x, pos.y);
                float dist = Vector2.Distance(current, target);

                Vector2 next = current;

                if (dist > SnapDistance)
                {
                    next = target;
                    co.tf.position = new Vector3(next.x, next.y, pos.z);
                }
                else if (dist > 0.02f)
                {
                    next = Vector2.Lerp(current, target, 1f - Mathf.Exp(-InterpSpeed * deltaTime));
                    co.tf.position = new Vector3(next.x, next.y, pos.z);
                }

                Vector2 delta = next - current;
                bool moving = delta.sqrMagnitude > 0.0001f;

                // Направление взгляда
                if (moving)
                {
                    float angle = Mathf.Atan2(-delta.x, delta.y) * Mathf.Rad2Deg;
                    co.tf.rotation = Quaternion.AngleAxis(angle, Vector3.forward);
                }

                // Анимация Walk / Idle
                SetMoveAnim(co, moving);
            }
        }

        private static Animator? GetCoAnimator(CondOwner co)
        {
            if (co == null) return null;

            _animField ??= typeof(CondOwner).GetField(
                "anim",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (_animField != null)
            {
                var a = _animField.GetValue(co) as Animator;
                if (a != null) return a;
            }

            return co.GetComponentInChildren<Animator>();
        }

        private static void SetMoveAnim(CondOwner co, bool moving)
        {
            Animator? anim = GetCoAnimator(co);
            if (anim == null || !anim.isActiveAndEnabled) return;

            int state = moving ? 1 : 0; // Walk=1, Idle=0

            if (Interaction.dictAnims != null)
            {
                if (moving &&
                    !string.IsNullOrEmpty(co.strWalkAnim) &&
                    Interaction.dictAnims.TryGetValue(co.strWalkAnim, out int walk))
                {
                    state = walk;
                }
                else if (!moving &&
                         !string.IsNullOrEmpty(co.strIdleAnim) &&
                         Interaction.dictAnims.TryGetValue(co.strIdleAnim, out int idle))
                {
                    state = idle;
                }
            }

            anim.speed = 1f;
            anim.SetInteger(AnimStateHash, state);
        }

        public static bool IsMoving(CondOwner co)
        {
            if (co == null || co.tf == null) return false;

            string id = CoLookup.Id(co);
            if (string.IsNullOrEmpty(id)) return false;
            if (!_targets.TryGetValue(id, out Vector2 target)) return false;

            Vector2 cur = new Vector2(co.tf.position.x, co.tf.position.y);
            return Vector2.Distance(cur, target) > 0.05f;
        }

        public static void ForceWalkAnim(CondOwner co)
        {
            SetMoveAnim(co, true);
        }

        public static void Clear()
        {
            _targets.Clear();
            _timer = 0f;
        }
    }
}
