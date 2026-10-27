#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using YC.Application.Sessions;
using YC.Infrastructure.Persistence;
using YC.Domain.Presentation;

namespace YC.Presentation.Validation
{
    public sealed partial class HotseatUiValidationDriver
    {
        private sealed class R21IsolatedIconCatalog : IGameplayTextCatalog, IGameplayIconResourceCatalog
        {
            private readonly IGameplayTextCatalog original;
            private readonly GameplayIconResource resource;
            public R21IsolatedIconCatalog(IGameplayTextCatalog original, GameplayIconResource resource) { this.original = original; this.resource = resource; }
            public string ContentHash => original.ContentHash;
            public string Locale => original.Locale;
            public bool TryGetTemplate(string key, out GameplayTextTemplateDefinition definition) => original.TryGetTemplate(key, out definition);
            public bool TryGetIcon(string key, out GameplayIconDefinition definition) => original.TryGetIcon(key, out definition);
            public IEnumerable<GameplayIconResource> IconResources { get { yield return resource; } }
            public bool TryGetIconResource(string key, out GameplayIconResource result) { result = key == resource.AssetKey ? resource : null; return result != null; }
        }
        // 本轮显式Editor隔离验证；不启动对局、不写场景或预制体。
        public static void VerifyR21Integration()
        {
            string Arg(string key) => Environment.GetCommandLineArgs().FirstOrDefault(v => v.StartsWith(key, StringComparison.Ordinal))?.Substring(key.Length);
            string destination = Arg("--qy004-r21-output="), sourceSave = Arg("--qy004-r21-source-save=");
            Require(Directory.Exists(destination) && Directory.Exists(sourceSave), "R21隔离输入输出缺失");
            var checks = new List<object>();
            void Check(string name, Action action)
            {
                File.AppendAllText(Path.Combine(destination, "原生检查进度.jsonl"), JsonConvert.SerializeObject(new { Name = name, Begin = true }) + "\n");
                action(); checks.Add(new { Name = name, Passed = true });
                File.AppendAllText(Path.Combine(destination, "原生检查进度.jsonl"), JsonConvert.SerializeObject(new { Name = name, Passed = true }) + "\n");
            }
            Check("R20原生阶段推进正反条件", () => {
                var owner = new object(); var before = new[] { "source:a" }; var after = new[] { "target:b" };
                Require(MapDraftProgressed(true, owner, owner, "source", "target", before, after), "合法阶段推进未识别");
                Require(!MapDraftProgressed(false, owner, owner, "source", "target", before, after) &&
                    !MapDraftProgressed(true, owner, new object(), "source", "target", before, after) &&
                    !MapDraftProgressed(true, owner, owner, "source", "source", before, after) &&
                    !MapDraftProgressed(true, owner, owner, "source", "target", before, before) &&
                    !MapDraftProgressed(true, owner, owner, "", "target", before, after) &&
                    !MapDraftProgressed(true, owner, owner, "source", "target", Array.Empty<string>(), after), "无真实进展被放行");
                Require(!MapDraftProgressed(true, owner, owner, "source", "target", new[] { "a", "b" }, new[] { "b", "a", "a" }), "仅重排重复被放行");
            });
            Check("两缓存默认关闭及纯值快照", () => {
                Require(!ExternalContentRuntime.ReadMemoryDiagnostics().Enabled && !GameplayIconResourceResolver.ReadMemoryDiagnostics().Enabled, "默认计量开启");
                foreach (var type in new[] { typeof(ArtworkMemorySnapshot), typeof(MatchSaveSizeStatistics) })
                    Require(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).All(f => f.FieldType.IsValueType), "快照持有引用");
                var fields = typeof(ArtworkMemoryCounters).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
                Require(fields.All(f => f.FieldType == typeof(ArtworkMemorySnapshot) || f.FieldType == typeof(Dictionary<string, ArtworkTextureMemorySample>)), "计数器新增强资源引用");
            });
            Check("两缓存重复启用及Reset代际", () => {
                ExternalContentRuntime.SetMemoryDiagnosticsEnabled(true); GameplayIconResourceResolver.SetMemoryDiagnosticsEnabled(true);
                var a = ExternalContentRuntime.ReadMemoryDiagnostics(); var i = GameplayIconResourceResolver.ReadMemoryDiagnostics();
                ExternalContentRuntime.SetMemoryDiagnosticsEnabled(true); GameplayIconResourceResolver.SetMemoryDiagnosticsEnabled(true);
                Require(a.ObservationEpoch == ExternalContentRuntime.ReadMemoryDiagnostics().ObservationEpoch &&
                    i.ObservationEpoch == GameplayIconResourceResolver.ReadMemoryDiagnostics().ObservationEpoch, "重复开启改变代际");
                typeof(ExternalContentRuntime).GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
                Require(ExternalContentRuntime.ReadMemoryDiagnostics().ClearGeneration == a.ClearGeneration + 1 &&
                    GameplayIconResourceResolver.ReadMemoryDiagnostics().ClearGeneration == i.ClearGeneration + 1, "清空未推进两代际");
            });
            Check("真实已安装卡图冷加载命中重建与释放请求", () => {
                var pack = ExternalContentRuntime.Pack;
                string path = pack.Definitions.First(d => d.Enabled && !string.IsNullOrEmpty(d.Artwork)).Artwork;
                var load = typeof(ExternalContentRuntime).GetMethod("LoadTexture", BindingFlags.NonPublic | BindingFlags.Static);
                var before = ExternalContentRuntime.ReadMemoryDiagnostics();
                var texture = (Texture2D)load.Invoke(null, new object[] { path });
                var first = ExternalContentRuntime.ReadMemoryDiagnostics();
                Require(first.CacheDecodeSuccesses == before.CacheDecodeSuccesses + 1 && first.CachedTextureEntries == before.CachedTextureEntries + 1 && first.LastSizeStatus != 0, "冷加载未计量");
                Require(ReferenceEquals(texture, load.Invoke(null, new object[] { path })) && ExternalContentRuntime.ReadMemoryDiagnostics().CacheDecodeSuccesses == first.CacheDecodeSuccesses, "缓存命中重新计数");
                UnityEngine.Object.DestroyImmediate(texture); // 仅隔离Editor资源，测试原有失效键重建分支。
                load.Invoke(null, new object[] { path });
                var rebuilt = ExternalContentRuntime.ReadMemoryDiagnostics();
                Require(rebuilt.CacheTextureRebuilds == first.CacheTextureRebuilds + 1, "原失效键未记重建");
                File.WriteAllText(Path.Combine(destination, "卡图原生能力.json"), JsonConvert.SerializeObject(rebuilt, Formatting.Indented));
            });
            Check("同语义已导入图标的隔离资源入口解码命中Validate分离及Clear", () => {
                var original = ExternalContentRuntime.Pack.Presentation;
                var theme = UnityEditor.AssetDatabase.LoadAssetAtPath<GameplayIconCatalog>("Assets/YC/Presentation/Text/Content/GameplayIconCatalog.asset");
                var definition = original.Icons.First(d => theme.TryResolve(d, GameplayIconTone.Dark, out _));
                Require(theme.TryResolve(definition, GameplayIconTone.Dark, out var sourceSprite), "现有主题图标不存在");
                string sourcePath = UnityEditor.AssetDatabase.GetAssetPath(sourceSprite);
                var resource = new GameplayIconResource("core", GameplayIconCatalog.AssetKeyFor(definition, GameplayIconTone.Dark), sourcePath, File.ReadAllBytes(sourcePath));
                var catalog = new R21IsolatedIconCatalog(original, resource);
                File.WriteAllText(Path.Combine(destination, "图标隔离输入身份.json"), JsonConvert.SerializeObject(new { sourcePath, resource.Sha256, resource.AssetKey,
                    OriginalThemeSource = true, IsolatedResourceCatalog = true, NormalPackageResourceCoverageClaimed = false }, Formatting.Indented));
                GameplayIconResourceResolver.UseCatalog(catalog);
                var before = GameplayIconResourceResolver.ReadMemoryDiagnostics();
                Require(GameplayIconResourceResolver.TryResolve(catalog, null, definition, GameplayIconTone.Dark, out var sprite), "真实图标未解析");
                var first = GameplayIconResourceResolver.ReadMemoryDiagnostics();
                Require(first.CacheDecodeSuccesses == before.CacheDecodeSuccesses + 1 && first.SpriteCreateSuccesses == before.SpriteCreateSuccesses + 1 && first.LastSizeStatus != 0, "图标成功未计数");
                Require(GameplayIconResourceResolver.TryResolve(catalog, null, definition, GameplayIconTone.Dark, out var same) && ReferenceEquals(sprite, same) && GameplayIconResourceResolver.ReadMemoryDiagnostics().CacheDecodeSuccesses == first.CacheDecodeSuccesses, "图标命中重新解码");
                GameplayIconResourceResolver.Validate(catalog);
                var validated = GameplayIconResourceResolver.ReadMemoryDiagnostics();
                Require(validated.ValidationDecodeSuccesses > first.ValidationDecodeSuccesses && validated.CacheDecodeSuccesses == first.CacheDecodeSuccesses && validated.CachedTextureEntries == first.CachedTextureEntries, "Validate混入缓存");
                GameplayIconResourceResolver.Clear();
                var cleared = GameplayIconResourceResolver.ReadMemoryDiagnostics();
                Require(cleared.CachedTextureEntries == 0 && cleared.KnownTextureBytes == 0 && cleared.TextureReleaseRequests > validated.TextureReleaseRequests && cleared.ClearGeneration == validated.ClearGeneration + 1, "Clear计数不符");
                File.WriteAllText(Path.Combine(destination, "图标原生能力.json"), JsonConvert.SerializeObject(new { First = first, Validated = validated, Cleared = cleared }, Formatting.Indented));
            });
            Check("禁用保持累计重开新代际与未知基线", () => {
                var before = ExternalContentRuntime.ReadMemoryDiagnostics();
                ExternalContentRuntime.SetMemoryDiagnosticsEnabled(false); GameplayIconResourceResolver.SetMemoryDiagnosticsEnabled(false);
                var disabled = ExternalContentRuntime.ReadMemoryDiagnostics();
                Require(!disabled.CurrentValuesAvailable && disabled.CacheDecodeSuccesses == before.CacheDecodeSuccesses, "禁用错误清累计");
                ExternalContentRuntime.SetMemoryDiagnosticsEnabled(true);
                var enabled = ExternalContentRuntime.ReadMemoryDiagnostics();
                Require(enabled.ObservationEpoch == before.ObservationEpoch + 1 && enabled.UnknownTextureEntries == enabled.CachedTextureEntries && enabled.KnownTextureBytes == 0, "重开基线错误");
                File.WriteAllText(Path.Combine(destination, "原生Profiler能力与getter耗时.json"), JsonConvert.SerializeObject(PerformanceMemoryAttribution(), Formatting.Indented));
                ExternalContentRuntime.SetMemoryDiagnosticsEnabled(false); GameplayIconResourceResolver.SetMemoryDiagnosticsEnabled(false);
            });
            var data = new MatchSaveStore(sourceSave).Read(3); // R18真实档只用于隔离保存入口；不是新局。
            Check("保存默认关闭及SaveAsync成功序号与实际字节", () => {
                var disabled = new MatchSaveStore(Path.Combine(destination, "隔离保存关闭"));
                disabled.SaveAsync(data).GetAwaiter().GetResult();
                var off = disabled.ReadSizeStatistics(); Require(!off.Enabled && off.LastEnqueuedSequence == 0 && off.LastCommittedFileBytes == null, "关闭仍发布规模");
                var store = new MatchSaveStore(Path.Combine(destination, "隔离保存同步"), true);
                store.SaveAsync(data).GetAwaiter().GetResult();
                var s = store.ReadSizeStatistics();
                Require(s.LastPreparedSequence == 1 && s.LastCommittedSequence == 1 && s.LastFinishedSequence == 1 && s.LastFinishedPhase == MatchSaveSizePhase.Completed &&
                    s.LastPreparedArchiveChars == data.ArchiveJson.Length && s.LastPreparedContinuationChars == data.ContinuationJson.Length &&
                    s.LastCommittedFileBytes == new FileInfo(Path.Combine(destination, "隔离保存同步", "auto-1.json")).Length, "同步保存规模或序号不符");
                File.WriteAllText(Path.Combine(destination, "同步保存规模.json"), JsonConvert.SerializeObject(s, Formatting.Indented));
            });
            Check("异步两笔入队及准备失败取消不串号", () => {
                var store = new MatchSaveStore(Path.Combine(destination, "隔离保存队列"), true);
                var gate = new TaskCompletionSource<MatchSaveData>();
                var first = store.SavePreparedWithValidationAsync(_ => gate.Task, data.GameId, data.ContentHash);
                var second = store.SavePreparedWithValidationAsync(_ => Task.FromException<MatchSaveData>(new InvalidOperationException("R21隔离准备失败")), data.GameId, data.ContentHash);
                Require(store.ReadSizeStatistics().LastEnqueuedSequence == 2, "未形成两笔队列");
                gate.SetResult(data); first.GetAwaiter().GetResult();
                try { second.GetAwaiter().GetResult(); throw new Exception("失败未传播"); } catch (InvalidOperationException) { }
                var failed = store.ReadSizeStatistics();
                Require(failed.LastPreparedSequence == 1 && failed.LastCommittedSequence == 1 && failed.LastFinishedSequence == 2 && failed.LastFinishedPhase == MatchSaveSizePhase.Failed, "失败串号");
                using var cancellation = new CancellationTokenSource();
                var third = store.SavePreparedWithValidationAsync(_ => { cancellation.Cancel(); return Task.FromResult(data); }, data.GameId, data.ContentHash, cancellation: cancellation.Token);
                try { third.GetAwaiter().GetResult(); throw new Exception("取消未传播"); } catch (OperationCanceledException) { }
                var cancelled = store.ReadSizeStatistics();
                Require(cancelled.LastPreparedSequence == 3 && cancelled.LastCommittedSequence == 1 && cancelled.LastFinishedSequence == 3 && cancelled.LastFinishedPhase == MatchSaveSizePhase.Cancelled, "准备后取消串号");
                store.SavePreparedWithValidationAsync(_ => Task.FromResult(data), data.GameId, data.ContentHash).GetAwaiter().GetResult();
                var completed = store.ReadSizeStatistics();
                Require(completed.LastPreparedSequence == 4 && completed.LastCommittedSequence == 4 && completed.LastFinishedSequence == 4 && completed.LastFinishedPhase == MatchSaveSizePhase.Completed, "后继成功串号");
                File.WriteAllText(Path.Combine(destination, "异步保存各阶段规模.json"), JsonConvert.SerializeObject(new { Failed = failed, Cancelled = cancelled, Completed = completed }, Formatting.Indented));
            });
            File.WriteAllText(Path.Combine(destination, "R21原生有限核验.json"), JsonConvert.SerializeObject(new {
                Passed = true, Checks = checks, MainMvid = typeof(HotseatUiValidationDriver).Assembly.ManifestModule.ModuleVersionId,
                Unity = UnityEngine.Application.unityVersion, NormalGameplayClaimed = false,
                Scope = "本轮新增合同的Editor隔离验证；正常地图多阶段与内存趋势须由新Player自然局覆盖"
            }, Formatting.Indented));
        }
    }
}
#endif
