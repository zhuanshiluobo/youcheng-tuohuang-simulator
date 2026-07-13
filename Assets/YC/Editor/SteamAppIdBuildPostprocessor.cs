using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace YC.Editor
{
    public sealed class SteamAppIdBuildPostprocessor : IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.StandaloneWindows &&
                report.summary.platform != BuildTarget.StandaloneWindows64)
                return;

            var source = Path.Combine(Directory.GetCurrentDirectory(), "steam_appid.txt");
            var outputDirectory = Path.GetDirectoryName(report.summary.outputPath);
            if (!File.Exists(source) || string.IsNullOrEmpty(outputDirectory))
            {
                Debug.LogError("未找到 AppID 480 的 steam_appid.txt，无法复制到本地构建目录。");
                return;
            }

            File.Copy(source, Path.Combine(outputDirectory, "steam_appid.txt"), true);
        }
    }
}
