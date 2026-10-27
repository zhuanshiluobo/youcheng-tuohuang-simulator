using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Infrastructure.Lua;
using YC.Domain.State;
using YC.Domain.Rules;
using YC.Domain.Influence;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 真实模块原文+生产Lua Host。手工快照用例仅证明构树，不计来源授权/正常页面。
    public sealed class AgnesContentTests
    {
        [TestCase(2, 1)] [TestCase(4, 2)] [TestCase(200, 29)]
        public void RealHostFreeChoiceLoadsOnlySelectedPositiveQuantity(int n, int k)
        {
            using var f = new AgnesContentTestFixture();
            f.Host("return A.gainCommand(ctx,{}," + n + ")", supply: Math.Max(29, k)); f.Start();
            Assert.That(f.Open.AllowDecline, Is.True);
            Assert.That(f.Open.CandidateIds, Does.Not.Contain("0"));
            Assert.That(f.Open.CandidateIds, Does.Not.Contain("decline"));
            Assert.That(f.Open.CandidateIds, Does.Contain("" + k));
            f.Answer("" + k);
            Assert.That(f.Stored, Is.EqualTo(k));
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(100));
            Assert.That(f.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
        }
        [Test] public void RealHostDeclineEndsOnlyGainAndRunsSiblingEffect()
        {
            using var f = new AgnesContentTestFixture();
            f.Host("local e=A.gainCommand(ctx,{},2); e[#e+1]=Effect.GainResource({recipient=ctx.playerId,resourceType='iron',amount=1}); return e");
            f.Start(); f.Decline(); Assert.That(f.Stored, Is.Zero);
            Assert.That(f.State.FindPlayer(1).Resources.Iron, Is.EqualTo(1));
        }
        [TestCase(1)] [TestCase(4)] [TestCase(29)]
        public void RealHostFixedPurchasePaysExactGoldAndDeliversOneFixedGroup(int k)
        {
            using var f = new AgnesContentTestFixture();
            f.Host("return A.purchaseCommand(ctx," + k + ")", supply: 29); f.Start(); f.Answer("continue");
            Assert.That(f.Stored, Is.EqualTo(k));
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(100 - 2 * k));
            Assert.That(f.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
            Assert.That(f.State.EffectRuntime.InfluenceDepartures, Is.Empty, "牌上取得/支付不是地图离场");
        }
        [Test] public void PurchaseDeclineKeepsInventoryAndContinuesSibling()
        {
            using var f = new AgnesContentTestFixture();
            f.Host("local e=A.purchaseCommand(ctx,2);e[#e+1]=Effect.GainResource({recipient=ctx.playerId,resourceType='iron',amount=1});return e");
            f.Start(); f.Answer("decline"); Assert.That(f.Stored, Is.Zero);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(100));
            Assert.That(f.State.FindPlayer(1).Resources.Iron, Is.EqualTo(1));
        }
        [TestCase(200, 29, -1, 0, 29)]
        [TestCase(200, 150, -1, 0, 99)]
        [TestCase(200, 29, 4, 2, 4)]
        public void CompleteRequestedNIsPreservedWhileLegalKUsesStockCapacityAndDeclaredRange(int n, int stock, int capacity, int pending, int largest)
        {
            using var f = new AgnesContentTestFixture(); var snapshot = Inventory(stock, capacity, pending);
            var result = f.Lua("{}", "return A.gainCommand(ctx,{}," + n + ")", snapshot: snapshot);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            var choice = result.Effects.Single();
            Assert.That(Number(choice.ContinuationContext, "n"), Is.EqualTo(n));
            var options = Value(choice.RawArguments, "options").Items.Select(x => x.StringValue).ToList();
            Assert.That(options.First(), Is.EqualTo("" + largest));
            Assert.That(options.Last(), Is.EqualTo("1"));
            Assert.That(options.Count, Is.EqualTo(largest));
        }
        [Test] public void MissingCapacityFactIsRejectedInsteadOfAssumingUnlimited()
        {
            using var f = new AgnesContentTestFixture(); var s = Inventory(29, -1, 0, omitCapacity: true);
            var result = f.Lua("{}", "return A.gainCommand(ctx,{},200)", snapshot: s);
            Assert.That(result.IsSuccess, Is.False); Assert.That(result.Diagnostic, Does.Contain("agnes_capacity_unavailable"));
        }
        [Test] public void EmptySupplyDoesNotCreateQuantityWindow()
        {
            using var f = new AgnesContentTestFixture();
            var result = f.Lua("{}", "return A.gainCommand(ctx,{},2)", snapshot: Inventory(0, -1, 0));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(result.Effects, Is.Empty);
        }
        [Test] public void A05CleanupPaysOneBeforeOptionalTacticAndDoesNotAddEntryWrappers()
        {
            using var f = new AgnesContentTestFixture();
            var result = f.Lua("{\"abilityId\":\"entrepreneur.A05.cleanup\",\"callableActions\":[{\"optionId\":\"payload.unauthorized\",\"enabled\":true}]}",
                snapshot: Inventory(0, -1, 0), sourceFacts: CallableFacts(1, 1));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            var condition = result.Effects.Single();
            Assert.That(condition.EffectTypeId, Is.EqualTo(EffectTypeIds.Condition));
            Assert.That(condition.NestedEffectGroups[0].Single().EffectTypeId, Is.EqualTo(ItemTransferEffectExecutor.TypeId));
            Assert.That(Number(condition.NestedEffectGroups[0].Single().RawArguments, "requiredCount"), Is.EqualTo(1));
            var choice = condition.NestedEffectGroups[1].Single();
            Assert.That(Number(choice.RawArguments, "minSelections"), Is.Zero);
            Assert.That(Number(choice.RawArguments, "maxSelections"), Is.EqualTo(1));
            Assert.That(condition.NestedEffectGroups[1].Count, Is.EqualTo(1));
        }
        [Test] public void QuickCommandCostRemainsTwo()
        {
            using var f = new AgnesContentTestFixture();
            var result = f.Lua("{}", "return A.quickCost(ctx)", snapshot: Inventory(0, -1, 0), sourceFacts: CallableFacts(0, 3));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(result.Effects.Single().EffectTypeId, Is.EqualTo(ItemTransferEffectExecutor.TypeId));
            Assert.That(Number(result.Effects.Single().RawArguments, "requiredCount"), Is.EqualTo(2));
            Assert.That(Value(result.Effects.Single().RawArguments, "itemIds").Items.Count, Is.EqualTo(2));
        }
        [Test] public void A03CreatesTwoSiblingFreeMovesInsideItsOwnCondition()
        {
            using var f = new AgnesContentTestFixture();
            var result = f.Lua("{\"abilityId\":\"entrepreneur.A03.special\"}");
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            var condition = result.Effects.Single(); Assert.That(condition.NestedEffectGroups[1].Count, Is.EqualTo(2));
            foreach (var move in condition.NestedEffectGroups[1])
            {
                Assert.That(move.EffectTypeId, Is.EqualTo("effect.city.move"));
                Assert.That(Value(move.RawArguments, "waiveBaseCost").BooleanValue, Is.True);
                Assert.That(Value(move.RawArguments, "consumeMainAction").BooleanValue, Is.False);
            }
        }
        [Test] public void A08BuildingBlocksKeepTwoPlacementsAndMandatoryEmptySafeRemovalSeparate()
        {
            using var f = new AgnesContentTestFixture();
            var places = f.Lua("{}", "return A.a08Placements(ctx)");
            var cleanup = f.Lua("{}", "return A.a08Cleanup(ctx)");
            Assert.That(places.IsSuccess, Is.True, places.Diagnostic); Assert.That(cleanup.IsSuccess, Is.True, cleanup.Diagnostic);
            Assert.That(places.Effects.Count, Is.EqualTo(2));
            Assert.That(places.Effects.All(x => x.EffectTypeId == "effect.influence.place"), Is.True);
            Assert.That(Number(cleanup.Effects.Single().RawArguments, "count"), Is.EqualTo(1));
            Assert.That(Number(cleanup.Effects.Single().RawArguments, "requiredCount"), Is.Zero);
            // 此构树例不证明登记；正式A08行动/来源退出/收尾内新增另见本文件Host用例。
        }
        [TestCase(true, true, true)] [TestCase(false, true, false)] [TestCase(true, false, false)]
        public void PriceRuleUsesActualSelectedCandidateAndActiveEntity(bool declared, bool active, bool paid)
        {
            using var f = new AgnesContentTestFixture(); var s = Selected("A06", "price", active);
            string rules = declared ? "{['candidate.custom']={'entrepreneur.rule.A.command_price'}}" : "{}";
            var result = f.Lua("{}", "return A.gainCommand(ctx," + rules + ",1)", snapshot: s);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(result.Effects.Single().EffectTypeId, Is.EqualTo(paid ? EffectTypeIds.Condition : LuaDomainEffectTypeIds.Choice));
        }
        [Test] public void PaidQuantityUsesAvailableGoldAfterReservation()
        {
            using var f = new AgnesContentTestFixture(); var s = Selected("A06", "price", true);
            var player = s.Players.Single(p => p.PlayerId == "p1");
            s.Players.Remove(player); s.Players.Add(new LuaPlayerSnapshot("p1", 0, 1000, 2, 998));
            var result = f.Lua("{}", "return A.gainCommand(ctx,{['candidate.custom']={'entrepreneur.rule.A.command_price'}},200)", snapshot: s);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(Value(result.Effects.Single().RawArguments, "options").Items.Select(x => x.StringValue), Is.EqualTo(new[] { "1" }));
        }
        [Test] public void FixedGoldCostForNinetyNineCommandsIsNotMistakenForTotalDemandCap()
        {
            using var f = new AgnesContentTestFixture(); var s = Selected("A06", "price", true);
            var purchase = f.Lua("{}", "return A.purchaseCommand(ctx,99)", snapshot: s);
            Assert.That(purchase.IsSuccess, Is.True, purchase.Diagnostic);
            var condition = purchase.Effects.Single(); var cost = Value(condition.NestedEffectGroups[0].Single().RawArguments, "cost");
            Assert.That(Number(cost, "GoldVoucher"), Is.EqualTo(198));
            Assert.That(Number(condition.NestedEffectGroups[1].Single().RawArguments, "count"), Is.EqualTo(99));
            // total需求0..99属于另一种付款mode；固定GoldVoucher成本不可被误截成99。
            // 此处仅构造固定组，不声称官方29供应能实际交付99。
        }
        [TestCase("p1", 1)] [TestCase("p2", 0)]
        public void CoverModuleReadsCompletedPlayerListRatherThanGlobalEventPlayer(string covered, int effectCount)
        {
            using var f = new AgnesContentTestFixture(); var s = Selected("A09", "cover", true);
            var result = f.Lua("{\"abilityId\":\"entrepreneur.A01.cover\",\"triggeringPlayerId\":-1,\"coveredPlayerIds\":[\"" + covered + "\"]}",
                "return A.invoke(ctx,{['candidate.custom']={'entrepreneur.rule.A.cover_command'}})", snapshot: s);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(result.Effects.Count, Is.EqualTo(effectCount));
            // G-A-COVER未接通；本例仅检查正式模块如何消费正式事件的投影形状。
        }
        [Test] public void A07IndependentUpgradeRemainsSiblingOfDeclinableGain()
        {
            using var f = new AgnesContentTestFixture(); var s = Inventory(29, -1, 0);
            var content = JObject.Parse(ContentJson(s));
            ((JArray)content["zones"]).Add(new JObject { ["id"] = "enterprises", ["templateId"] = "core.zone.enterprise_display" });
            ((JArray)content["instances"]).Add(new JObject { ["id"] = "enterprise.real", ["contentType"] = "enterprise", ["typeId"] = "core.board_card", ["zoneId"] = "enterprises" });
            ((JArray)content["instances"]).Add(new JObject { ["id"] = "enterprise.retired", ["contentType"] = "enterprise", ["typeId"] = "core.board_card", ["zoneId"] = "supply" });
            s.GlobalContent = AgnesContentTestFixture.Snapshot(content);
            var result = f.Lua("{\"abilityId\":\"entrepreneur.A07.immediate.2\"}", snapshot: s);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(result.Effects.Count, Is.EqualTo(2));
            Assert.That(Number(result.Effects[0].RawArguments, "minSelections"), Is.Zero);
            Assert.That(Number(result.Effects[1].RawArguments, "levels"), Is.EqualTo(2));
            Assert.That(Value(result.Effects[1].RawArguments, "enterpriseItemIds").Items.Single().StringValue, Is.EqualTo("enterprise.real"));
        }
        [Test] public void A05NewGainIsBuiltAfterMapOperationCompletion()
        {
            using var f = new AgnesContentTestFixture();
            var body = f.Lua("{\"abilityId\":\"entrepreneur.A05.tactic\"}");
            Assert.That(body.IsSuccess, Is.True, body.Diagnostic); Assert.That(body.Effects.Count, Is.EqualTo(1));
            Assert.That(body.Effects.Single().EffectTypeId, Is.EqualTo(LuaDomainEffectTypeIds.Choice));
            Assert.That(Value(body.Effects[0].ContinuationContext, "stage").StringValue, Is.EqualTo("after_map_gain"));
            var after = f.Lua("{\"outcome\":\"completed\",\"continuationContext\":{\"stage\":\"after_map_gain\"}}",
                eventType: "EffectCompleted", snapshot: Inventory(1, -1, 0));
            Assert.That(after.IsSuccess, Is.True, after.Diagnostic);
            Assert.That(Value(after.Effects.Single().RawArguments, "options").Items.Single().StringValue, Is.EqualTo("1"));
        }
        [Test] public void A05ChosenTacticCreatesOnlyAuthorizedDirectInvocation()
        {
            using var f = new AgnesContentTestFixture();
            var result = f.Lua("{\"outcome\":\"completed\",\"normalizedResult\":{\"value\":\"real.option\"},\"continuationContext\":{\"stage\":\"tactic_selected\",\"actions\":{\"real.option\":{\"itemId\":\"real.card\",\"actionId\":\"real.action\",\"callPolicyRef\":\"real.policy\"}}}}", eventType: "EffectCompleted");
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(result.Effects.Count, Is.EqualTo(1));
            Assert.That(Value(result.Effects[0].RawArguments, "callPolicyRef").StringValue, Is.EqualTo("real.policy"));
            // 宿主仍需复验真实policy、来源及轨道；此输入不提供授权。
        }
        [TestCase(false, 1, false)] [TestCase(true, 0, false)] [TestCase(true, 1, true)]
        public void CleanupAvailabilityRequiresRealCandidateAndOneTransferableCommand(bool candidate, int stock, bool expected)
        {
            using var f = new AgnesContentTestFixture();
            var result = f.Lua("{}", "return A.availability(ctx)", responseKind: "settlementAvailability", sourceFacts: CallableFacts(candidate ? 1 : 0, stock));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(result.SettlementAvailability.Enabled, Is.EqualTo(expected));
            // 手工只读快照仅核消费算法；真实注入前过滤在正式Host用例验证。
        }
        [Test] public void CleanupAvailabilityFailsClosedOnMissingFacts()
        {
            using var f = new AgnesContentTestFixture();
            var result = f.Lua("{}", "return A.availability(ctx)", responseKind: "settlementAvailability");
            Assert.That(result.IsSuccess, Is.False); Assert.That(result.Diagnostic, Does.Contain("source_facts_unavailable"));
        }
        [TestCase(0, 0, -1)] [TestCase(2, 2, -1)] [TestCase(3, 2, 2)]
        public void PhysicalFinalRequiresStrictLeadAndIgnoresCompanyPieces(int mine, int other, int score)
        {
            using var f = new AgnesContentTestFixture(); var s = Selected("A04", "final", true); var physical = new JArray();
            for (int i = 0; i < mine; i++) physical.Add(new JObject { ["subjectKind"] = "player", ["playerId"] = 1, ["typeId"] = i == 0 ? "entrepreneur.gang" : "core.player_marker" });
            for (int i = 0; i < other; i++) physical.Add(new JObject { ["subjectKind"] = "player", ["playerId"] = 2 });
            for (int i = 0; i < 8; i++) physical.Add(new JObject { ["subjectKind"] = "company", ["playerId"] = 2 });
            s.GlobalScoring = AgnesContentTestFixture.Snapshot(new JObject { ["physicalInfluences"] = physical, ["regions"] = new JArray() });
            var result = f.Lua("{\"abilityId\":\"entrepreneur.A01.final\"}", "return A.invoke(ctx,{['candidate.custom']={'entrepreneur.rule.A.physical_final'}})", snapshot: s);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(result.Effects.Single().EffectTypeId, Is.EqualTo(score > 0 ? ResourceEffectTypeIds.GainScore : LuaDomainEffectTypeIds.LoseScore));
            Assert.That(score > 0 ? result.Effects.Single().Amount : Number(result.Effects.Single().RawArguments, "amount"), Is.EqualTo(Math.Abs(score)));
        }
        [TestCase("[1,2]", 1)] [TestCase("[2,3]", 0)] [TestCase("[]", 0)] [TestCase("[1]", 0)]
        public void A02ClaimsOnlySelfInCompletePositiveTie(string highest, int claims)
        {
            using var f = new AgnesContentTestFixture();
            var result = f.Lua("{\"abilityId\":\"entrepreneur.A02.scoring\",\"queryKind\":\"scoringModifiers.v1\",\"queryId\":\"q.real\",\"basisDigest\":\"digest.real\",\"facts\":{\"pass\":\"tie_claim\",\"regionId\":\"region.real\",\"positiveHighest\":" + highest + "}}", responseKind: "abilityRuleQuery");
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(Value(result.AbilityQueryResult, "items").Items.Count, Is.EqualTo(claims));
            Assert.That(Value(result.AbilityQueryResult, "queryId").StringValue, Is.EqualTo("q.real"));
            Assert.That(Value(result.AbilityQueryResult, "basisDigest").StringValue, Is.EqualTo("digest.real"));
        }
        [TestCase(1, "tie_claim", "entrepreneur.A02.tie", 1)]
        [TestCase(1, "highest", "entrepreneur.A02.tie", 0)]
        [TestCase(2, "tie_claim", "entrepreneur.A02.tie", 0)]
        [TestCase(1, "tie_claim", "other.rule", 0)]
        public void A02ValueDecreaseRequiresItsOwnTieControl(int controller, string reason, string rule, int changes)
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(1, "A09", declineGain: true); f.Choose(7, "A02");
            var owner = f.Registry.QuerySources(f.State, 1).Single(s => s.AbilityId == "entrepreneur.A02.scoring");
            var sourceFacts = SourceFactsLuaCatalog.Create(f.State, f.Registry, owner);
            var snapshot = LuaFacadeSnapshot.FromInvocation("p1", 0, 0, 2, "1.0.0");
            snapshot.StateRevision = f.State.EffectRuntime.StateRevision;
            var payload = new JObject { ["abilityId"] = "entrepreneur.A02.scoring", ["queryKind"] = "scoringModifiers.v1", ["queryId"] = "q.real", ["basisDigest"] = "digest.real",
                ["facts"] = new JObject { ["pass"] = "region_value_delta", ["regionId"] = "region.real", ["controllerPlayerId"] = controller, ["controlReason"] = reason,
                    ["controlClaims"] = new JArray(new JObject { ["playerId"] = 1, ["ruleId"] = rule, ["sourceKey"] = owner.SourceKey }) } };
            var result = f.Lua(payload.ToString(), responseKind: "abilityRuleQuery", snapshot: snapshot, sourceFacts: sourceFacts);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); var items = Value(result.AbilityQueryResult, "items").Items;
            Assert.That(items.Count, Is.EqualTo(changes)); if (changes > 0) Assert.That(Number(items[0], "delta"), Is.EqualTo(-2));
        }
        [TestCase(0, 0)] [TestCase(1, 1)]
        public void A03CityModifierDoesNotRequireOrdinaryInfluence(int city, int changes)
        {
            using var f = new AgnesContentTestFixture();
            var result = f.Lua("{\"abilityId\":\"entrepreneur.A03.scoring\",\"queryKind\":\"scoringModifiers.v1\",\"queryId\":\"q.real\",\"basisDigest\":\"digest.real\",\"facts\":{\"pass\":\"influence_delta\",\"regionId\":\"region.real\",\"physicalCounts\":[{\"playerId\":1,\"count\":0}],\"cityCounts\":[{\"playerId\":1,\"count\":" + city + "}]}}", responseKind: "abilityRuleQuery");
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); var items = Value(result.AbilityQueryResult, "items").Items;
            Assert.That(items.Count, Is.EqualTo(changes)); if (changes > 0) Assert.That(Number(items[0], "delta"), Is.EqualTo(2));
        }
        [Test] public void A02FinalAddsAllControlledRegionsRegardlessOfControlReason()
        {
            using var f = new AgnesContentTestFixture(); var s = Inventory(0, -1, 0);
            s.GlobalScoring = AgnesContentTestFixture.Snapshot(new JObject { ["regions"] = new JArray(
                new JObject { ["controllerPlayerId"] = 1, ["controlReason"] = "tie_claim" },
                new JObject { ["controllerPlayerId"] = 1, ["controlReason"] = "highest" }, new JObject { ["controllerPlayerId"] = 2 }) });
            var result = f.Lua("{\"abilityId\":\"entrepreneur.A02.final\"}", snapshot: s);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(result.Effects.Single().Amount, Is.EqualTo(2));
        }
        [TestCase(1, 1)] [TestCase(2, 0)]
        public void DepartureModuleKeepsCompleteBatchNAndIgnoresOtherOwner(int owner, int effects)
        {
            using var f = new AgnesContentTestFixture();
            var result = f.Lua("{\"abilityId\":\"entrepreneur.A06.departure\",\"ownerPlayerId\":" + owner + ",\"departureCount\":200}", snapshot: Inventory(29, -1, 0));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(result.Effects.Count, Is.EqualTo(effects));
            if (effects > 0) Assert.That(Number(result.Effects[0].ContinuationContext, "n"), Is.EqualTo(200));
            // 批次封口、同物再次离场和跨来源旁路必须另做真实集成，不能由此手工输入代替。
        }
        [Test] public void CallableCandidatesReadEveryPageAndIgnorePayloadAndLegacyFacts()
        {
            using var f = new AgnesContentTestFixture();
            var result = f.Lua("{\"abilityId\":\"entrepreneur.A05.cleanup\",\"callableActions\":[{\"optionId\":\"forged\",\"enabled\":true}]}",
                snapshot: Inventory(0, -1, 0), sourceFacts: CallableFacts(9, 1, disabledLast: 2));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            var options = Value(result.Effects.Single().NestedEffectGroups[1].Single().RawArguments, "options").Items.Select(x => x.StringValue).ToArray();
            Assert.That(options, Is.EqualTo(Enumerable.Range(0, 7).Select(i => "real.option." + i).ToArray()));
            Assert.That(options, Does.Not.Contain("forged"));
            var availability = f.Lua("{}", "return A.availability(ctx)", responseKind: "settlementAvailability", sourceFacts: CallableFacts(9, 1, disabledLast: 2));
            Assert.That(availability.IsSuccess, Is.True, availability.Diagnostic); Assert.That(availability.SettlementAvailability.Enabled, Is.True);
        }
        [Test] public void A08MainBodyHasTwoIndependentPlacementsThenOneFrozenSchedule()
        {
            using var f = new AgnesContentTestFixture();
            var result = f.Lua("{\"abilityId\":\"entrepreneur.A08.tactic\"}");
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(result.Effects.Select(e => e.EffectTypeId),
                Is.EqualTo(new[] { EffectTypeIds.PlaceInfluence, EffectTypeIds.PlaceInfluence, CleanupObligation.ScheduleType }));
            var schedule = result.Effects[2]; Assert.That(Number(schedule.RawArguments, "dueRound"), Is.EqualTo(1));
            Assert.That(Value(schedule.RawArguments, "handlerRef").StringValue, Is.EqualTo("entrepreneur.A08.cleanup_due"));
        }
        [TestCase(true)][TestCase(false)]
        public void DueBodyUsesOnlyFrozenContextAndNeverRequeriesOrSchedules(bool valid)
        {
            using var f = new AgnesContentTestFixture();
            var result = f.Lua("{\"arguments\":{\"protocolVersion\":1,\"cleanupKind\":\"remove_own\",\"count\":" + (valid ? "1" : "2") + ",\"requiredCount\":0}}", "return A.cleanupDue(ctx)");
            Assert.That(result.IsSuccess, Is.EqualTo(valid), result.Diagnostic);
            if (valid) { Assert.That(result.Effects.Single().EffectTypeId, Is.EqualTo(ContentPrimitiveEffectExecutor.SelectInfluence)); Assert.That(Value(result.Effects[0].RawArguments, "allowDecline").BooleanValue, Is.False); }
        }
        [Test] public void FormalA08QuickConsumesTwoAndCreatesActualActionObligationOnce()
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(1, "A08", "2");
            const string action = "entrepreneur.A08.tactic.quick";
            Assert.That(f.Stored, Is.EqualTo(2));
            int mapBefore = f.OwnMapCount;
            f.Invoke(action);
            string declinedId = f.AssertQuickEntryPending(action);
            Assert.That(f.Stored, Is.EqualTo(2)); Assert.That(f.State.EffectRuntime.CleanupObligations, Is.Empty);
            f.Answer("decline");
            var declined = f.State.EffectRuntime.AbilityActionInvocations.Single(i => i.InvocationId == declinedId);
            Assert.That(declined.PaymentCommitted, Is.False); Assert.That(declined.ActivationCommitted, Is.False);
            Assert.That(declined.BodyEffectIds, Is.Empty);
            Assert.That(f.Stored, Is.EqualTo(2)); Assert.That(f.OwnMapCount, Is.EqualTo(mapBefore));
            Assert.That(f.State.EffectRuntime.CleanupObligations, Is.Empty);
            Assert.That(f.Action("entrepreneur.A08.tactic.special").Enabled, Is.True);
            // 外层放弃只结束本次调用；新命令仍可合法重试，原命令不能被当作新激活。
            f.Invoke(action, commandId: "qy018.A08.quick.after-decline");
            f.AssertQuickEntryPending(action); f.Answer("continue"); f.ResolveMapRequests();
            Assert.That(f.Stored, Is.Zero); Assert.That(f.OwnMapCount, Is.EqualTo(2));
            var record = f.State.EffectRuntime.CleanupObligations.Single();
            Assert.That(record.OriginKind, Is.EqualTo("action")); Assert.That(record.SourceRef.AbilityId, Is.EqualTo("entrepreneur.A08.tactic"));
            Assert.That(record.HandlerRef, Is.EqualTo("entrepreneur.A08.cleanup_due")); Assert.That(record.Status, Is.EqualTo("pending"));
            Assert.That(f.Action("entrepreneur.A08.tactic.special").Enabled, Is.False);
            f.Cleanup(); Assert.That(f.OwnMapCount, Is.EqualTo(2)); f.StartCleanupObligation(); Assert.That(f.Open.AllowDecline, Is.False); f.ResolveMapRequests();
            Assert.That(f.OwnMapCount, Is.EqualTo(1)); Assert.That(f.State.EffectRuntime.CleanupObligations.Single().Status, Is.EqualTo("completed"));
        }
        [Test] public void FormalA05WithOnlyOneCommandCallsA08WithoutEntryWrappersAndSettlesNewDutyNow()
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(1, "A08", "1"); f.Choose(5, "A05");
            int gold = f.State.FindPlayer(1).Resources.GoldVoucher, budget = f.State.FindPlayer(1).RemainingMainActionsThisTurn;
            string target = f.Action("entrepreneur.A08.tactic.quick").OptionId;
            f.Cleanup(); string cleanupWindow = f.State.EffectRuntime.ActiveSettlementWindowId; f.StartCleanupOption(); f.Answer("continue"); Assert.That(f.Stored, Is.Zero);
            Assert.That(f.Open.CandidateIds, Does.Contain(target)); f.Answer(target); f.ResolveMapRequests();
            var dueOption = f.Registry.GetSettlementOptions(f.State, 1).Single(o => o.LocalId == CleanupObligation.SettlementLocalId);
            Assert.That(dueOption.WindowId, Is.EqualTo(cleanupWindow)); Assert.That(dueOption.IsEnabled, Is.True);
            Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == dueOption.TargetEffectId).ParentEffectId, Is.EqualTo(dueOption.OwnerEffectId));
            Assert.That(f.OwnMapCount, Is.EqualTo(2)); f.StartCleanupObligation(); f.ResolveMapRequests();
            var call = f.State.EffectRuntime.AbilityActionInvocations.Single(i => i.ActionId == "entrepreneur.A08.tactic.quick");
            Assert.That(call.CallPolicyRef, Is.EqualTo("entrepreneur.A05.call.A08")); Assert.That(call.ActivationCommitted, Is.True);
            Assert.That(call.PaymentEffectIds, Is.Empty); Assert.That(call.MainActionCommitted, Is.False);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold)); Assert.That(f.State.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(budget));
            Assert.That(f.Stored, Is.Zero); Assert.That(f.OwnMapCount, Is.EqualTo(1));
            var duty = f.State.EffectRuntime.CleanupObligations.Single();
            Assert.That(duty.SourceRef.AbilityId, Is.EqualTo("entrepreneur.A08.tactic"), "真实义务来源是被激活战术，不能借A05来源身份");
            Assert.That(duty.DueRound, Is.EqualTo(5)); Assert.That(duty.Status, Is.EqualTo("completed"));
            Assert.That(f.Action("entrepreneur.A08.tactic.special").Enabled, Is.False);
        }
        [Test] public void FormalA05PaidDeclineKeepsCostAndUnusedTrackAndClosesOnce()
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(1, "A08", "1"); f.Choose(5, "A05");
            f.Cleanup(); f.StartCleanupOption(); f.Answer("continue"); var request = f.Open;
            Assert.That(f.Stored, Is.Zero); f.Decline();
            Assert.That(f.Stored, Is.Zero); Assert.That(f.State.EffectRuntime.CleanupObligations, Is.Empty);
            var track = f.State.EffectRuntime.UsageTrackBindings.Single(b => b.TrackId == "entrepreneur.A08.track" && b.PlayerId == 1);
            Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == track.MarkerItemId).SlotId, Is.EqualTo("unused"));
            Assert.That(f.Registry.GetSettlementOptions(f.State, 1).Any(o => o.LocalId == "entrepreneur.A05.cleanup.cleanup_order"), Is.False);
            Assert.That(f.Engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateBoolean(false), out _), Is.False);
        }
        [Test] public void FormalA05WithACommandButNoUnusedTacticDoesNotInject()
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(5, "A05");
            f.Invoke("entrepreneur.A05.tactic.special"); f.Answer("remove"); f.Answer("1");
            Assert.That(f.Stored, Is.EqualTo(1));
            f.Cleanup();
            Assert.That(f.State.EffectRuntime.AbilitySourceUses.Any(u => u.AbilityId == "entrepreneur.A05.cleanup"), Is.False);
            Assert.That(f.Registry.GetSettlementOptions(f.State, 1).Any(o => o.LocalId == "entrepreneur.A05.cleanup.cleanup_order"), Is.False);
        }
        [Test] public void FormalCoverRemainsIndependentOfDeclinedImmediateAndOtherOwnerIsIgnored()
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(1, "A09", declineGain: true);
            Assert.That(f.Stored, Is.Zero);
            f.Signal("CharacterCoverCompleted", -1, Object("coveredPlayerIds", NormalizedValue.CreateArray(new[] { NormalizedValue.CreateStableReference("player", "p2") })));
            Assert.That(f.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
            f.Signal("CharacterCoverCompleted", -1, Object("coveredPlayerIds", NormalizedValue.CreateArray(new[] { NormalizedValue.CreateStableReference("player", "p1") })));
            f.Answer("1"); Assert.That(f.Stored, Is.EqualTo(1));
            Assert.That(f.State.EffectRuntime.AbilitySourceUses.Count(u => u.AbilityId == "entrepreneur.A01.cover" && u.Outcome == "completed"), Is.EqualTo(2), "两次不同阶段事件各有真实来源收据，第一份无本人收益");
        }
        [Test] public void ActualIndependentH1ReadsItsOwnPoliciesWhenH0HasNoCallableActions()
        {
            using var f = new AgnesContentTestFixture(independentH1: true); f.Formal(); f.Choose(1, "A08", "1"); f.Choose(5, "A05");
            var owner = f.Registry.QuerySources(f.State, 1).Single(s => s.AbilityId == "entrepreneur.A05.cleanup");
            var facts = SourceFactsLuaCatalog.Create(f.State, f.Registry, owner);
            Assert.That(facts.Get("callableActions").Items, Is.Empty, "纯声明H0不能借用H1政策");
            var snapshot = LuaFacadeSnapshot.FromInvocation("p1", 0, 0, 2, "1.1.0"); snapshot.StateRevision = f.State.EffectRuntime.StateRevision;
            var pure = f.Lua("{}", "return A.availability(ctx)", responseKind: "settlementAvailability", snapshot: snapshot, sourceFacts: facts);
            Assert.That(pure.IsSuccess, Is.True, pure.Diagnostic); Assert.That(pure.SettlementAvailability.Enabled, Is.False);
            string target = f.Action("entrepreneur.A08.tactic.quick").OptionId;
            f.Invoke("qy013.A05.H1.action"); f.Answer("continue"); Assert.That(f.Open.CandidateIds, Does.Contain(target));
            f.Answer(target); f.ResolveMapRequests();
            var call = f.State.EffectRuntime.AbilityActionInvocations.Single(i => i.ActionId == "qy013.A05.H1.action");
            Assert.That(call.SourceRef.AbilityId, Is.EqualTo(owner.AbilityId));
            Assert.That(call.VersionRef.HandlerHash, Is.Not.EqualTo(facts.Get("sources").Items.Single(s => s.Get("sourceKey").StringValue == owner.SourceKey).Get("handlerHash").StringValue));
            Assert.That(f.State.EffectRuntime.CleanupObligations.Single().SourceRef.AbilityId, Is.EqualTo("entrepreneur.A08.tactic"));
        }
        [Test] public void FormalDirectStillOffersA07WhenItsInternalShardConditionCannotBePaid()
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(1, "A08", "1"); f.Choose(3, "A07", declineGain: true); f.Choose(5, "A05");
            int shards = f.State.FindPlayer(1).Resources.OriginiumShard;
            if (shards > 0) f.Run(ResourceEffectSpecFactory.Pay(1, ResourceType.OriginiumShard, shards));
            string target = f.Action("entrepreneur.A07.tactic.quick").OptionId;
            f.Cleanup(); f.StartCleanupOption(); f.Answer("continue"); Assert.That(f.Open.CandidateIds, Does.Contain(target));
            int mapCount = f.State.Map.Influences.Count;
            f.Answer(target);
            if (f.State.EffectRuntime.InteractionRequests.Any(q => q.Status == "open"))
            { Assert.That(f.Open.CandidateIds, Does.Contain("decline")); f.Answer("decline"); }
            var invoked = f.State.EffectRuntime.AbilityActionInvocations.Single(i => i.ActionId == "entrepreneur.A07.tactic.quick");
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ContentPrimitiveEffectExecutor.IncompleteEffect &&
                invoked.BodyEffectIds.Any(id => InfluenceDepartureProtocol.IsWithin(f.State, n.EffectId, id))), Is.False);
            Assert.That(f.State.Map.Influences.Count, Is.EqualTo(mapCount));
            Assert.That(invoked.ActivationCommitted, Is.True); Assert.That(invoked.CallPolicyRef, Is.EqualTo("entrepreneur.A05.call.A07"));
            Assert.That(f.State.FindPlayer(1).Resources.OriginiumShard, Is.Zero); Assert.That(f.Action("entrepreneur.A07.tactic.special").Enabled, Is.False);
        }
        [TestCase("A09", "A06", "A09")][TestCase("A08", "A06", "A06")]
        [TestCase("A08", "A07", "A07")][TestCase("A08", "A07", "A05")]
        public void EveryOtherOfficialTacticRemainsARealDirectCandidateAndActivatesItsSharedTrack(string first, string third, string targetFace)
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(1, first, "2"); f.Choose(3, third, declineGain: true); f.Choose(5, "A05");
            if (targetFace == "A07")
            {
                f.Artifacts.WriteLabeledText("A07直接正文隔离资金", "仅本规则正例用真实Gain原语预置2碎片，验证条件右侧；不足费分支由独立负例覆盖，正常覆盖0。");
                f.Run(ResourceEffectSpecFactory.Gain(1, ResourceType.OriginiumShard, 2, "qy007.r2.isolated.a07.funds"));
            }
            string action = "entrepreneur." + targetFace + ".tactic.quick", option = f.Action(action).OptionId;
            f.Cleanup(); f.StartCleanupOption(); f.Answer("continue"); f.AssertNoTokenPaymentSelection();
            Assert.That(f.Open.CandidateIds, Does.Contain(option)); f.Answer(option);
            for (int guard = 0; guard < 30 && f.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"); guard++)
            {
                var request = f.Open;
                if (request.CandidateIds.Contains("remove")) f.Answer(targetFace == "A06" ? "place" : "remove");
                else if (request.CandidateIds.Contains("iron")) f.Answer("iron");
                else if (request.CandidateIds.Contains("continue")) f.Answer("decline");
                else f.Answer(request.CandidateIds.First());
            }
            var invocation = f.State.EffectRuntime.AbilityActionInvocations.Single(i => i.ActionId == action);
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ContentPrimitiveEffectExecutor.IncompleteEffect &&
                invocation.BodyEffectIds.Any(id => InfluenceDepartureProtocol.IsWithin(f.State, n.EffectId, id))), Is.False);
            Assert.That(invocation.CallPolicyRef, Is.EqualTo("entrepreneur.A05.call." + targetFace)); Assert.That(invocation.ActivationCommitted, Is.True);
            Assert.That(invocation.PaymentEffectIds, Is.Empty); Assert.That(invocation.MainActionCommitted, Is.False);
            Assert.That(f.Action("entrepreneur." + targetFace + ".tactic.special").Enabled, Is.False);
        }
        internal static LuaSnapshotValue CallableFacts(int candidates, int transferable, int disabledLast = 0)
            => AgnesContentTestFixture.Snapshot(CallableFactsJson(candidates, transferable, disabledLast));

        internal static JObject CallableFactsJson(int candidates, int transferable, int disabledLast = 0)
        {
            var actions = new JArray();
            for (int i = 0; i < candidates; i++) actions.Add(new JObject { ["optionId"] = "real.option." + i, ["itemId"] = "real.card." + i,
                ["actionId"] = "real.action." + i, ["callPolicyRef"] = "real.policy." + i, ["enabled"] = i < candidates - disabledLast,
                ["sourceKey"] = "opaque.source." + i, ["handlerHash"] = "actual.execution.handler." + i });
            var tokens = new JArray(Enumerable.Range(0, transferable).Select(i => TokenFact("command." + i)));
            return new JObject { ["protocolVersion"] = 1, ["stateRevision"] = 0, ["contentHash"] = "qy013.readonly.snapshot",
                ["authorizedPlayerId"] = "p1", ["ruleOwnerSourceKey"] = "opaque.owner", ["ruleOwnerItemId"] = "real.owner", ["transferableTokenCount"] = transferable,
                ["callableActions"] = actions, ["sources"] = new JArray(new JObject { ["itemId"] = "a01", ["definitionId"] = "entrepreneur.ability.A01",
                    ["authorizedPlayerId"] = "p1", ["sourceKey"] = "opaque.a01" }), ["grants"] = new JArray(), ["transferableTokens"] = tokens };
        }
        internal static JObject TokenFact(string id, string card = "a01", string owner = "p1") => new JObject {
            ["itemId"] = id, ["typeId"] = "core.player_marker", ["physicalKind"] = "Cube", ["zoneId"] = "commands",
            ["carrierItemId"] = card, ["localZoneId"] = "commands", ["originalSupplyZoneId"] = "supply",
            ["ownerSubject"] = owner, ["controller"] = owner, ["ownerKind"] = "Player", ["ownerPlayerId"] = owner,
            ["ownerCompanyId"] = "", ["controllerKind"] = "Player", ["controllerPlayerId"] = owner, ["controllerCompanyId"] = "",
            ["localState"] = new JObject(), ["localStateDigest"] = "isolated.local-state", ["hasOpenDeparture"] = false,
            ["operations"] = new JArray("transfer.original_supply") };
        private static LuaFacadeSnapshot Selected(string face, string rule, bool active)
        {
            var s = Inventory(29, -1, 0); var content = JObject.Parse(ContentJson(s)); var items = (JArray)content["instances"];
            items[0]["roundChoices"] = new JArray(new JObject { ["selectedCandidateId"] = "candidate.custom", ["activeFaceId"] = face,
                ["candidates"] = new JArray(new JObject { ["candidateId"] = "candidate.custom", ["faceId"] = face, ["entityItemId"] = "round.entity" }) });
            items.Add(new JObject { ["id"] = "round.entity", ["controller"] = "p1", ["zoneId"] = active ? "active" : "supply" });
            s.GlobalContent = AgnesContentTestFixture.Snapshot(content); return s;
        }
        private static string ContentJson(LuaFacadeSnapshot s)
        {
            // 只转换本测试自建快照，不访问Host状态或保存文件。
            JToken Json(LuaSnapshotValue v) => v.Kind switch {
                LuaSnapshotValueKind.Object => new JObject(v.Properties.Select(e => new JProperty(e.Name, Json(e.Value)))),
                LuaSnapshotValueKind.Array => new JArray(v.Items.Select(Json)),
                LuaSnapshotValueKind.String => new JValue(v.StringValue), LuaSnapshotValueKind.Integer => new JValue(v.IntegerValue),
                LuaSnapshotValueKind.Boolean => new JValue(v.BooleanValue), _ => JValue.CreateNull() };
            return Json(s.GlobalContent).ToString();
        }
        internal static LuaFacadeSnapshot Inventory(int stock, int capacity, int pending, bool omitCapacity = false)
        {
            var s = LuaFacadeSnapshot.FromInvocation("p1", 0, 1000, 2, "1.0.0");
            var items = new JArray(new JObject { ["id"] = "a01", ["definitionId"] = "entrepreneur.ability.A01", ["controller"] = "p1", ["zoneId"] = "active", ["roundChoices"] = new JArray() });
            for (int i = 0; i < stock; i++) items.Add(new JObject { ["id"] = "marker." + i, ["definitionId"] = "core.player_marker", ["typeId"] = "core.player_marker", ["ownerSubject"] = "p1", ["controller"] = "p1", ["zoneId"] = "supply" });
            var commands = new JObject { ["id"] = "commands", ["parentId"] = "a01", ["localZoneId"] = "commands", ["templateId"] = "core.zone.card_cube_storage", ["itemCount"] = 0 };
            if (!omitCapacity) commands["capacity"] = capacity;
            s.GlobalContent = AgnesContentTestFixture.Snapshot(new JObject { ["instances"] = items, ["definitions"] = new JArray(), ["zones"] = new JArray(commands,
                new JObject { ["id"] = "supply", ["templateId"] = "core.zone.player_supply", ["owner"] = "p1" },
                new JObject { ["id"] = "active", ["templateId"] = "core.zone.entrepreneur_active" }) });
            return s;
        }
    }
}
