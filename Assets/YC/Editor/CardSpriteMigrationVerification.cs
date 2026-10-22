using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using YC.Presentation;

namespace YC.Editor
{
    public static class CardSpriteMigrationVerification
    {
        public static void Play()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
            var registry = scene.GetRootGameObjects().SelectMany(o => o.GetComponentsInChildren<GameplayDialogRegistry>(true)).Single();
            var arg = Environment.GetCommandLineArgs().Single(a => a.StartsWith("--yc-card-sprite-check="));
            var output = arg.Substring(arg.IndexOf('=') + 1);
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "实际资产来源.json"), new JObject {
                ["scene"] = scene.path, ["sceneRegistry"] = GlobalObjectId.GetGlobalObjectIdSlow(registry).ToString(),
                ["viewer"] = AssetDatabase.GetAssetPath(registry.CardViewerPrefab),
                ["cardPicker"] = AssetDatabase.GetAssetPath(registry.CardPickerPrefab),
                ["eventChoice"] = AssetDatabase.GetAssetPath(registry.EventChoiceDialogPrefab),
                ["cityStyle"] = AssetDatabase.GetAssetPath(registry.CityStyleDeclarationPreviewPrefab),
                ["catalog"] = AssetDatabase.GetAssetPath(registry.CardVisualCatalog) }.ToString());
            // 使用项目已有的只读 GameView 设置，不生成或保存场景。
            typeof(YC.Presentation.Editor.GameplaySupplementalPageCapture).GetMethod("PrepareGameView", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, null);
            typeof(YC.Presentation.Editor.GameplaySupplementalPageCapture).GetMethod("SelectSize", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, new object[] { new Vector2Int(1920, 1080) });
            EditorApplication.EnterPlaymode();
        }

        public static void Build()
        {
            BuildTo("Builds/CardSpriteMigration", "prompt/卡牌Sprite切片替换/执行记录/构建报告.json");
        }

        public static void BuildFlowFix()
        {
            BuildTo("Builds/CardFlowFix", "Logs/CardFlowFix/build-report.json");
        }

        private static void BuildTo(string outputDirectory, string evidence)
        {
            var output = Path.GetFullPath(outputDirectory);
            if (Directory.Exists(output)) throw new BuildFailedException("验收构建目录已存在，请先核对旧证据，避免覆盖或复用旧打包结果。");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = Path.Combine(output, "tuohuang.exe"), target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development | BuildOptions.DetailedBuildReport });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(evidence)));
            File.WriteAllText(evidence, new JObject { ["result"] = report.summary.result.ToString(),
                ["output"] = output, ["totalBytes"] = report.summary.totalSize, ["errors"] = report.summary.totalErrors,
                ["warnings"] = report.summary.totalWarnings, ["duration"] = report.summary.totalTime.ToString(),
                ["packedSources"] = new JArray(report.packedAssets.SelectMany(a => a.contents).Select(c => c.sourceAssetPath).Distinct().OrderBy(s => s)) }.ToString());
            if (report.summary.result != BuildResult.Succeeded) throw new BuildFailedException("卡牌切片验收构建失败");
        }
    }
}
