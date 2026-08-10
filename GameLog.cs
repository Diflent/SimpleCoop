using UnityEngine;

namespace SimpleCoop
{
    public static class GameLog
    {
        public static void Info(string msg)
        {
            SimpleCoop.Logger?.LogInfo(msg);

            if (ConsoleToGUI.instance != null)
                ConsoleToGUI.instance.LogInfo("[SimpleCoop] " + msg);
            else
                Debug.Log("[SimpleCoop] " + msg);
        }

        public static void Warn(string msg)
        {
            SimpleCoop.Logger?.LogWarning(msg);

            if (ConsoleToGUI.instance != null)
                ConsoleToGUI.instance.Log(
                    "<color=yellow><b>[Warning]</b></color>: [SimpleCoop] " + msg,
                    string.Empty,
                    LogType.Warning);
            else
                Debug.LogWarning("[SimpleCoop] " + msg);
        }

        public static void Error(string msg)
        {
            SimpleCoop.Logger?.LogError(msg);

            if (ConsoleToGUI.instance != null)
                ConsoleToGUI.instance.Log(
                    "<color=red><b>[Error]</b></color>: [SimpleCoop] " + msg,
                    string.Empty,
                    LogType.Error);
            else
                Debug.LogError("[SimpleCoop] " + msg);
        }
    }
}