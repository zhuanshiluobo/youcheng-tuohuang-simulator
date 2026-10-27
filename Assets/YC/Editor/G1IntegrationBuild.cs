using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using YC.Application.Sessions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Editor
{
    /// <summary>独占调用的同源待验收构建。只消费已启用场景，不生成或保存界面资产。</summary>
    public static class G1IntegrationBuild
    {
        private static string SourceHash(string project, string relative)
        {
            if (string.IsNullOrEmpty(relative) || !relative.StartsWith("Assets/", StringComparison.Ordinal)) return null;
            string full = Path.GetFullPath(Path.Combine(project, relative));
            if (!full.StartsWith(Path.Combine(project, "Assets") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException("构建资产路径越界：" + relative);
            if (!File.Exists(full)) return null;
            using var stream = File.OpenRead(full); using var sha = System.Security.Cryptography.SHA256.Create();
            return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
        }
        private static string BinaryHash(string path)
        {
            using var stream = File.OpenRead(path); using var sha = System.Security.Cryptography.SHA256.Create();
            return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
        }
        public static void Build()
        {
            string Argument(string key)
            {
                var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key);
                if (i < 0 || i + 1 >= args.Length) throw new BuildFailedException("缺少构建参数：" + key);
                return args[i + 1];
            }
            string project = Path.GetFullPath(Directory.GetCurrentDirectory());
            string executable = Path.GetFullPath(Argument("-ycG1BuildOutput"));
            string directory = Path.GetDirectoryName(executable);
            string allowed = Path.Combine(project, "Builds") + Path.DirectorySeparatorChar;
            if (!Path.IsPathRooted(executable) || !directory.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(executable) != ".exe" || Directory.Exists(directory) || File.Exists(directory))
                throw new BuildFailedException("构建必须写入新的明确项目内Builds子目录：" + executable);
            string reportPath = Path.GetFullPath(Argument("-ycG1BuildEvidence"));
            if (!reportPath.StartsWith(project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || File.Exists(reportPath))
                throw new BuildFailedException("构建记录路径越界或已经存在。");
            var config = new GameRuleConfiguration { PlayerCount = 2, MapId = "map-three-players", RuleModeId = "entrepreneur",
                EnabledExpansionIds = new System.Collections.Generic.List<string> { "core", "enterprise", "infected" } };
            var catalog = ActiveContentCatalogBuilder.Build(ExternalContentPack.Current, config);
            var registry = new YC.Domain.Effects.EffectRegistry(); catalog.Register(registry);
            RuleModeReadiness.ValidateContent(catalog.ContentDefinitions, catalog.SetupRecipe);
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0 || scenes.Any(s => !File.Exists(s))) throw new BuildFailedException("启用场景不完整。");
            Directory.CreateDirectory(directory);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = scenes, locationPathName = executable,
                target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development | BuildOptions.DetailedBuildReport });
            var evidence = new { status = report.summary.result.ToString(), scope = "G1待正常验收；正常游戏0", executable,
                totalBytes = report.summary.totalSize, errors = report.summary.totalErrors, warnings = report.summary.totalWarnings,
                started = report.summary.buildStartedAt, ended = report.summary.buildEndedAt, scenes,
                GameProtocolVersions.Identity, host = HostSnapshotDto.CurrentSchemaVersion, journal = HostJournalDto.CurrentSchemaVersion,
                runtime = EffectRuntimeState.CurrentSchemaVersion, entity = GameProtocolVersions.Entity, room = GameProtocolVersions.Room,
                installedContentHash = ExternalContentPack.Current.ContentHash, activeContentHash = catalog.ContentPackHash,
                implementedCapabilities = RuleModeReadiness.ImplementedCapabilities,
                formalAssembly = new { entrepreneurs = catalog.ContentDefinitions.Entrepreneurs.Count, scripts = catalog.Scripts.Count,
                    abilityDeclarations = registry.AbilityDeclarations.Count },
                loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => new[] { "Assembly-CSharp", "YC.Domain", "YC.Application", "YC.Infrastructure.Lua", "YC.Presentation.Workflows" }.Contains(a.GetName().Name))
                    .Select(a => new { name = a.GetName().Name, path = a.Location, sha256 = BinaryHash(a.Location) }).ToArray(),
                unity = UnityEngine.Application.unityVersion,
                bundleVersion = PlayerSettings.bundleVersion,
                files = report.GetFiles().Select(f => new { f.path, f.role, f.size }).ToArray(),
                packedAssets = report.packedAssets.Select(p => new { p.shortPath,
                    contents = p.contents.Select(a => new { a.sourceAssetPath, a.packedSize,
                        sha256 = SourceHash(project, a.sourceAssetPath) }).ToArray() }).ToArray(),
                steps = report.steps.Select(s => new { s.name, durationSeconds = s.duration.TotalSeconds,
                    messages = s.messages.Select(m => new { type = m.type.ToString(), m.content }).ToArray() }).ToArray() };
            using (var stream = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream)) writer.Write(JsonConvert.SerializeObject(evidence, Formatting.Indented));
            if (report.summary.result != BuildResult.Succeeded) throw new BuildFailedException("G1构建失败，原始记录已保留。");
        }
    }
}
