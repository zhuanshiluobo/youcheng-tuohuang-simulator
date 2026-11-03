using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ItemTransferEffectTests
    {
        [TestCase("return {Effect.SelectInfluence({player=ctx.playerId,operation='remove',subjectFilter='self',count=1})}")]
        [TestCase("return {Effect.SelectInfluence({player=ctx.playerId,operation='move',subjectFilter='self',count=2})}")]
        [TestCase("return {Effect.SelectInfluence({player=ctx.playerId,operation='replace',subjectFilter='opponent',count=1})}")]
        [TestCase("local old=Effect.SelectInfluence; return {old({player=ctx.playerId,operation='remove',count=1})}")]
        [TestCase("return {Effect.Condition({leftEffects={{kind='effect_spec',effectTypeId='effect.influence.select_action',player=ctx.playerId,operation='remove',count=1}},rightEffects={}})}")]
        [TestCase("return {{kind='effect_spec',effectTypeId='effect.influence.select_action',player=ctx.playerId,operation='remove',count=1}}")]
        public void RetiredSelectorCannotBeConstructedThroughLuaOrAliases(string body)
        {
            var result=new MoonSharpLuaRuntimeHost().Invoke(ShuffleScript("return function(ctx) "+body+" end"),new LuaInvocationContext("selector.retirement","test","p1",0,"1.0.0",2,0,0));
            Assert.That(result.IsSuccess,Is.False);
            if(body.Contains("effect.influence.select_action"))Assert.That(result.Diagnostic,Does.Contain(ContentPrimitiveEffectExecutor.RetiredInfluenceSelectionReason));
            Assert.That(MoonSharpLuaRuntimeHost.SupportedEffectConstructors,Does.Not.Contain("SelectInfluence"));
            Assert.That(new LuaEffectTypeCatalog().TryGet(ContentPrimitiveEffectExecutor.RetiredInfluenceSelectionTypeId,out _),Is.False);
            Assert.That(Registry().TryGet(ContentPrimitiveEffectExecutor.RetiredInfluenceSelectionTypeId,out _),Is.False);
        }



        [TestCase(false)][TestCase(true)]
        public void RetiredSelectorCannotBypassCompilerOrKernelThroughManualSpecs(bool nested)
        {
            var args = Object("player", NormalizedValue.CreateString("1"), "operation", NormalizedValue.CreateString("remove"), "count", NormalizedValue.CreateInteger(1));
            var leaf = LuaEffectSpec.FromArguments(ContentPrimitiveEffectExecutor.RetiredInfluenceSelectionTypeId, args);
            var spec = nested ? LuaEffectSpec.FromEffectGroups(EffectTypeIds.Condition, Object(),
                new IReadOnlyList<LuaEffectSpec>[] { new[] { leaf }, Array.Empty<LuaEffectSpec>() }) : leaf;
            var success = typeof(LuaInvocationResult).GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Single(method => method.Name == "Success" && method.GetParameters().Length == 2);
            var invocation = (LuaInvocationResult)success.Invoke(null, new object[] { ShuffleScript("return function(ctx) return {} end"), new[] { spec } });
            var catalog = new LuaEffectTypeCatalog();
            // 隔离模拟放宽过参数校验的目录，退役门禁仍必须先拒绝旧SelectInfluence类型。
            var entries = (Dictionary<string, LuaEffectTypeRegistration>)typeof(LuaEffectTypeCatalog)
                .GetField("registrations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(catalog);
            entries[ContentPrimitiveEffectExecutor.RetiredInfluenceSelectionTypeId] = new LuaEffectTypeRegistration(ContentPrimitiveEffectExecutor.RetiredInfluenceSelectionTypeId, null, item => item.RawArguments.Clone());
            Assert.That(new LuaEffectSpecCompiler(catalog).TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 },
                out _, out var fault), Is.False);
            Assert.That(fault.Diagnostic, Does.Contain("influence_selection_api_retired"));
            var state = State();
            Func<string> rules = () => JsonConvert.SerializeObject(new { state.ItemZones, state.Players, state.Map, state.RandomRuntime });
            string before = rules();
            var registry = new EffectRegistry(); registry.Register(ContentPrimitiveEffectExecutor.RetiredInfluenceSelectionTypeId, _ => throw new AssertionException("旧SelectInfluence类型不能执行"));
            var node = EffectSpec.Create(ContentPrimitiveEffectExecutor.RetiredInfluenceSelectionTypeId, args, 1);
            if (nested) node = EffectSpec.Condition(new[] { node }, Array.Empty<EffectSpec>());
            Assert.That(new EffectTreeExecutor(state, registry).TryCreateRoot(node, "", "", out _), Is.False);
            Assert.That(rules(), Is.EqualTo(before));
            Assert.That(state.EffectRuntime.EffectNodes, Is.Empty);
            Assert.That(state.EffectRuntime.LastFaultMessage, Does.Contain("influence_selection_api_retired"));
        }


        [TestCase("terminal", false)][TestCase("terminal", true)]
        [TestCase("nested", false)][TestCase("nested", true)]
        [TestCase("settlement", false)][TestCase("settlement", true)]
        [TestCase("deferred", false)][TestCase("deferred", true)]
        [TestCase("departure_offer", false)][TestCase("departure_offer", true)]
        [TestCase("place_mapping", false)][TestCase("place_mapping", true)]
        public void RetiredSelectorRecoveryRejectsEverySavedSpecCarrierWithoutRewriting(string carrier, bool journal)
        {
            var state = State(); var registry = Registry(); Run(state, Shuffle(state), registry);
            // 上下文中的同名业务数据不能被当成可执行规格。
            state.EffectRuntime.EffectNodes[0].ContinuationData = Object("EffectTypeId", NormalizedValue.CreateString(ContentPrimitiveEffectExecutor.RetiredInfluenceSelectionTypeId),
                "NormalizedArguments", Object("player", NormalizedValue.CreateString("1")));
            var dto = HostRecoveryService.CreateArchive(state);
            if (journal) { Run(state, Shuffle(state), registry); HostRecoveryService.AppendJournal(dto, state); }
            string clean = HostRecoveryService.SerializeArchive(dto);
            Assert.That(HostRecoveryService.RecoverJson(clean, registry).Succeeded, Is.True);
            var archive = JObject.Parse(clean); var runtime = (JObject)(journal ? archive.SelectTokens("$..StateAfter").OfType<JObject>().Last()["EffectRuntime"] : archive["Snapshot"]["State"]["EffectRuntime"]);
            var old = JObject.FromObject(new EffectNodeRuntimeState { EffectId = "old.selector", EffectTypeId = ContentPrimitiveEffectExecutor.RetiredInfluenceSelectionTypeId,
                PlayerId = 1, Status = EffectNodeStatus.Completed, NormalizedArguments = Object("player", NormalizedValue.CreateString("1"), "operation", NormalizedValue.CreateString("remove"), "count", NormalizedValue.CreateInteger(1)) });
            if (carrier == "terminal") ((JArray)runtime["EffectNodes"]).Add(old);
            if (carrier == "nested") ((JArray)runtime["EffectNodes"][0]["NestedEffects"]).Add(old);
            if (carrier == "settlement") runtime["EffectNodes"][0]["SettlementWindow"] = new JObject {
                ["Entries"] = new JArray(new JObject { ["Effects"] = new JArray(old) }) };
            if (carrier == "deferred") runtime["DeferredResponses"] = new JArray(new JObject { ["PendingBody"] = new JArray(old) });
            if (carrier == "departure_offer") runtime["InfluenceDepartureBatches"] = new JArray(new JObject {
                ["Offers"] = new JArray(new JObject { ["Body"] = old }) });
            if (carrier == "place_mapping") runtime["InfluencePlaceMappings"] = new JArray(new JObject { ["OriginalSpec"] = old });
            string before = archive.ToString();
            var result = HostRecoveryService.RecoverJson(before, registry);
            Assert.That(result.Succeeded, Is.False); Assert.That(result.Diagnostic, Is.EqualTo("influence_selection_api_retired"));
            // 使用正式深复制保留位置集合转换器；不以默认Json.NET重建整个GameState。
            var typed = GameStateCloneService.DeepClone(state);
            var saved = old.ToObject<EffectSpecRuntimeState>();
            if (carrier == "terminal") typed.EffectRuntime.EffectNodes.Add(old.ToObject<EffectNodeRuntimeState>());
            if (carrier == "nested") typed.EffectRuntime.EffectNodes[0].NestedEffects.Add(saved);
            if (carrier == "settlement") typed.EffectRuntime.EffectNodes[0].SettlementWindow = new SettlementWindowRuntimeState {
                Entries = new List<SettlementEntryRuntimeState> { new SettlementEntryRuntimeState { Effects = new List<EffectSpecRuntimeState> { saved } } } };
            if (carrier == "deferred") typed.EffectRuntime.DeferredResponses.Add(new DeferredResponseState { PendingBody = new List<EffectSpecRuntimeState> { saved } });
            if (carrier == "departure_offer") typed.EffectRuntime.InfluenceDepartureBatches.Add(new InfluenceDepartureBatch {
                Offers = new List<DepartureBatchOffer> { new DepartureBatchOffer { Body = saved } } });
            if (carrier == "place_mapping") typed.EffectRuntime.InfluencePlaceMappings.Add(new InfluencePlaceMapping { OriginalSpec = saved });
            Assert.That(ContentPrimitiveEffectExecutor.TryValidateContentContinuations(typed, out string typedReason), Is.False);
            Assert.That(typedReason, Is.EqualTo("influence_selection_api_retired"));
            Assert.That(archive.ToString(), Is.EqualTo(before));
            artifacts.WriteLabeledText("SelectInfluence退役拒绝原始归档_" + carrier, before);
        }
    }
}
