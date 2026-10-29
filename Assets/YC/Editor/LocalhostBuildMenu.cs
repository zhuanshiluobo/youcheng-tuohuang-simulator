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
        private const string BatchReleaseBuildArgument = "-ycBuildRelease";

        [InitializeOnLoadMethod]
        private static void RunBatchBuildWhenRequested()
        {
            var arguments = Environment.GetCommandLineArgs();
            var buildDevelopment = arguments.Contains(BatchDevelopmentBuildArgument);
            var buildRelease = arguments.Contains(BatchReleaseBuildArgument);
            if (!buildDevelopment && !buildRelease && !arguments.Contains(BatchBuildArgument))
            {
                return;
            }

            EditorApplication.delayCall += () =>
            {
                try
                {
                    if (buildRelease)
                    {
                        BuildRelease();
                    }
                    else if (buildDevelopment)
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

        [MenuItem("YC/Build/Release")]
        public static void BuildRelease()
        {
            var version = PlayerSettings.bundleVersion;
            if (string.IsNullOrWhiteSpace(version) || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new BuildFailedException("PlayerSettings.bundleVersion 不能用于发行目录：" + version);
            }

            var outputDirectory = Path.Combine("Builds", "v" + version);
            var arguments = Environment.GetCommandLineArgs();
            var outputArgument = Array.IndexOf(arguments, "-ycReleaseOutput");
            if (outputArgument >= 0)
            {
                if (outputArgument + 1 >= arguments.Length)
                    throw new BuildFailedException("缺少 -ycReleaseOutput 路径。");
                outputDirectory = Path.GetFullPath(arguments[outputArgument + 1]);
                var allowed = Path.GetFullPath("Builds") + Path.DirectorySeparatorChar;
                if (!outputDirectory.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) ||
                    Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
                    throw new BuildFailedException("发行输出必须是项目 Builds 内尚不存在的子目录。");
            }

            BuildLocalhost(
                outputDirectory,
                BuildOptions.None,
                "Release build succeeded");
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
