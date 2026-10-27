using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 隔离费用协议回归；不计正常对局、正式行动或页面验收。
    public sealed class ResourceQueryPlayerContractTests
    {
        private const string ScriptPath = "lua/city_styles/composite_power_system.lua";
        private const string ActionId = "special_action.city_style.composite_power_system";

        [TestCase("p1", 1)] [TestCase("1", 1)]
        [TestCase("P2", 2)] [TestCase("2", 2)]
        public void QueryAndNormalizedEffectPlayerReferencesResolveWithoutMutation(string reference, int expected)
        {
            var args = Demand(reference); string before = args.ToDeterministicString();
            Assert.That(ResourcePaymentDemandParser.TryParse(args, out var demand, out int player), Is.Empty);
            Assert.That(player, Is.EqualTo(expected)); AssertCompositeDemand(demand);
            Assert.That(args.ToDeterministicString(), Is.EqualTo(before));
        }

        [Test]
        public void InvalidPlayerReferencesAndFieldTypesRemainRejected()
        {
            var values = new[] { "", "p", "p0", "0", "-1", "p-1", "+1", "p+1", " 1", "p1 ",
                "p 1", "pp1", "p1.0", "1e0", "p2147483648", "2147483648", "p１" }
                .Select(NormalizedValue.CreateString).Concat(new[] { NormalizedValue.CreateNull(),
                    NormalizedValue.CreateInteger(1), NormalizedValue.CreateBoolean(true), Strings(new[] { "p1" }) });
            foreach (var value in values)
            {
                var args = Demand("p1"); Set(args, "player", value);
                Assert.That(ResourcePaymentDemandParser.TryParse(args, out var demand, out _),
                    Is.EqualTo("payment_total_arguments_invalid"), value.ToDeterministicString());
                Assert.That(demand, Is.Null);
            }
            var duplicate = Demand("p1");
            duplicate.Properties.Add(new NormalizedValueEntry { Name = "player", Value = NormalizedValue.CreateString("2") });
            Assert.That(ResourcePaymentDemandParser.TryParse(duplicate, out _, out _), Is.Not.Empty);
        }

        [Test]
        public void PlayerCompatibilityDoesNotRelaxResourceOrQuantityValidation()
        {
            var invalid = new[] {
                ("allowedResourceTypes", Strings(new[] { "Iron", "Iron" }), "payment_total_resource_types_invalid"),
                ("allowedResourceTypes", Strings(new[] { "iron" }), "payment_total_resource_types_invalid"),
                ("allowedResourceTypes", Strings(new[] { "total" }), "payment_total_resource_types_invalid"),
                ("amount", NormalizedValue.CreateInteger(-1), "payment_total_demand_invalid"),
                ("amount", NormalizedValue.CreateInteger(100), "payment_total_demand_invalid"),
                ("amount", NormalizedValue.CreateString("2.5"), "payment_total_demand_invalid"),
                ("cost", Object("OriginiumShard", NormalizedValue.CreateInteger(-1)), "payment_total_fixed_cost_invalid"),
                ("cost", Object("Unknown", NormalizedValue.CreateInteger(1)), "payment_total_fixed_cost_invalid") };
            foreach (var row in invalid)
            {
                var args = Demand("p1"); Set(args, row.Item1, row.Item2);
                Assert.That(ResourcePaymentDemandParser.TryParse(args, out _, out _), Is.EqualTo(row.Item3));
            }
            Assert.That(ResourcePaymentDemandParser.TryParse(Demand("p1"), out var demand, out _), Is.Empty);
            foreach (var allocation in new[] { new ResourceSet { Iron = 2 }, new ResourceSet { Iron = 4 },
                new ResourceSet { Iron = 2, OriginiumShard = 1 } })
                Assert.That(ResourcePaymentAllocationService.TryValidate(demand, allocation, out _, out _), Is.False);
        }

        [TestCase("p1", true)] [TestCase("1", true)]
        [TestCase("p2", false)] [TestCase("2", false)] [TestCase("p999", false)]
        public void RegisteredQueryChecksTheActualAuthorizedPlayer(string returnedPlayer, bool accepted)
        {
            using var f = new PaymentTestFixture();
            const string handler = "test.qy007.payment.demand";
            var declaration = new AbilitySourceDeclaration { DefinitionId = "test.payment.source_0",
                AbilityId = handler, HandlerRef = handler, DeclarationVersion = "1.0.0", Kind = "ruleQuery",
                AuthorizationPolicy = "controller", EffectiveZones = new List<string> { FindZone(f.State, "hand", 1).TemplateId },
                QueryKinds = new List<string> { ResourcePaymentCostPreview.DemandQueryKind }, PresentationRef = "test.payment.source_0" };
            f.Registry.RegisterAbilityHandler(handler, "test.qy007.payment.demand/1", _ => Array.Empty<EffectSpec>());
            f.Registry.RegisterAbilityPureHandler(handler, (call, query) =>
            {
                var value = InvokeActualLua(f, "p" + call.SourceRef.AuthorizedPlayerId, query).AbilityQueryResult;
                // 仅隔离攻击输入：改变返回身份，必须由生产查询合同拒绝，不能替换调用者权限。
                Set(Value(value, "items").Items.Single(), "player", NormalizedValue.CreateString(returnedPlayer));
                return value;
            });
            f.Registry.FreezeAbilityCatalog(new[] { declaration });
            f.State.ContentDefinitions.AbilitySources = new List<AbilitySourceDeclaration> { declaration };
            ResourcePaymentSourceService.Register(f.Registry);
            var source = f.Registry.QuerySources(f.State, 1, "ruleQuery").Single();
            var q = Query(f); q.SourceRef = source;
            string before = JsonConvert.SerializeObject(f.State);
            if (accepted)
            {
                var result = f.Registry.QueryRules(f.State, q).Single();
                Assert.That(result.SourceRef.SourceKey, Is.EqualTo(source.SourceKey));
                Assert.That(result.SourceRef.AuthorizedPlayerId, Is.EqualTo(1));
                Assert.That(ResourcePaymentDemandParser.TryParse(result.Items.Items.Single(), out var demand, out int player), Is.Empty);
                Assert.That(player, Is.EqualTo(1)); AssertCompositeDemand(demand);
            }
            else Assert.That(Assert.Throws<InvalidOperationException>(() => f.Registry.QueryRules(f.State, q)).Message,
                Is.EqualTo("ability_query_return_invalid:payment_permission_denied"));
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before), "查询不得生成付款、消耗或修改状态。");
        }

        [Test]
        public void ActualLuaQueryAndEffectConstructionDeclareTheSameCompositeCost()
        {
            using var f = new PaymentTestFixture();
            var query = InvokeActualLua(f, "p1", Query(f));
            var raw = Value(query.AbilityQueryResult, "items").Items.Single();
            Assert.That(Text(raw, "player"), Is.EqualTo("p1"));
            Assert.That(ResourcePaymentDemandParser.TryParse(raw, out var demand, out int player), Is.Empty);
            Assert.That(player, Is.EqualTo(1)); AssertCompositeDemand(demand);
            var body = InvokeActualLua(f, "p1", null).Effects.Single();
            var payment = body.NestedEffectGroups[0].Single();
            Assert.That(payment.EffectTypeId, Is.EqualTo(ResourceEffectTypeIds.Pay));
            Assert.That(Text(payment.RawArguments, "player"), Is.EqualTo("1"));
            Assert.That(ResourcePaymentDemandParser.TryParse(payment.RawArguments, out var effectDemand, out int effectPlayer), Is.Empty);
            Assert.That(effectPlayer, Is.EqualTo(player));
            Assert.That(JsonConvert.SerializeObject(effectDemand), Is.EqualTo(JsonConvert.SerializeObject(demand)));
            var move = body.NestedEffectGroups[1].Single();
            Assert.That(move.EffectTypeId, Is.EqualTo("effect.city.move"));
            Assert.That(move.CompletionHandlerId, Is.EqualTo("city-styles.composite.after_move"));
            Assert.That(Value(move.RawArguments, "completionHandlerId"), Is.Null, "显式续接属于返回spec，不进入移动业务参数。");
            Assert.That(f.State.EffectRuntime.EffectNodes, Is.Empty, "构树不是正式行动提交。");
        }

        [TestCase("p1")] [TestCase("1")]
        public void OriginalPaymentSurvivesRecoveryAndCommitsOnlyOnce(string reference)
        {
            using var f = new PaymentTestFixture();
            var owner = f.Run(EffectSpec.Create(ResourceEffectTypeIds.Pay, Demand(reference), 1));
            string ownerId = owner.EffectId, paymentId = owner.Payment.PaymentId;
            var request = f.Open(); string requestId = request.GetStableInteractionId();
            var before = f.State.FindPlayer(1).Resources.Clone();
            Assert.That(f.Engine.TryPrepareTotalPaymentDraft(1, ownerId, requestId, owner.Payment.PaymentGeneration,
                new ResourceSet { Iron = 2, Originium = 1 }, f.State.EffectRuntime.StateRevision, "007合计费用草稿", out var reason), Is.True, reason);
            Assert.That(PaymentResources.Same(f.State.FindPlayer(1).Resources, before), Is.True);
            f.Restore("007原付款请求恢复"); owner = f.Engine.GetNode(ownerId);
            Assert.That(f.Open().GetStableInteractionId(), Is.EqualTo(requestId));
            Assert.That(owner.Payment.PaymentId, Is.EqualTo(paymentId));
            Assert.That(owner.Payment.OwnerEffectId, Is.EqualTo(ownerId));
            Assert.That(owner.Payment.IsCommitted, Is.False);
            Assert.That(PaymentResources.Same(owner.Payment.PaymentCost,
                new ResourceSet { Iron = 2, Originium = 1, OriginiumShard = 1 }), Is.True);
            string answer = PaymentResources.Candidate(owner.Payment.PaymentCost);
            f.Answer(answer);
            var expected = before.Clone(); expected.Iron -= 2; expected.Originium--; expected.OriginiumShard--;
            Assert.That(PaymentResources.Same(f.State.FindPlayer(1).Resources, expected), Is.True);
            Assert.That(f.Engine.GetNode(ownerId).Payment.IsCommitted, Is.True);
            f.Restore("007已付费用恢复");
            string receipt = JsonConvert.SerializeObject(f.Engine.GetNode(ownerId).Payment);
            Assert.That(f.Engine.TrySubmitInteraction(requestId, 1, f.State.EffectRuntime.StateRevision,
                NormalizedValue.CreateString(answer), out _), Is.False);
            f.Drain();
            Assert.That(PaymentResources.Same(f.State.FindPlayer(1).Resources, expected), Is.True);
            Assert.That(JsonConvert.SerializeObject(f.Engine.GetNode(ownerId).Payment), Is.EqualTo(receipt));
            Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => n.Payment != null), Is.EqualTo(1));
        }

        [TestCase("p2")] [TestCase("2")]
        public void PaymentCannotSpendAnotherPlayersResources(string reference)
        {
            using var f = new PaymentTestFixture();
            var before = f.State.Players.Select(p => JsonConvert.SerializeObject(p.Resources)).ToArray();
            var node = f.Run(EffectSpec.Create(ResourceEffectTypeIds.Pay, Demand(reference), 1));
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(node.Payment, Is.Null);
            Assert.That(f.State.EffectRuntime.InteractionRequests, Is.Empty);
            Assert.That(f.State.Players.Select(p => JsonConvert.SerializeObject(p.Resources)), Is.EqualTo(before));
        }

        [TestCase(false)] [TestCase(true)]
        public void InsufficientCompositeCostCannotPartPayOrOpenTheRightSide(bool missingFixedCost)
        {
            using var f = new PaymentTestFixture();
            var resources = f.State.FindPlayer(1).Resources;
            if (missingFixedCost) resources.OriginiumShard = 0;
            else { resources.Iron = 1; resources.Originium = 1; }
            var before = resources.Clone();
            var condition = EffectSpec.Condition(new[] { EffectSpec.Create(ResourceEffectTypeIds.Pay, Demand("p1"), 1) },
                new[] { ResourceEffectSpecFactory.GainScore(1, 7) }, ruleCondition: true); condition.PlayerId = 1;
            f.Run(condition);
            Assert.That(PaymentResources.Same(resources, before), Is.True);
            Assert.That(f.State.FindPlayer(1).Score, Is.Zero);
            Assert.That(f.State.EffectRuntime.InteractionRequests, Is.Empty);
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => n.Payment?.IsCommitted == true), Is.False);
        }

        private static NormalizedValue Demand(string player) => Object("apiVersion", NormalizedValue.CreateInteger(2),
            "player", NormalizedValue.CreateString(player), "resourceType", NormalizedValue.CreateString("total"),
            "allowedResourceTypes", Strings(new[] { "Originium", "Iron" }), "amount", NormalizedValue.CreateInteger(3),
            "cost", PaymentResources.Write(new ResourceSet { OriginiumShard = 1 }));
        private static void Set(NormalizedValue value, string field, NormalizedValue replacement) =>
            value.Properties.Single(p => p.Name == field).Value = replacement;
        private static void AssertCompositeDemand(ResourcePaymentDemandState demand)
        {
            Assert.That(demand.TotalCount, Is.EqualTo(3));
            Assert.That(demand.AllowedResourceTypes, Is.EquivalentTo(new[] { ResourceType.Originium, ResourceType.Iron }));
            Assert.That(PaymentResources.Same(demand.FixedCost, new ResourceSet { OriginiumShard = 1 }), Is.True);
        }
        private static AbilityQueryContext Query(PaymentTestFixture f) => new AbilityQueryContext {
            QueryId = "qy007.demand.query", QueryKind = ResourcePaymentCostPreview.DemandQueryKind, FactsDigest = "qy007.demand.facts",
            MapId = f.State.MapId, ContentVersion = f.Registry.ContentPackHash, SubjectPlayerId = 1,
            Facts = Object("actionId", NormalizedValue.CreateString(ActionId), "playerId", NormalizedValue.CreateInteger(1)) };
        private static LuaInvocationResult InvokeActualLua(PaymentTestFixture f, string player, AbilityQueryContext query)
        {
            var pack = ExternalContentPack.Load(f.PrivateContent);
            var content = pack.ActiveDefinitions.Single(d => d.DefinitionId == "city_style_composite_power_system");
            var handler = content.AbilityHandlers.Single(h => h.HandlerId == (query == null ? "city-styles.composite" : "city-styles.composite.demand"));
            string script = File.ReadAllText(Path.Combine(f.PrivateContent, handler.Script));
            Assert.That(handler.Script, Is.EqualTo(ScriptPath));
            var payload = query == null ? LuaSnapshotValue.Object(Array.Empty<LuaSnapshotEntry>()) : LuaSnapshotValue.Object(new[] {
                new LuaSnapshotEntry("queryId", LuaSnapshotValue.String(query.QueryId)),
                new LuaSnapshotEntry("queryKind", LuaSnapshotValue.String(query.QueryKind)),
                new LuaSnapshotEntry("basisDigest", LuaSnapshotValue.String(query.FactsDigest)),
                new LuaSnapshotEntry("facts", LuaSnapshotValue.Object(new[] { new LuaSnapshotEntry("actionId", LuaSnapshotValue.String(ActionId)) })) });
            var context = new LuaInvocationContext("qy007.demand.event", "test.resource_query", player, 0, handler.Version, 2, 0, 0,
                new LuaFacadeSnapshot { EventPayload = payload }) { ResponseKind = query == null ? "effects" : "abilityRuleQuery" };
            var result = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition(content.DefinitionId, "city_styles.composite",
                handler.HandlerId, handler.Version, script, LuaContentHasher.ComputeSha256(script)), context);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            f.Artifacts.WriteLabeledText("007实际Lua查询或构树", JsonConvert.SerializeObject(new { handler.HandlerId, handler.Version,
                sha256 = LuaContentHasher.ComputeSha256(script), context.ResponseKind, result.AbilityQueryResult }));
            return result;
        }
    }
}
