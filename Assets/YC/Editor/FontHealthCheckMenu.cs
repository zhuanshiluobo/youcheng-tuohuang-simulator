using UnityEditor;
using UnityEngine;
using YC.Presentation;

namespace YC.Editor
{
    public static class FontHealthCheckMenu
    {
        [MenuItem("YC/Dev/Run Font Health Check Snapshot")]
        public static void RunFontHealthCheckSnapshot()
        {
            LogResult(FontHealthCheckMode.CheckOnly);
        }

        [MenuItem("YC/Dev/Run Font Health Check (Simulate Recreate)")]
        public static void RunFontHealthCheckSimulation()
        {
            LogResult(FontHealthCheckMode.SimulateRecreate);
        }

        private static void LogResult(FontHealthCheckMode mode)
        {
            var result = FontHealthCheckRunner.Run(mode);
            if (result.RecreatedManagedFonts)
            {
                Debug.LogWarning(result.Snapshot);
                return;
            }

            Debug.Log(result.Snapshot);
        }
    }
}