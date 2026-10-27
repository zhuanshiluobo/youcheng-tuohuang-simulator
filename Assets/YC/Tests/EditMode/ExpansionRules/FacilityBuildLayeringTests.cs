using System;
using System.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>Lua夹具只声明协议规则；M13正式卡面及正常页面仍由内容/UI责任人验收。</summary>
    public sealed class FacilityBuildLayeringTests : FacilityLayerTestFixture
    {

        [Test] public void QY010B2RealRequiredResponseStateAfterKeepsBuildPlayerAndPreventsEarlyBinding()
        {
            var state = State(response: true); OwnInfluence(state);
            string low = Definition(state, d => !d.Unique && !d.HasEntryEffect);
            Put(state, Card(state, low).ItemId, 0);
            int oldScore = FacilityCardDatabase.Get(state, low).Score;
            string target = Card(state, Definition(state, d => !d.Unique && d.Score > oldScore && d.HasEntryEffect && d.OnBuiltReward.Iron > 0)).ItemId;
            var source = Registry.FacilityBuildQueries.ResolveBuiltFacts(state, 1).SourceResults.Single();
            var carrier = state.ItemZones.Items.Single(i => i.DefinitionId == Token && i.OwnerSubject.PlayerId == 1);
            const string response = "return function(ctx) if ctx.payload.ownerPlayerId~=1 then return {} end; return {Effect.Choice({player=ctx.playerId,options={'p2'}})} end";
            new YC.Infrastructure.Lua.LuaEffectEventDispatcher(Registry).Register(new YC.Infrastructure.Lua.LuaHandlerDefinition(
                "QY010B2.required-response", "InfluenceRemoved", "", new YC.Infrastructure.Lua.LuaScriptDefinition(Token, QueryAbility,
                    "QY010B2.required-response", "1.0.0", response, YC.Infrastructure.Lua.LuaContentHasher.ComputeSha256(response)),
                contentInstanceId: carrier.ItemId, abilityId: QueryAbility));
            var build = Run(state, Build(state, target, 0));
            var facts = FacilityBuildJournal.Read(build);
            int score = state.FindPlayer(1).Score, iron = state.FindPlayer(1).Resources.Iron;
            Assert.That(facts.ScoreCommitted && facts.AssemblyCommitted, Is.True);
            Assert.That(facts.ObligationEffectId, Is.Not.Empty);
            Assert.That(facts.EntryEffectId, Is.Not.Empty);
            var archive = HostRecoveryService.CreateArchive(state);
            var engine = new EffectTreeExecutor(state, Registry); var remove = Open(state);
            Assert.That(engine.TrySubmitInteraction(remove.GetStableInteractionId(), 1, remove.StateRevision,
                NormalizedValue.CreateString(remove.CandidateIds[0]), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var responseRequest = Open(state);
            Assert.That(responseRequest.CandidateIds, Is.EqualTo(new[] { "p2" }));
            Assert.That(engine.GetNode(facts.ObligationEffectId).Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
            Assert.That(engine.GetNode(facts.EntryEffectId).FlowStage, Is.Empty);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(iron), "必做移除的真实响应待答时不得提前原入场");
            HostRecoveryService.AppendJournal(archive, state);
            var raw = Newtonsoft.Json.Linq.JObject.Parse(HostRecoveryService.SerializeArchive(archive));
            var checkpoint = ((Newtonsoft.Json.Linq.JArray)raw["Journal"]["Entries"]).Last(e => e["StateAfter"] is Newtonsoft.Json.Linq.JObject)["StateAfter"];
            Assert.That(((Newtonsoft.Json.Linq.JArray)checkpoint["EffectRuntime"]["EffectNodes"]).Any(n => (string)n["EffectId"] == facts.ObligationEffectId), Is.True);
            artifacts.WriteLabeledText("QY010B2必做响应待答真实StateAfter", raw.ToString());
            var restored = HostRecoveryService.RecoverJson(raw.ToString(), Registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic);
            state = restored.State; engine = new EffectTreeExecutor(state, Registry); responseRequest = Open(state);
            var before = Assert.Throws<InvalidOperationException>(() => EffectExecutionBinding.Create(state, Registry, build.EffectId,
                source.SourceRef, responseRequest.GetStableInteractionId(), 2, source.VersionRef));
            Assert.That(before.Message, Is.EqualTo("effect_execution_build_not_ready"));
            Assert.That(engine.TrySubmitInteraction(responseRequest.GetStableInteractionId(), 1, responseRequest.StateRevision,
                NormalizedValue.CreateString("p2"), out reason), Is.True, reason);
            // 真实Choice虽已提交，但移除必要响应/原entry尚未由调度结清，仍不得提前取得对手执行资格。
            var committed = Assert.Throws<InvalidOperationException>(() => EffectExecutionBinding.Create(state, Registry, build.EffectId,
                source.SourceRef, responseRequest.GetStableInteractionId(), 2, source.VersionRef));
            Assert.That(committed.Message, Is.EqualTo("effect_execution_build_not_ready"));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(engine.GetNode(facts.ObligationEffectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(engine.GetNode(facts.EntryEffectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(engine.GetNode(build.EffectId).PlayerId, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.GreaterThan(iron));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.ExecutionBinding != null), Is.Zero, "等待义务不创建伪对手scope");
            string entryEvent = engine.GetNode(facts.EntryEffectId).CompletedEventId;
            int completedIron = state.FindPlayer(1).Resources.Iron;
            state = Recover(state, "QY010B2真实必做响应与本人入场完成后");
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(completedIron));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == facts.EntryEffectId).CompletedEventId, Is.EqualTo(entryEvent));
        }

        [Test] public void FullBoardUsesPureLuaStrictScoreAndReadsCoveredBuiltInstances()
        {
            var state = State(copies: BuildFacilityService.CityBoardSlotCount + 2); OwnInfluence(state);
            string low = Definition(state, d => !d.Unique && !d.HasEntryEffect);
            int lowScore = FacilityCardDatabase.Get(state, low).Score;
            string high = Definition(state, d => !d.Unique && d.Score > lowScore);
            for (int slot = 0; slot < BuildFacilityService.CityBoardSlotCount; slot++) Put(state, Card(state, low).ItemId, slot);
            string same = Card(state, low).ItemId, higher = Card(state, high).ItemId;
            string before = RuleStateDigestService.Compute(state);
            Assert.That(Registry.FacilityBuildQueries.ResolveSlots(state, 1, same), Is.Empty);
            Assert.That(Registry.FacilityBuildQueries.ResolveSlots(state, 1, higher).Count, Is.EqualTo(BuildFacilityService.CityBoardSlotCount));
            Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(before), "纯查询不得改状态");
            string old = Cell(state, 0).TopItemId; Overlay(state, higher, 0);
            Assert.That(Registry.FacilityBuildQueries.ResolveSlots(state, 1, same), Is.Empty, "低于新顶牌或同于其他顶牌均不可叠建");
            Assert.That(Registry.FacilityBuildQueries.ResolveBuiltFacts(state, 1).ItemIds, Does.Contain(old));
            Assert.That(FacilityLayerQuery.TopCityItems(state, 1), Does.Not.Contain(old));
            Assert.That(Registry.FacilityBuildQueries.ResolveBuiltFacts(state, 2).ItemIds, Is.Empty, "另一玩家的真实纯来源不得混入本人结果");
        }

        [Test] public void CoveredUniqueFacilityStillBlocksAnotherInstance()
        {
            var state = State(); string unique = Definition(state, d => d.Unique);
            string first = Card(state, unique).ItemId; Put(state, first, 0);
            string cover = Card(state, Definition(state, d => !d.Unique)).ItemId; Overlay(state, cover, 0);
            var facts = Registry.FacilityBuildQueries.ResolveBuiltFacts(state, 1);
            Assert.That(FacilityCardDatabase.PlayerHasBuiltUniqueFacilityForInstances(state, state.FindPlayer(1),
                FacilityCardDatabase.Get(state, unique), facts.ItemIds), Is.True);
            Assert.That(facts.ItemIds.Distinct().Count(), Is.EqualTo(2));
        }

        [Test] public void CoveredColorContributesToSameReadOnlyQuoteAsTopColor()
        {
            var state = State();
            var definitions = state.ContentDefinitions.Facilities;
            var bottomDefinition = definitions.First(d => !d.Unique && d.Color != "rainbow");
            var topDefinition = definitions.First(d => !d.Unique && d.Color != "rainbow" && d.Color != bottomDefinition.Color);
            string bottom = Card(state, bottomDefinition.FacilityId).ItemId; Put(state, bottom, 0);
            string top = Card(state, topDefinition.FacilityId).ItemId; Overlay(state, top, 0);
            var discounted = definitions.First(d => d.ResourceCost.Originium > 2 && d.CostReductionPerDistinctBuiltColor.Originium > 0);
            var service = new FacilityBuildCostService();
            var full = service.GetEffectiveResourceCostForInstances(state, state.FindPlayer(1), discounted,
                Registry.FacilityBuildQueries.ResolveBuiltFacts(state, 1).ItemIds);
            var visibleOnly = service.GetEffectiveResourceCostForInstances(state, state.FindPlayer(1), discounted,
                FacilityLayerQuery.TopCityItems(state, 1));
            Assert.That(full.Originium, Is.LessThan(visibleOnly.Originium));
            var build = new BuildFacilityService(); build.UseQueries(Registry.FacilityBuildQueries);
            var sameQuote = build.WithReadOnlyQuery(state, 1, null, q => q.GetEffectiveResourceCost(discounted.FacilityId));
            Assert.That(sameQuote.Originium, Is.EqualTo(full.Originium));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void U20ScoresAndAssemblesRequiredRemovalBeforeOriginalEntry(bool withEntry)
        {
            var state = State(response: true); OwnInfluence(state);
            string low = Definition(state, d => !d.Unique && !d.HasEntryEffect);
            string old = Card(state, low).ItemId; Put(state, old, 0);
            int oldScore = FacilityCardDatabase.Get(state, low).Score;
            string target = Card(state, Definition(state, d => !d.Unique && d.HasEntryEffect == withEntry && d.Score > oldScore)).ItemId;
            int score = state.FindPlayer(1).Score;
            var build = Run(state, Build(state, target, 0)); var facts = FacilityBuildJournal.Read(build);
            Assert.That(Cell(state, 0).AllItemIds, Is.EqualTo(new[] { target, old }));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score + FacilityCardDatabase.Get(state, state.ItemZones.Items.Single(i => i.ItemId == target).DefinitionId).Score));
            Assert.That(facts.ScoreCommitted && facts.AssemblyCommitted, Is.True);
            Assert.That(facts.ObligationEffectId, Is.Not.Empty);
            Assert.That(Open(state).AllowDecline, Is.False);
            if (withEntry)
            {
                Assert.That(build.ChildEffectIds.IndexOf(facts.ObligationEffectId), Is.LessThan(build.ChildEffectIds.IndexOf(facts.EntryEffectId)));
                Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == facts.EntryEffectId).Status,
                    Is.EqualTo(EffectNodeStatus.Created).Or.EqualTo(EffectNodeStatus.Ready));
            }
            else Assert.That(facts.EntryEffectId, Is.Empty, "无入场牌仍须挂必做移除");
            state = Recover(state, "U20落位计分且必做移除等待回答");
            Answer(state, Open(state).CandidateIds[0]);
            var restored = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == build.EffectId);
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == facts.ObligationEffectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(FacilityBuildJournal.Read(restored).ScoreCommitted, Is.True);
            Evidence(state, "U20移除完成后实际后续状态"); // 原入场可能还有其自身合法请求，不跳过。
        }

        [TestCase("pay")]
        [TestCase("take")]
        [TestCase("refill")]
        [TestCase("awaiting_placement")]
        [TestCase("place")]
        [TestCase("score")]
        [TestCase("awaiting_entry")]
        public void BuildBreakpointsRecoverWithoutDuplicatePlacementScoreOrObligation(string stage)
        {
            var state = State(response: true);
            // 仅该阶段恢复夹具的初始开局顺序；保持后续完整摘要比较，不过滤恢复差异。
            state.LaunchPlayerOrder = state.Players.Select(player => player.PlayerId).ToList();
            OwnInfluence(state);
            string low = Definition(state, d => !d.Unique && !d.HasEntryEffect);
            string old = Card(state, low).ItemId; Put(state, old, 0);
            string target = Card(state, Definition(state, d => !d.Unique && !d.HasEntryEffect && d.Score > FacilityCardDatabase.Get(state, low).Score)).ItemId;
            // 未固定格时先产生正式选格请求，后续只回答其真实候选。
            var engine = new EffectTreeExecutor(state, Registry);
            string id = engine.CreateRoot(Build(state, target, -1));
            for (int guard = 0; guard < 2000 && engine.GetNode(id).FlowStage != stage; guard++)
            {
                if (!engine.Advance())
                {
                    var open = state.EffectRuntime.InteractionRequests.LastOrDefault(i => i.Status == "open");
                    Assert.That(open, Is.Not.Null, "尚未抵达目标断点且无法推进");
                    string choice = open.CandidateIds.Contains("0") ? "0" : open.CandidateIds[0];
                    Assert.That(engine.TrySubmitInteraction(open.GetStableInteractionId(), open.AnsweringPlayerId, open.StateRevision,
                        NormalizedValue.CreateString(choice), out string reason), Is.True, reason);
                }
            }
            Assert.That(engine.GetNode(id).FlowStage, Is.EqualTo(stage));
            string digest = RuleStateDigestService.Compute(state);
            state = Recover(state, "Build阶段-" + stage);
            Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(digest));
            engine = new EffectTreeExecutor(state, Registry);
            for (int guard = 0; guard < 2000 && engine.GetNode(id).Status != EffectNodeStatus.Completed; guard++)
            {
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
                var open = state.EffectRuntime.InteractionRequests.LastOrDefault(i => i.Status == "open");
                if (open == null) break;
                string choice = open.CandidateIds.Contains("0") ? "0" : open.CandidateIds[0];
                Answer(state, choice);
            }
            Assert.That(engine.GetNode(id).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Cell(state, 0).TopItemId, Is.EqualTo(target));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => FacilityBuildJournal.Read(n)?.BuildId == id), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.SourceEffectId == id && e.EventType == FacilityBuildJournal.EventType), Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(FacilityCardDatabase.Get(state, state.ItemZones.Items.Single(i => i.ItemId == target).DefinitionId).Score));
        }

        [Test] public void SourceChangedBeforePreparationDoesNotPayOrBuildFallback()
        {
            // G6取消资格仍由007主修；本例保存候选侧真实来源变化且不替选的回归。
            var state = State(); string definition = Definition(state, d => !d.Unique && !d.HasEntryEffect);
            string chosen = Card(state, definition).ItemId;
            string slot = FindZone(state, "facility_supply_0").ZoneId;
            Transfer(state, Leg(state, chosen, slot, "top"));
            var choose = Run(state, EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId,
                Object("sourceZone", NormalizedValue.CreateString("supply"), "paymentMode", NormalizedValue.CreateString("gold"), "allowDecline", NormalizedValue.CreateBoolean(true)), 1));
            Assert.That(Open(state).CandidateIds, Does.Contain(chosen));
            int balance = state.FindPlayer(1).Resources.GoldVoucher;
            Run(state, MoveSpec(1, new[] { chosen }, slot, FindZone(state, "facility_deck").ZoneId, "bottom", 1));
            var request = Open(state); var engine = new EffectTreeExecutor(state, Registry);
            engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString(chosen), out _);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(balance));
            Assert.That(FacilityLayerQuery.TopCityItems(state, 1), Is.Empty);
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.ParentEffectId == choose.EffectId && n.EffectTypeId == FacilityEntryEffectTypeIds.Build), Is.False);
            state = Recover(state, "G6来源变化未付款不替选");
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(balance));
        }

        [Test] public void ChangedSourceRefreshesBuildChoiceWithoutPaymentOrFallback()
        {
            var state = State(); string definition = Definition(state, d => !d.Unique && !d.HasEntryEffect && d.GoldVoucherCost > 0);
            string chosen = Card(state, definition).ItemId;
            Transfer(state, Leg(state, chosen, FindZone(state, "facility_supply_0").ZoneId, "top"));
            string fallback = Card(state, definition).ItemId;
            Transfer(state, Leg(state, fallback, FindZone(state, "facility_supply_1").ZoneId, "top"));
            var choose = Run(state, EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId,
                Object("sourceZone", NormalizedValue.CreateString("supply"), "paymentMode", NormalizedValue.CreateString("gold"),
                    "allowDecline", NormalizedValue.CreateBoolean(true)), 1));
            var original = Open(state); var engine = new EffectTreeExecutor(state, Registry);
            string source = state.ItemZones.Items.Single(i => i.ItemId == chosen).ZoneId;
            Assert.That(engine.TryPrepareEffectBuildPayment(1, choose.EffectId, original.GetStableInteractionId(),
                original.CandidateSetId, original.CandidateSetVersion, chosen,
                Object("paymentMode", NormalizedValue.CreateString("gold"), "expectedSourceZoneId", NormalizedValue.CreateString(source)),
                state.EffectRuntime.StateRevision, Guid.NewGuid().ToString("N"), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var preparation = state.EffectRuntime.EffectNodes.Last(n => n.Payment?.Preparation?.AuthorizationKind == "effect_build");
            string preparationId = preparation.EffectId; var pay = Open(state);
            int gold = state.FindPlayer(1).Resources.GoldVoucher, score = state.FindPlayer(1).Score;
            Run(state, MoveSpec(1, new[] { chosen }, source, FindZone(state, "facility_deck").ZoneId, "bottom", 1));
            engine = new EffectTreeExecutor(state, Registry);
            Assert.That(engine.TrySubmitInteraction(pay.GetStableInteractionId(), 1, pay.StateRevision,
                NormalizedValue.CreateString("pay"), out _), Is.False, "真实来源离开后不得确认付款");
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold));
            state = Recover(state, "G6付款预览后来源离开且未付");
            engine = new EffectTreeExecutor(state, Registry);
            Assert.That(engine.TryCancelPreparedEffectBuildPayment(1, preparationId, state.EffectRuntime.StateRevision,
                Guid.NewGuid().ToString("N"), out reason), Is.True, reason); // 007主修的合法未付取消资格。
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo(FacilitySelectionEffectExecutor.NormalBuildInteractionType));
            Assert.That(Open(state).CandidateIds, Does.Not.Contain(chosen));
            Assert.That(Open(state).CandidateIds, Does.Contain(fallback));
            Assert.That(FacilityLayerQuery.TopCityItems(state, 1), Is.Empty);
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build), Is.False);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score));
            state = Recover(state, "G6合法取消刷新后不替选");
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold));
        }

        [Test] public void LostRequiredTargetKeepsCommittedPlacementAndScoreAndFaults()
        {
            var state = State(response: true); OwnInfluence(state);
            string low = Definition(state, d => !d.Unique && !d.HasEntryEffect); Put(state, Card(state, low).ItemId, 0);
            string target = Card(state, Definition(state, d => !d.Unique && !d.HasEntryEffect && d.Score > FacilityCardDatabase.Get(state, low).Score)).ItemId;
            var build = Run(state, Build(state, target, 0)); var request = Open(state);
            string selectedInfluence = request.CandidateIds[0]; int committedScore = state.FindPlayer(1).Score;
            // 隔离故障定位：通过另一真实移除使已确认的必做目标失效，禁止改库存或伪造结果。
            var engine = new EffectTreeExecutor(state, Registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString(selectedInfluence), out string reason), Is.True, reason);
            Evidence(state, "U20必做选择已真实受理且目标仍在场");
            string competing = engine.CreateRoot(InfluenceEffectSpecFactory.RemoveInfluence(1, selectedInfluence));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.True);
            Assert.That(engine.GetNode(competing).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.PausedFault));
            Assert.That(Cell(state, 0).TopItemId, Is.EqualTo(target));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(committedScore));
            Assert.That(FacilityBuildJournal.Read(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == build.EffectId)).ObligationEffectId, Is.Not.Empty);
            Evidence(state, "U20目标失效后保留已建计分首次故障");
        }

        [Test] public void RequiredFailureCannotBecomeOrdinaryFailedOrReleaseEntry()
        {
            var state = State();
            Registry.Register(new EffectRegistration("qy010.fixture.required_failure", _ => EffectStepResult.Failed("lost_target")));
            var root = EffectSpec.Create("qy010.fixture.required_failure", playerId: 1);
            root.RequiredOutcome = new RequiredEffectOutcome { ResultField = "completedCount", ExpectedValue = 1 };
            var engine = new EffectTreeExecutor(state, Registry); string id = engine.CreateRoot(root);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.True);
            Assert.That(engine.GetNode(id).Status, Is.EqualTo(EffectNodeStatus.Faulted));
            Evidence(state, "必做失败故障首次现场");
        }
    }
}

