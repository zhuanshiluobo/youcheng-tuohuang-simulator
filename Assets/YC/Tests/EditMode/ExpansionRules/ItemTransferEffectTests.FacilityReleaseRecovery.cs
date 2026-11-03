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
        [TestCase("terminal", false)][TestCase("terminal", true)]
        [TestCase("nested", false)][TestCase("nested", true)]
        [TestCase("settlement", false)][TestCase("settlement", true)]
        [TestCase("deferred", false)][TestCase("deferred", true)]
        [TestCase("departure_offer", false)][TestCase("departure_offer", true)]
        [TestCase("place_mapping", false)][TestCase("place_mapping", true)]
        public void RetiredFacilityReleaseRecoveryRejectsEverySavedSpecCarrierWithoutRewriting(string carrier, bool journal)
        {
            var state = State(); var registry = Registry(); Run(state, Shuffle(state), registry);
            // 上下文中的同名业务数据不能被当成可执行规格。
            state.EffectRuntime.EffectNodes[0].ContinuationData = Object("EffectTypeId", NormalizedValue.CreateString(ContentPrimitiveEffectExecutor.RetiredFacilityReleaseTypeId),
                "NormalizedArguments", Object("player", NormalizedValue.CreateString("1")));
            var dto = HostRecoveryService.CreateArchive(state);
            if (journal) { Run(state, Shuffle(state), registry); HostRecoveryService.AppendJournal(dto, state); }
            string clean = HostRecoveryService.SerializeArchive(dto);
            Assert.That(HostRecoveryService.RecoverJson(clean, registry).Succeeded, Is.True);
            var archive = JObject.Parse(clean); var runtime = (JObject)(journal ? archive.SelectTokens("$..StateAfter").OfType<JObject>().Last()["EffectRuntime"] : archive["Snapshot"]["State"]["EffectRuntime"]);
            var old = JObject.FromObject(new EffectNodeRuntimeState { EffectId = "old.character.zone.move", EffectTypeId = ContentPrimitiveEffectExecutor.RetiredFacilityReleaseTypeId,
                PlayerId = 1, Status = EffectNodeStatus.Completed, NormalizedArguments = Object("player", NormalizedValue.CreateString("1")) });
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
            Assert.That(result.Succeeded, Is.False); Assert.That(result.Diagnostic, Is.EqualTo("facility_release_binding_api_retired"));
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
            Assert.That(typedReason, Is.EqualTo("facility_release_binding_api_retired"));
            Assert.That(archive.ToString(), Is.EqualTo(before));
            artifacts.WriteLabeledText("ReleaseFacilityBinding退役拒绝原始归档_" + carrier, before);
        }
    }
}
