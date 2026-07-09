using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace YC.Editor
{
    public static class LocalhostBuildMenu
    {
        private const string OutputDirectory = "Builds/Localhost";
        private const string OutputFileName = "tuohuang.exe";
        private const string BatchBuildArgument = "-ycBuildLocalhost";

        [InitializeOnLoadMethod]
        private static void RunBatchBuildWhenRequested()
        {
            if (!Environment.GetCommandLineArgs().Contains(BatchBuildArgument))
            {
                return;
            }

            EditorApplication.delayCall += () =>
            {
                try
                {
                    BuildLocalhost();
                    EditorApplication.Exit(0);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    EditorApplication.Exit(1);
                }
            };
        }

        [MenuItem("YC/Build/Localhost Simulator")]
        public static void BuildLocalhost()
        {
            var scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                throw new BuildFailedException("No enabled scenes are configured in EditorBuildSettings.");
            }

            Directory.CreateDirectory(OutputDirectory);
            var outputPath = Path.Combine(OutputDirectory, OutputFileName);
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            };

            var report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new BuildFailedException(
                    "Localhost simulator build failed with result " + report.summary.result + ".");
            }

            Debug.Log("Localhost simulator build succeeded: " + outputPath);
        }
    }
}
