#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Editor
{
    // 只读原文件的隔离诊断；可恢复和执行私有副本，不操作原程序、不写原档、不作为正常验收。
    public static class EffectExecutionBindingDiagnostic
    {
        public static void Run()
        {
            string Arg(string key) { var a = Environment.GetCommandLineArgs(); int i = Array.IndexOf(a, key); if (i < 0 || i + 1 >= a.Length) throw new ArgumentException(key); return a[i + 1]; }
            var envelope = JObject.Parse(File.ReadAllText(Arg("-ycBindingArchive")));
            var payload = JObject.Parse(envelope.Value<string>("Payload"));
            var archive = JObject.Parse(payload.Value<string>("ArchiveJson"));
            var settings = (JsonSerializerSettings)typeof(HostRecoveryService).GetField("ArchiveSettings", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var state = archive["Snapshot"]["State"].ToObject<GameState>(JsonSerializer.Create(settings));
            var catalog = ActiveContentCatalogBuilder.Build(ExternalContentPack.Load(Arg("-ycBindingContent")), state.RuleConfiguration);
            var registry = new EffectRegistry { ContentPackHash = catalog.ContentPackHash }; ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry); catalog.Register(registry);
            InfluenceEffectExecutor.Register(registry, state, new YC.Domain.Maps.MapQueryService(YC.Domain.Maps.StaticMapDefinitions.Resolve(state.MapId)));
            var rounds = new YC.Domain.Rules.RoundExecutionService(registry);
            var rows = new List<object>();
            object Detail(EffectExecutionBinding b)
            {
                var choice = state.EffectRuntime.InteractionRequests.SingleOrDefault(r => r.GetStableInteractionId() == b.ChoiceInteractionId);
                var build = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == b.ParentBuildId);
                var owner = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == choice?.OwnerEffectId);
                bool Match(bool historical) => (bool)typeof(EffectExecutionBinding).GetMethod("ChoiceSourceMatches", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { state, registry, choice?.OwnerEffectId ?? "", b.ParentBuildId, b.SourceRef, b.VersionRef, historical });
                return new { b, choice, BuildPlayer = build?.PlayerId, OwnerPlayer = owner?.PlayerId,
                    Within = choice != null && EffectExecutionBinding.Within(state, choice.OwnerEffectId, b.ParentBuildId),
                    SourceCurrent = registry.IsCurrentFacilityExecutionSource(state, b.SourceRef), MatchLive = choice != null && Match(false), MatchHistorical = choice != null && Match(true) };
            }
            foreach (var n in state.EffectRuntime.EffectNodes.Where(n => n.ExecutionBinding != null))
            {
                try { EffectExecutionBinding.ValidateNode(state, registry, n, n.ExecutionBinding); }
                catch (Exception error) { rows.Add(new { Phase = "原快照既有绑定", n.EffectId, n.EffectTypeId, Error = error.ToString(), Detail = Detail(n.ExecutionBinding) }); }
            }
            var maps = state.EffectRuntime.InfluencePlaceMappings;
            foreach (var map in maps.ToList())
            {
                state.EffectRuntime.InfluencePlaceMappings = new List<InfluencePlaceMapping> { map };
                bool valid = InfluencePlaceAttachment.Validate(state, registry, out string mappingReason);
                var rootNode = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == map.ActualRootEffectId);
                string specError = "";
                try {
                    EffectSpec FromSpec(EffectSpecRuntimeState spec) => (EffectSpec)typeof(EffectRegistry).GetMethod("FromRuntimeSpec", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { spec });
                    var originalSpec = FromSpec(map.OriginalSpec);
                    registry.TryGet(originalSpec.EffectTypeId, out var reg); typeof(EffectRegistration).GetMethod("Validate", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(reg, new object[] { originalSpec });
                    var runtime = new EffectSpecRuntimeState { EffectTypeId = rootNode.EffectTypeId, NormalizedArguments = rootNode.NormalizedArguments,
                        PlayerId = rootNode.PlayerId, AllowDecline = rootNode.AllowDecline, DecisionPlayerId = rootNode.DecisionPlayerId,
                        NestedEffects = rootNode.NestedEffects, NestedGroupSizes = rootNode.NestedGroupSizes };
                    typeof(EffectTreeExecutor).GetMethod("ValidatePlaceReplacement", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { originalSpec, FromSpec(runtime) });
                } catch (Exception e) { specError = e.ToString(); }
                rows.Add(new { Phase = "逐映射只读谓词", map.ActualRootEffectId, Valid = valid, mappingReason, specError,
                    Sources = map.Sources.Select(src => new { src.AbilityId, Key = src.SourceKey == StableIdFactory.Create("ability-source", src.ItemId, src.AbilityId, src.ActiveFaceId, src.DeclarationVersion, src.AuthorizedPlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        Declared = registry.AbilityDeclarations.SingleOrDefault(d => d.AbilityId == src.AbilityId && d.DefinitionId == src.DefinitionId),
                        Hash = typeof(EffectRegistry).GetMethod("HandlerHash", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(registry, new object[] { registry.AbilityDeclarations.Single(d => d.AbilityId == src.AbilityId && d.DefinitionId == src.DefinitionId).HandlerRef }) }), map.HandlerHashes });
            }
            state.EffectRuntime.InfluencePlaceMappings = maps;
            string id = Arg("-ycBindingChoice"); int target = int.Parse(Arg("-ycBindingTarget"));
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == id);
            var node = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == request.OwnerEffectId);
            var saved = node.ContinuationContext;
            string buildId = YC.Domain.Effects.ItemTransferEffectExecutor.Text(saved, "buildId");
            // 只在私有对象补齐原已受理命令的回答事实，用于定位Create谓词；不执行该回答。
            request.Status = "answered"; request.NormalizedAnswer = NormalizedValue.CreateString("p" + target);
            request.AnsweredAtRevision = ++state.EffectRuntime.StateRevision;
            if (!registry.TryResolveAbilityExecutionSource(state, node.EffectId, node.ContinuationAbilityId, out var source, out var version)) throw new InvalidOperationException("诊断未解析真实原来源");
            var proposed = new EffectExecutionBinding { PlayerId = target, ParentBuildId = buildId, ChoiceInteractionId = id,
                AuthorizedAtRevision = state.EffectRuntime.StateRevision, SourceRef = source, VersionRef = version };
            try { var result = EffectExecutionBinding.Create(state, registry, buildId, source, id, target, version); rows.Add(new { Phase = "原受理答案的静态绑定控制", Passed = true, Binding = result }); }
            catch (Exception error) { rows.Add(new { Phase = "原受理答案的静态绑定控制", Passed = false, Error = error.ToString(), Detail = Detail(proposed) }); }
            var recovered = HostRecoveryService.RecoverJson(payload.Value<string>("ArchiveJson"), registry);
            if (!recovered.Succeeded) rows.Add(new { Phase = "同一旧包的隔离恢复控制", Passed = false, recovered.Diagnostic });
            else
            {
                state = recovered.State;
                var engine = new EffectTreeExecutor(state, registry);
                var original = state.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == id);
                bool accepted = engine.TrySubmitInteraction(id, original.AnsweringPlayerId, original.StateRevision,
                    NormalizedValue.CreateArray(new List<NormalizedValue> { NormalizedValue.CreateString("p" + target) }), out string reason);
                var result = engine.RunUntilQuiescent();
                rows.Add(new { Phase = "原命令的隔离执行", Accepted = accepted, reason, result.Faulted,
                    state.EffectRuntime.LastFaultCode, state.EffectRuntime.LastFaultMessage });
                if (registry.TryResolveAbilityExecutionSource(state, node.EffectId, node.ContinuationAbilityId, out source, out version))
                {
                    proposed = new EffectExecutionBinding { PlayerId = target, ParentBuildId = buildId, ChoiceInteractionId = id,
                        AuthorizedAtRevision = state.EffectRuntime.StateRevision, SourceRef = source, VersionRef = version };
                    rows.Add(new { Phase = "隔离执行后谓词", Detail = Detail(proposed) });
                }
                File.WriteAllText(Arg("-ycBindingOutput") + ".state.json", JsonConvert.SerializeObject(state));
            }
            File.WriteAllText(Arg("-ycBindingOutput"), JsonConvert.SerializeObject(new { Scope = "只读旧包和原档；仅恢复／执行私有副本用于定位，不恢复／推进原程序、不计正常通过", state.GameId,
                state.ContentPackHash, RegistryHash = registry.ContentPackHash, Rows = rows }, Formatting.Indented));
        }
    }
}
#endif
