using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class CardMarkerContractTests
    {
        private EffectSpec NativeTokenCost(int count)
        {
            var spec = ItemMovementV4.TransferSpec(1, Stored.OrderBy(i => i.ItemId, System.StringComparer.Ordinal).Take(count).Select(i => i.ItemId),
                Storage.ZoneId, "", required: count, profile: "core.cube_storage");
            spec.NormalizedArguments.Properties.Single(p => p.Name == "destination").Value = Object("kind", NormalizedValue.CreateString("original_supply"));
            return spec;
        }
        [TestCase(1)][TestCase(3)]
        public void NativeV4AndFiniteInputUseTheSamePureCostPlan(int inputVersion)
        {
            State(); Invoke(SeedStoredTokens(2)); var native = NativeTokenCost(2);
            var ids = Value(Value(native.NormalizedArguments, "selector"), "itemIds").Items.Select(i => i.StringValue).ToList();
            var input = inputVersion == 3 ? TokenArguments(1, ids, Storage.ZoneId, null, 2) :
                MoveSpec(1, ids, Storage.ZoneId, FindZone(state, "supply", 1).ZoneId, "unordered", 2, "core.cube_storage").NormalizedArguments;
            var source = registry.QuerySources(state, 1).Single(s => s.AbilityId == "qy005.allowed");
            string original = JsonConvert.SerializeObject(input), before = JsonConvert.SerializeObject(state);
            var service = new ItemTransferService();
            Assert.That(service.TryPlanTokens(state, source, native.NormalizedArguments, registry.AbilityDeclarations, "same-cost", out var current, out var reason), Is.True, reason);
            Assert.That(service.TryPlanTokens(state, source, input, registry.AbilityDeclarations, "same-cost", out var legacy, out reason), Is.True, reason);
            Assert.That(JsonConvert.SerializeObject(legacy), Is.EqualTo(JsonConvert.SerializeObject(current)));
            Assert.That(JsonConvert.SerializeObject(input), Is.EqualTo(original)); Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
        }
        [TestCase(false)][TestCase(true)]
        public void NativeV4EntryCostUsesPurePreviewAndOneActualReceiptAcrossRecovery(bool fullPreview)
        {
            State(tokenCost: true, nativeTokenCost: true); body = () => new[] { ResourceEffectSpecFactory.GainScore(1, 1) };
            if (fullPreview) registry.RegisterActionCostPreview((s, source, action, costs) => ResourcePaymentCostPreview.PreviewActionCosts(s, registry, source, action, costs));
            string before = JsonConvert.SerializeObject(state);
            Assert.That(registry.QueryActions(state, 1).Single(o => o.ActionId == "qy005.paid").Enabled, Is.False);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before), "纯费用报价不创建选择、事件或扣费。");
            Invoke(SeedStoredTokens(2)); body = () => new[] { ResourceEffectSpecFactory.GainScore(1, 1) };
            var option = registry.QueryActions(state, 1).Single(o => o.ActionId == "qy005.paid"); Assert.That(option.Enabled, Is.True, option.DisabledReason);
            Assert.That(Engine().TryInvokeAbilityAction(1, option.OptionId, option.RequestRef.InteractionId, option.RequestRef.FactsDigest, option.VersionRef.ContentPackHash,
                "native-cost-repeat", Object(), out string owner, out string error), Is.True, error); Run();
            var cost = state.EffectRuntime.EffectNodes.Single(n => IsTokenTransfer(n));
            Assert.That(cost.ItemMoveInput, Is.Null); Assert.That(cost.PositionChanges.Count, Is.EqualTo(2));
            Assert.That(AbilityActionExecutor.HasCommittedTokenTransfer(state, cost), Is.True);
            string receipt = TokenReceipt(cost).ReceiptId;
            Assert.That(Stored, Is.Empty); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1)); Restore("原生v4入口费提交后恢复");
            var repeated = Engine().TryInvokeAbilityAction(1, option.OptionId, option.RequestRef.InteractionId, option.RequestRef.FactsDigest, option.VersionRef.ContentPackHash,
                "native-cost-repeat", Object(), out _, out _); Run();
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1)); Assert.That(Stored, Is.Empty);
            Assert.That(TokenReceipt(state.EffectRuntime.EffectNodes.Single(IsTokenTransfer)).ReceiptId, Is.EqualTo(receipt));
            artifacts.WriteLabeledText("同命令重试结果", repeated.ToString());
        }
        [TestCase("actor")][TestCase("source")][TestCase("shortage")][TestCase("duplicate")]
        [TestCase("select")][TestCase("empty_target")][TestCase("provenance")][TestCase("unprivileged")]
        public void NativeV4EntryCostCannotBypassExactInventoryOrPaymentSourcePermission(string change)
        {
            State(); Invoke(SeedStoredTokens(2)); var spec = NativeTokenCost(2); var args = spec.NormalizedArguments;
            var source = registry.QuerySources(state, 1).Single(s => s.AbilityId == (change == "unprivileged" ? "qy005.unprivileged" : "qy005.allowed"));
            if (change == "actor") Value(args, "player").StringValue = "2";
            if (change == "source") Value(Value(Value(args, "selector"), "expectedSource"), "zoneId").StringValue = FindZone(state, "supply", 1).ZoneId;
            if (change == "shortage") Value(args, "requiredCount").IntegerValue = 3;
            if (change == "duplicate") Value(Value(args, "selector"), "itemIds").Items[1] = Value(Value(args, "selector"), "itemIds").Items[0].Clone();
            if (change == "select") args.Properties.Single(p => p.Name == "selector").Value = Object("mode", NormalizedValue.CreateString("select"), "zoneIds", Strings(new[] { Storage.ZoneId }),
                "itemTypeId", NormalizedValue.CreateString("core.player_marker"), "ownerFilter", NormalizedValue.CreateString("self"), "count", NormalizedValue.CreateInteger(2), "allowDecline", NormalizedValue.CreateBoolean(false));
            if (change == "empty_target") args.Properties.Single(p => p.Name == "destination").Value = Object("kind", NormalizedValue.CreateString("original_supply"), "mustBeEmpty", NormalizedValue.CreateBoolean(true));
            if (change == "provenance") { spec.ItemMoveInput = TransferTokens(2).ItemMoveInput; spec.ItemMoveInput.OriginalArgumentsDigest = "corrupt"; }
            string before = JsonConvert.SerializeObject(state);
            Assert.That(registry.PreviewItemTransferEntryCost(state, source, spec), Is.Not.Empty);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before)); Assert.That(Stored.Count, Is.EqualTo(2));
        }
        [Test]
        public void HostCoverPreparationUsesSameMainBodyAndRejectsOldExecutionVersion()
        {
            State(initialCharacters:true); CharacterCoverEffectExecutor.Register(registry);
            // 独立规则准备任务，不计正常菜单/建房/开局；实际当前夹具只有手牌/牌区，不注入盖放结果。
            var ids = state.ItemZones.Items.Where(i => i.TypeId == "core.player_marker").Select(i => i.ItemId).ToList();
            var engine = Engine(); string root = engine.CreateRoot(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Run();
            var task = engine.GetNode(root); var child = MainCoverAuthority.Body(state, task);
            Assert.That(child, Is.Null); Assert.That(task.PositionChanges, Is.Empty);
            Assert.That(task.MainCoverFlowV1, Is.Not.Null);Assert.That(state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open").OwnerEffectId,Is.EqualTo(root));
            Assert.That(task.ChildEffectIds, Is.Empty);
            Assert.That(state.ItemZones.Items.Where(i => i.TypeId == "core.player_marker").Select(i => i.ItemId), Is.EqualTo(ids));
            var archive = HostRecoveryService.CreateArchive(state); archive.Snapshot.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).DefinitionVersion = "3.0.0";
            var rejected = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), registry);
            Assert.That(rejected.Succeeded, Is.False); Assert.That(rejected.Diagnostic, Does.Contain("character_cover_contract_version_unsupported"));
        }
        [TestCase(false, "root")][TestCase(true, "root")][TestCase(false, "child")][TestCase(true, "child")]
        public void HostCoverPreparationCannotBorrowAnotherRootOrDetachItsCommonBody(bool journal, string change)
        {
            State(initialCharacters:true); CharacterCoverEffectExecutor.Register(registry); var archive = HostRecoveryService.CreateArchive(state);
            string root = Engine().CreateRoot(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Run();
            var request=state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");
            Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(),1,request.StateRevision,NormalizedValue.CreateString(request.CandidateIds[0]),out var reason),Is.True,reason);Run();
            var control = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(control.Succeeded, Is.True, control.Diagnostic);
            if (journal) HostRecoveryService.AppendJournal(archive, state, pack.ContentHash); else archive = HostRecoveryService.CreateArchive(state);
            var saved = journal ? archive.Journal.Entries.Last(e => e.StateAfter != null).StateAfter : archive.Snapshot.State;
            var task = saved.EffectRuntime.EffectNodes.Single(n => n.EffectId == root); var body = MainCoverAuthority.Body(saved, task);
            Assert.That(body, Is.Not.Null);
            if (change == "root") task.SourceId = "not-the-original-host-root"; else body.ParentEffectId = "other-parent";
            var result = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), registry);
            Assert.That(result.Succeeded, Is.False); Assert.That(result.Diagnostic, Is.Not.Empty);
        }
    }
}
