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
            var settingsPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/GameSettings/GameSettingsMenu.prefab");
            var driver = settingsPrefab == null ? null : settingsPrefab.GetComponent<FontRefreshDriver>();
            var reason = string.Empty;
            if (driver == null || !driver.TryValidateConfiguration(out reason))
            {
                throw new System.InvalidOperationException(
                    string.IsNullOrEmpty(reason) ? "Missing serialized FontRefreshDriver." : reason);
            }

            var result = FontHealthCheckRunner.RunWithSerializedFonts(
                mode,
                driver.CjkFont,
                driver.LatinFont,
                driver);
            if (result.RecreatedManagedFonts)
            {
                Debug.LogWarning(result.Snapshot);
                return;
            }

            Debug.Log(result.Snapshot);
        }
    }
}
