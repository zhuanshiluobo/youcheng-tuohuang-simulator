using YC.Application.DevTools;
using UnityEditor;
using UnityEngine;

namespace YC.Editor
{
    public static class LocalhostAutoplayMenu
    {
        [MenuItem("YC/Dev/Run Localhost Autoplay")]
        public static void RunLocalhostAutoplay()
        {
            var result = LocalhostAutoplayRunner.RunToRound8Settlement();
            if (result.Succeeded)
            {
                Debug.Log(result.Snapshot);
                return;
            }

            Debug.LogError(result.Snapshot);
        }
    }
}
