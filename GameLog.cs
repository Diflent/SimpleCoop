using UnityEngine;

namespace SimpleCoop
{
    public static class GameLog
    {
        public static void Info(string msg)
        {
            SimpleCoop.Logger?.LogInfo(msg);
            ModOverlay.Instance?.AddLine(msg);
        }

        public static void Warn(string msg)
        {
            SimpleCoop.Logger?.LogWarning(msg);
            ModOverlay.Instance?.AddLine("[W] " + msg);
        }

        public static void Error(string msg)
        {
            SimpleCoop.Logger?.LogError(msg);
            ModOverlay.Instance?.AddLine("[E] " + msg);
        }
    }
}