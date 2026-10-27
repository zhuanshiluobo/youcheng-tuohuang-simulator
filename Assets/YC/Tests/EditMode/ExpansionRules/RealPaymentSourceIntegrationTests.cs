using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 正式角色/奖励与通用原语组合的规则隔离；私有发放来源，不计正常游戏或正式企业家正文。
    public sealed class RealPaymentSourceIntegrationTests : FacilityLayerTestFixture
    {
        private const string Reward = "entrepreneur.award.M01", Grant = "qy007.real-source.grant";
        private GameState state;
        private string passenger;
        [TearDown] public void PreserveActualState() { if(state!=null) Evidence(state,"实际来源完整现场"); }
        [SetUp] public void AddPrivateGrantSource()
        {
            var token = JObject.Parse(File.ReadAllText(Path.Combine(Pack.RootPath, "component/" + Token + ".json")));
            ((JArray)token["abilitySources"]).Add(JObject.FromObject(new AbilitySourceDeclaration {
                AbilityId = Grant, Kind = "event", HandlerRef = Grant, DeclarationVersion = "1.0.0", AuthorizationPolicy = "owner",
                EffectiveZones = new List<string> { "core.zone.box_reserve" }, ObservedEvents = new List<string> { "RoundStarted" }, PresentationRef = Token,
                AllowedOperations = new List<string> { "reward.grant:" + Reward + ":core.award_card:1:other:core.zone.player_rewards" } }));
            ((JArray)token["abilityHandlers"]).Add(new JObject { ["handlerId"] = Grant, ["version"] = "1.0.0", ["script"] = "lua/qy007_real_source_grant.lua" });
            var reward = JObject.Parse(File.ReadAllText(Path.Combine(Pack.RootPath, "award/" + Reward + ".json")));
            reward["applicableModes"] = new JArray("standard"); reward["requiredCapabilities"] = new JArray();
            string root = artifacts.CopyContent(Pack.RootPath, "真实来源指定发放私有输入", new Dictionary<string,string> {
                ["component/" + Token + ".json"] = token.ToString(), ["award/" + Reward + ".json"] = reward.ToString(),
                ["lua/qy007_real_source_grant.lua"] = "return function(ctx) if ctx.playerId~='p2' then return {} end; return {Effect.GrantReward({apiVersion=1,player=ctx.playerId,definitionId='" + Reward + "',typeId='core.award_card',count=1,recipientPlayer='p1',destination='core.zone.player_rewards',operationId='give-p1'})} end" });
            Pack = ExternalContentPack.Load(root); artifacts.RecordContent("真实来源实际装载", root, Pack.ContentHash);
        }
        private EffectTreeExecutor Engine => new EffectTreeExecutor(state, Registry);
        private void Prepare()
        {
            state = State(extraRecipe: new[] { new ItemRecipeEntry { EntryId = "passenger", DefinitionId = "operator.passenger", TypeId = "core.operator_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" } });
            CharacterLifecycleService.Register(Registry);
            Registry.Register("qy007.real-source.round", c => EffectStepResult.Completed().AddEvent(new EffectEventRequest { EventType = "RoundStarted", PlayerId = 2, Payload = Object() }));
            passenger = state.ItemZones.Items.Single(i => i.DefinitionId == "operator.passenger" && i.ControllerSubject.PlayerId == 1).ItemId;
            Assert.That(Registry.QuerySources(state, 1, queryKind: ResourcePaymentSourceService.QueryKind), Is.Empty);
            Run(state, EffectSpec.Create("qy007.real-source.round", playerId: 2));
            var reward = state.ItemZones.Items.Single(i => i.DefinitionId == Reward);
            Assert.That(ContentLifecycleEffectExecutor.IsHeld(state, reward, 1), Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.RewardGrant != null).RewardGrant.RecipientPlayerId, Is.EqualTo(1));
            state = Recover(state, "真实M01指定发放持有恢复");
            Assert.That(Registry.QuerySources(state, 1, queryKind: ResourcePaymentSourceService.QueryKind).Count, Is.EqualTo(1));
            Run(state, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Answer(state, passenger);
            ContentPrimitiveEffectExecutor.Register(Registry,new YC.Domain.Maps.MapQueryService(Map),Registry.InfluenceService);
            var use=Run(state, CharacterLifecycleService.CreateUseSpec(1, passenger, "tactic"));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r=>r.Status=="open"),Is.True,use.FailureReason+":"+string.Join(";",state.EffectRuntime.EffectNodes.Where(n=>n.Status==EffectNodeStatus.Failed).Select(n=>n.EffectTypeId+":"+n.FailureReason)));
            Assert.That(Registry.QuerySources(state,1,queryKind:ResourcePaymentSourceService.QueryKind).Any(s=>s.AbilityId=="operator.passenger.payment"),Is.False,"首次装载未完成不能提前激活被链接的付款来源");
            Assert.That(Registry.QuerySources(state,1,"event").Any(s=>s.AbilityId=="operator.passenger.tactic"),Is.False,"载体装载证明不能提前开放持续事件来源");
            Answer(state, "2");
            Assert.That(CharacterLifecycleService.IsSubscriptionActive(state, passenger, "operator.passenger.tactic"), Is.True);
            Assert.That(Registry.QuerySources(state, 1, queryKind: ResourcePaymentSourceService.QueryKind).Select(s => s.AbilityId), Is.EquivalentTo(new[] { "operator.passenger.payment", "entrepreneur.award.M01.payment" }));
            state = Recover(state, "异客真实订阅装载两枚后恢复");
        }
        private EffectNodeRuntimeState OpenBuild(int slot, int substitution = 0)
        {
            string definition = Definition(state, d => !d.Unique && !d.HasEntryEffect && d.ResourceCost.Originium + d.ResourceCost.OriginiumShard + d.ResourceCost.Iron >= 3);
            if(substitution>0) {
                var minimum=state.ContentDefinitions.Facilities.Single(d=>d.FacilityId==definition).ResourceCost.Clone(); Take(minimum,substitution);
                minimum.GoldVoucher=200; state.FindPlayer(1).Resources=minimum;
            }
            var build = Run(state, Build(state, Card(state, definition).ItemId, slot, "resources"));
            var payment = Engine.GetNode(Open(state).OwnerEffectId);
            Assert.That(ResourcePaymentSourceService.BuildIdentity(state, payment), Is.EqualTo("build:" + build.EffectId));
            Assert.That(payment.Payment.SourceDrafts, Is.Empty);
            Assert.That(payment.SettlementWindow == null || payment.SettlementWindow.Entries.All(e => e.AbilityId != "operator.passenger.tactic"), Is.True, "protocol2不能双注册旧替代入口");
            return payment;
        }
        private static ResourceSet Take(ResourceSet remaining, int count)
        {
            var allocation = new ResourceSet();
            foreach (var type in new[] { ResourceType.Originium, ResourceType.OriginiumShard, ResourceType.Iron }) {
                int n = Math.Min(remaining.Get(type), count); allocation.Set(type, n); remaining.Set(type, remaining.Get(type)-n); count -= n;
            }
            Assert.That(count, Is.Zero); return allocation;
        }
        private void Set(EffectNodeRuntimeState payment, PaymentSourceRule rule, ResourceSet allocation, IReadOnlyList<string> ids)
        {
            Assert.That(ResourcePaymentDraftService.TrySetSourceDraft(state, Registry, payment, rule.SourceRef.SourceKey, rule.RuleId,
                payment.Payment.PaymentGeneration, 0, allocation, ids, "", out string reason), Is.True, reason);
        }
        [TestCase(true,false)] [TestCase(false,true)] [TestCase(true,true)]
        public void GrantedM01AndActivatedPassengerUseIndependentRealSourcesWithinOneBuild(bool bank, bool markers)
        {
            Prepare(); var payment = OpenBuild(0,(bank?1:0)+(markers?2:0)); var remaining = payment.Payment.PaymentCost.Clone();
            var rules = ResourcePaymentSourceService.Query(state, Registry, payment, payment.Payment.PaymentCost);
            var m = rules.Single(r => r.RuleId == "martin.concession"); var p = rules.Single(r => r.RuleId == "passenger.stored_markers");
            Assert.That(m.QuotaScope, Is.EqualTo("build")); Assert.That(m.MaxUnits, Is.EqualTo(1)); Assert.That(p.MaxUnits, Is.EqualTo(2));
            var markerIds = state.ItemZones.Items.Where(i => state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.ParentId == passenger && z.LocalZoneId == "markers")).Select(i => i.ItemId).ToArray();
            Assert.That(markerIds.Length, Is.EqualTo(2));
            var tooMany = Take(remaining.Clone(), 2);
            Assert.That(ResourcePaymentDraftService.TrySetSourceDraft(state, Registry, payment, m.SourceRef.SourceKey, m.RuleId, payment.Payment.PaymentGeneration, 0, tooMany, Array.Empty<string>(), "", out _), Is.False);
            if (bank) Set(payment, m, Take(remaining, 1), Array.Empty<string>());
            if (markers) Set(payment, p, Take(remaining, 2), markerIds);
            Assert.That(ResourcePaymentPlanService.TryBuild(state, Registry, payment, out var plan, out string reason), Is.True, reason);
            Assert.That(plan.FinalCost.GoldVoucher, Is.EqualTo(payment.Payment.PaymentCost.GoldVoucher + (bank ? 1 : 0)));
            var before = state.FindPlayer(1).Resources.Clone(); string paymentId = payment.Payment.PaymentId, owner = payment.EffectId;
            state = Recover(state, "真实来源联合未付恢复"); payment = Engine.GetNode(owner);
            Answer(state, "pay"); payment = Engine.GetNode(owner);
            Assert.That(payment.Payment.IsCommitted, Is.True); Assert.That(payment.Payment.PaymentId, Is.EqualTo(paymentId));
            Assert.That(payment.Payment.SourceReceipts.Count, Is.EqualTo((bank?1:0)+(markers?1:0)));
            foreach (var type in PaymentResources.Types) Assert.That(state.FindPlayer(1).Resources.Get(type), Is.EqualTo(before.Get(type)-plan.FinalCost.Get(type)));
            if (markers) {
                foreach (string id in markerIds) { var item = state.ItemZones.Items.Single(i => i.ItemId == id); Assert.That(item.ZoneId, Is.EqualTo(item.OriginalSupplyZoneId)); }
                Assert.That(state.ItemZones.Items.Single(i => i.ItemId == passenger).ZoneId, Is.EqualTo(FindZone(state,"discard",1).ZoneId));
                Assert.That(Registry.QuerySources(state, 1, queryKind: ResourcePaymentSourceService.QueryKind).Any(s => s.AbilityId == "operator.passenger.payment"), Is.False);
            }
            Assert.That(state.ItemZones.Items.Single(i => i.DefinitionId == Reward).ZoneId, Is.EqualTo(FindZone(state,"rewards",1).ZoneId));
            state = Recover(state, "真实来源联合已付恢复"); string digest = RuleStateDigestService.Compute(state);
            Assert.That(Engine.RunUntilQuiescent().Faulted, Is.False); Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(digest));
            foreach(var type in new[]{ResourceType.Originium,ResourceType.OriginiumShard,ResourceType.Iron}) Run(state,ResourceEffectSpecFactory.Gain(1,type,10,"qy007.isolated.second-build-funds"));
            var next = OpenBuild(1); var nextRule = ResourcePaymentSourceService.Query(state,Registry,next,next.Payment.PaymentCost).Single(r => r.RuleId == m.RuleId);
            Set(next,nextRule,Take(next.Payment.PaymentCost.Clone(),1),Array.Empty<string>());
            Assert.That(ResourcePaymentPlanService.TryBuild(state,Registry,next,out _,out reason),Is.True,reason);
            Answer(state,"pay"); state = Recover(state,"第二Build独立M01额度恢复");
        }
    }
}
