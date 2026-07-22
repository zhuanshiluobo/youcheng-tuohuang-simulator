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
        private const string DevelopmentOutputDirectory = "Builds/LocalhostDevelopment";
        private const string OutputFileName = "tuohuang.exe";
        private const string BatchBuildArgument = "-ycBuildLocalhost";
        private const string BatchDevelopmentBuildArgument = "-ycBuildLocalhostDevelopment";

        [InitializeOnLoadMethod]
        private static void RunBatchBuildWhenRequested()
        {
            var arguments = Environment.GetCommandLineArgs();
            var buildDevelopment = arguments.Contains(BatchDevelopmentBuildArgument);
            if (!buildDevelopment && !arguments.Contains(BatchBuildArgument))
            {
                return;
            }

            EditorApplication.delayCall += () =>
            {
                try
                {
                    if (buildDevelopment)
                    {
                        BuildLocalhostDevelopment();
                    }
                    else
                    {
                        BuildLocalhost();
                    }

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
            BuildLocalhost(OutputDirectory, BuildOptions.None, "Localhost simulator build succeeded");
        }

        [MenuItem("YC/Build/Localhost Development Simulator")]
        public static void BuildLocalhostDevelopment()
        {
            BuildLocalhost(
                DevelopmentOutputDirectory,
                BuildOptions.Development,
                "Localhost development simulator build succeeded");
        }

        private static void BuildLocalhost(
            string outputDirectory,
            BuildOptions buildOptions,
            string successMessage)
        {
            var scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                throw new BuildFailedException("No enabled scenes are configured in EditorBuildSettings.");
            }

            Directory.CreateDirectory(outputDirectory);
            var outputPath = Path.Combine(outputDirectory, OutputFileName);
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = BuildTarget.StandaloneWindows64,
                options = buildOptions
            };

            var report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new BuildFailedException(
                    "Localhost simulator build failed with result " + report.summary.result + ".");
            }

            Debug.Log(successMessage + ": " + outputPath);
        }
    }
}
