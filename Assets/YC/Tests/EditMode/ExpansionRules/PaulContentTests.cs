using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 正式Lua与真实宿主隔离测试源码；CONTENT-R2阶段未执行，不计正常覆盖。
    public sealed class PaulContentTests
    {
        private PaulContentTestFixture f;
        [TearDown] public void End() => f?.Dispose();
        // 原I01身份负例保留；R3沿真实SourceData/Dispatcher投影定位，不计正式选择/恢复或正常界面验收。
        private ItemInstanceState I01SelectedItem(bool departure)
        {
            var group = f.State.EntrepreneurSetup.Players.Single(p => p.PlayerId == 1).RoundGroups.Single(g => g.RoundNumber == (departure ? 1 : 3));
            return f.State.ItemZones.Items.Single(i => i.ItemId == group.Candidates.Single(c => c.CandidateId == group.SelectedCandidateId).EntityItemId);
        }
        private void I01AssertOtherGrantRemains(string targetItemId)
        {
            var sources = f.Registry.QuerySources(f.State, 1);
            Assert.That(sources.Any(x => x.ItemId == targetItemId), Is.False, "目标面须真正不再提供有效来源");
            Assert.That(sources.Single(x => x.ItemId == f.AbilityItem && x.AbilityId == "entrepreneur.P09.place").GrantingSourceRefs, Is.Not.Empty, "另一真实选面grant须仍然存在，不能靠空列表避开问题");
        }
        private static void I01AssertNoSharedGrant(LuaInvocationResult result, bool departure, bool invalidAbility = false)
        {
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            if (departure) Assert.That(result.Effects, Is.Empty);
            else if (invalidAbility) Assert.That(result.RewriteOperation, Is.EqualTo("keep"));
            else { Assert.That(result.RewriteOperation, Is.EqualTo("replace")); Assert.That(result.Effects.Single().EffectTypeId, Is.EqualTo(LuaDomainEffectTypeIds.Choice), "没有该面许可时保留P09前期普通/帮派选择，不直接强制帮派"); }
        }
        [TestCase(false)][TestCase(true)]
        public void I01SameFaceFromAnotherDefinitionDoesNotGrantSharedRule(bool departure)
        {
            f = new PaulContentTestFixture(); f.Choose(1, "P01"); f.Choose(3, "P03"); var item = I01SelectedItem(departure);
            var group = f.State.EntrepreneurSetup.Players.Single(p => p.PlayerId == 1).RoundGroups.Single(g => g.RoundNumber == (departure ? 1 : 3));
            // 明确的隔离输入：保留裸face和candidateId，实体与候选都指向另一个真实存在的定义。
            // 不声称这份变更后的选择收据合法；本例专测Lua不能借其它仍有效grant认裸face。
            group.Candidates.Single(c => c.CandidateId == group.SelectedCandidateId).DefinitionId = item.DefinitionId = "entrepreneur.round.P.5";
            f.Artifacts.WriteLabeledText("I01同名face不同定义隔离输入", JsonConvert.SerializeObject(new { item.ItemId, item.DefinitionId, group.ActiveFaceId }));
            I01AssertOtherGrantRemains(item.ItemId); I01AssertNoSharedGrant(f.InvokeP09IdentityGuard(departure), departure);
        }
        [TestCase(false)][TestCase(true)]
        public void I01ExitedFaceDoesNotBorrowAnotherFaceGrant(bool departure)
        {
            f = new PaulContentTestFixture(); f.Choose(1, "P01"); f.Choose(3, "P03"); var item = I01SelectedItem(departure); string id = item.ItemId;
            string transfer = f.Run(IsolatedEntrepreneurTransfer.ToRetired(f.State,f.Registry,1,id));
            Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == transfer).Status, Is.EqualTo(EffectNodeStatus.Completed));
            I01AssertOtherGrantRemains(id); I01AssertNoSharedGrant(f.InvokeP09IdentityGuard(departure), departure);
        }
        [TestCase(false)][TestCase(true)]
        public void I01ChangedControllerDoesNotBorrowAnotherFaceGrant(bool departure)
        {
            f = new PaulContentTestFixture(); f.Choose(1, "P01"); f.Choose(3, "P03"); var item = I01SelectedItem(departure);
            item.ControllerSubject = ItemSubjectRef.Player(2);
            f.Artifacts.WriteLabeledText("I01控制者变更隔离输入", "仅改变隔离State的目标面控制人，所有者/active位置及另一面grant保留；不计合法转交或正常流程。");
            I01AssertOtherGrantRemains(item.ItemId); I01AssertNoSharedGrant(f.InvokeP09IdentityGuard(departure), departure);
        }
        [TestCase(false)][TestCase(true)]
        public void I01AnotherAbilityInstanceCannotBorrowOriginalSetupOrGrant(bool departure)
        {
            f = new PaulContentTestFixture(); f.Choose(1, "P01"); f.Choose(3, "P03");
            var add = typeof(ItemZoneStateFactory).GetMethod("AddItem", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(add, Is.Not.Null);
            var item = (ItemInstanceState)add.Invoke(new ItemZoneStateFactory(), new object[] { f.State, "entrepreneur.ability.P09", "entrepreneur.ability_card", "qy015.i01.other_ability", 0,
                f.State.ItemZones.Items.Single(i => i.ItemId == f.AbilityItem).ZoneId, ItemSubjectRef.Player(1) });
            Assert.That(item.ItemId, Is.Not.EqualTo(f.AbilityItem));
            f.Artifacts.WriteLabeledText("I01另一能力实例隔离输入", "用正式实例工厂增一同定义/同主人/同控制者/同active区能力牌，不发组件、不增选人记录。调用指针故意指向它，不能借原实例选面和grant。");
            I01AssertNoSharedGrant(f.InvokeP09IdentityGuard(departure, item.ItemId), departure, invalidAbility: true);
        }
        [TestCase(false)][TestCase(true)]
        public void I01MissingSelectionReceiptCannotBeAuthorizedByUnrelatedGrant(bool departure)
        {
            f = new PaulContentTestFixture(); f.Choose(1, "P01"); f.Choose(3, "P03"); var item = I01SelectedItem(departure);
            Assert.That(item.LocalState.Properties.RemoveAll(x => x.Name == "sourceSelectionReceipt"), Is.EqualTo(1));
            f.Artifacts.WriteLabeledText("I01收据缺失隔离输入", "损坏目标面选择收据，其定义/face/所有者/控制者/active位置保留；另一选面grant仍真实有效。不计Host已接受此损坏状态。");
            I01AssertOtherGrantRemains(item.ItemId); I01AssertNoSharedGrant(f.InvokeP09IdentityGuard(departure), departure);
        }
        [TestCase(false)][TestCase(true)]
        public void OfficialFaceResolvedGrantFactsAuthorizeBothRules(bool departure)
        {
            f = new PaulContentTestFixture(); f.Choose(1, "P01"); f.Choose(3, "P03");
            Assert.That(f.Registry.QuerySources(f.State, 1).Any(s => s.ItemId == I01SelectedItem(departure).ItemId), Is.True);
            f.DockCity(f.Location);
            if (departure)
            {
                string gang = f.Place(f.OtherLocation, 0, type: "entrepreneur.gang_marker");
                int count = f.State.Map.Influences.Count(i => i.LocationId == f.Location);
                string root = f.Run(InfluenceEffectSpecFactory.RemoveInfluence(2, gang));
                Assert.That(f.State.Map.Influences.Count(i => i.LocationId == f.Location), Is.EqualTo(count + 1));
                Assert.That(f.Open.Any(q => f.Within(q.OwnerEffectId, root)), Is.False);
                Assert.That(f.State.EffectRuntime.InfluenceDepartures.Count(d => d.ItemId == gang), Is.EqualTo(1));
            }
            else
            {
                string slot = InfluenceService.GetLocationSlotId(f.OtherLocation, 0);
                string root = f.Run(InfluenceEffectSpecFactory.PlaceInfluence(1, slot));
                Assert.That(f.Open.Any(q => f.Within(q.OwnerEffectId, root)), Is.False);
                var placed = f.State.Map.Influences.Single(i => i.SlotId == slot);
                Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == placed.InfluenceId).TypeId, Is.EqualTo("entrepreneur.gang_marker"));
                string ordinary = f.Place(f.OtherLocation, 1);
                Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == ordinary).TypeId, Is.EqualTo("core.player_marker"));
            }
            f.Restore("R6真实已登记规则执行后的当前选面授权"); f.Pump();
            // 使用真实EffectTreeExecutor/已登记规则调用；不再把sourceKey命名的新节点当执行owner。
        }
        // 64由005补充真实预检的无P01许可对照，不改卡面效果或原R6入口。
        [Test]
        public void OfficialDepartureWithoutP01GrantDoesNotReplenishAfterRecovery()
        {
            // P01和P02均有补放许可；不选首回合面，保留真实P03的独立放置许可作为对照。
            f = new PaulContentTestFixture(); f.Choose(3, "P03"); f.DockCity(f.Location);
            var departure = f.Registry.QuerySources(f.State, 1).Single(s => s.AbilityId == "entrepreneur.P09.departure");
            Assert.That(f.Registry.QuerySources(f.State, 1).Where(s => departure.GrantingSourceRefs.Contains(s.SourceKey))
                .Any(s => s.ActiveFaceId == "P01" || s.ActiveFaceId == "P02"), Is.False);
            string gang = f.Place(f.OtherLocation, 0, type: "entrepreneur.gang_marker");
            int count = f.State.Map.Influences.Count(i => i.LocationId == f.Location);
            string root = f.Run(InfluenceEffectSpecFactory.RemoveInfluence(2, gang));
            Assert.That(f.Open.Any(q => f.Within(q.OwnerEffectId, root)), Is.False);
            Assert.That(f.State.Map.Influences.Count(i => i.LocationId == f.Location), Is.EqualTo(count));
            f.Restore("005无P01许可离场恢复"); f.Pump();
            Assert.That(f.State.Map.Influences.Count(i => i.LocationId == f.Location), Is.EqualTo(count));
            Assert.That(f.State.EffectRuntime.InfluenceDepartures.Count(d => d.ItemId == gang), Is.EqualTo(1));
        }
        [Test] public void I01DerivedInputAndLoadedIdentityComeFromSealedBaseline()
        {
            f = new PaulContentTestFixture(); Assert.That(f.BaselineInputRoot, Is.Not.EqualTo(f.InputRoot));
            Assert.That(File.Exists(Path.Combine(f.BaselineInputRoot, "pack.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(f.InputRoot, "pack.json")), Is.True);
            Assert.That(f.Pack.ContentHash, Is.Not.Empty); Assert.That(f.Registry.ContentPackHash, Is.EqualTo(f.Catalog.ContentPackHash));
            // CopyContent两份input.json及夹具写出的关联文件才是前后树指纹证据；本例不自行造ContentHash。
        }
        [TestCase(false)][TestCase(true)]
        public void OfficialAuthorizationWithMoreThanOneGrantPage(bool departure)
        {
            f = new PaulContentTestFixture(); f.Choose(1, "P01"); f.Choose(3, "P03"); f.Choose(5, "P05"); f.Choose(7, "P07"); f.DockCity(f.Location);
            var owner = f.Registry.QuerySources(f.State, 1).Single(x => x.AbilityId == (departure ? "entrepreneur.P09.departure" : "entrepreneur.P09.place"));
            Assert.That(owner.GrantingSourceRefs.Count, Is.GreaterThan(SourceFactsLuaCatalog.PageSize), "实际有效grant必须跨页，不能把第一页当全集");
            f.Artifacts.WriteLabeledText("R3实际跨页授权输入", JsonConvert.SerializeObject(new { owner.ItemId, owner.GrantingSourceRefs, SourceFactsLuaCatalog.PageSize }));
            var result = f.InvokeP09IdentityGuard(departure);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(result.Effects.Single().EffectTypeId, Is.EqualTo(InfluenceEffectTypeIds.PlaceInfluence));
        }
        [TestCase(false)][TestCase(true)]
        public void RevokedTargetGrantCannotBorrowStillCurrentOtherFace(bool departure)
        {
            f = new PaulContentTestFixture(); f.Choose(1, "P01"); f.Choose(3, "P03");
            var target = I01SelectedItem(departure);
            var targetKeys = f.Registry.QuerySources(f.State, 1).Where(x => x.ItemId == target.ItemId).Select(x => x.SourceKey).ToList();
            Assert.That(targetKeys, Is.Not.Empty);
            var ability = f.State.ItemZones.Items.Single(i => i.ItemId == f.AbilityItem);
            Assert.That(Value(ability.LocalState, "grantingSourceRefs").Items.RemoveAll(x => targetKeys.Contains(x.StringValue)), Is.EqualTo(targetKeys.Count));
            var current = f.Registry.QuerySources(f.State, 1);
            Assert.That(current.Any(x => x.ItemId == target.ItemId), Is.True, "目标面自身仍有效，此处只撤销它给P09的grant");
            var owner = current.Single(x => x.AbilityId == "entrepreneur.P09.place");
            Assert.That(owner.GrantingSourceRefs, Is.Not.Empty); Assert.That(owner.GrantingSourceRefs.Intersect(targetKeys), Is.Empty);
            f.Artifacts.WriteLabeledText("R3撤销特定grant隔离输入", JsonConvert.SerializeObject(new { target.ItemId, targetKeys, owner.GrantingSourceRefs }));
            I01AssertNoSharedGrant(f.InvokeP09IdentityGuard(departure), departure);
        }
        [Test] public void WrongPlayerCannotUsePaulsCurrentSelectedFace()
        {
            f = new PaulContentTestFixture(); f.Choose(1, "P01"); f.Choose(3, "P03");
            string root = f.Run(InfluenceEffectSpecFactory.PlaceInfluence(2, InfluenceService.GetLocationSlotId(f.OtherLocation, 0)));
            Assert.That(f.Open.Any(q => f.Within(q.OwnerEffectId, root)), Is.False);
            var placed = f.State.Map.Influences.Single(i => i.SlotId == InfluenceService.GetLocationSlotId(f.OtherLocation, 0));
            Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == placed.InfluenceId).TypeId, Is.EqualTo("core.player_marker"));
            Assert.That(placed.PlayerId, Is.EqualTo(2));
        }
        [Test] public void PreservesOfficialEntitiesRecipeAndSourceScopes()
        {
            f = new PaulContentTestFixture();
            var ability = f.Pack.Definitions.Single(d => d.DefinitionId == "entrepreneur.ability.P09");
            Assert.That(ability.Data["entrepreneur"]["components"].Single()["quantity"].Value<int>(), Is.EqualTo(4));
            Assert.That(f.State.ItemZones.Items.Count(i => i.TypeId == "entrepreneur.gang_marker" && i.OwnerSubject.PlayerId == 1), Is.EqualTo(4));
            foreach (int round in new[] { 1, 3, 5, 7 })
                Assert.That(f.State.ItemZones.Items.Count(i => i.DefinitionId == "entrepreneur.round.P." + round && i.OwnerSubject.PlayerId == 1), Is.EqualTo(1));
            var declared = f.Registry.AbilityDeclarations.Where(d => d.DefinitionId.StartsWith("entrepreneur.", StringComparison.Ordinal) && d.AbilityId.Contains(".P")).ToList();
            Assert.That(declared.Count(d => d.QueryKinds.Contains("influence_place_attaching.v1")), Is.EqualTo(1));
            Assert.That(declared.Single(d => d.AbilityId == "entrepreneur.P09.departure").ObservedEvents, Is.EqualTo(new[] { "InfluenceRemoved" }));
            Assert.That(declared.Where(d => d.Kind == "cleanup").All(d => d.RepeatPolicy == "once"), Is.True);
            Assert.That(declared.Where(d => d.ActiveFaceId == "P04").SelectMany(d => d.Actions), Is.Empty);
            Assert.That(f.Registry.QuerySources(f.State, 1).Any(s => s.ActiveFaceId.Length > 0), Is.False, "预览不启用选面");
        }
        [Test] public void EarlyPlacementChoiceHasBothTypesAndRestoresSelectedLeaf()
        {
            f = new PaulContentTestFixture();
            string root = f.Run(InfluenceEffectSpecFactory.PlaceInfluence(1, InfluenceService.GetLocationSlotId(f.Location, 0)));
            var request = f.Request(root, "effect.choice.execute");
            Assert.That(request.CandidateIds, Is.EquivalentTo(new[] { "ordinary", "gang" }));
            f.Answer(request, "gang");
            var influence = f.State.Map.Influences.Single(i => i.SlotId == InfluenceService.GetLocationSlotId(f.Location, 0));
            Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == influence.InfluenceId).TypeId, Is.EqualTo("entrepreneur.gang_marker"));
            Assert.That(f.State.EffectRuntime.InfluencePlaceMappings.Single(m => m.LogicalOperationId == root).SelectedLeafEffectId, Is.Not.Empty);
            f.Restore("类型Choice所选叶及实际结果");
        }
        [TestCase("P03")][TestCase("P04")]
        public void SelectedThirdRoundForcesGangButPreservesExplicitOrdinary(string face)
        {
            f = new PaulContentTestFixture(); f.Choose(3, face);
            Assert.That(f.Registry.QuerySources(f.State, 1).Single(s => s.AbilityId == "entrepreneur.P09.place").GrantingSourceRefs, Is.Not.Empty);
            string root = f.Run(InfluenceEffectSpecFactory.PlaceInfluence(1, InfluenceService.GetLocationSlotId(f.OtherLocation, 0)));
            Assert.That(f.Open.Any(q => f.Within(q.OwnerEffectId, root) && q.InteractionTypeId == "effect.choice.execute"), Is.False);
            var placed = f.State.Map.Influences.Single(i => i.SlotId == InfluenceService.GetLocationSlotId(f.OtherLocation, 0));
            Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == placed.InfluenceId).TypeId, Is.EqualTo("entrepreneur.gang_marker"));
            string ordinary = f.Place(f.OtherLocation, 1);
            Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == ordinary).TypeId, Is.EqualTo("core.player_marker"));
        }
        [TestCase("P01")][TestCase("P02")] public void FirstRoundOtherPlayerDepartureReplenishesOnceWithoutMapClick(string face)
        {
            f = new PaulContentTestFixture(); f.Choose(1, face); f.DockCity(f.Location);
            string gang = f.Place(f.OtherLocation, 0, type: "entrepreneur.gang_marker");
            int before = f.State.Map.Influences.Count(i => i.LocationId == f.Location);
            string root = f.Run(InfluenceEffectSpecFactory.RemoveInfluence(2, gang));
            Assert.That(f.State.Map.Influences.Count(i => i.LocationId == f.Location), Is.EqualTo(before + 1));
            Assert.That(f.Open.Any(q => f.Within(q.OwnerEffectId, root)), Is.False);
            var departures = f.State.EffectRuntime.InfluenceDepartures.Where(d => d.ItemId == gang).ToList();
            Assert.That(departures, Has.Count.EqualTo(1)); f.Restore("他人移除本人帮派独立补放后");
        }
        [Test] public void P01OnlyFirstMoveIsOptionalAndLaterStepsStayIndependent()
        {
            f = new PaulContentTestFixture(); f.Choose(1, "P01"); f.Place(f.Location, 0);
            string root = f.Invoke("entrepreneur.face.P01");
            var optional = f.Request(root); Assert.That(optional.CandidateIds, Does.Contain("decline")); f.Answer(optional, "decline");
            var next = f.Request(root);
            Assert.That(Text(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == next.OwnerEffectId).NormalizedArguments, "operation"), Is.EqualTo("remove"));
            f.Restore("P01首移放弃后仍待本人移除");
        }
        [Test] public void P02PaymentPrecedesReplacementAndDeclineDoesNotCharge()
        {
            f = new PaulContentTestFixture(); f.Choose(1, "P02"); f.Place(f.Location, 0); f.State.FindPlayer(1).Resources.GoldVoucher = 4;
            f.Artifacts.WriteLabeledText("付款隔离前提", "初始金券设为4，仅核真实Lua付款/右式，不计正常资源获得。");
            string declined = f.Invoke("entrepreneur.face.P02"); var q = f.Request(declined); f.Decline(q);
            Assert.That(f.Resource(1, ResourceType.GoldVoucher), Is.EqualTo(4));
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => f.Within(n.EffectId, declined) && n.EffectTypeId == ContentPrimitiveEffectExecutor.SelectInfluence), Is.False);
            string paid = f.Invoke("entrepreneur.face.P02"); f.ConfirmPayment(paid);
            Assert.That(f.Resource(1, ResourceType.GoldVoucher), Is.Zero);
            q = f.Request(paid); var selection = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == q.OwnerEffectId);
            Assert.That(Text(selection.NormalizedArguments, "operation"), Is.EqualTo("replace"));
            Assert.That(f.State.Map.Influences.Count(i => i.LocationId == f.Location), Is.EqualTo(1), "付款后仍等真实替换选择，不提前移走");
            f.Restore("P02付4券待替换");
        }
        [TestCase(2, 0, 0)][TestCase(0, 2, 0)][TestCase(0, 0, 2)][TestCase(1, 1, 0)][TestCase(1, 0, 1)][TestCase(0, 1, 1)]
        public void P03TotalCostAllowsEveryTwoUnitComposition(int rock, int shard, int iron)
        {
            f = new PaulContentTestFixture(); f.Choose(3, "P03");
            var resources = f.State.FindPlayer(1).Resources; resources.Originium = rock; resources.OriginiumShard = shard; resources.Iron = iron;
            f.Artifacts.WriteLabeledText("合计付款隔离前提", $"源岩={rock}，碎片={shard}，异铁={iron}；只提供这一足额组合，不计正常获得。");
            string root = f.Invoke("entrepreneur.face.P03");
            var pay = f.State.EffectRuntime.EffectNodes.Single(n => f.Within(n.EffectId, root) && n.EffectTypeId == ResourceEffectTypeIds.Pay);
            Assert.That(pay.Payment.Demand.TotalCount, Is.EqualTo(2)); Assert.That(pay.Payment.Demand.AllowedResourceTypes.Count, Is.EqualTo(3));
            var request = f.Request(root, ResourcePaymentChoiceEffectExecutor.InteractionTypeId);
            Assert.That(request.Presentation.Prompt.Key, Is.EqualTo("core:entrepreneur.P.P03.condition"));
            Assert.That(request.Presentation.Progress.Key, Is.EqualTo("core:resource.payment.choice.prompt"));
            string presentation = Newtonsoft.Json.JsonConvert.SerializeObject(request.Presentation);
            f.Artifacts.WriteLabeledText("R14正式P03合计条件正文", presentation);
            f.Restore("P03合计2正式付款待答");
            Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(f.Request(root, ResourcePaymentChoiceEffectExecutor.InteractionTypeId).Presentation), Is.EqualTo(presentation));
            f.ConfirmPayment(root);
            Assert.That(f.Resource(1, ResourceType.Originium) + f.Resource(1, ResourceType.OriginiumShard) + f.Resource(1, ResourceType.Iron), Is.Zero);
            Assert.That(f.Open.Any(q => f.Within(q.OwnerEffectId, root)), Is.True, "付款完成后等真实放置目标");
            var placement = f.State.EffectRuntime.EffectNodes.Where(n => f.Within(n.EffectId, root) && n.EffectTypeId == "effect.influence.place").ToList();
            Assert.That(placement.Any(n => Text(n.NormalizedArguments, "itemTypeId") == "entrepreneur.gang_marker"), Is.True);
        }
        [TestCase("P05")][TestCase("P06")]
        public void FifthRoundImmediateRetainsIndependentOrderedEffects(string face)
        {
            f = new PaulContentTestFixture(); int score = f.State.FindPlayer(1).Score, pure = f.Resource(1, ResourceType.PureOriginium), gold = f.Resource(1, ResourceType.GoldVoucher);
            f.Choose(5, face);
            var entry = f.State.EntrepreneurSetup.Players.Single(p => p.PlayerId == 1).RoundGroups.Single(g => g.RoundNumber == 5).ImmediateEntries.Single();
            var body = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == entry.EffectRootId);
            var nodes = body.ChildEffectIds.Select(id => f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == id)).ToList();
            Assert.That(nodes.Take(3).Select(n => n.EffectTypeId), Is.EqualTo(new[] { LuaDomainEffectTypeIds.LoseScore, ResourceEffectTypeIds.Gain, ResourceEffectTypeIds.Gain }));
            Assert.That(f.Resource(1, ResourceType.PureOriginium) - pure, Is.EqualTo(1)); Assert.That(f.Resource(1, ResourceType.GoldVoucher) - gold, Is.EqualTo(15));
            if (face == "P05") Assert.That(f.State.FindPlayer(1).Score - score, Is.EqualTo(-2));
            else { Assert.That(nodes.Last().EffectTypeId, Is.EqualTo("effect.operator.dispatch")); Assert.That(Number(nodes.Last().NormalizedArguments, "drawCount"), Is.EqualTo(3)); }
            f.Restore("独立立即顺序及结清" + face);
        }
        [TestCase("accept")][TestCase("decline")][TestCase("no_candidates")]
        public void P06OfficialImmediateDispatchDistinguishesThreeOutcomes(string outcome)
        {
            f = new PaulContentTestFixture();
            if (outcome == "no_candidates")
            {
                // 隔离前提通过真实卡移动清空可返还区，保留实物，不改随机流或特派结果。
                var zones = new[] { FindZone(f.State, "hand", 1).ZoneId, FindZone(f.State, "discard", 1).ZoneId };
                foreach (string zone in zones)
                {
                    var ids = f.State.ItemZones.Items.Where(i => i.ZoneId == zone).Select(i => i.ItemId).ToList();
                    if (ids.Count > 0) f.Run(MoveSpec(1, ids, zone, FindZone(f.State, "retired").ZoneId, "unordered", profile: "core.character_card_transfer"));
                }
                f.Artifacts.WriteLabeledText("特派无候选隔离前提", "用真实移动把本人手牌/弃牌回盒；不删实例、不改抽牌或分支结果。");
            }
            int score = f.State.FindPlayer(1).Score; f.Choose(5, "P06", stopAtDispatch: true);
            var group = f.State.EntrepreneurSetup.Players.Single(p => p.PlayerId == 1).RoundGroups.Single(g => g.RoundNumber == 5);
            string root = group.ImmediateEntries.Single().EffectRootId;
            var dispatch = f.State.EffectRuntime.EffectNodes.Single(n => f.Within(n.EffectId, root) && n.EffectTypeId == LuaDomainEffectTypeIds.DispatchOperator);
            if (outcome != "no_candidates")
            {
                var q = f.Request(root, "operator.dispatch.choose"); Assert.That(q.CandidateIds.Count(id => id != "decline"), Is.EqualTo(3));
                f.Restore("P06真实三候选待答" + outcome); q = f.Request(root, "operator.dispatch.choose");
                if (outcome == "decline") f.Decline(q);
                else { f.Answer(q, q.CandidateIds.First(id => id != "decline")); var back = f.Request(root, "operator.dispatch.return"); f.Answer(back, back.CandidateIds.First()); }
                dispatch = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == dispatch.EffectId);
            }
            Assert.That(dispatch.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Flag(dispatch.NormalizedResult, "accepted"), Is.EqualTo(outcome == "accept"));
            Assert.That(f.State.FindPlayer(1).Score - score, Is.EqualTo(outcome == "decline" ? -1 : -2));
            if (outcome == "no_candidates") Assert.That(Text(dispatch.NormalizedResult, "reasonCode"), Is.EqualTo("no_candidates"));
            int after = f.State.FindPlayer(1).Score; f.Restore("P06特派结清" + outcome); f.Pump(); Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(after));
        }
        [TestCase(0)][TestCase(1)]
        public void P5ShortTargetsExecuteLeftFailWithNoDepartureAndNoReward(int count)
        {
            f = new PaulContentTestFixture(); f.Choose(5, "P05"); f.DockCity(f.Location);
            for (int i = 0; i < count; i++) f.Place(f.Location, i);
            string before = JsonConvert.SerializeObject(f.State.Map.Influences);
            int removedBefore = f.State.EffectRuntime.RuleEvents.Count(e => e.EventType == "InfluenceRemoved");
            var resources = JsonConvert.SerializeObject(f.State.FindPlayer(1).Resources);
            string root = f.Invoke("entrepreneur.face.P05"); f.Answer(f.Request(root), "continue");
            var strict = f.State.EffectRuntime.EffectNodes.Single(n => f.Within(n.EffectId, root) && n.EffectTypeId == "effect.influence.remove");
            Assert.That(strict.Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(Number(strict.NormalizedResult, "actualRemovedCount"), Is.Zero);
            Assert.That(JsonConvert.SerializeObject(f.State.Map.Influences), Is.EqualTo(before));
            Assert.That(f.State.EffectRuntime.RuleEvents.Count(e => e.EventType == "InfluenceRemoved"), Is.EqualTo(removedBefore));
            Assert.That(JsonConvert.SerializeObject(f.State.FindPlayer(1).Resources), Is.EqualTo(resources));
            f.Restore("P5真实短targets失败零变更" + count);
        }
        [Test] public void P5AtomicRemovalUsesPostResponseGangPointsAndRealAmountOnce()
        {
            f = new PaulContentTestFixture(); f.Choose(5, "P05");
            f.Place(f.Location, 0); f.Place(f.Location, 1, 2); f.DockCity(f.Location);
            f.Place(f.OtherLocation, 0, type: "entrepreneur.gang_marker"); f.Place(f.OtherLocation, 1, type: "entrepreneur.gang_marker");
            var point = f.State.Map.ResourceTokens.Single(p => p.LocationId == f.OtherLocation);
            int before = f.Resource(1, point.ResourceType); int departed = f.State.EffectRuntime.InfluenceDepartures.Count;
            string root = f.Invoke("entrepreneur.face.P05"); f.Answer(f.Request(root), "continue");
            Assert.That(f.State.Map.Influences.Any(i => i.LocationId == f.Location), Is.False);
            Assert.That(f.State.EffectRuntime.InfluenceDepartures.Count - departed, Is.EqualTo(2));
            var records = f.State.EffectRuntime.InfluenceDepartures.Skip(departed).ToList(); Assert.That(records.Select(d => d.BatchId).Distinct().Count(), Is.EqualTo(1));
            Assert.That(f.Resource(1, point.ResourceType) - before, Is.EqualTo(point.Amount));
            Assert.That(f.State.EffectRuntime.EffectNodes.Where(n => f.Within(n.EffectId, root) && n.EffectTypeId == "effect.resource.gain").Count(), Is.EqualTo(1));
            f.Restore("P5整批及按点去重真实amount");
        }
        [TestCase("P03")][TestCase("P04")]
        public void ShareKeepsOriginalPointAndPaysOwnerAndOpponentOnce(string face)
        {
            f = new PaulContentTestFixture(); string ordinary = f.Place(f.Location, 0);
            var route = f.Map.Routes.First(r => r.InfluenceSlotCount > 0);
            string routeSlot = InfluenceService.GetRouteSlotId(route.RouteId, 0);
            f.Run(InfluenceEffectSpecFactory.PlaceInfluence(1, routeSlot, itemTypeId: "entrepreneur.gang_marker"));
            f.Choose(3, face);
            string gang = f.Place(f.OtherLocation, 0, type: "entrepreneur.gang_marker"); f.Place(f.OtherLocation, 1, 2);
            var point = f.State.Map.ResourceTokens.Single(p => p.LocationId == f.OtherLocation); int one = f.Resource(1, point.ResourceType), two = f.Resource(2, point.ResourceType);
            string root = U10ShareTestSupport.StartCleanup(f, face); f.Answer(f.Request(root, "action.decline_effect"), "continue");
            var target = f.Request(root, "effect.influence.remove.target");
            Assert.That(target.CandidateIds, Is.EqualTo(new[] { gang }), "真实候选仅本人资源点帮派，排除普通影响力/对手/航道。");
            U10ShareTestSupport.AnswerThroughMap(f, target, gang);
            U10ShareTestSupport.AssertOneCondition(f, root);
            Assert.That(f.Resource(1, point.ResourceType) - one, Is.EqualTo(point.Amount));
            Assert.That(f.Resource(2, point.ResourceType) - two, Is.EqualTo(point.Amount));
            Assert.That(f.State.Map.Influences.Any(i => i.InfluenceId == gang), Is.False);
            Assert.That(f.State.Map.Influences.Any(i => i.InfluenceId == ordinary), Is.True);
            Assert.That(f.State.Map.Influences.Any(i => i.SlotId == routeSlot), Is.True);
            var selection = f.State.EffectRuntime.EffectNodes.Single(n => f.Within(n.EffectId, root) && n.EffectTypeId == ContentPrimitiveEffectExecutor.SelectInfluence);
            Assert.That(Text(Value(selection.NormalizedResult, "removedInfluences").Items.Single(), "locationId"), Is.EqualTo(f.OtherLocation));
            f.Restore("分享右式清单及双方真实收益" + face);
        }
        [Test] public void ShareWithoutOpponentStillPaysOwnerOnceAfterStrictRemoval()
        {
            f = new PaulContentTestFixture(); f.Choose(3, "P03");
            string gang = f.Place(f.OtherLocation, 0, type: "entrepreneur.gang_marker");
            var point = f.State.Map.ResourceTokens.Single(p => p.LocationId == f.OtherLocation); int before = f.Resource(1, point.ResourceType);
            string root = U10ShareTestSupport.StartCleanup(f, "P03"); f.Answer(f.Request(root, "action.decline_effect"), "continue");
            f.Answer(f.Request(root, "effect.influence.remove.target"), gang);
            U10ShareTestSupport.AssertOneCondition(f, root);
            Assert.That(f.Resource(1, point.ResourceType), Is.EqualTo(before + point.Amount));
            Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => f.Within(n.EffectId, root) && n.EffectTypeId == ResourceEffectTypeIds.Gain), Is.EqualTo(1));
        }
        [Test] public void DeclinedShareDoesNotSelectRemoveOrGiveResources()
        {
            f = new PaulContentTestFixture(); f.Choose(3, "P04"); f.Place(f.OtherLocation, 0, type: "entrepreneur.gang_marker");
            string before = JsonConvert.SerializeObject(new { f.State.Map.Influences, Resources = f.State.Players.Select(p => p.Resources).ToList() });
            string root = U10ShareTestSupport.StartCleanup(f, "P04"); f.Answer(f.Request(root, "action.decline_effect"), "decline");
            U10ShareTestSupport.AssertOneCondition(f, root);
            Assert.That(JsonConvert.SerializeObject(new { f.State.Map.Influences, Resources = f.State.Players.Select(p => p.Resources).ToList() }), Is.EqualTo(before));
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => f.Within(n.EffectId, root) &&
                new[] { ContentPrimitiveEffectExecutor.SelectInfluence, InfluenceEffectTypeIds.RemoveInfluence, ResourceEffectTypeIds.Gain }.Contains(n.EffectTypeId)), Is.False);
        }
        [Test] public void FailedRequiredRemovalCannotConstructShareIncome()
        {
            f = new PaulContentTestFixture(); f.Choose(3, "P03");
            string before = JsonConvert.SerializeObject(f.State.Players.Select(p => p.Resources).ToList());
            string id = f.Run(EffectSpec.Create(ContentPrimitiveEffectExecutor.SelectInfluence, Object(
                "player", NormalizedValue.CreateString("1"), "operation", NormalizedValue.CreateString("remove"),
                "ownerFilter", NormalizedValue.CreateString("self"), "candidateScope", NormalizedValue.CreateString("resource_points"),
                "candidateIds", Strings(new[] { "qy007.u10.missing-target" }), "count", NormalizedValue.CreateInteger(1),
                "requiredCount", NormalizedValue.CreateInteger(1), "requireCompleteSelection", NormalizedValue.CreateBoolean(true)), 1));
            var node = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(node.FailureReason, Is.EqualTo("insufficient_items"));
            var snapshot = LuaFacadeSnapshot.FromInvocation("p1", 0, 0, 2, "1.1.3");
            snapshot.EventPayload = LuaSnapshotValue.Object(new[] {
                new LuaSnapshotEntry("effectTypeId", LuaSnapshotValue.String(node.EffectTypeId)),
                new LuaSnapshotEntry("outcome", LuaSnapshotValue.String("failed")),
                new LuaSnapshotEntry("continuationContext", LuaSnapshotValue.Object(new[] { new LuaSnapshotEntry("stage", LuaSnapshotValue.String("share")) })) });
            string source = f.Pack.GetScript("lua/entrepreneurs/paul.p03_p04.cleanup.lua");
            var context = new LuaInvocationContext("qy007.u10.failed", "EffectCompleted", "p1", f.State.EffectRuntime.StateRevision, "1.1.3", 2, 0, 0, snapshot);
            var result = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("entrepreneur.round.P.3", "entrepreneur.P03.cleanup", "entrepreneur.P03.cleanup", "1.1.3", source, LuaContentHasher.ComputeSha256(source)), context);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(result.Effects, Is.Empty);
            Assert.That(JsonConvert.SerializeObject(f.State.Players.Select(p => p.Resources).ToList()), Is.EqualTo(before));
            f.Artifacts.WriteLabeledText("分享失败边界", "真实严格数量选择因无合法目标Failed；将该真实失败类型/结果交给正式分享Lua的完成分支，零收益。该隔离边界控制不声称正常调度可在选中后夺走目标。");
        }
        [TestCase("normal")][TestCase("extra")]
        public void P04PureFreeLocationsDoNotAddNonLegalPointsOrDuplicateCity(string purpose)
        {
            f = new PaulContentTestFixture(); f.Choose(3, "P04"); f.DockCity(f.Location);
            f.Place(f.OtherLocation, 0, type: "entrepreneur.gang_marker"); f.Place(f.OtherLocation, 1, type: "entrepreneur.gang_marker");
            var query = new CollectionQueryContext(1, purpose, "isolated.collection", "isolated.plan", f.Location, f.State.ContentPackHash, new[] { f.Location, f.OtherLocation });
            var result = new CollectionFreeLocationQuery(f.Registry, new MapQueryService(f.Map)).Query(f.State, query);
            Assert.That(result.FreeLocationIds, Is.EquivalentTo(new[] { f.Location, f.OtherLocation }));
            string before = JsonConvert.SerializeObject(f.State);
            new CollectionFreeLocationQuery(f.Registry, new MapQueryService(f.Map)).Query(f.State, query);
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before), "纯查询不发资源/不改状态");
        }
        [TestCase(1, "ordinary")][TestCase(2, "ordinary")][TestCase(1, "free")][TestCase(2, "free")][TestCase(1, "specified")][TestCase(2, "specified")]
        public void P06HardExclusionAppliesToEveryMovingPlayerAndPurpose(int mover, string purpose)
        {
            f = new PaulContentTestFixture(); f.Choose(5, "P06");
            f.Place(f.OtherLocation, 0, type: "entrepreneur.gang_marker"); f.Place(f.OtherLocation, 1);
            var result = new CityDestinationQueryService(new MapQueryService(f.Map), f.Registry).Query(f.State,
                new CityDestinationQuery(mover, "isolated.city.move", purpose, "standard", new[] { f.Location, f.OtherLocation }));
            Assert.That(result.AllowedLocationIds, Does.Not.Contain(f.OtherLocation));
            Assert.That(result.HardExclusions.Select(x => x.CandidateId), Does.Contain(CityDestinationQueryService.CandidateId(f.OtherLocation)));
        }
        [Test] public void P05InfluenceBonusIsOncePerRegionForItsOwner()
        {
            f = new PaulContentTestFixture(); f.Choose(5, "P05"); f.Place(f.OtherLocation, 0, type: "entrepreneur.gang_marker"); f.Place(f.OtherLocation, 1, type: "entrepreneur.gang_marker");
            var snapshot = new ScoringModifierQueryService(new MapQueryService(f.Map), f.Registry).BuildControlSnapshot(f.State, "isolated.p05.control");
            string region = f.Map.Locations.Single(l => l.LocationId == f.OtherLocation).RegionId;
            var result = snapshot.Regions.Single(r => r.RegionId == region);
            Assert.That(result.InfluenceModifiers.Where(m => m.Source.AbilityId == "entrepreneur.P05.region").Sum(m => m.Delta), Is.EqualTo(1));
            Assert.That(result.InfluenceModifiers.Where(m => m.Source.AbilityId == "entrepreneur.P05.region").All(m => m.PlayerId == 1), Is.True);
            Assert.That(result.InfluenceCounts[1], Is.EqualTo(result.PhysicalCounts[1] + result.CityCounts[1] + 1));
        }
        [TestCase("map-three-players", 7, 5, 30)][TestCase("map-four-players", 6, 4, 25)]
        public void P07ActualFinalLedgerConsumesOnlyReplacementProfile(string map, int rock, int iron, int gold)
        {
            f = new PaulContentTestFixture(map); f.Choose(7, "P07");
            var resource = f.State.FindPlayer(1).Resources; resource.Originium = rock; resource.Iron = iron; resource.GoldVoucher = gold; resource.PureOriginium = 1; resource.OriginiumShard = 90;
            f.Artifacts.WriteLabeledText("终局资源隔离前提", "设置四种恰好各1分的资源及90碎片，只核正式Session消费本人profile，不计正常资源取得。");
            f.FinishIsolatedLastRound(); Assert.That(f.State.FinalScoring.IsResolved, Is.True);
            Assert.That(f.State.FinalScoring.Session.Players.Single(p => p.PlayerId == 1).ResourceScore, Is.EqualTo(4));
            f.Restore("P07正式终局profile到账" + map);
        }
        [TestCase("map-three-players", 7, 5, 30)][TestCase("map-four-players", 6, 4, 25)]
        public void P07ReplacesWholeProfileByMapWhileSeatCountStaysTwo(string map, int rock, int iron, int gold)
        {
            f = new PaulContentTestFixture(map); f.Choose(7, "P07");
            var result = new ScoringModifierQueryService(new MapQueryService(f.Map), f.Registry).QueryResourceProfile(f.State, "isolated.final", 1);
            Assert.That(result.Entries.Single(e => e.ResourceType == "Originium").Units, Is.EqualTo(rock));
            Assert.That(result.Entries.Single(e => e.ResourceType == "Iron").Units, Is.EqualTo(iron));
            Assert.That(result.Entries.Single(e => e.ResourceType == "GoldVoucher").Units, Is.EqualTo(gold));
            Assert.That(result.Entries.Single(e => e.ResourceType == "PureOriginium").Units, Is.EqualTo(1));
            Assert.That(result.Entries.Any(e => e.ResourceType == "OriginiumShard"), Is.False);
            Assert.That(new ScoringModifierQueryService(new MapQueryService(f.Map), f.Registry).QueryResourceProfile(f.State, "isolated.final", 2), Is.Null);
        }
        [TestCase(false)][TestCase(true)] public void P08ActualSessionPaysPerRegionOnceAndRetainsControllerAward(bool opponentControls)
        {
            f = new PaulContentTestFixture(); f.Choose(7, "P08"); f.Place(f.OtherLocation, 0, type: "entrepreneur.gang_marker");
            string region = f.Map.Locations.Single(l => l.LocationId == f.OtherLocation).RegionId;
            if (opponentControls)
            {
                var slots = f.Map.Locations.Where(l => l.RegionId == region).SelectMany(l => Enumerable.Range(0, l.InfluenceSlotCount).Select(i => InfluenceService.GetLocationSlotId(l.LocationId, i)))
                    .Concat(f.Map.Routes.Where(r => r.RegionId == region).SelectMany(r => Enumerable.Range(0, r.InfluenceSlotCount).Select(i => InfluenceService.GetRouteSlotId(r.RouteId, i))))
                    .Where(slot => f.State.Map.Influences.All(i => i.SlotId != slot)).Take(4).ToList();
                Assert.That(slots.Count, Is.EqualTo(4), "本隔离地图须有足够真实空槽，不能伪造控制者");
                foreach (string slot in slots) f.Run(InfluenceEffectSpecFactory.PlaceInfluence(2, slot, itemTypeId: "core.player_marker"));
            }
            string root = f.FinishIsolatedLastRound(); var session = f.State.FinalScoring.Session;
            Assert.That(f.State.FinalScoring.IsResolved, Is.True); Assert.That(session.OwnerEffectId, Is.EqualTo(root));
            Assert.That(session.Players.Single(p => p.PlayerId == 1).SpecialScore, Is.EqualTo(1));
            var control = session.Control.Regions.Single(r => r.RegionId == region); Assert.That(control.ScoreValue, Is.EqualTo(control.BaseScoreValue + 1));
            int controller = opponentControls ? 2 : 1; Assert.That(control.ControllerPlayerId, Is.EqualTo(controller));
            Assert.That(session.Players.Single(p => p.PlayerId == controller).RegionScore, Is.GreaterThanOrEqualTo(control.ScoreValue));
            int score = f.State.FindPlayer(1).Score; string sessionId = session.SessionId; f.Restore("P08正式终局账本完成"); f.Pump();
            Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(score)); Assert.That(f.State.FinalScoring.Session.SessionId, Is.EqualTo(sessionId));
            Assert.That(f.State.EffectRuntime.RuleEvents.Count(e => e.EventType == "FinalScoringStarted"), Is.EqualTo(1));
        }
        [Test] public void P08ValueModifierAndBonusSourceRemainSeparate()
        {
            f = new PaulContentTestFixture(); f.Choose(7, "P08"); f.Place(f.OtherLocation, 0, type: "entrepreneur.gang_marker");
            var view = new ScoringModifierQueryService(new MapQueryService(f.Map), f.Registry).BuildControlSnapshot(f.State, "isolated.control");
            string region = f.Map.Locations.Single(l => l.LocationId == f.OtherLocation).RegionId;
            Assert.That(view.Regions.Single(x => x.RegionId == region).ValueModifiers.Count(x => x.Source.AbilityId == "entrepreneur.face.P08"), Is.EqualTo(1));
            Assert.That(view.Regions.Single(x => x.RegionId == region).ValueModifiers.Single(x => x.Source.AbilityId == "entrepreneur.face.P08").Delta, Is.EqualTo(1));
            Assert.That(f.Registry.QuerySources(f.State, 1, "event", "FinalScoringStarted").Count(s => s.AbilityId == "entrepreneur.P08.bonus"), Is.EqualTo(1));
            // 实际特殊收益/控制者归属消费由统一终局组合继续验证，不用本纯查询假称已入账。
        }
    }
}
