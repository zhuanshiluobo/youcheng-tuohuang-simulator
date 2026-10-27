using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Application.Presentation;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
#if !QY007_R10_CLR
    public sealed class CompactCandidateTextTests
    {
        [NUnit.Framework.TestCase("paid_4")][NUnit.Framework.TestCase("paid_29")][NUnit.Framework.TestCase("paid_99")]
        [NUnit.Framework.TestCase("free_4")][NUnit.Framework.TestCase("free_29")][NUnit.Framework.TestCase("free_99")]
        [NUnit.Framework.TestCase("contracts")][NUnit.Framework.TestCase("validation")]
        [NUnit.Framework.TestCase("select_99")][NUnit.Framework.TestCase("decline_99")]
        [NUnit.Framework.TestCase("script_99")][NUnit.Framework.TestCase("merge_budget")]
        public void CompactLabelsKeepAuthorizedCandidateIdentityAndPresentation(string mode)
        {
            var artifacts = IsolatedTestArtifacts.BeginCase();
            try { artifacts.WriteLabeledText("紧凑整数候选标签隔离结果", JsonConvert.SerializeObject(CompactCandidateTextChecks.Run(mode))); }
            finally { artifacts.Finish(); }
        }
    }
#endif
    internal static class CompactCandidateTextChecks
    {
        private const string Paid = "core:fixture.integer.paid", Free = "core:fixture.integer.free", Explicit = "core:fixture.integer.explicit";
        private static void Need(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        private static void Reject(Action operation, string reason)
        { try { operation(); } catch (ArgumentException) { return; } catch (InvalidDataException) { return; } catch (TargetInvocationException e) when (e.InnerException is ArgumentException || e.InnerException is InvalidDataException) { return; } throw new InvalidOperationException("应拒绝：" + reason); }
        private static GameplayCandidateIntegerLabel Compact(bool paid) => new GameplayCandidateIntegerLabel { Key = paid ? Paid : Free,
            Arguments = (paid ? new[] { new GameplayCandidateIntegerArgument { Name = "fee", Multiplier = 2 }, new GameplayCandidateIntegerArgument { Name = "count", Multiplier = 1 } }
                : new[] { new GameplayCandidateIntegerArgument { Name = "count", Multiplier = 1 } }).ToList() };
        private static GameplayTextRef Label(GameplayInteractionPresentation presentation, IEnumerable<string> ids, string id) =>
            GameplayCandidateLabelResolver.Resolve(new InteractionRequestProjection { VisibleToViewer = true, Presentation = presentation, CandidateIds = ids.ToList() }, id);
        internal static object Run(string mode)
        {
            var trace = new List<object>();
            try
            {
                var catalog = Catalog(); var resolver = new GameplayTextResolver(catalog);
                if (mode.StartsWith("paid_", StringComparison.Ordinal) || mode.StartsWith("free_", StringComparison.Ordinal))
                {
                    bool paid = mode.StartsWith("paid_", StringComparison.Ordinal); int count = int.Parse(mode.Substring(5));
                    var invocation = Invoke(count, paid); Need(invocation.IsSuccess, invocation.Diagnostic);
                    var effect = invocation.Effects.Single(); var ids = Value(effect.RawArguments, "options").Items.Select(v => v.StringValue).ToArray();
                    Need(ids.SequenceEqual(Enumerable.Range(1, count).Reverse().Select(v => v.ToString(CultureInfo.InvariantCulture))), "候选ID/顺序/数量改变");
                    Need(Number(effect.RawArguments, "minSelections") == 0 && Number(effect.RawArguments, "maxSelections") == 1, "选择协议改变");
                    var presentation = effect.Presentation.Interaction; catalog.ValidateReferences(presentation);
                    var normalized = GameplayPresentationNormalizer.ToNormalized(presentation);
                    presentation = GameplayPresentationNormalizer.FromNormalized<GameplayInteractionPresentation>(normalized);
                    presentation = GameplayPresentationNormalizer.ProjectInteraction(presentation, true, ids);
                    Need(presentation.Options.Count == 0, "持久Options被展开");
                    string before = JsonConvert.SerializeObject(presentation);
                    foreach (string id in ids)
                    {
                        var label = Label(presentation, ids, id);
                        var document = resolver.Resolve(label, new GameplayTextContext { Catalog = catalog });
                        string expected = paid ? "支付" + (int.Parse(id) * 2) + "金券，获得" + id + "份" : "获得" + id + "份";
                        Need(document.ToPlainText() == expected, "标签错误：" + id + "：" + document.ToPlainText());
                        Need(document.Runs.OfType<IconRun>().Count() == (paid ? 1 : 0), "金券未沿IconRun转译");
                    }
                    Need(JsonConvert.SerializeObject(presentation) == before, "解析标签写回持久状态");
                    trace.Add(new { count, paid, ids, options = presentation.Options.Count, normalizedBytes = JsonConvert.SerializeObject(normalized).Length, templatesCompiled = resolver.CompiledTemplateCount });
                }
                else if (mode == "contracts") Contracts(trace);
                else if (mode == "validation") Validation(catalog, trace);
                else if (mode == "script_99") Script(trace);
                else if (mode == "merge_budget") Merge(trace);
                else Identity(mode == "select_99", trace);
                Need(NormalizedValue.MaxElements == 128 && LuaExecutionBudget.Default.MaxInstructions == 10000, "原预算被改变");
                return new { success = true, mode, scope = "无企业家模板的真实Lua/紧凑投影/通用转译/原生Choice与Host恢复隔离检查，非Unity或正常界面", trace };
            }
            catch (Exception error) { throw new InvalidOperationException(JsonConvert.SerializeObject(new { success = false, mode, error = error.ToString(), trace }), error); }
        }
        private static GameplayPresentationCatalog Catalog()
        {
            string root = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            var original = JObject.Parse(File.ReadAllText(Path.Combine(root, "presentation/icons.json")));
            var gold = original["icons"].Single(i => (string)i["id"] == "core:resource.gold_voucher").DeepClone();
            var messages = JObject.Parse(File.ReadAllText(Path.Combine(root, "presentation/texts.zh-CN.json")))["messages"];
            var text = new JObject { ["schemaVersion"] = 1, ["locale"] = "zh-CN", ["messages"] = new JObject {
                [Paid] = new JObject { ["parameters"] = new JObject { ["fee"] = "integer", ["count"] = "integer" }, ["template"] = "支付{fee}[icon:core:resource.gold_voucher]，获得{count}份" },
                [Free] = new JObject { ["parameters"] = new JObject { ["count"] = "integer" }, ["template"] = "获得{count}份" },
                [Explicit] = new JObject { ["parameters"] = new JObject(), ["template"] = "显式标签" },
                ["core:fixture.integer.text"] = new JObject { ["parameters"] = new JObject { ["count"] = "text" }, ["template"] = "内容{count}" },
                ["core:resource.gold_voucher.alt"] = messages["core:resource.gold_voucher.alt"].DeepClone()
            } };
            var icons = new JObject { ["schemaVersion"] = 1, ["icons"] = new JArray(gold) };
            var defaults = new JObject { ["schemaVersion"] = 1, ["effectTypes"] = new JObject(), ["interactions"] = new JObject(), ["outcomes"] = new JObject() };
            var declaration = new JObject { ["schemaVersion"] = 1, ["texts"] = new JArray("texts"), ["icons"] = "icons", ["defaults"] = "defaults" };
            Func<string, JObject> read = p => p == "texts" ? text : p == "icons" ? icons : defaults;
            return (GameplayPresentationCatalog)typeof(GameplayPresentationCatalog).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { declaration, "core", read, (Func<string, byte[]>)(p => File.ReadAllBytes(Path.Combine(root, p))) });
        }
        private static LuaInvocationResult Invoke(int count, bool paid)
        {
            string label = paid ? "{key='" + Paid + "',arguments={{name='fee',multiplier=2},{name='count',multiplier=1}}}" : "{key='" + Free + "',arguments={{name='count',multiplier=1}}}";
            string code = "return function(ctx) local ids={}; for i=ctx.payload.limit,1,-1 do ids[#ids+1]=tostring(i) end return {Effect.WithPresentation(Effect.Choice({player=ctx.playerId,options=ids,minSelections=0,maxSelections=1,interactionType='effect.choice.execute'}),{interaction={candidateIntegerLabel=" + label + "}})} end";
            var script = new LuaScriptDefinition("fixture.integer", "fixture.integer.choice", "fixture.integer.handler", "1.0.0", code, LuaContentHasher.ComputeSha256(code));
            var snapshot = LuaFacadeSnapshot.FromInvocation("p1", 0, 0, 2, "1.0.0");
            snapshot.EventPayload = LuaSnapshotValue.Object(new[] { new LuaSnapshotEntry("limit", LuaSnapshotValue.Integer(count)) });
            return new MoonSharpLuaRuntimeHost().Invoke(script, new LuaInvocationContext("fixture.integer.event", "Fixture", "p1", 0, "1.0.0", 2, 0, 0, snapshot));
        }
        private static void Contracts(List<object> trace)
        {
            var p = new GameplayInteractionPresentation { CandidateIntegerLabel = Compact(true), Options = new List<GameplayOptionPresentation> { new GameplayOptionPresentation { CandidateId = "go", Label = new GameplayTextRef(Explicit) } } };
            var ids = new[] { "2", "go", "-2", "02", "1000000000", "bad", "1.5", " 2", "1000000001" };
            Need(Label(p, ids, "go").Key == Explicit, "显式标签未优先");
            Need(Label(p, ids, "3") == null, "未授权ID泄漏标签");
            Need(Label(GameplayPresentationNormalizer.ProjectInteraction(p, false, ids), ids, "2") == null, "隐藏请求泄漏");
            Need(GameplayCandidateLabelResolver.Resolve(new InteractionRequestProjection { VisibleToViewer = false, Presentation = p, CandidateIds = ids.ToList() }, "2") == null, "不可见请求泄漏标签");
            foreach (string id in new[] { "1000000000", "bad", "1.5", " 2", "1000000001" }) Reject(() => Label(p, ids, id), id);
            Need(Label(p, ids, "02").Arguments.Single(a => a.Name == "count").Value.IntegerValue == 2, "字符串ID读取错误");
            Need(Label(p, ids, "-2").Arguments.Single(a => a.Name == "fee").Value.IntegerValue == -4, "有符号整数错误");
            p.CandidateIntegerLabel.Arguments[0].Multiplier = NormalizedValue.MaxInteger + 1;
            Reject(() => Label(p, ids, "2"), "乘数范围"); trace.Add("优先级、查看权限、非法ID、整数范围和乘法范围均符合合同");
        }
        private static void Validation(GameplayPresentationCatalog catalog, List<object> trace)
        {
            var label = Compact(true); catalog.ValidateReferences(label);
            label.Arguments.Add(new GameplayCandidateIntegerArgument { Name = "fee" }); Reject(() => GameplayPresentationNormalizer.ToNormalized(label), "重复参数");
            label = Compact(false); label.Key = "core:fixture.missing"; Reject(() => catalog.ValidateReferences(label), "未登记模板");
            label.Key = Paid; Reject(() => catalog.ValidateReferences(label), "模板参数签名");
            label.Key = "core:fixture.integer.text"; Reject(() => catalog.ValidateReferences(label), "模板参数种类");
            var n = GameplayPresentationNormalizer.ToNormalized(Compact(false)); n.Properties.Add(new NormalizedValueEntry { Name = "unknown", Value = NormalizedValue.CreateInteger(1) });
            Reject(() => GameplayPresentationNormalizer.FromNormalized<GameplayCandidateIntegerLabel>(n), "未知字段");
            n = GameplayPresentationNormalizer.ToNormalized(Compact(false)); Value(n, "arguments").Items[0].Properties.Single(p => p.Name == "multiplier").Value = NormalizedValue.CreateString("2");
            Reject(() => GameplayPresentationNormalizer.FromNormalized<GameplayCandidateIntegerLabel>(n), "参数类型");
            foreach (string malformed in new[] { "unknown=1,", "" })
            {
                string code = "function Present(context) return {interaction={candidateIntegerLabel={" + malformed + "key='" + Free + "',arguments={{name='count',multiplier=" + (malformed.Length == 0 ? "1.5" : "1") + "}}}}} end";
                var script = new LuaScriptDefinition("fixture.invalid", "fixture.invalid", "fixture.invalid", "1.0.0", code, LuaContentHasher.ComputeSha256(code));
                Need(!new MoonSharpLuaRuntimeHost().TryPresent(script, new GameplayPresentationContext { Purpose = "interaction", StateRevision = 0, Facts = Object() }, out _, out _, out _), "Lua封闭字段/非整数未拒绝");
            }
            trace.Add("封闭DTO、Lua入口与既有模板签名检查通过");
        }
        private static void Script(List<object> trace)
        {
            string code = "function Present(context) return {interaction={candidateIntegerLabel={key='" + Paid + "',arguments={{name='fee',multiplier=2},{name='count',multiplier=1}}}}} end";
            var script = new LuaScriptDefinition("fixture.presentation", "fixture.integer.choice", "fixture.present", "1.0.0", code, LuaContentHasher.ComputeSha256(code));
            var facts = Object("candidateIds", Strings(Enumerable.Range(1, 99).Select(i => i.ToString()).ToArray()));
            Need(new MoonSharpLuaRuntimeHost().TryPresent(script, new GameplayPresentationContext { Purpose = "interaction", StateRevision = 1, Facts = facts }, out var result, out _, out string why), why);
            Need(result.Interaction.Options.Count == 0 && result.Interaction.CandidateIntegerLabel.Arguments.Count == 2, "显示脚本展开候选"); trace.Add("含99候选上下文的显示脚本紧凑返回通过");
        }
        private static void Merge(List<object> trace)
        {
            var p = new GameplayInteractionPresentation { CandidateIntegerLabel = Compact(true) };
            var method = typeof(EffectTreeExecutor).GetMethod("MergeInteractionPresentation", BindingFlags.Static | BindingFlags.NonPublic);
            var merged = (GameplayInteractionPresentation)method.Invoke(null, new object[] { new GameplayInteractionPresentation { Prompt = new GameplayTextRef(Explicit) }, p });
            Need(merged.CandidateIntegerLabel.Key == Paid && merged.Prompt.Key == Explicit, "内核显示合并未透传");
            var expanded = new GameplayInteractionPresentation { Options = Enumerable.Range(1, 29).Select(i => new GameplayOptionPresentation { CandidateId = i.ToString(), Label = new GameplayTextRef(Paid) { Arguments = new List<GameplayTextArgument> {
                new GameplayTextArgument { Name = "fee", Kind = "integer", Value = NormalizedValue.CreateInteger(i * 2) }, new GameplayTextArgument { Name = "count", Kind = "integer", Value = NormalizedValue.CreateInteger(i) } } } }).ToList() };
            Reject(() => GameplayPresentationNormalizer.ToNormalized(expanded), "原128元素预算"); trace.Add("只新增显示透传，原逐项展开预算拒绝保留");
        }
        private static void Identity(bool select, List<object> trace)
        {
            var invocation = Invoke(99, true); Need(invocation.IsSuccess, invocation.Diagnostic);
            var compiler = new LuaEffectSpecCompiler(); Need(compiler.TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1, ContentInstanceId = "fixture.integer", AbilityId = "fixture.integer.choice" }, out var specs, out var fault), fault?.Diagnostic);
            var registry = new EffectRegistry { ContentPackHash = "fixture.integer.content" }; GenericLuaEffectExecutor.Register(registry); ResourceEffectExecutor.Register(registry);
            var map = StaticMapDefinitions.CreateThreePlayerMap();
            var initial = new GameState { GameId = "fixture.integer." + select, MapId = map.MapId, ContentPackHash = registry.ContentPackHash, ContentDefinitions = new GameContentDefinitions(),
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = registry.ContentPackHash }, Players = new List<PlayerState> { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } },
                Round = 1, ActionRound = 1, Phase = GamePhase.ActionRound1, CurrentPlayerId = 1, StartPlayerId = 1 };
            var state = new ItemZoneStateFactory().CreateForSetup(initial, map, Array.Empty<ItemRecipeEntry>()); state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            registry.Register("fixture.integer.sequence", c => {
                if (c.Node.FlowStage == "") return EffectStepResult.Continue("chosen").AddChild(specs.Single());
                if (c.Node.FlowStage == "chosen")
                {
                    var value = c.ChildNodes.Single().NormalizedResult;
                    return value?.Kind == NormalizedValueKind.String ? EffectStepResult.Continue("reward").AddChild(ResourceEffectSpecFactory.GainScore(1, int.Parse(value.StringValue))) : EffectStepResult.Completed();
                }
                return EffectStepResult.Completed();
            }, EffectExecutorKind.IntrinsicFlow);
            var engine = new EffectTreeExecutor(state, registry); string owner = engine.CreateRoot(EffectSpec.Create("fixture.integer.sequence", playerId: 1)); Need(!engine.RunUntilQuiescent().Faulted, state.EffectRuntime.LastFaultMessage);
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open"); string interaction = request.GetStableInteractionId();
            Need(request.CandidateIds.Count == 99 && request.MinSelections == 0 && request.MaxSelections == 1 && request.Presentation.CandidateIntegerLabel.Key == Paid && request.Presentation.Options.Count == 0, "持久请求未保持紧凑描述/选择协议");
            Need(GameplayCandidateLabelResolver.Resolve(InteractionRequestProjector.ProjectForPlayer(request, 1), "99") != null &&
                GameplayCandidateLabelResolver.Resolve(InteractionRequestProjector.ProjectForPlayer(request, 2), "99") == null, "查看者投影越权");
            var restored = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry); Need(restored.Succeeded, restored.Diagnostic); state = restored.State;
            request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open"); Need(request.GetStableInteractionId() == interaction && request.CandidateIds.First() == "99", "恢复改变选择身份");
            engine = new EffectTreeExecutor(state, registry); Need(engine.TrySubmitInteraction(interaction, 1, request.StateRevision, select ? NormalizedValue.CreateString("99") : NormalizedValue.CreateArray(new List<NormalizedValue>()), out string why), why);
            Need(!engine.RunUntilQuiescent().Faulted, state.EffectRuntime.LastFaultMessage);
            Need(state.FindPlayer(1).Score == (select ? 99 : 0) && engine.GetNode(owner).Status == EffectNodeStatus.Completed, "选择/放弃后继改变");
            Need(state.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == interaction).Presentation.Options.Count == 0, "选择后持久状态展开标签");
            trace.Add(new { select, interaction, candidates = request.CandidateIds.ToArray(), min = request.MinSelections, max = request.MaxSelections, score = state.FindPlayer(1).Score });
        }
    }
}
