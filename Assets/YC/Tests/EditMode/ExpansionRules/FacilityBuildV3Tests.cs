using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Application.Gameplay;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // API-11：人工准备实物候选的规则隔离；不计正常菜单、UI或对局验收。
    public sealed class FacilityBuildV3Tests : FacilityLayerTestFixture
    {
        protected override bool UseInMemoryContent => true;
        private const string Base = "apiVersion=3,targetMode='select',player=ctx.playerId,sourceKind='supply',paymentMode='free',consumeMainAction=false";
        private LuaInvocationResult Invoke(string fields)
        {
            string code = "return function(ctx) return {Effect.Build({" + fields + "})} end";
            return new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition(Pack.PackId, "api11", "api11", "1.0.0", code, LuaContentHasher.ComputeSha256(code)),
                new LuaInvocationContext("api11", "isolated", "p1", 0, "1.0.0", 2, 0, 0));
        }
        private EffectSpec Compile(string fields)
        {
            var invocation = Invoke(fields); Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler(presentationPack: Pack).TryCompile(invocation,
                new LuaEffectCompilationContext { PlayerId = 1, SourceId = "api11.isolated" }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            return specs.Single();
        }
        private string SupplyCard(GameState state, int index = 0)
        {
            string definition = Definition(state, f => !f.HasEntryEffect && f.Color != "rainbow");
            string id = Card(state, definition, index).ItemId;
            var slot = state.ItemZones.Zones.Where(z => z.TemplateId == FacilityLayerQuery.SupplyTemplate).ElementAt(index);
            Transfer(state, Leg(state, id, slot.ZoneId, "top")); return id;
        }
        private void Prepare(GameState state, string root, string item, string mode = "gold")
        {
            var q = Open(state); Assert.That(q.OwnerEffectId, Is.EqualTo(root));
            var args = Object("paymentMode", NormalizedValue.CreateString(mode), "expectedSourceZoneId", NormalizedValue.CreateString(state.ItemZones.Items.Single(i => i.ItemId == item).ZoneId));
            Assert.That(new EffectTreeExecutor(state, Registry).TryPrepareEffectBuildPayment(1, root, q.GetStableInteractionId(), q.CandidateSetId,
                q.CandidateSetVersion, item, args, state.EffectRuntime.StateRevision, Guid.NewGuid().ToString("N"), out string error), Is.True, error);
        }
        [Test] public void FreeSelectionOwnsActualBuildAndItsResultAcrossRecovery()
        {
            var state = State(); string item = SupplyCard(state); string root = Run(state, Compile(Base)).EffectId;
            Assert.That(Open(state).OwnerEffectId, Is.EqualTo(root));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ContentPrimitiveEffectExecutor.RetiredBuildSelectionTypeId), Is.False);
            state = Recover(state, "v3免费建设选牌待答"); Answer(state, item);
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo(FacilityBuildEffectExecutor.PlacementInteractionType));
            string actual = Open(state).OwnerEffectId; Assert.That(actual, Is.Not.EqualTo(root));
            state = Recover(state, "v3免费建设待放置"); Answer(state, "0");
            var result = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root);
            Assert.That(result.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(Text(result.NormalizedResult, "outcome"), Is.EqualTo("built"));
            Assert.That(Text(result.NormalizedResult, "builtItemId"), Is.EqualTo(item)); Assert.That(Text(result.NormalizedResult, "buildEffectId"), Is.EqualTo(actual));
            Assert.That(Cell(state, 0).TopItemId, Is.EqualTo(item));
            var completions = state.EffectRuntime.RuleEvents.Where(e => e.EventType == "EffectCompleted" &&
                (e.SourceEffectId == root || e.SourceEffectId == actual)).ToList();
            var declaration = new AbilitySourceDeclaration { CompletedEffectTypes = new System.Collections.Generic.List<string> { FacilityEntryEffectTypeIds.Build } };
            Assert.That(completions.Count, Is.EqualTo(2));
            Assert.That(completions.Where(e => AbilityEventLuaFacts.MatchesCompletion(state, e, declaration)).Select(e => e.SourceEffectId), Is.EqualTo(new[] { actual }));
            var rootEvent = completions.Single(e => e.SourceEffectId == root); var actualEvent = completions.Single(e => e.SourceEffectId == actual);
            Assert.That(Value(FacilityLayerLuaCatalog.EnrichEventPayload(state, rootEvent, rootEvent.Payload), "buildFacts"), Is.Null);
            Assert.That(Value(FacilityLayerLuaCatalog.EnrichEventPayload(state, actualEvent, actualEvent.Payload), "buildFacts"), Is.Not.Null);
            Recover(state, "v3免费建设完成");
        }
        [Test] public void CompletedVersionTwoTwoSelectionRetainsItsOriginalContractDigest()
        {
            var state = State(); string item = SupplyCard(state); string root = Run(state, Compile(Base)).EffectId;
            Answer(state, item); Answer(state, "0");
            var node = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root);
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed));
            node.DefinitionVersion = "2.2.0";
            string digest = StableIdFactory.Create("facility.build.v3.1", node.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                node.SourceId ?? "", node.NormalizedArguments.ToDeterministicString());
            Set(node, "buildContractArgumentsDigest", NormalizedValue.CreateString(digest));
            Assert.That(FacilityBuildContract.TryValidateNode(state, node, out var valid), Is.True, valid);
            string clean = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state));
            var restored = HostRecoveryService.RecoverJson(clean, Registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic);
            var broken = JObject.Parse(clean);
            var raw = broken["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"].Single(n => (string)n["EffectId"] == root);
            raw["ContinuationData"]["Properties"].Single(p => (string)p["Name"] == "buildContractArgumentsDigest")["Value"]["StringValue"] = "forged";
            Assert.That(HostRecoveryService.RecoverJson(broken.ToString(), Registry).Succeeded, Is.False);
            broken = JObject.Parse(clean);
            broken["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"].Single(n => (string)n["EffectId"] == root)["Status"] = (int)EffectNodeStatus.Ready;
            Assert.That(HostRecoveryService.RecoverJson(broken.ToString(), Registry).Succeeded, Is.False);
        }
        [Test] public void PaidSelectionHasOnePreparationAndNeverChargesAgainAfterRecovery()
        {
            var state = State(); string item = SupplyCard(state); int gold = state.FindPlayer(1).Resources.GoldVoucher;
            int price = FacilityCardDatabase.Get(state, state.ItemZones.Items.Single(i => i.ItemId == item).DefinitionId).GoldVoucherCost;
            string root = Run(state, Compile(Base.Replace("paymentMode='free'", "paymentMode='gold'"))).EffectId;
            Prepare(state, root, item); state = Recover(state, "v3准备后未付款");
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold)); Answer(state, "pay");
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold - price));
            state = Recover(state, "v3已付待放置"); Answer(state, "0"); state = Recover(state, "v3付费完成");
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold - price));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.Payment?.Preparation?.IsPromoted == true), Is.EqualTo(1));
            Assert.That(Text(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).NormalizedResult, "builtItemId"), Is.EqualTo(item));
        }
        [Test] public void ExplicitModeSkipsCardSelectionAndUsesTheSameCompletedResult()
        {
            var state = State(); string definition = Definition(state, f => !f.HasEntryEffect && f.Color != "rainbow"); string item = Card(state, definition).ItemId;
            string fields = Base.Replace("targetMode='select'", "targetMode='explicit'").Replace("sourceKind='supply'", "sourceKind='deck'") + ",facilityId='" + item + "'";
            string root = Run(state, Compile(fields)).EffectId;
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo(FacilityBuildEffectExecutor.PlacementInteractionType)); Assert.That(Open(state).OwnerEffectId, Is.EqualTo(root));
            state = Recover(state, "v3显式实例待放置"); Answer(state, "0");
            var result = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).NormalizedResult;
            Assert.That(Text(result, "outcome"), Is.EqualTo("built")); Assert.That(Text(result, "builtItemId"), Is.EqualTo(item));
            Assert.That(Text(result, "buildEffectId"), Is.EqualTo(root)); Recover(state, "v3显式建设完成");
        }
        [TestCase("deck", "resources", -1)] [TestCase("deck", "gold", 3)]
        [TestCase("supply", "gold", -1)] [TestCase("supply", "resources", 3)]
        [TestCase("authorized", "gold", -1)]
        public void ExplicitAutoLocksOneCardAndPaysChosenModeOnceAcrossRecovery(string source, string mode, int slot)
        {
            var fixtureDefinition = Pack.CreateFacilities().Where(f => !f.HasEntryEffect && f.Color != "rainbow").OrderBy(f => f.Score).ThenBy(f => f.FacilityId, StringComparer.Ordinal).First().FacilityId;
            var state = State(extraRecipe: source != "authorized" ? null : new[] { new ItemRecipeEntry {
                EntryId = "explicit-authorized-facility", DefinitionId = fixtureDefinition, TypeId = "core.facility_card",
                Quantity = 1, PerPlayer = true, InitialZoneRole = "supply" } });
            string definition = Definition(state, f => !f.HasEntryEffect && f.Color != "rainbow");
            string item = source == "supply" ? SupplyCard(state) : Card(state, definition).ItemId;
            string sourceKey = "";
            if (source == "authorized")
            {
                string zone = state.ItemZones.Zones.First(z => z.TemplateId == "core.zone.player_supply" && z.OwnerPlayerId == 1).ZoneId;
                item = state.ItemZones.Items.Single(i => i.ZoneId == zone && i.DefinitionId == fixtureDefinition).ItemId;
                sourceKey = Registry.FacilityBuildQueries.ResolveAuthorizedSources(state, 1, "authorized", "test.explicit").Single(a => a.ItemId == item).SourceKey;
            }
            var original = state.FindPlayer(1).Resources.Clone();
            var facility = FacilityCardDatabase.Get(state, state.ItemZones.Items.Single(i => i.ItemId == item).DefinitionId);
            var cost = mode == "gold" ? new ResourceSet { GoldVoucher = facility.GoldVoucherCost } :
                new FacilityBuildCostService().GetEffectiveResourceCostForInstances(state, state.FindPlayer(1), facility, Array.Empty<string>());
            var expected = original.Clone(); Assert.That(expected.TryPay(cost), Is.True);
            string fields = Base.Replace("targetMode='select'", "targetMode='explicit'").Replace("paymentMode='free'", "paymentMode='auto'")
                .Replace("sourceKind='supply'", "sourceKind='" + source + "'") + ",facilityId='" + item + "',cityBoardSlotIndex=" + slot;
            if (sourceKey.Length > 0) fields += ",sourceKey='" + sourceKey + "'";
            string root = Run(state, Compile(fields)).EffectId;
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo(FacilitySelectionEffectExecutor.NormalBuildInteractionType));
            Assert.That(Open(state).CandidateIds, Is.EqualTo(new[] { item }));
            Assert.That(Text(Open(state).PromptParameters, "fixedFacilityId"), Is.EqualTo(item));
            Assert.That(Open(state).AllowDecline, Is.False);
            Assert.That(EffectBuildScope.TryReadEffectBuildScope(state, GameStateViewProjector.ProjectForPlayer(state, 1), 1, root, out var scope), Is.True);
            Assert.That(scope.TryGetCandidateSourceZoneId(item, out string actualSource), Is.True);
            Assert.That(actualSource, Is.EqualTo(state.ItemZones.Items.Single(i => i.ItemId == item).ZoneId));
            Assert.That(JsonConvert.SerializeObject(state.FindPlayer(1).Resources), Is.EqualTo(JsonConvert.SerializeObject(original)));
            state = Recover(state, "指定牌支付方式待选");
            var q = Open(state);
            string other = state.ItemZones.Items.First(i => i.ItemId != item && i.TypeId == "core.facility_card").ItemId;
            Assert.That(new EffectTreeExecutor(state, Registry).TryPrepareEffectBuildPayment(1, root, q.GetStableInteractionId(), q.CandidateSetId,
                q.CandidateSetVersion, other, Object("paymentMode", NormalizedValue.CreateString(mode), "expectedSourceZoneId",
                    NormalizedValue.CreateString(state.ItemZones.Items.Single(i => i.ItemId == other).ZoneId)),
                state.EffectRuntime.StateRevision, Guid.NewGuid().ToString("N"), out _), Is.False);
            Prepare(state, root, item, mode); state = Recover(state, "指定牌付款待确认");
            Answer(state, "pay");
            Assert.That(JsonConvert.SerializeObject(state.FindPlayer(1).Resources), Is.EqualTo(JsonConvert.SerializeObject(expected)));
            if (slot < 0)
            {
                Assert.That(Open(state).InteractionTypeId, Is.EqualTo(FacilityBuildEffectExecutor.PlacementInteractionType));
                Assert.That(Text(Open(state).PromptParameters, "fixedFacilityId"), Is.EqualTo(item));
                state = Recover(state, "指定牌已付待落位"); Answer(state, "0");
            }
            state = Recover(state, "指定牌建成");
            Assert.That(Cell(state, slot < 0 ? 0 : slot).TopItemId, Is.EqualTo(item));
            Assert.That(JsonConvert.SerializeObject(state.FindPlayer(1).Resources), Is.EqualTo(JsonConvert.SerializeObject(expected)));
            Assert.That(Text(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).NormalizedResult, "builtItemId"), Is.EqualTo(item));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.Payment?.Preparation?.IsPromoted == true), Is.EqualTo(1));
        }
        [Test] public void ExplicitAutoCanChangePaymentPreparationWithoutChangingTarget()
        {
            var state = State(); string item = SupplyCard(state);
            string root = Run(state, Compile(Base.Replace("targetMode='select'", "targetMode='explicit'")
                .Replace("paymentMode='free'", "paymentMode='auto'") + ",facilityId='" + item + "'")).EffectId;
            string before = JsonConvert.SerializeObject(state.FindPlayer(1).Resources);
            Prepare(state, root, item, "resources");
            Assert.That(new EffectTreeExecutor(state, Registry).TryCancelPreparedEffectBuildPayment(1, Open(state).OwnerEffectId,
                state.EffectRuntime.StateRevision, Guid.NewGuid().ToString("N"), out var reason), Is.True, reason);
            Assert.That(Open(state).CandidateIds, Is.EqualTo(new[] { item }));
            state = Recover(state, "指定牌改选支付方式"); Prepare(state, root, item, "gold");
            Assert.That(JsonConvert.SerializeObject(state.FindPlayer(1).Resources), Is.EqualTo(before));
            Answer(state, "pay"); Answer(state, "0"); Recover(state, "指定牌改选后建成");
        }
        [TestCase("source_zone_changed")] [TestCase("insufficient_resources")]
        public void ExplicitAutoInvalidInputFailsWithoutOpeningAnUnanswerablePanel(string failure)
        {
            var state = State(); var definition = Definition(state, d => !d.HasEntryEffect && d.GoldVoucherCost > 0 && d.ResourceCost.Enumerate().Any(v => v.Value > 0));
            string item = Card(state, definition).ItemId;
            if (failure == "insufficient_resources") state.FindPlayer(1).Resources = new ResourceSet();
            string fields = Base.Replace("targetMode='select'", "targetMode='explicit'").Replace("sourceKind='supply'", "sourceKind='deck'")
                .Replace("paymentMode='free'", "paymentMode='auto'") + ",facilityId='" + item + "'";
            if (failure == "source_zone_changed") fields += ",expectedSourceZoneId='missing'";
            var node = Run(state, Compile(fields));
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(node.FailureReason, Is.EqualTo(failure));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(q => q.OwnerEffectId == node.EffectId), Is.False);
            Recover(state, "指定牌无效输入明确失败");
        }
        [Test] public void ExplicitAutoRestoreRejectsChangedFixedCardOrSource()
        {
            var state = State(); string item = SupplyCard(state);
            Run(state, Compile(Base.Replace("targetMode='select'", "targetMode='explicit'")
                .Replace("paymentMode='free'", "paymentMode='auto'") + ",facilityId='" + item + "'"));
            string archive = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state));
            foreach (string field in new[] { "fixedFacilityId", "expectedSourceZoneId" })
            {
                var bad = JObject.Parse(archive);
                var request = bad["Snapshot"]["State"]["EffectRuntime"]["InteractionRequests"].Single(q => (string)q["Status"] == "open");
                request["PromptParameters"]["Properties"].Single(p => (string)p["Name"] == field)["Value"]["StringValue"] = "forged";
                Assert.That(HostRecoveryService.RecoverJson(bad.ToString(), Registry).Succeeded, Is.False, field);
            }
            Recover(state, "指定牌合法选择页未被反例修改");
        }
        [Test] public void CancellationReturnsToCurrentCandidatesWithoutSpendingOrReusingProof()
        {
            var state = State(); string item = SupplyCard(state); int gold = state.FindPlayer(1).Resources.GoldVoucher;
            string root = Run(state, Compile(Base.Replace("paymentMode='free'", "paymentMode='gold'"))).EffectId; Prepare(state, root, item);
            string payment = Open(state).OwnerEffectId;
            Assert.That(new EffectTreeExecutor(state, Registry).TryCancelPreparedEffectBuildPayment(1, payment, state.EffectRuntime.StateRevision,
                Guid.NewGuid().ToString("N"), out string error), Is.True, error);
            state = Recover(state, "v3取消准备回选牌"); Assert.That(Open(state).OwnerEffectId, Is.EqualTo(root));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold)); Prepare(state, root, item); Answer(state, "pay"); Answer(state, "0");
            Assert.That(Cell(state, 0).TopItemId, Is.EqualTo(item)); Recover(state, "v3取消后重新建设");
        }
        [TestCase(",sourceZone='supply'")][TestCase(",reserveForFree=true")][TestCase(",recipient='p1'")]
        [TestCase(",facilityId='missing'")][TestCase(",maxGoldVoucherCost=4294967296")][TestCase(",excludeRainbow=1")]
        [TestCase(",consumeMainAction=nil")][TestCase(",player='p2'")]
        public void CanonicalFieldsCannotBeMixedLostOrTruncated(string changes)
        {
            var result = Invoke(Base + changes); Assert.That(result.IsSuccess, Is.False, "不应接受：" + changes);
        }
        [Test] public void FreeSelectionDeclineHasNoBuildIdentityOrCost()
        {
            var state = State(); SupplyCard(state); string root = Run(state, Compile(Base + ",allowDecline=true")).EffectId;
            string before = JsonConvert.SerializeObject(new { state.Players, state.ItemZones }); var q = Open(state);
            var engine = new EffectTreeExecutor(state, Registry);
            Assert.That(engine.TrySubmitInteraction(q.GetStableInteractionId(), 1, q.StateRevision, NormalizedValue.CreateBoolean(false), out string error), Is.True, error);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(JsonConvert.SerializeObject(new { state.Players, state.ItemZones }), Is.EqualTo(before));
            Assert.That(Text(engine.GetNode(root).NormalizedResult, "outcome"), Is.EqualTo("player_declined"));
            Assert.That(Text(engine.GetNode(root).NormalizedResult, "builtItemId"), Is.Empty); Recover(state, "v3免费选择放弃");
        }
        [TestCase(false)][TestCase(true)]
        public void RawBuildFieldsAndPublishedModeCannotBeReconstructedOnRecovery(bool journal)
        {
            var state = State(); SupplyCard(state); var archive = HostRecoveryService.CreateArchive(state);
            string root = Run(state, Compile(Base)).EffectId;
            if (journal) HostRecoveryService.AppendJournal(archive, state); else archive = HostRecoveryService.CreateArchive(state);
            var raw = JObject.Parse(HostRecoveryService.SerializeArchive(archive)); string clean = raw.ToString();
            Assert.That(HostRecoveryService.RecoverJson(clean, Registry).Succeeded, Is.True);
            foreach (string mutation in new[] { "AllowDecline", "DecisionPlayerId", "FlowStage", "requestFlag", "versionValue", "mode", "digest" })
            {
                var bad = (JObject)raw.DeepClone(); var saved = journal ? bad["Journal"]["Entries"].Last["StateAfter"] : bad["Snapshot"]["State"];
                var node = (JObject)saved["EffectRuntime"]["EffectNodes"].Single(n => (string)n["EffectId"] == root);
                var args = (JArray)node["NormalizedArguments"]["Properties"];
                if (mutation == "requestFlag") ((JObject)saved["EffectRuntime"]["InteractionRequests"].Single(q => (string)q["OwnerEffectId"] == root)).Remove("AllowDecline");
                else if (mutation == "versionValue") ((JObject)args.Single(p => (string)p["Name"] == "apiVersion")["Value"]).Remove("IntegerValue");
                else if (mutation == "digest") args.Single(p => (string)p["Name"] == "consumeMainAction")["Value"]["BooleanValue"] = true;
                else if (mutation == "mode")
                {
                    args.Single(p => (string)p["Name"] == "targetMode").Remove(); args.Single(p => (string)p["Name"] == "apiVersion").Remove();
                    ((JArray)node["ContinuationData"]["Properties"]).Single(p => (string)p["Name"] == "buildContractProtocolVersion").Remove();
                }
                else node.Remove(mutation);
                string original = bad.ToString(); var restored = HostRecoveryService.RecoverJson(original, Registry);
                Assert.That(restored.Succeeded, Is.False, mutation); Assert.That(restored.Diagnostic, Does.Contain("build_contract_"), mutation);
                Assert.That(bad.ToString(), Is.EqualTo(original)); artifacts.WriteLabeledText("API11原始恢复拒绝_" + mutation, original);
            }
            Assert.That(raw.ToString(), Is.EqualTo(clean));
        }
        [TestCase(false)][TestCase(true)]
        public void SelectionSpendsOnlyItsDeclaredMainActionOnce(bool consume)
        {
            var state = State(); string item = SupplyCard(state);
            new YC.Domain.Rules.MainActionBudgetService().GrantAdditionalMainActions(state, 1, 2);
            int before = state.FindPlayer(1).RemainingMainActionsThisTurn;
            string root = Run(state, Compile(Base.Replace("consumeMainAction=false", "consumeMainAction=" + (consume ? "true" : "false")))).EffectId;
            Answer(state, item); Answer(state, "0"); state = Recover(state, "v3行动预算完成恢复");
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(before - (consume ? 1 : 0)));
            Assert.That(Text(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).NormalizedResult, "outcome"), Is.EqualTo("built"));
        }
        [Test] public void ExplicitDefinitionIdCannotFallbackToARealSupplyInstance()
        {
            var state = State(); string item = SupplyCard(state); string definition = state.ItemZones.Items.Single(i => i.ItemId == item).DefinitionId;
            string before = JsonConvert.SerializeObject(new { state.Players, state.ItemZones });
            var node = Run(state, Compile(Base.Replace("targetMode='select'", "targetMode='explicit'") + ",facilityId='" + definition + "'"));
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(node.FailureReason, Is.EqualTo("facility_instance_required"));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(q => q.OwnerEffectId == node.EffectId), Is.False);
            Assert.That(JsonConvert.SerializeObject(new { state.Players, state.ItemZones }), Is.EqualTo(before));
        }
        [TestCase(false)][TestCase(true)] public void UnifiedCompletionWaitsForRealOverlayResponseAndEntry(bool explicitMode)
        {
            var state = State(response: true); OwnInfluence(state);
            string low = Definition(state, d => !d.Unique && !d.HasEntryEffect); Put(state, Card(state, low).ItemId, 0);
            int old = FacilityCardDatabase.Get(state, low).Score;
            string item = Card(state, Definition(state, d => !d.Unique && d.Score > old && d.HasEntryEffect && d.OnBuiltReward.Iron > 0)).ItemId;
            int iron = state.FindPlayer(1).Resources.Iron;
            Transfer(state, Leg(state, item, FindZone(state, "facility_supply_0").ZoneId, "top"));
            string fields = explicitMode ? Base.Replace("targetMode='select'", "targetMode='explicit'") + ",facilityId='" + item + "',cityBoardSlotIndex=0" : Base;
            string root = Run(state, Compile(fields)).EffectId;
            string actual = root;
            if (!explicitMode) { Answer(state, item); actual = Open(state).OwnerEffectId; Answer(state, "0"); }
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(iron)); state = Recover(state, "v3叠建必做响应待答");
            Answer(state, Open(state).CandidateIds.First());
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.GreaterThan(iron));
            Assert.That(Text(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).NormalizedResult, "buildEffectId"), Is.EqualTo(actual));
            state = Recover(state, "v3叠建响应及入场完成"); int completedIron = state.FindPlayer(1).Resources.Iron;
            var bad = GameStateCloneService.DeepClone(state); bad.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).NormalizedResult.Properties.Single(p => p.Name == "builtItemId").Value = NormalizedValue.CreateString("forged");
            Assert.That(ContentPrimitiveEffectExecutor.TryValidateContentContinuations(bad, out string error), Is.False); Assert.That(error, Is.EqualTo("build_contract_result_invalid"));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(completedIron));
        }
        [Test] public void RetiredBuildSelectorPendingAnswerIsRejectedBeforeAnyMutation()
        {
            var state = State(); SupplyCard(state); var root = Run(state, Compile(Base)); var request = Open(state);
            root.EffectTypeId = ContentPrimitiveEffectExecutor.RetiredBuildSelectionTypeId;
            root.NormalizedArguments = Object("player", NormalizedValue.CreateString("1"), "sourceZone", NormalizedValue.CreateString("supply"));
            request.InteractionTypeId = "facility.entry.choice"; request.PromptParameters = Object();
            string before = JsonConvert.SerializeObject(state);
            Assert.That(new EffectTreeExecutor(state, Registry).TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString(request.CandidateIds.First()), out string error), Is.False);
            Assert.That(error, Is.EqualTo(ContentPrimitiveEffectExecutor.RetiredBuildSelectionReason));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before)); artifacts.WriteLabeledText("API13旧建设待答拒绝副本", before);
        }
        private EffectSpec FormalContent(string path)
        {
            // 实际正式Lua正文与当前pack，处理器身份为隔离消费者，不冒充正常能力行动。
            string source = Pack.GetScript(path), id = "api12.isolated." + System.IO.Path.GetFileNameWithoutExtension(path);
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition(Pack.PackId, id, id, "1.0.0", source,
                LuaContentHasher.ComputeSha256(source)), new LuaInvocationContext(id, "Api12Isolated", "p1", 0, "1.0.0", 2, 0, 0));
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler(presentationPack: Pack).TryCompile(invocation,
                new LuaEffectCompilationContext { PlayerId = 1, SourceId = id }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            return specs.Single();
        }
        [Test] public void FormalEngineeringRewardKeepsPrintedLimitAndOnlyItsOriginalOuterPayment()
        {
            var state = State(); state.FindPlayer(1).Resources.PureOriginium = 1;
            string allowed = Card(state, Definition(state, d => !d.ReserveOnly && !d.HasEntryEffect && d.GoldVoucherCost <= 23)).ItemId;
            string expensive = Card(state, Definition(state, d => !d.ReserveOnly && d.GoldVoucherCost > 23)).ItemId;
            Transfer(state, Leg(state, allowed, FindZone(state, "facility_supply_0").ZoneId, "top"),
                Leg(state, expensive, FindZone(state, "facility_supply_1").ZoneId, "top"));
            int gold = state.FindPlayer(1).Resources.GoldVoucher, actions = state.FindPlayer(1).RemainingMainActionsThisTurn;
            string root = Run(state, FormalContent("lua/departments/enterprise.department.engineering.level_5.reward.lua")).EffectId;
            Answer(state, "continue"); Assert.That(state.FindPlayer(1).Resources.PureOriginium, Is.Zero);
            Assert.That(Open(state).CandidateIds, Does.Contain(allowed)); Assert.That(Open(state).CandidateIds, Does.Not.Contain(expensive));
            Assert.That(Open(state).CandidateIds.All(id => FacilityCardDatabase.Get(state, state.ItemZones.Items.Single(i => i.ItemId == id).DefinitionId).GoldVoucherCost <= 23), Is.True);
            state = Recover(state, "工程五级原支付后限价免费候选"); Answer(state, allowed); Answer(state, "0");
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold)); Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(actions));
            Assert.That(Cell(state, 0).TopItemId, Is.EqualTo(allowed)); Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Recover(state, "工程五级建成不重付");
        }
        [Test] public void FormalHydeRefreshKeepsFrozenOldSupplyAndCanDeclineTheNewFreeBuild()
        {
            var state = State(); SupplyCard(state); var old = FacilityLayerQuery.Supply(state).SelectMany(c => c.ItemIds).ToArray();
            int originium = state.FindPlayer(1).Resources.Originium, gold = state.FindPlayer(1).Resources.GoldVoucher;
            Run(state, FormalContent("lua/enterprises/enterprise.hyde_brothers.level_4.special.lua")); Answer(state, "continue");
            var refresh = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == RefreshTypeId);
            Assert.That(refresh.FacilitySupplyFlowV1.ReturnedItems.Select(v => v.ItemId), Is.EquivalentTo(old));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(originium - 6));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == DeckShuffleEffectExecutor.TypeId && n.Status == EffectNodeStatus.Completed), Is.EqualTo(1));
            Assert.That(Open(state).AllowDecline, Is.True); state = Recover(state, "海德旧供应刷新后新建设待答");
            var q = Open(state); var engine = new EffectTreeExecutor(state, Registry);
            Assert.That(engine.TrySubmitInteraction(q.GetStableInteractionId(), 1, q.StateRevision, NormalizedValue.CreateBoolean(false), out string error), Is.True, error);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build && !FacilityBuildContract.IsSelection(n.EffectTypeId, n.NormalizedArguments)), Is.False);
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(originium - 6)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold));
            Assert.That(FacilityLayerQuery.Supply(state).All(c => c.ItemIds.Count == 1), Is.True); Recover(state, "海德放弃免费建设保留刷新和原费用");
        }
        [TestCase("select", EffectNodeStatus.Created)][TestCase("select", EffectNodeStatus.Ready)][TestCase("select", EffectNodeStatus.Running)]
        [TestCase("explicit", EffectNodeStatus.Created)][TestCase("explicit", EffectNodeStatus.Ready)][TestCase("explicit", EffectNodeStatus.Running)]
        public void UnstartedBuildRecoversAtEveryRealCommittedSchedulerStatus(string mode, EffectNodeStatus status)
        {
            var state = State(); string item = SupplyCard(state);
            string fields = mode == "select" ? Base : Base.Replace("targetMode='select'", "targetMode='explicit'") + ",facilityId='" + item + "'";
            var engine = new EffectTreeExecutor(state, Registry); string root = engine.CreateRoot(Compile(fields));
            for (int step = 0; step < 50 && engine.GetNode(root).Status != status; step++) Assert.That(engine.Advance(), Is.True);
            Assert.That(engine.GetNode(root).Status, Is.EqualTo(status)); Assert.That(engine.GetNode(root).FlowStage, Is.Empty);
            Assert.That(Value(engine.GetNode(root).ContinuationData, "buildContractProtocolVersion"), Is.Null);
            Evidence(state, "首次执行前真实调度提交_" + status); state = Recover(state, "v3首次执行前_" + mode + "_" + status);
            engine = new EffectTreeExecutor(state, Registry); Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            if (mode == "select") Answer(state, item); Answer(state, "0");
            Assert.That(Cell(state, 0).TopItemId, Is.EqualTo(item));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build && !FacilityBuildContract.IsSelection(n.EffectTypeId, n.NormalizedArguments)), Is.EqualTo(1));
            Recover(state, "首次执行前恢复后建设只完成一次");
        }
        [Test] public void UnstartedDeclarationCannotHideProgressWithoutTheBuildProtocol()
        {
            var state = State(); string item = SupplyCard(state); var engine = new EffectTreeExecutor(state, Registry);
            string root = engine.CreateRoot(Compile(Base));
            Assert.That(ContentPrimitiveEffectExecutor.TryValidateContentContinuations(state, out string valid), Is.True, valid);
            var bad = GameStateCloneService.DeepClone(state); var node = bad.EffectRuntime.EffectNodes.Single(n => n.EffectId == root);
            Set(node, "selectedItemIds", Strings(new[] { item }));
            Assert.That(ContentPrimitiveEffectExecutor.TryValidateContentContinuations(bad, out string error), Is.False);
            Assert.That(error, Is.EqualTo("build_contract_state_invalid"));
            Assert.That(engine.GetNode(root).ContinuationData.Properties, Is.Empty);
        }
    }
}
