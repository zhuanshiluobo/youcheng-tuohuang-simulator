using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class MultiSourceResourcePaymentTests
    {
        private PaymentTestFixture f;
        private List<PaymentSourceRule> rules;
        private string markerZone;
        private List<string> markerIds;
        private static object FixtureProperty(string name)
        {
            // 参数化方法的Property保存在父测试上；不能把256库存专项默默降成默认6枚。
            NUnit.Framework.Interfaces.ITest test = NUnit.Framework.Internal.TestExecutionContext.CurrentContext.CurrentTest;
            while (test != null)
            {
                var value = test.Properties.Get(name); if (value != null) return value;
                test = test.Parent;
            }
            // Unity重建参数化测试树时可能不携带方法Property；以实际编译方法的声明作后备。
            var method = typeof(MultiSourceResourcePaymentTests).GetMethod(TestContext.CurrentContext.Test.MethodName);
            if (method == null) throw new InvalidOperationException("payment_fixture_test_method_missing");
            return method.GetCustomAttributes(typeof(PropertyAttribute), true).Cast<PropertyAttribute>()
                .Select(attribute => attribute.Properties.Get(name)).FirstOrDefault(value => value != null);
        }
        [SetUp] public void SetUp()
        {
            f = new PaymentTestFixture((int)(FixtureProperty("paymentMarkerInventory") ?? 6),
                FixtureProperty("paymentFormalMap") != null ? YC.Domain.Maps.StaticMapDefinitions.CreateThreePlayerMap() : null);
            f.Artifacts.WriteLabeledText("实际隔离库存数量", JsonConvert.SerializeObject(new {
                method = TestContext.CurrentContext.Test.MethodName, declared = FixtureProperty("paymentMarkerInventory"),
                actual = f.State.ItemZones.Items.Count(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1) }));
            var card = f.State.ItemZones.Items.Single(i => i.DefinitionId == "test.payment.source_1" && i.ControllerSubject.PlayerId == 1);
            Assert.That(new ItemZoneStateFactory().TryCreateCardZone(f.State, card.ItemId, new CardZoneDefinition { LocalZoneId = "markers", ZoneTemplateId = "core.zone.card_cube_storage",
                AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, AcceptedTypeIds = new List<string> { "core.player_marker" }, EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer",
                CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Unordered, VisibilityPolicyId = "core.public_items", CarrierExitPolicyId = "core.preserve_contents" }, out markerZone, out var reason), Is.True, reason);
            markerIds = f.State.ItemZones.Items.Where(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1).OrderBy(i => i.ItemId).Take(2).Select(i => i.ItemId).ToList();
            // 仅隔离夹具的初始库存，不作为正常游戏步骤或正式角色激活证据。
            foreach (string id in markerIds) typeof(ItemZoneStateMigrator).GetMethod("SetPosition", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { f.State, f.State.ItemZones.Items.Single(i => i.ItemId == id), markerZone });
            var declarations = new List<AbilitySourceDeclaration>(); ResourcePaymentSourceService.Register(f.Registry);
            for (int i = 0; i < 2; i++)
            {
                int index = i; string handler = "test.payment.handler_" + i;
                var declaration = new AbilitySourceDeclaration { DefinitionId = "test.payment.source_" + i, AbilityId = "test.payment.ability_" + i, HandlerRef = handler, DeclarationVersion = "1.0.0", Kind = "ruleQuery",
                    AuthorizationPolicy = "controller", EffectiveZones = new List<string> { FindZone(f.State, "hand", 1).TemplateId }, QueryKinds = new List<string> { ResourcePaymentSourceService.QueryKind }, PresentationRef = "test.payment.source_" + i };
                if (i == 1) declaration.AllowedOperations.Add("carrier.transfer.original_supply:test.payment.source_1:markers:core.player_marker");
                declarations.Add(declaration);
                f.Registry.RegisterAbilityHandler(handler, "test.payment.hash_" + i, _ => Array.Empty<EffectSpec>());
                f.Registry.RegisterAbilityPureHandler(handler, (call, query) => Object("queryId", NormalizedValue.CreateString(query.QueryId), "basisDigest", NormalizedValue.CreateString(query.FactsDigest),
                    "items", NormalizedValue.CreateArray(new[] { Rule(index) })));
            }
            f.Registry.FreezeAbilityCatalog(declarations); f.State.ContentDefinitions.AbilitySources = declarations;
        }
        private static NormalizedValue Rule(int index)
        {
            var value = Object("ruleId", NormalizedValue.CreateString("test.rule_" + index), "purposeKind", NormalizedValue.CreateString("payment"),
                "allowedResourceTypes", Strings(new[] { "Originium", "OriginiumShard", "Iron" }), "maxUnits", NormalizedValue.CreateInteger(index == 0 ? 1 : 2),
                "quotaGroupId", NormalizedValue.CreateString("test.quota_" + index), "quotaLimit", NormalizedValue.CreateInteger(index == 0 ? 1 : 2), "quotaScope", NormalizedValue.CreateString("payment"),
                "unitResourceCost", PaymentResources.Write(index == 0 ? new ResourceSet { GoldVoucher = 1 } : new ResourceSet()));
            if (index == 1) value.Properties.Add(new NormalizedValueEntry { Name = "itemCost", Value = Object("localZoneId", NormalizedValue.CreateString("markers"), "countPerUnit", NormalizedValue.CreateInteger(1), "destinationKind", NormalizedValue.CreateString("original_supply")) });
            value.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            return value;
        }
        private EffectNodeRuntimeState OpenPayment()
        {
            var n = f.Run(ResourceEffectSpecFactory.Pay(1, new ResourceSet { Originium = 2, Iron = 1 }));
            rules = ResourcePaymentSourceService.Query(f.State, f.Registry, n, n.Payment.PaymentCost).ToList();
            Assert.That(rules.Count, Is.EqualTo(2)); return n;
        }
        private PaymentSourceRule RuleAt(int index) => rules.Single(r => r.RuleId == "test.rule_" + index);
        private void Set(EffectNodeRuntimeState n, int index, ResourceSet allocation, IReadOnlyList<string> ids = null, int revision = 0)
        {
            var r = RuleAt(index);
            Assert.That(ResourcePaymentDraftService.TrySetSourceDraft(f.State, f.Registry, n, r.SourceRef.SourceKey, r.RuleId, n.Payment.PaymentGeneration, revision, allocation, ids ?? Array.Empty<string>(), "", out var reason), Is.True, reason);
        }
        private EffectNodeRuntimeState OpenTotalPayment()
        {
            var n = f.Run(EffectSpec.Create(ResourceEffectTypeIds.Pay, PaymentTestFixture.TotalArgs(3, new ResourceSet { GoldVoucher = 4 }), 1));
            rules = ResourcePaymentSourceService.Query(f.State, f.Registry, n, n.Payment.PaymentCost).ToList();
            Assert.That(rules.Count, Is.EqualTo(2));
            return ChangeTotal(n, new ResourceSet { Iron = 3 });
        }
        private EffectNodeRuntimeState ChangeTotal(EffectNodeRuntimeState n, ResourceSet allocation)
        {
            var request = f.Open();
            Assert.That(f.Engine.TryPrepareTotalPaymentDraft(1, n.EffectId, request.GetStableInteractionId(), n.Payment.PaymentGeneration, allocation,
                f.State.EffectRuntime.StateRevision, Guid.NewGuid().ToString("N"), out var reason), Is.True, reason);
            return f.Engine.GetNode(n.EffectId);
        }
        private EffectNodeRuntimeState SourceCommand(EffectNodeRuntimeState n, int index, string operation, ResourceSet allocation = null, IReadOnlyList<string> ids = null, string editId = "")
        {
            var r = RuleAt(index); var d = n.Payment.SourceDrafts.SingleOrDefault(v => v.Rule.RuleId == r.RuleId); var request = f.Open();
            Assert.That(f.Engine.TryEditPaymentSource(1, n.EffectId, request.GetStableInteractionId(), operation, r.SourceRef.SourceKey, r.RuleId, editId,
                n.Payment.PaymentGeneration, d?.DraftRevision ?? 0, allocation ?? new ResourceSet(), ids ?? Array.Empty<string>(), f.State.EffectRuntime.StateRevision,
                Guid.NewGuid().ToString("N"), out var reason), Is.True, reason);
            return f.Engine.GetNode(n.EffectId);
        }
        private PaymentSourceQuery CurrentQuery(EffectNodeRuntimeState n)
        {
            var request = f.Open();
            return new PaymentSourceQuery { PlayerId = 1, OwnerEffectId = n.EffectId, InteractionId = request.GetStableInteractionId(),
                StateRevision = f.State.EffectRuntime.StateRevision, PaymentGeneration = n.Payment.PaymentGeneration,
                SnapshotId = Text(Value(request.PromptParameters, "paymentSourceQuery"), "snapshotId") };
        }
        private PaymentSourceProjection ReadSources(EffectNodeRuntimeState n)
        {
            string before = JsonConvert.SerializeObject(f.State);
            var query = CurrentQuery(n); PaymentSourceProjection view = null;
            while (true)
            {
                Assert.That(f.Engine.TryQueryPaymentSources(query, out var page, out var reason), Is.True, reason);
                Assert.That(page.Sources.Count, Is.EqualTo(1)); Assert.That(page.Sources[0].AvailableItems.Count, Is.LessThanOrEqualTo(ResourcePaymentDraftHost.SourceQueryItemPageSize));
                if (view == null) { view = GameStateCloneService.DeepClone(page); view.Sources.Clear(); }
                var row = page.Sources.Single();
                if (query.ItemOffset == 0) view.Sources.Add(GameStateCloneService.DeepClone(row));
                else view.Sources.Last().AvailableItems.AddRange(row.AvailableItems);
                if (page.NextSourceIndex == -1) break;
                Assert.That(page.NextSourceIndex > query.SourceIndex || page.NextSourceIndex == query.SourceIndex && page.NextItemOffset > query.ItemOffset, Is.True);
                query.SourceIndex = page.NextSourceIndex; query.ItemOffset = page.NextItemOffset;
            }
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before), "只读查询不能改变付款、库存、事件或修订");
            Assert.That(view.Sources.Count, Is.EqualTo(2));
            foreach (var row in view.Sources) Assert.That(row.AvailableItems.Select(i => i.ItemId).Distinct().Count(), Is.EqualTo(row.AvailableItemCount));
            return view;
        }
        private static int Elements(NormalizedValue value) => 1 + (value.Kind == NormalizedValueKind.Array ? value.Items.Sum(Elements) :
            value.Kind == NormalizedValueKind.Object ? value.Properties.Sum(p => Elements(p.Value)) : 0);
        [TearDown] public void TearDown() => f?.Dispose();
        [TestCase("martin_concession",1)][TestCase("passenger_markers",2)]
        public void ActualLuaRulesReturnIndependentPricesWithoutChangingState(string file, int limit)
        {
            var owner = OpenPayment();
            string source = File.ReadAllText(Path.Combine(f.PrivateContent, "lua/payment_substitution/" + file + ".lua"));
            var script = new LuaScriptDefinition("test.payment.actual_lua", "test.payment.actual_lua", "test.payment.actual_lua", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            var handler = new LuaHandlerDefinition("test.payment.actual_lua", "AbilityRuleQuery", "", script, EffectHandlerRole.Primary,
                contentInstanceId: RuleAt(0).SourceRef.ItemId, abilityId: "test.payment.actual_lua");
            var ruleEvent = new RuleEvent { EventId = "test.payment.pure-query", EventType = "AbilityRuleQuery", PlayerId = 1, SourceEffectId = owner.EffectId, OwnerNodeId = owner.EffectId,
                Payload = Object("queryId", NormalizedValue.CreateString("test.query"), "basisDigest", NormalizedValue.CreateString("test.facts"), "queryKind", NormalizedValue.CreateString(ResourcePaymentSourceService.QueryKind),
                    "facts", Object("purposeKind", NormalizedValue.CreateString("build"), "buildIdentity", NormalizedValue.CreateString("test.build"), "playerId", NormalizedValue.CreateInteger(1))) };
            var before = JsonConvert.SerializeObject(f.State);
            var dispatcher = new LuaEffectEventDispatcher(f.Registry);
            var result = (NormalizedValue)typeof(LuaEffectEventDispatcher).GetMethod("InvokeAbilityQuery", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(dispatcher, new object[] { handler, new EffectEventHandlerContext(f.State, ruleEvent, owner, null) });
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before));
            var returned = Value(result, "items").Items.Single(); Assert.That(Number(returned, "maxUnits"), Is.EqualTo(limit)); Assert.That(Number(returned, "quotaLimit"), Is.EqualTo(limit));
            if (file == "martin_concession") { Assert.That(Number(Value(returned, "unitResourceCost"), "GoldVoucher"), Is.EqualTo(1)); Assert.That(Text(returned, "quotaScope"), Is.EqualTo("build")); }
            else { Assert.That(Number(Value(returned, "itemCost"), "countPerUnit"), Is.EqualTo(1)); Assert.That(Text(Value(returned, "itemCost"), "destinationKind"), Is.EqualTo("original_supply")); }
            f.Artifacts.WriteLabeledText("实际Lua纯费用规则", result.ToDeterministicString());
        }
        [Test] public void TwoIndependentSourcesCommitOneVoucherAndTwoRealMarkersExactlyOnce()
        {
            var n = OpenPayment(); Set(n, 0, new ResourceSet { Originium = 1 }); Set(n, 1, new ResourceSet { Originium = 1, Iron = 1 }, markerIds);
            Assert.That(ResourcePaymentPlanService.TryBuild(f.State, f.Registry, n, out var plan, out var reason), Is.True, reason);
            Assert.That(plan.FinalCost.GoldVoucher, Is.EqualTo(1)); Assert.That(plan.FinalCost.Originium + plan.FinalCost.Iron, Is.Zero);
            var tokenPlan = plan.TokenPlans.Single();
            Assert.That(Number(tokenPlan.Arguments, "apiVersion"), Is.EqualTo(3));
            Assert.That(Number(tokenPlan.Arguments, "requiredCount"), Is.EqualTo(2));
            Assert.That(tokenPlan.Request.Transfers.SelectMany(t => t.ItemIds), Is.EquivalentTo(markerIds));
            Assert.That(tokenPlan.Request.Transfers.All(t => t.ExpectedSource.ZoneId == markerZone && t.RequiredCount == t.ItemIds.Count), Is.True);
            int gold = f.State.FindPlayer(1).Resources.GoldVoucher; string requestId = f.Open().GetStableInteractionId(); int requestRevision = f.Open().StateRevision; f.Answer("pay");
            n = f.Engine.GetNode(n.EffectId); Assert.That(n.Payment.SourceReceipts.Count, Is.EqualTo(2)); Assert.That(n.Payment.IsCommitted, Is.True);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold - 1));
            foreach (var receipt in n.Payment.SourceReceipts) foreach (string id in receipt.ItemIds) Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == id).ZoneId, Is.EqualTo(f.State.ItemZones.Items.Single(i => i.ItemId == id).OriginalSupplyZoneId));
            AssertTokenReceipt(n, markerIds);
            var before = JsonConvert.SerializeObject(f.State); f.Drain(); Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before));
            Assert.That(f.Engine.TrySubmitInteraction(requestId, 1, requestRevision, NormalizedValue.CreateString("pay"), out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before));
            f.Restore("多来源已付恢复"); Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold - 1));
            AssertTokenReceipt(f.Engine.GetNode(n.EffectId), markerIds);
        }
        private void AssertTokenReceipt(EffectNodeRuntimeState owner, IReadOnlyList<string> expectedIds)
        {
            var receipt = owner.Payment.SourceReceipts.Single(r => r.Rule.ItemCost != null);
            Assert.That(receipt.ProtocolVersion, Is.EqualTo(2));
            Assert.That(receipt.ItemIds, Is.EquivalentTo(expectedIds));
            Assert.That(Number(receipt.TokenArguments, "requiredCount"), Is.EqualTo(expectedIds.Count));
            Assert.That(receipt.TokenTransfer.Request.ProtocolVersion, Is.EqualTo(3));
            Assert.That(receipt.TokenTransfer.Request.Transfers.SelectMany(t => t.ItemIds), Is.EquivalentTo(expectedIds));
            Assert.That(ItemTransferService.TryValidateTokenCommit(f.State, 1, receipt.TokenArguments, receipt.TokenTransfer, out string reason), Is.True, reason);
            Assert.That(owner.Payment.FinalReceipt.CostReceiptPolicyVersion, Is.EqualTo(ResourcePaymentSourceService.TokenCostContractVersion));
        }
        [TestCase(false,false)][TestCase(true,false)][TestCase(false,true)]
        public void EachSourceAlsoWorksAloneWithoutBorrowingTheOthersBudget(bool bank, bool markers)
        {
            var n = OpenPayment();
            if (bank) Set(n, 0, new ResourceSet { Originium = 1 });
            if (markers) Set(n, 1, new ResourceSet { Originium = 1, Iron = 1 }, markerIds);
            Assert.That(ResourcePaymentPlanService.TryBuild(f.State, f.Registry, n, out var plan, out var reason), Is.True, reason);
            Assert.That(plan.FinalCost.GoldVoucher, Is.EqualTo(bank ? 1 : 0)); Assert.That(plan.TokenPlans.Count, Is.EqualTo(markers ? 1 : 0));
            f.Answer("pay"); Assert.That(f.Engine.GetNode(n.EffectId).Payment.SourceReceipts.Count, Is.EqualTo((bank ? 1 : 0) + (markers ? 1 : 0)));
            if (markers) AssertTokenReceipt(f.Engine.GetNode(n.EffectId), markerIds);
            else Assert.That(f.Engine.GetNode(n.EffectId).Payment.SourceReceipts.All(r => r.TokenTransfer == null), Is.True);
        }
        [Test] public void SourceFeeCannotBeSubstitutedAndRepeatedPhysicalMarkerCannotBeSpentTwice()
        {
            var n = OpenPayment(); Set(n, 0, new ResourceSet { Originium = 1 }); var r = RuleAt(1);
            Assert.That(ResourcePaymentDraftService.TrySetSourceDraft(f.State, f.Registry, n, r.SourceRef.SourceKey, r.RuleId, 1, 0, new ResourceSet { GoldVoucher = 1 }, markerIds.Take(1).ToList(), "", out _), Is.False);
            Assert.That(ResourcePaymentDraftService.TrySetSourceDraft(f.State, f.Registry, n, r.SourceRef.SourceKey, r.RuleId, 1, 0, new ResourceSet { Originium = 1, Iron = 1 }, new[] { markerIds[0], markerIds[0] }, "", out _), Is.False);
            Assert.That(n.Payment.IsCommitted, Is.False); Assert.That(n.Payment.SourceReceipts, Is.Empty);
        }
        [Test] public void OverlapAndInsufficientSourceFeeLeaveWholePaymentUnpaid()
        {
            var n = OpenPayment(); Set(n, 1, new ResourceSet { Originium = 2 }, markerIds); var r = RuleAt(0);
            Assert.That(ResourcePaymentDraftService.TrySetSourceDraft(f.State, f.Registry, n, r.SourceRef.SourceKey, r.RuleId, 1, 0, new ResourceSet { Originium = 1 }, Array.Empty<string>(), "", out var reason), Is.False); Assert.That(reason, Is.EqualTo("payment_substitution_overlap"));
            Set(n, 0, new ResourceSet { Iron = 1 }); f.State.FindPlayer(1).Resources.GoldVoucher = 0;
            string before = JsonConvert.SerializeObject(f.State);
            Assert.That(ResourcePaymentPlanService.TryBuild(f.State, f.Registry, n, out _, out reason), Is.False); Assert.That(reason, Is.EqualTo("insufficient_resource"));
            Assert.That(n.Payment.IsCommitted, Is.False); Assert.That(markerIds.All(id => f.State.ItemZones.Items.Single(i => i.ItemId == id).ZoneId == markerZone), Is.True);
            Assert.That(n.Payment.SourceReceipts, Is.Empty); Assert.That(n.Payment.FinalReceipt, Is.Null);
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before), "整笔预检失败不扣任何来源token或资源");
        }
        [Test] public void CancelEditAndWithdrawOneSourceKeepTheOtherDraft()
        {
            var n = OpenPayment(); Set(n, 0, new ResourceSet { Iron = 1 }); Set(n, 1, new ResourceSet { Originium = 2 }, markerIds); var r = RuleAt(0);
            string other = JsonConvert.SerializeObject(n.Payment.SourceDrafts.Single(d => d.Rule.RuleId == "test.rule_1"));
            Assert.That(ResourcePaymentDraftService.TryBeginEdit(f.State, f.Registry, n, r.SourceRef.SourceKey, r.RuleId, 1, out var edit, out var reason), Is.True, reason);
            Assert.That(ResourcePaymentDraftService.TryCancelEdit(n, edit.EditId, out reason), Is.True, reason);
            Assert.That(ResourcePaymentDraftService.TryWithdrawSource(n, r.SourceRef.SourceKey, r.RuleId, 1, 1, out reason), Is.True, reason);
            Assert.That(JsonConvert.SerializeObject(n.Payment.SourceDrafts.Single(d => d.Rule.RuleId == "test.rule_1")), Is.EqualTo(other)); Assert.That(n.Payment.IsCommitted, Is.False);
        }
        [Test] public void ChangedQuoteRequiresExplicitReselectionAndOldGenerationCannotWrite()
        {
            var n = OpenPayment(); Set(n, 0, new ResourceSet { Iron = 1 });
            Assert.That(ResourcePaymentDraftService.TryChangeFixedQuote(f.State, f.Registry, n, new ResourceSet { GoldVoucher = 3 }, 1, out var reason), Is.True, reason);
            Assert.That(n.Payment.PaymentGeneration, Is.EqualTo(2)); Assert.That(n.Payment.SourceDrafts.Single().Status, Is.EqualTo("reselect_required"));
            Assert.That(ResourcePaymentPlanService.TryBuild(f.State, f.Registry, n, out _, out _), Is.False);
            var r = RuleAt(0); Assert.That(ResourcePaymentDraftService.TryWithdrawSource(n, r.SourceRef.SourceKey, r.RuleId, 1, 2, out _), Is.False);
            Assert.That(ResourcePaymentDraftService.TryWithdrawSource(n, r.SourceRef.SourceKey, r.RuleId, 2, 2, out reason), Is.True, reason);
        }
        [Test] public void SourceLeavesItsAuthorizedZoneAndCannotBePaidFromStaleDraft()
        {
            var n = OpenPayment(); Set(n, 0, new ResourceSet { Iron = 1 }); var item = f.State.ItemZones.Items.Single(i => i.ItemId == RuleAt(0).SourceRef.ItemId);
            typeof(ItemZoneStateMigrator).GetMethod("SetPosition", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { f.State, item, FindZone(f.State, "discard", 1).ZoneId });
            Assert.That(ResourcePaymentPlanService.TryBuild(f.State, f.Registry, n, out _, out var reason), Is.False); Assert.That(reason, Is.EqualTo("payment_substitution_source_changed"));
            Assert.That(n.Payment.SourceReceipts, Is.Empty);
        }
        [Test] public void WithdrawnSourceTracksRepeatedTotalChangesAndCanBeEditedAfterRecovery()
        {
            var n = OpenTotalPayment();
            n = SourceCommand(n, 0, "save_source", new ResourceSet { Iron = 1 });
            n = SourceCommand(n, 1, "save_source", new ResourceSet { Iron = 2 }, markerIds);
            n = SourceCommand(n, 0, "withdraw_source");
            var oldQuery = CurrentQuery(n); string events = JsonConvert.SerializeObject(f.State.EffectRuntime.RuleEvents);
            string beforeReceipts = JsonConvert.SerializeObject(n.Payment.BeforePaymentReceipts); int gold = f.State.FindPlayer(1).Resources.GoldVoucher;
            n = ChangeTotal(n, new ResourceSet { Originium = 1, Iron = 2 });
            n = ChangeTotal(n, new ResourceSet { OriginiumShard = 1, Iron = 2 });
            var tombstone = n.Payment.SourceDrafts.Single(d => d.Rule.RuleId == "test.rule_0");
            Assert.That(tombstone.Status, Is.EqualTo("withdrawn")); Assert.That(tombstone.PaymentGeneration, Is.EqualTo(n.Payment.PaymentGeneration));
            Assert.That(tombstone.DraftRevision, Is.EqualTo(4)); Assert.That(PaymentResources.Total(tombstone.Allocation), Is.Zero); Assert.That(tombstone.SelectedItemIds, Is.Empty);
            var other = n.Payment.SourceDrafts.Single(d => d.Rule.RuleId == "test.rule_1");
            Assert.That(other.Status, Is.EqualTo("reselect_required")); Assert.That(other.Allocation.Iron, Is.EqualTo(2)); Assert.That(other.SelectedItemIds, Is.EqualTo(markerIds));
            Assert.That(ResourcePaymentPlanService.TryBuild(f.State, f.Registry, n, out _, out _), Is.False, "新代数保留事实但必须明确重选");
            Assert.That(ResourcePaymentRecovery.TryValidateNode(f.State, f.Registry, n, out var reason), Is.True, reason);
            var corrupt = GameStateCloneService.DeepClone(f.State); var corruptNode = corrupt.EffectRuntime.EffectNodes.Single(v => v.EffectId == n.EffectId);
            var corruptTombstone = corruptNode.Payment.SourceDrafts.Single(d => d.Status == "withdrawn"); corruptTombstone.PaymentGeneration--;
            Assert.That(ResourcePaymentRecovery.TryValidateNode(corrupt, f.Registry, corruptNode, out _), Is.False);
            corruptTombstone.PaymentGeneration++; corruptTombstone.Allocation.Iron = 1;
            Assert.That(ResourcePaymentRecovery.TryValidateNode(corrupt, f.Registry, corruptNode, out _), Is.False);
            f.Restore("R3撤回墓碑多次改配比恢复"); n = f.Engine.GetNode(n.EffectId);
            Assert.That(f.Engine.TryQueryPaymentSources(oldQuery, out _, out _), Is.False);
            var r = RuleAt(0); var request = f.Open(); int revision = f.State.EffectRuntime.StateRevision;
            Assert.That(f.Engine.TryEditPaymentSource(1, n.EffectId, request.GetStableInteractionId(), "withdraw_source", r.SourceRef.SourceKey, r.RuleId, "",
                1, 4, new ResourceSet(), Array.Empty<string>(), revision, "R3旧代数不可写", out _), Is.False);
            Assert.That(f.Engine.TryEditPaymentSource(1, n.EffectId, request.GetStableInteractionId(), "save_source", r.SourceRef.SourceKey, r.RuleId, "",
                n.Payment.PaymentGeneration, 2, new ResourceSet { OriginiumShard = 1 }, Array.Empty<string>(), revision, "R3旧墓碑版本不可写", out _), Is.False);
            n = SourceCommand(n, 0, "begin_source_edit"); var edit = ReadSources(n).Edit;
            Assert.That(edit.EnteredDraftRevision, Is.EqualTo(4)); Assert.That(n.Payment.SourceEdits.Last().PreviousDraft.Status, Is.EqualTo("withdrawn"));
            n = SourceCommand(n, 0, "cancel_source_edit", editId:edit.EditId);
            Assert.That(n.Payment.SourceDrafts.Single(d => d.Rule.RuleId == r.RuleId).Status, Is.EqualTo("withdrawn"));
            n = SourceCommand(n, 0, "begin_source_edit"); edit = ReadSources(n).Edit;
            n = SourceCommand(n, 0, "save_source", new ResourceSet { OriginiumShard = 1 }, editId:edit.EditId);
            Assert.That(n.Payment.SourceDrafts.Single(d => d.Rule.RuleId == r.RuleId).DraftRevision, Is.EqualTo(5));
            Assert.That(n.Payment.SourceDrafts.Single(d => d.Rule.RuleId == "test.rule_1").SelectedItemIds, Is.EqualTo(markerIds));
            Assert.That(JsonConvert.SerializeObject(f.State.EffectRuntime.RuleEvents), Is.EqualTo(events));
            Assert.That(JsonConvert.SerializeObject(n.Payment.BeforePaymentReceipts), Is.EqualTo(beforeReceipts));
            Assert.That(n.Payment.IsCommitted, Is.False); Assert.That(n.Payment.SourceReceipts, Is.Empty);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold));
            Assert.That(markerIds.All(id => f.State.ItemZones.Items.Single(i => i.ItemId == id).ZoneId == markerZone), Is.True);
            // 有效草稿也继续拒绝旧代数，不降低原协议约束。
            var bad = GameStateCloneService.DeepClone(f.State); var badNode = bad.EffectRuntime.EffectNodes.Single(v => v.EffectId == n.EffectId);
            badNode.Payment.SourceDrafts[0].PaymentGeneration--;
            Assert.That(ResourcePaymentRecovery.TryValidateNode(bad, f.Registry, badNode, out _), Is.False);
        }
        [Test] public void WithdrawnSourceSurvivesSameBuildResourceGoldResourceChanges()
        {
            f.Run(RefillSpec(1, FindZone(f.State, "facility_supply_0").ZoneId));
            string target = new ItemZoneQuery().GetItemIds(f.State, FindZone(f.State, "facility_supply_0").ZoneId).Single();
            f.Run(EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId, Object("sourceZone", NormalizedValue.CreateString("supply"), "allowDecline", NormalizedValue.CreateBoolean(true)), 1));
            var selection = f.Open();
            Assert.That(f.Engine.TryPrepareEffectBuildPayment(1, selection.OwnerEffectId, selection.GetStableInteractionId(), selection.CandidateSetId, selection.CandidateSetVersion, target,
                Object("expectedSourceZoneId", NormalizedValue.CreateString(FindZone(f.State, "facility_supply_0").ZoneId), "paymentMode", NormalizedValue.CreateString("resources")),
                f.State.EffectRuntime.StateRevision, "R3同Build正式付款准备", out var reason), Is.True, reason);
            var n = f.Engine.GetNode(f.Open().OwnerEffectId); string paymentId = n.Payment.PaymentId; string build = ResourcePaymentSourceService.BuildIdentity(f.State, n);
            rules = ResourcePaymentSourceService.Query(f.State, f.Registry, n, n.Payment.PaymentCost).ToList();
            Assert.That(build, Is.Not.Empty); Assert.That(n.Payment.PaymentCost.Iron, Is.GreaterThanOrEqualTo(1));
            n = SourceCommand(n, 0, "save_source", new ResourceSet { Iron = 1 }); n = SourceCommand(n, 0, "withdraw_source");
            string events = JsonConvert.SerializeObject(f.State.EffectRuntime.RuleEvents); int gold = f.State.FindPlayer(1).Resources.GoldVoucher;
            foreach (string mode in new[] { "gold", "resources", "gold" })
            {
                var request = f.Open();
                Assert.That(f.Engine.TryChangePreparedPaymentMode(1, n.EffectId, request.GetStableInteractionId(), mode, n.Payment.PaymentGeneration,
                    f.State.EffectRuntime.StateRevision, Guid.NewGuid().ToString("N"), out reason), Is.True, reason);
                n = f.Engine.GetNode(n.EffectId); var withdrawn = n.Payment.SourceDrafts.Single();
                Assert.That(withdrawn.Status, Is.EqualTo("withdrawn")); Assert.That(withdrawn.PaymentGeneration, Is.EqualTo(n.Payment.PaymentGeneration));
                Assert.That(ResourcePaymentRecovery.TryValidateNode(f.State, f.Registry, n, out reason), Is.True, reason);
                Assert.That(ResourcePaymentSourceService.BuildIdentity(f.State, n), Is.EqualTo(build)); Assert.That(n.Payment.PaymentId, Is.EqualTo(paymentId));
            }
            f.Restore("R3同Build资源金券反复切换后墓碑恢复"); n = f.Engine.GetNode(n.EffectId);
            Assert.That(n.Payment.SourceDrafts.Single().DraftRevision, Is.EqualTo(5)); Assert.That(n.Payment.IsCommitted, Is.False);
            Assert.That(JsonConvert.SerializeObject(f.State.EffectRuntime.RuleEvents), Is.EqualTo(events));
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold)); Assert.That(f.State.Map.Facilities, Is.Empty);
        }
        [TestCase(false)][TestCase(true)]
        public void TwoSourceFormalRequestAndEditRemainValidForFixedAndTotal(bool total)
        {
            var n = total ? OpenTotalPayment() : OpenPayment();
            Assert.That(NormalizedValue.CreateArray(Enumerable.Range(0, NormalizedValue.MaxElements).Select(i => NormalizedValue.CreateInteger(i)).ToList()).IsValid(), Is.False,
                "整棵树计数仍包含根，不放松旧的NormalizedValue校验");
            Assert.That(f.Open().PromptParameters.IsValid(), Is.True); Assert.That(Elements(f.Open().PromptParameters), Is.LessThanOrEqualTo(NormalizedValue.MaxElements));
            Assert.That(Value(f.Open().PromptParameters, "paymentSources"), Is.Null); Assert.That(Value(f.Open().PromptParameters, "paymentEdit"), Is.Null);
            var view = ReadSources(n); Assert.That(view.Sources.Single(s => s.Rule.RuleId == "test.rule_1").AvailableItems.Select(i => i.ItemId), Is.EqualTo(markerIds));
            var oldQuery = CurrentQuery(n); n = SourceCommand(n, 1, "begin_source_edit"); view = ReadSources(n);
            Assert.That(view.Edit, Is.Not.Null); Assert.That(view.Edit.SourceKey, Is.EqualTo(RuleAt(1).SourceRef.SourceKey));
            Assert.That(f.Open().PromptParameters.IsValid(), Is.True); Assert.That(f.Engine.TryQueryPaymentSources(oldQuery, out _, out _), Is.False);
            f.Restore(total ? "R3合计双来源编辑恢复" : "R3固定双来源编辑恢复"); n = f.Engine.GetNode(n.EffectId);
            Assert.That(ReadSources(n).Edit.EditId, Is.EqualTo(view.Edit.EditId));
            n = SourceCommand(n, 1, "cancel_source_edit", editId:view.Edit.EditId);
            Assert.That(ReadSources(n).Edit, Is.Null); Assert.That(f.Open().PromptParameters.IsValid(), Is.True); Assert.That(n.Payment.IsCommitted, Is.False);
        }
        [TestCase(false)][TestCase(true)]
        public void MixedGroupsExposeEveryLegalSelectionAndRequireExplicitChoice(bool total)
        {
            var extra = f.State.ItemZones.Items.First(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1 && i.ZoneId != markerZone);
            typeof(ItemZoneStateMigrator).GetMethod("SetPosition", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { f.State, extra, markerZone });
            // 真实实例的不同LocalState属于不同等价组；全部仍经过同一载体许可。
            extra.LocalState = Object("amount", NormalizedValue.CreateInteger(1));
            var n = total ? OpenTotalPayment() : OpenPayment(); var view = ReadSources(n); var row = view.Sources.Single(s => s.Rule.RuleId == "test.rule_1");
            Assert.That(row.AvailableItems.Count, Is.EqualTo(3)); Assert.That(row.AvailableItems.Select(i => i.SelectionGroupId).Distinct().Count(), Is.EqualTo(2));
            var r = RuleAt(1); var request = f.Open(); string before = JsonConvert.SerializeObject(f.State);
            Assert.That(f.Engine.TryEditPaymentSource(1, n.EffectId, request.GetStableInteractionId(), "save_source", r.SourceRef.SourceKey, r.RuleId, "", n.Payment.PaymentGeneration,
                0, new ResourceSet { Iron = 1 }, Array.Empty<string>(), f.State.EffectRuntime.StateRevision, "R3禁止自动选混合组", out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("payment_token_selection_required")); Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before));
            var all = row.AvailableItems.Select(i => i.ItemId).ToList();
            for (int a = 0; a < all.Count; a++) for (int b = a + 1; b < all.Count; b++)
            {
                var selected = new[] { all[a], all[b] }; n = SourceCommand(n, 1, "save_source", total ? new ResourceSet { Iron = 2 } : new ResourceSet { Originium = 1, Iron = 1 }, selected);
                // total当前为3铁；为两枚标记分配两份铁，固定付款使用1铁+1源岩。
                Assert.That(ReadSources(n).Sources.Single(s => s.Rule.RuleId == r.RuleId).SelectedItemIds, Is.EqualTo(selected));
                Assert.That(f.Open().PromptParameters.IsValid(), Is.True);
            }
            Assert.That(n.Payment.IsCommitted, Is.False); Assert.That(f.State.ItemZones.Items.Count(i => i.ZoneId == markerZone), Is.EqualTo(3));
        }
        [TestCase(false)][TestCase(true)][Property("paymentMarkerInventory", 256)]
        public void LargeInventoryIsCompleteOutsideTheBoundedFormalPrompt(bool total)
        {
            var inventory = f.State.ItemZones.Items.Where(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1).OrderBy(i => i.ItemId, StringComparer.Ordinal).ToList();
            for (int i = 0; i < inventory.Count; i++)
            {
                typeof(ItemZoneStateMigrator).GetMethod("SetPosition", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { f.State, inventory[i], markerZone });
                inventory[i].LocalState = Object("amount", NormalizedValue.CreateInteger(i));
            }
            Assert.That(inventory.Count, Is.GreaterThan(NormalizedValue.MaxElements));
            var n = total ? OpenTotalPayment() : OpenPayment(); int nodes = Elements(f.Open().PromptParameters); var view = ReadSources(n);
            var row = view.Sources.Single(s => s.Rule.RuleId == "test.rule_1");
            Assert.That(row.AvailableItems.Select(i => i.ItemId), Is.EqualTo(inventory.Select(i => i.ItemId))); Assert.That(row.AvailableItems.All(i => !string.IsNullOrEmpty(i.OriginalSupplyZoneId)), Is.True);
            Assert.That(row.AvailableItems.Select(i => i.SelectionGroupId).Distinct().Count(), Is.EqualTo(inventory.Count));
            Assert.That(f.Open().PromptParameters.IsValid(), Is.True); Assert.That(nodes, Is.LessThanOrEqualTo(NormalizedValue.MaxElements));
            n = SourceCommand(n, 1, "begin_source_edit"); view = ReadSources(n);
            Assert.That(Elements(f.Open().PromptParameters), Is.LessThanOrEqualTo(NormalizedValue.MaxElements));
            Assert.That(Value(f.Open().PromptParameters, "paymentSources"), Is.Null); Assert.That(Value(f.Open().PromptParameters, "paymentEdit"), Is.Null);
            Assert.That(view.Sources.Single(s => s.Rule.RuleId == "test.rule_1").AvailableItems.Count, Is.EqualTo(inventory.Count));
            string editId = view.Edit.EditId; var selected = new[] { inventory[0].ItemId, inventory.Last().ItemId };
            n = SourceCommand(n, 1, "save_source", total ? new ResourceSet { Iron = 2 } : new ResourceSet { Originium = 1, Iron = 1 }, selected, editId);
            Assert.That(ReadSources(n).Sources.Single(s => s.Rule.RuleId == "test.rule_1").SelectedItemIds, Is.EqualTo(selected));
            Assert.That(f.State.ItemZones.Items.Count(i => i.ZoneId == markerZone), Is.EqualTo(inventory.Count)); Assert.That(n.Payment.IsCommitted, Is.False);
            f.Restore(total ? "R3大库存合计完整明细恢复" : "R3大库存固定完整明细恢复"); n = f.Engine.GetNode(n.EffectId);
            Assert.That(ReadSources(n).Sources.Single(s => s.Rule.RuleId == "test.rule_1").AvailableItems.Count, Is.EqualTo(inventory.Count));
            f.Artifacts.WriteLabeledText("R3有界请求与完整库存", JsonConvert.SerializeObject(new { requestNodes = nodes, view }));
        }
        [Test] public void SourceReadQueryRejectsWrongIdentityRevisionGenerationAndChangedInventory()
        {
            var n = OpenTotalPayment(); var query = CurrentQuery(n); string before = JsonConvert.SerializeObject(f.State);
            foreach (var bad in Enumerable.Range(0, 7).Select(i => { var q = GameStateCloneService.DeepClone(query);
                if (i == 0) q.PlayerId = 2; if (i == 1) q.StateRevision--; if (i == 2) q.PaymentGeneration--; if (i == 3) q.InteractionId = "wrong"; if (i == 4) q.SnapshotId = "wrong";
                if (i == 5) q.SourceIndex = 2; if (i == 6) q.ItemOffset = -1; return q; }))
                Assert.That(f.Engine.TryQueryPaymentSources(bad, out _, out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before));
            var view = ReadSources(n); view.Sources.Single(s => s.Rule.RuleId == "test.rule_1").AvailableItems[0].LocalState.Properties.Add(
                new NormalizedValueEntry { Name = "clientOnly", Value = NormalizedValue.CreateBoolean(true) });
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before), "明细不能暴露可修改的权威状态引用");
            // 同一修订下的隔离篡改也须拒绝旧摘要；正式游戏状态变更仍通过正常提交推进修订。
            f.State.ItemZones.Items.Single(i => i.ItemId == markerIds[0]).LocalState = Object("isolatedChanged", NormalizedValue.CreateBoolean(true));
            Assert.That(f.Engine.TryQueryPaymentSources(query, out _, out var reason), Is.False); Assert.That(reason, Is.EqualTo("payment_source_query_stale"));
        }
        [TestCase("missing", false)] [TestCase("type", false)] [TestCase("one", false)] [TestCase("forged_preparation", false)]
        [TestCase("missing", true)] [TestCase("type", true)] [TestCase("one", true)] [TestCase("forged_preparation", true)]
        public void Qy007B2RealBuildCreatesV2InternalPaymentAndCannotRestoreWithoutItsProtocolProof(string damage, bool checkpoint)
        {
            var baseline = HostRecoveryService.CreateArchive(f.State);
            var source = FindZone(f.State, "facility_supply_0"); f.Run(RefillSpec(1, source.ZoneId));
            string target = new ItemZoneQuery().GetItemIds(f.State, source.ZoneId).Single();
            var build = f.Run(FacilityBuildEffectSpecFactory.Build(1, target, 0, "resources", false,
                "qy007.b2p.actual-build", false, source.ZoneId, "supply"));
            var payment = f.State.EffectRuntime.EffectNodes.Single(n => n.ParentEffectId == build.EffectId && n.Payment != null);
            Assert.That(payment.EffectTypeId, Is.EqualTo(FacilityBuildEffectExecutor.PaymentTypeId));
            Assert.That(Number(payment.NormalizedArguments, "paymentProtocolVersion"), Is.EqualTo(2));
            Assert.That(payment.Payment.PaymentProtocolVersion, Is.EqualTo(2));
            Assert.That(payment.Payment.Preparation, Is.Null); Assert.That(payment.Payment.IsCommitted, Is.False);
            if (checkpoint) HostRecoveryService.AppendJournal(baseline, f.State);
            string legal = HostRecoveryService.SerializeArchive(checkpoint ? baseline : HostRecoveryService.CreateArchive(f.State));
            f.Artifacts.WriteLabeledText("原Build内部付款2合法归档" + damage, legal);
            var control = HostRecoveryService.RecoverJson(legal, f.Registry); Assert.That(control.Succeeded, Is.True, control.Diagnostic);
            Assert.That(control.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == payment.EffectId).Payment.PaymentId, Is.EqualTo(payment.EffectId));
            var bad = JObject.Parse(legal);
            var raw = checkpoint ? (JObject)((JArray)bad["Journal"]["Entries"]).Last(e => e["StateAfter"] is JObject)["StateAfter"] : (JObject)bad["Snapshot"]["State"];
            var node = ((JArray)raw["EffectRuntime"]["EffectNodes"]).OfType<JObject>().Single(n => (string)n["EffectId"] == payment.EffectId);
            var props = (JArray)node["NormalizedArguments"]["Properties"];
            var marker = props.OfType<JObject>().Single(p => (string)p["Name"] == "paymentProtocolVersion");
            if (damage == "type") marker["Value"] = JObject.FromObject(NormalizedValue.CreateString("2"));
            else if (damage == "one") marker["Value"] = JObject.FromObject(NormalizedValue.CreateInteger(1));
            else
            {
                marker.Remove();
                if (damage == "forged_preparation") node["Payment"]["Preparation"] = JObject.FromObject(new PreparedActionPaymentContext {
                    AuthorizationKind = "unproven", PreparationOwnerEffectId = payment.EffectId, ActionKind = "build", TargetId = target,
                    ActionArguments = Object("expectedSourceZoneId", NormalizedValue.CreateString(source.ZoneId), "paymentMode", NormalizedValue.CreateString("resources")) });
            }
            string original = JsonConvert.SerializeObject(f.State);
            f.Artifacts.WriteLabeledText("内部付款标记或身份坏档" + damage, bad.ToString());
            var rejected = HostRecoveryService.RecoverJson(bad.ToString(), f.Registry);
            f.Artifacts.WriteLabeledText("内部付款坏档拒绝结果" + damage, JsonConvert.SerializeObject(new { rejected.Succeeded, rejected.Diagnostic }));
            Assert.That(rejected.Succeeded, Is.False, "没有真实Preparation身份的内部付款2不能跳过显式标记");
            Assert.That(rejected.Diagnostic, Is.Not.Empty); Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(original));
            Assert.That(f.Engine.GetNode(payment.EffectId).Payment.IsCommitted, Is.False);
        }
    }
}
