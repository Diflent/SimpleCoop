using Ostranauts.Inventory;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SimpleCoop
{
    public class ModOverlay : MonoBehaviour
    {
        public static ModOverlay? Instance { get; private set; }

        private bool _visible = true;
        private readonly Queue<string> _lines = new Queue<string>();
        private const int MaxLines = 12;
        private Vector2 _scroll;

        private void Awake()
        {
            Instance = this;
        }

        public void AddLine(string msg)
        {
            _lines.Enqueue(msg);
            while (_lines.Count > MaxLines)
                _lines.Dequeue();
        }

        private void Update()
        {
            // F10 — показать/скрыть оверлей
            if (Input.GetKeyDown(KeyCode.F10))
                _visible = !_visible;
        }

        private void OnGUI()
        {
            if (!_visible) return;

            float w = 420f;
            float h = 240f;
            Rect box = new Rect(12f, 12f, w, h);

            GUI.Box(box, "");

            GUILayout.BeginArea(new Rect(box.x + 10, box.y + 8, w - 20, h - 16));

            GUILayout.Label("<b>SimpleCoop</b>");
            GUILayout.Space(4);

            var net = NetworkManager.Current;
            string role = net != null ? net.Role.ToString() : "None";
            string run = net != null && net.IsRunning ? "ON" : "OFF";

            GUILayout.Label($"Network: {run} | Role: {role}");
            GUILayout.Label("F5 Host | F6 Client | F7 Stop | F8 Chat");
            GUILayout.Label("F9 CMD | F10 Hide/Show this panel");
            GUILayout.Space(6);

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(130));
            foreach (string line in _lines)
                GUILayout.Label(line);
            GUILayout.EndScrollView();

            GUILayout.EndArea();
        }
    }
}