using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Exploration;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class RouteTollEscrowTests
    {
        [Test]
        public void ReserveProtectsStartingFundsAndSettlementDoesNotDebitReward()
        {
            var state = State(4); var owner = Owner(state); var service = Service();
            Reserve(service, state, owner, 4, Plan(Line("A1", "player:2"), Line("A2", "bank")));
            AssertBalance(state, 1, 0, 4, 4);
            Assert.That(state.FindPlayer(1).Resources.CanPay(new ResourceSet { GoldVoucher = 1 }), Is.False);
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.Zero);
            Assert.That(state.EffectRuntime.RuleEvents, Is.Empty);
            state.FindPlayer(1).Resources.GoldVoucher += 2;
            AssertBalance(state, 1, 2, 4, 6);
            EventChild(state, owner, EffectNodeStatus.Completed);
            Assert.That(service.TrySettle(state, owner, out var receipt, out bool changed).IsValid, Is.True);
            Assert.That(changed, Is.True); Assert.That(receipt.State, Is.EqualTo("settled"));
            AssertBalance(state, 1, 2, 0, 2);
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(service.TrySettle(state, owner, out var repeated, out changed).IsValid, Is.True);
            Assert.That(changed, Is.False);
            Assert.That(repeated.SettlementReceiptId, Is.EqualTo(receipt.SettlementReceiptId));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2));
        }

        [TestCase(0)] [TestCase(2)]
        public void FutureGoldCannotMakeAnInsufficientStartingBudgetValid(int budget)
        {
            var state = State(10); var owner = Owner(state); var service = Service();
            var result = service.TryReserveValidatedPlan(state, owner, "selection", "session", 0,
                budget, Plan(Line("A1", "bank"), Line("A2", "bank")), out _, out bool changed);
            Assert.That(result.IsValid, Is.False); Assert.That(changed, Is.False);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(10));
            Assert.That(owner.ContinuationData.Properties, Is.Empty);
            Assert.That(owner.ChildEffectIds, Is.Empty);
        }

        [Test]
        public void CurrentWalletMustStillCoverTheFrozenStartingBudgetPlan()
        {
            var state = State(1); var owner = Owner(state);
            var result = Service().TryReserveValidatedPlan(state, owner, "selection", "session", 0, 4,
                Plan(Line("A1", "bank"), Line("A2", "bank")), out _, out bool changed);
            Assert.That(result.IsValid, Is.False); Assert.That(changed, Is.False);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(1));
            Assert.That(owner.ContinuationData.Properties, Is.Empty);
        }

        [TestCase("company:stonehoove_security")]
        [TestCase("player:99")] [TestCase("player:1")] [TestCase("player:02")]
        [TestCase("player:-1")] [TestCase("Bank")]
        public void UnsupportedOrInvalidSubjectDoesNotBecomeAPseudoPlayer(string subject)
        {
            var state = State(4); var owner = Owner(state);
            var result = Service().TryReserveValidatedPlan(state, owner, "selection", "session", 0, 4,
                Plan(Line("A1", subject)), out _, out bool changed);
            Assert.That(result.IsValid, Is.False); Assert.That(changed, Is.False);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(4));
            Assert.That(owner.ContinuationData.Properties, Is.Empty);
        }

        [TestCase("A1")] [TestCase("unknown")]
        public void DuplicateOrUnknownRealRouteIsRejectedBeforeReservation(string secondRoute)
        {
            var state = State(4); var owner = Owner(state);
            var result = Service().TryReserveValidatedPlan(state, owner, "selection", "session", 0, 4,
                Plan(Line("A1", "bank"), Line(secondRoute, "bank")), out _, out _);
            Assert.That(result.IsValid, Is.False);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(4));
        }

        [Test]
        public void ReserveIsIdempotentButCannotReplaceItsFrozenRecipient()
        {
            var state = State(4); var owner = Owner(state); var service = Service();
            var plan = Plan(Line("A1", "bank")); Reserve(service, state, owner, 4, plan);
            Assert.That(service.TryReserveValidatedPlan(state, owner, "selection", "session", 0, 4,
                plan, out _, out bool changed).IsValid, Is.True);
            Assert.That(changed, Is.False); AssertBalance(state, 1, 2, 2, 4);
            Assert.That(service.TryReserveValidatedPlan(state, owner, "selection", "session", 0, 4,
                Plan(Line("A1", "player:2")), out _, out changed).IsValid, Is.False);
            Assert.That(changed, Is.False); AssertBalance(state, 1, 2, 2, 4);
        }

        [Test]
        public void CloningAtHeldAndSettledBoundariesPreservesOneFinancialOutcome()
        {
            var state = State(4); var owner = Owner(state); var service = Service();
            Reserve(service, state, owner, 4, Plan(Line("A1", "player:2"), Line("A2", "player:2")));
            EventChild(state, owner, EffectNodeStatus.Blocked);
            state = GameStateCloneService.DeepClone(state); owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == "explore");
            AssertBalance(state, 1, 0, 4, 4);
            Assert.That(service.TryValidateStateBase(state, out string diagnostic), Is.True, diagnostic);
            Assert.That(service.TrySettle(state, owner, out _, out _).IsValid, Is.False);
            state.EffectRuntime.EffectNodes.Single(n => n.EffectId == "event").Status = EffectNodeStatus.Completed;
            Assert.That(service.TrySettle(state, owner, out _, out bool changed).IsValid, Is.True);
            Assert.That(changed, Is.True);
            state = GameStateCloneService.DeepClone(state); owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == "explore");
            Assert.That(service.TrySettle(state, owner, out _, out changed).IsValid, Is.True);
            Assert.That(changed, Is.False); Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(4));
            AssertBalance(state, 1, 0, 0, 0);
        }

        [Test]
        public void MapChangesDuringEventDoNotRepriceFrozenPayments()
        {
            var state = State(4); var owner = Owner(state); var service = Service();
            Reserve(service, state, owner, 4, Plan(Line("A1", "player:2"), Line("A2", "bank")));
            state.Map.RoadRouteIds.Add("A1"); state.Map.RoadRouteIds.Add("A2");
            EventChild(state, owner, EffectNodeStatus.Completed);
            Assert.That(service.TrySettle(state, owner, out _, out _).IsValid, Is.True);
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(2));
            AssertBalance(state, 1, 0, 0, 0);
        }

        [Test]
        public void FailedEventWithEmptyResultCannotRefundReservation()
        {
            var state = State(4); var owner = Owner(state); var service = Service();
            Reserve(service, state, owner, 4, Plan(Line("A1", "bank"), Line("A2", "bank")));
            EventChild(state, owner, EffectNodeStatus.Failed);
            Assert.That(service.TryReleaseBeforeEventStart(state, owner, out _, out _).IsValid, Is.False);
            Assert.That(service.TrySettle(state, owner, out _, out _).IsValid, Is.False);
            AssertBalance(state, 1, 0, 4, 4);
        }

        [Test]
        public void SafePreEventReleaseReturnsExactlyOnce()
        {
            var state = State(4); var owner = Owner(state); var service = Service();
            Reserve(service, state, owner, 4, Plan(Line("A1", "bank")));
            Assert.That(service.TryReleaseBeforeEventStart(state, owner, out var first, out bool changed).IsValid, Is.True);
            Assert.That(changed, Is.True); AssertBalance(state, 1, 4, 0, 4);
            Assert.That(service.TryReleaseBeforeEventStart(state, owner, out var second, out changed).IsValid, Is.True);
            Assert.That(changed, Is.False); Assert.That(second.ReleaseReceiptId, Is.EqualTo(first.ReleaseReceiptId));
        }

        [Test]
        public void RecipientOverflowCannotPartiallyCreditAnotherPlayer()
        {
            var state = State(4); var owner = Owner(state); var service = Service();
            Reserve(service, state, owner, 4, Plan(Line("A1", "player:2"), Line("A2", "player:3")));
            state.FindPlayer(3).Resources.GoldVoucher = int.MaxValue;
            EventChild(state, owner, EffectNodeStatus.Completed);
            Assert.That(service.TrySettle(state, owner, out _, out bool changed).IsValid, Is.False);
            Assert.That(changed, Is.False); Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.Zero);
            Assert.That(state.FindPlayer(3).Resources.GoldVoucher, Is.EqualTo(int.MaxValue));
            AssertBalance(state, 1, 0, 4, 4);
        }

        [TestCase(23)] [TestCase(28)]
        public void MoreThanOfficialMapWidthIsSupportedWithinNormalizedCapacity(int lineCount)
        {
            var state = State(100); var owner = Owner(state); var map = WideMap(30);
            var service = new RouteTollEscrowService(new MapQueryService(map));
            Reserve(service, state, owner, 100, Plan(Enumerable.Range(1, lineCount).Select(i => Line("R" + i, "bank")).ToArray()));
            AssertBalance(state, 1, 100 - lineCount * 2, lineCount * 2, 100);
            Assert.That(owner.ContinuationData.IsValid(), Is.True);
        }

        [Test]
        public void TwentyNinePaymentLinesAreRejectedWithoutTruncationOrWalletMutation()
        {
            var state = State(100); var owner = Owner(state);
            var service = new RouteTollEscrowService(new MapQueryService(WideMap(30)));
            var result = service.TryReserveValidatedPlan(state, owner, "selection", "session", 0, 100,
                Plan(Enumerable.Range(1, 29).Select(i => Line("R" + i, "bank")).ToArray()), out _, out bool changed);
            Assert.That(result.IsValid, Is.False); Assert.That(changed, Is.False);
            Assert.That(result.Reason, Is.EqualTo("route_toll_receipt_capacity_exceeded"));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(100));
            Assert.That(owner.ContinuationData.Properties, Is.Empty);
        }

        [Test]
        public void OtherContinuationFieldsCountTowardTheSameCapacity()
        {
            var state = State(100); var owner = Owner(state);
            owner.ContinuationData = Object("existing", NormalizedValue.CreateArray(
                Enumerable.Range(0, 30).Select(i => NormalizedValue.CreateInteger(i)).ToList()));
            string before = owner.ContinuationData.ToDeterministicString();
            var service = new RouteTollEscrowService(new MapQueryService(WideMap(30)));
            var result = service.TryReserveValidatedPlan(state, owner, "selection", "session", 0, 100,
                Plan(Enumerable.Range(1, 22).Select(i => Line("R" + i, "bank")).ToArray()), out _, out _);
            Assert.That(result.IsValid, Is.False);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(100));
            Assert.That(owner.ContinuationData.ToDeterministicString(), Is.EqualTo(before));
        }

        [Test]
        public void ZeroCostExplorationStillHasAnIdempotentReceipt()
        {
            var state = State(0); var owner = Owner(state); var service = Service();
            Reserve(service, state, owner, 0, Plan());
            AssertBalance(state, 1, 0, 0, 0); EventChild(state, owner, EffectNodeStatus.Completed);
            Assert.That(service.TrySettle(state, owner, out var receipt, out _).IsValid, Is.True);
            Assert.That(receipt.State, Is.EqualTo("settled")); Assert.That(receipt.SettlementReceiptId, Is.Not.Empty);
        }

        [Test]
        public void NestedOwnersReadTheWalletWithoutDoubleSubtractingExistingEscrow()
        {
            var state = State(4); var owner = Owner(state); var service = Service();
            Reserve(service, state, owner, 4, Plan(Line("A1", "bank")));
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "selection2", PlayerId = 1 });
            var nested = Owner(state, "explore2", "selection2", "session2");
            Assert.That(service.TryReserveValidatedPlan(state, nested, "selection2", "session2", 0, 2,
                Plan(Line("A2", "bank")), out _, out _).IsValid, Is.True);
            AssertBalance(state, 1, 0, 4, 4);
        }

        [Test]
        public void SettledOwnerMayWaitForItsCompletionResponseWithoutHoldingFunds()
        {
            var state = State(2); var owner = Owner(state); var service = Service();
            Reserve(service, state, owner, 2, Plan(Line("A1", "bank")));
            EventChild(state, owner, EffectNodeStatus.Completed);
            Assert.That(service.TrySettle(state, owner, out _, out _).IsValid, Is.True);
            owner.Status = EffectNodeStatus.Blocked; owner.PendingOutcome = EffectPendingOutcome.Completed;
            owner.ChildEffectIds.Add("completion-response");
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "completion-response",
                ParentEffectId = owner.EffectId, EffectTypeId = "test.completion_response", Status = EffectNodeStatus.Blocked });
            Assert.That(service.TryValidateStateBase(state, out string diagnostic), Is.True, diagnostic);
            AssertBalance(state, 1, 0, 0, 0);
        }

        [Test]
        public void OrdinaryFailedOwnerCannotAbandonHeldMoney()
        {
            var state = State(2); var owner = Owner(state); var service = Service();
            Reserve(service, state, owner, 2, Plan(Line("A1", "bank"))); EventChild(state, owner, EffectNodeStatus.Failed);
            owner.Status = EffectNodeStatus.Failed;
            Assert.That(service.TryValidateStateBase(state, out _), Is.False);
            owner.Status = EffectNodeStatus.Faulted; state.EffectRuntime.Status = EffectRuntimeStatus.PausedFault;
            Assert.That(service.TryValidateStateBase(state, out string diagnostic), Is.True, diagnostic);
            AssertBalance(state, 1, 0, 2, 2);
        }

        [Test]
        public void MissingReceiptInWaitingStageCannotBecomeFreeExploration()
        {
            var state = State(2); var owner = Owner(state); owner.FlowStage = RouteTollEscrowService.AwaitingEventStage;
            Assert.That(Service().TryValidateStateBase(state, out _), Is.False);
            Assert.Throws<KernelException>(() => RouteTollEscrowService.QueryBalance(state, 1));
        }

        [Test]
        public void PublicViewShowsAvailableAndReservedWithoutExposingHostReceipts()
        {
            var state = State(4); var owner = Owner(state);
            Reserve(Service(), state, owner, 4, Plan(Line("A1", "bank"), Line("A2", "bank")));
            var view = GameStateViewProjector.ProjectForSpectator(state);
            var player = view.Players.Single(p => p.PlayerId == 1);
            Assert.That(player.Resources.GoldVoucher, Is.Zero);
            Assert.That(player.AvailableGoldVoucher, Is.Zero); Assert.That(player.ReservedGoldVoucher, Is.EqualTo(4));
            Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(view), Does.Not.Contain(RouteTollEscrowService.ReceiptField));
        }

        [Test]
        public void LuaRetainsOwnedValueAndExposesAvailableAndReserved()
        {
            const string script = "return function(ctx) local r=PlayerData.GetResources(ctx.playerId); " +
                "assert(ctx.goldVoucherCount==4 and ctx.availableGoldVoucherCount==0 and ctx.reservedGoldVoucherCount==4); " +
                "assert(r.goldVoucher==4 and r.goldVoucherCount==4 and r.availableGoldVoucherCount==0 and r.reservedGoldVoucherCount==4); " +
                "local p=PlayerData.Get(ctx.playerId); assert(p.goldVoucherCount==4 and p.availableGoldVoucherCount==0 and p.reservedGoldVoucherCount==4); return {} end";
            var definition = new LuaScriptDefinition("test.gold", "test.gold", "test.gold", "1.0.0", script, LuaContentHasher.ComputeSha256(script));
            var context = new LuaInvocationContext("test.event", "test.gold", "p1", 0, "1.0.0", 2, 0, 4, 0, 4);
            var result = new MoonSharpLuaRuntimeHost().Invoke(definition, context);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            var legacy = new LuaPlayerSnapshot("p1", 0, 4);
            Assert.That(legacy.GoldVoucherCount, Is.EqualTo(4)); Assert.That(legacy.AvailableGoldVoucherCount, Is.EqualTo(4));
            Assert.That(legacy.ReservedGoldVoucherCount, Is.Zero);
        }

        [Test]
        public void RealSealedSelectionResolvesWitnessAndReservesBeforeTheEvent()
        {
            var scenario = Production(); BeginProduction(scenario);
            var owner = scenario.State.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == ExplorationEffectTypeIds.Explore);
            AssertBalance(scenario.State, 1, 0, 4, 4);
            Assert.That(scenario.Authority.TryResolveFinalized(scenario.State, owner.ParentEffectId, out _, out var commitment).IsValid, Is.True);
            Assert.That(commitment.AcceptedPreview.ConnectivityWitness, Is.Not.Empty);
            Assert.That(commitment.AcceptedPreview.ChargeRouteIds, Is.EquivalentTo(new[] { "A1", "A2" }));
            var restored = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(scenario.State)), scenario.Registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic);
            scenario.State = restored.State;
            CompleteProduction(scenario);
            Assert.That(scenario.State.FindPlayer(1).RemainingMainActionsThisTurn, Is.Zero);
            Assert.That(scenario.State.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(1));
            Assert.That(RouteTollEscrowService.QueryBalance(scenario.State, 1).Reserved, Is.Zero);
            Assert.That(scenario.State.Logs.Count(log => log.Message.StartsWith("交付探索路费 ", StringComparison.Ordinal)), Is.EqualTo(1));
            restored = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(scenario.State)), scenario.Registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic);
            int gold = restored.State.FindPlayer(1).Resources.GoldVoucher;
            Assert.That(new EffectTreeExecutor(restored.State, scenario.Registry).RunUntilQuiescent().Faulted, Is.False);
            Assert.That(restored.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold));
            Assert.That(restored.State.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(1));
        }

        [TestCase("escrowAmount")] [TestCase("startAvailableGoldVoucher")]
        public void RawMissingZeroOrBudgetFieldCannotBeRestoredByInitializer(string missing)
        {
            var scenario = Production(missing == "escrowAmount"); BeginProduction(scenario);
            var json = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(scenario.State)));
            var node = ((JArray)json["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"]).OfType<JObject>()
                .Single(n => (string)n["EffectTypeId"] == ExplorationEffectTypeIds.Explore);
            var receipt = RawField((JObject)node["ContinuationData"], RouteTollEscrowService.ReceiptField);
            RawField(receipt, missing).Property("IntegerValue").Remove();
            var recovered = HostRecoveryService.RecoverJson(json.ToString(), scenario.Registry);
            Assert.That(recovered.Succeeded, Is.False); Assert.That(recovered.Diagnostic, Does.Contain("route_toll_raw"));
        }

        [TestCase("amount")] [TestCase("receiverSubjectKey")] [TestCase("claim")]
        public void ALocalReceiptOrClaimMutationCannotInventRestorableFunds(string mutation)
        {
            var scenario = Production(); BeginProduction(scenario);
            var json = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(scenario.State)));
            var nodes = ((JArray)json["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"]).OfType<JObject>().ToList();
            var owner = nodes.Single(n => (string)n["EffectTypeId"] == ExplorationEffectTypeIds.Explore);
            var receipt = RawField((JObject)owner["ContinuationData"], RouteTollEscrowService.ReceiptField);
            if (mutation == "claim")
            {
                var selection = nodes.Single(n => (string)n["EffectId"] == (string)owner["ParentEffectId"]);
                RawField(RawField((JObject)selection["ContinuationData"], "routeSelection"), "consumerEffectId")["StringValue"] = "";
            }
            else if (mutation == "amount") RawField(receipt, "escrowAmount")["IntegerValue"] = 2;
            else RawField((JObject)RawField(receipt, "frozenPayments")["Items"][0], "receiverSubjectKey")["StringValue"] = "player:2";
            var recovered = HostRecoveryService.RecoverJson(json.ToString(), scenario.Registry);
            Assert.That(recovered.Succeeded, Is.False); Assert.That(recovered.Diagnostic, Does.Contain("route_toll"));
        }

        private sealed class ProductionScenario
        {
            public GameState State; public EffectRegistry Registry; public RouteSelectionSessionAuthority Authority;
        }
        [Test]
        public void RawReceiptFieldsAreRequiredInJournalStateAfterAsWellAsTheSnapshot()
        {
            var scenario = Production(); BeginProduction(scenario);
            var archive = HostRecoveryService.CreateArchive(scenario.State);
            new EffectTreeExecutor(scenario.State, scenario.Registry).CreateRoot(ResourceEffectSpecFactory.Gain(1, ResourceType.GoldVoucher, 1));
            HostRecoveryService.AppendJournal(archive, scenario.State);
            var serialized = HostRecoveryService.SerializeArchive(archive);
            var baseline = HostRecoveryService.RecoverJson(serialized, scenario.Registry);
            Assert.That(baseline.Succeeded, Is.True, baseline.Diagnostic);
            var json = JObject.Parse(serialized);
            var after = (JObject)json["Journal"]["Entries"].Last["StateAfter"];
            var owner = ((JArray)after["EffectRuntime"]["EffectNodes"]).OfType<JObject>()
                .Single(n => (string)n["EffectTypeId"] == ExplorationEffectTypeIds.Explore);
            var receipt = RawField((JObject)owner["ContinuationData"], RouteTollEscrowService.ReceiptField);
            RawField(receipt, "settlementReceiptId").Property("StringValue").Remove();
            var recovered = HostRecoveryService.RecoverJson(json.ToString(), scenario.Registry);
            Assert.That(recovered.Succeeded, Is.False); Assert.That(recovered.Diagnostic, Does.Contain("route_toll_raw"));
        }
        [Test]
        public void ARealZeroCostExplorationRestoresItsExplicitZeroReceipt()
        {
            var scenario = Production(true); BeginProduction(scenario);
            AssertBalance(scenario.State, 1, 0, 0, 0);
            var restored = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(scenario.State)), scenario.Registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic); scenario.State = restored.State;
            CompleteProduction(scenario);
            var owner = scenario.State.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == ExplorationEffectTypeIds.Explore);
            Assert.That(RouteTollEscrowService.TryReadReceipt(owner, out var receipt, out var diagnostic), Is.True, diagnostic);
            Assert.That(receipt.State, Is.EqualTo("settled")); Assert.That(receipt.OriginalAmount, Is.Zero);
            Assert.That(scenario.State.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(1));
        }

        [Test]
        public void ARealEventFaultRetainsEscrowAndItsDrawAcrossRecovery()
        {
            var scenario = Production();
            scenario.Registry.RegisterEventHandler(new EffectEventHandlerRegistration("test.event_fault",
                EventCardEffectEventTypeIds.Revealed, "*", "test.event_fault",
                context => { throw new KernelException("test.event_fault", "测试事件响应故障。"); },
                role: EffectHandlerRole.Observer));
            var engine = new EffectTreeExecutor(scenario.State, scenario.Registry);
            engine.CreateRoot(EffectSpec.Create("test.route_selection", playerId: 1));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.True);
            AssertBalance(scenario.State, 1, 0, 4, 4);
            var card = scenario.State.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == EventCardEffectTypeIds.Resolve)
                .NormalizedResult.ToDeterministicString();
            int nodes = scenario.State.EffectRuntime.EffectNodes.Count;
            var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(scenario.State)), scenario.Registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            Assert.That(recovered.State.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.PausedFault));
            Assert.That(new EffectTreeExecutor(recovered.State, scenario.Registry).RunUntilQuiescent().Faulted, Is.True);
            AssertBalance(recovered.State, 1, 0, 4, 4);
            Assert.That(recovered.State.EffectRuntime.EffectNodes.Count, Is.EqualTo(nodes));
            Assert.That(recovered.State.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == EventCardEffectTypeIds.Resolve)
                .NormalizedResult.ToDeterministicString(), Is.EqualTo(card));
        }

        [Test]
        public void LegacyAlreadyPrepaidExplorationDoesNotAcquireANewEscrowOrTailPayment()
        {
            var scenario = Production(); var state = scenario.State;
            var path = new MapPath { LocationIds = new List<string> { "A-03", "A-02", "A-01" },
                RouteIds = new List<string> { "A2", "A1" } };
            var engine = new EffectTreeExecutor(state, scenario.Registry);
            engine.CreateRoot(ExplorationEffectSpecFactory.Explore(1, "A-01", path,
                InfluenceService.GetLocationSlotId("A-01", 0), null));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.Zero);
            var restored = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), scenario.Registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic); scenario.State = restored.State;
            CompleteProduction(scenario);
            Assert.That(scenario.State.Logs.Any(log => log.Message.StartsWith("交付探索路费 ", StringComparison.Ordinal)), Is.False);
            Assert.That(scenario.State.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == ExplorationEffectTypeIds.Explore)
                .All(n => n.ContinuationData.Properties.All(p => p.Name != RouteTollEscrowService.ReceiptField)), Is.True);
        }

        private static ProductionScenario Production(bool freeRoutes = false)
        {
            var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
            var config = new GameRuleConfiguration { PlayerCount = 2, EnabledExpansionIds = new List<string> { "core" } };
            var catalog = ActiveContentCatalogBuilder.Build(pack, config); config.ContentPackHash = catalog.ContentPackHash;
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1,
                new List<PlayerSeat> { new PlayerSeat { PlayerId = 1 }, new PlayerSeat { PlayerId = 2 } },
                config, 321, catalog.SetupRecipe, catalog.ContentDefinitions);
            state.GameId = "route-toll-production"; state.Phase = GamePhase.ActionRound1; state.CurrentPlayerId = 1; state.Round = 1;
            state.FindPlayer(1).Resources.GoldVoucher = freeRoutes ? 0 : 4; state.FindPlayer(1).RemainingMainActionsThisTurn = 1;
            var city = state.ItemZones.Items.Single(i => i.PhysicalKind == ItemPhysicalKind.MobileCity && i.OwnerSubject.PlayerId == 1);
            var location = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == "A-03");
            var dock = state.ItemZones.Zones.Single(z => z.ParentId == location.ZoneId && z.TemplateId == "core.zone.city_dock");
            typeof(ItemZoneStateMigrator).GetMethod("SetPosition", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { state, city, dock.ZoneId });
            var map = new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)); var influence = new InfluenceService(map);
            var decks = new EventDeckService(321); var tokens = new ResourceTokenService(); var authority = new RouteSelectionSessionAuthority(map);
            var registry = new EffectRegistry(); catalog.Register(registry); ResourceEffectExecutor.Register(registry);
            InfluenceEffectExecutor.Register(registry, influence); EventCardEffectExecutor.Register(registry, map, influence, decks, tokens);
            ExplorationEffectExecutor.Register(registry, map, new ExplorationService(map, influence, decks, tokens), decks, tokens, authority);
            if (freeRoutes)
            {
                foreach (string route in new[] { "A1", "A2" })
                {
                    var engine = new EffectTreeExecutor(state, registry);
                    engine.CreateRoot(InfluenceEffectSpecFactory.PlaceInfluence(1, InfluenceService.GetRouteSlotId(route, 0)));
                    Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
                }
            }
            registry.Register(new EffectRegistration("test.route_selection", context =>
            {
                if (context.Node.FlowStage == "waiting") return EffectStepResult.Completed();
                var options = new RouteSelectionOptions(RouteSelectionPurpose.Exploration, 1, "A-03",
                    new[] { "A-01" }, 1, 1, true, context.Node.EffectId);
                var opened = authority.TryOpen(context.State, context.Node, options, out var session);
                if (!opened.IsValid) throw new InvalidOperationException(opened.Reason);
                var prepared = authority.TryPrepareRequest(context.State, context.Node, session, new[] { "A-01" }, out var binding, out _);
                if (!prepared.IsValid) throw new InvalidOperationException(prepared.Reason);
                var draft = new RouteSelectionDraft(session.SessionId, 0, freeRoutes ? Array.Empty<string>() : new[] { "A1", "A2" },
                    new[] { "A-01" }, Array.Empty<RouteRecipientChoice>());
                var finalized = new RouteResourceSelectionService(map).TryFinalize(context.State, session, binding, draft, new[] { "A-01" }, out var commitment);
                if (!finalized.IsValid) throw new InvalidOperationException(finalized.Reason);
                var sealedResult = (ValidationResult)typeof(RouteSelectionSessionAuthority).GetMethod("Seal", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(authority, new object[] { context.State, context.Node, session, commitment });
                if (!sealedResult.IsValid) throw new InvalidOperationException(sealedResult.Reason);
                return EffectStepResult.Continue("waiting").AddChild(ExplorationEffectSpecFactory.ExploreFinalized(1,
                    context.Node.EffectId, session.SessionId, commitment.CommitmentId, commitment.AcceptedAtStateRevision,
                    "A-01", consumeMainAction: true));
            }, EffectExecutorKind.IntrinsicFlow, "1.0.0"));
            return new ProductionScenario { State = state, Registry = registry, Authority = authority };
        }
        private static void BeginProduction(ProductionScenario scenario)
        {
            var engine = new EffectTreeExecutor(scenario.State, scenario.Registry);
            engine.CreateRoot(EffectSpec.Create("test.route_selection", playerId: 1));
            var report = engine.RunUntilQuiescent(); Assert.That(report.Faulted, Is.False, scenario.State.EffectRuntime.LastFaultMessage);
            Assert.That(scenario.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.True);
        }
        private static void CompleteProduction(ProductionScenario scenario)
        {
            for (int i = 0; i < 40; i++)
            {
                var request = scenario.State.EffectRuntime.InteractionRequests.LastOrDefault(r => r.Status == "open");
                if (request == null) break;
                var values = request.CandidateIds.Take(Math.Max(1, request.MinSelections)).Select(NormalizedValue.CreateString).ToList();
                var answer = values.Count == 1 ? values[0] : NormalizedValue.CreateArray(values);
                var engine = new EffectTreeExecutor(scenario.State, scenario.Registry);
                Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, answer, out var reason), Is.True, reason);
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, scenario.State.EffectRuntime.LastFaultMessage);
            }
            Assert.That(scenario.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
        }
        private static JObject RawField(JObject value, string name) => (JObject)((JArray)value["Properties"])
            .OfType<JObject>().Single(e => (string)e["Name"] == name)["Value"];

        private static RouteTollEscrowService Service() => new RouteTollEscrowService(new MapQueryService(StaticMapDefinitions.CreateThreePlayerMap()));
        private static GameState State(int gold)
        {
            var state = new GameState { GameId = "escrow-fixture", MapId = StaticMapDefinitions.ThreePlayerMapId };
            state.Players.Add(new PlayerState { PlayerId = 1, Resources = new ResourceSet { GoldVoucher = gold } });
            state.Players.Add(new PlayerState { PlayerId = 2, Resources = new ResourceSet() });
            state.Players.Add(new PlayerState { PlayerId = 3, Resources = new ResourceSet() });
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "selection", PlayerId = 1 });
            return state;
        }
        private static EffectNodeRuntimeState Owner(GameState state, string id = "explore", string parent = "selection", string session = "session")
        {
            var owner = new EffectNodeRuntimeState { EffectId = id, ParentEffectId = parent, PlayerId = 1,
                EffectTypeId = ExplorationEffectTypeIds.Explore, DefinitionVersion = "1.0.0", Status = EffectNodeStatus.Ready,
                NormalizedArguments = Object("paymentProtocol", NormalizedValue.CreateString(RouteTollEscrowService.Protocol),
                    "selectionOwnerEffectId", NormalizedValue.CreateStableReference("effect", parent),
                    "selectionSessionId", NormalizedValue.CreateString(session), "selectionRevision", NormalizedValue.CreateInteger(0)) };
            state.EffectRuntime.EffectNodes.Add(owner); return owner;
        }
        private static void Reserve(RouteTollEscrowService service, GameState state, EffectNodeRuntimeState owner, int budget, NormalizedValue plan)
        {
            var result = service.TryReserveValidatedPlan(state, owner, "selection", "session", 0, budget, plan, out _, out bool changed);
            Assert.That(result.IsValid, Is.True, result.Reason); Assert.That(changed, Is.True);
        }
        private static void EventChild(GameState state, EffectNodeRuntimeState owner, EffectNodeStatus status)
        {
            owner.FlowStage = RouteTollEscrowService.AwaitingEventStage; owner.ChildEffectIds.Add("event");
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "event", ParentEffectId = owner.EffectId,
                EffectTypeId = EventCardEffectTypeIds.Resolve, PlayerId = 1, Status = status });
        }
        private static NormalizedValue Line(string route, string receiver) => Object(
            "routeId", NormalizedValue.CreateStableReference("route", route),
            "receiverSubjectKey", NormalizedValue.CreateString(receiver), "amount", NormalizedValue.CreateInteger(2));
        private static NormalizedValue Plan(params NormalizedValue[] lines) => NormalizedValue.CreateArray(lines.ToList());
        private static NormalizedValue Object(params object[] fields)
        {
            var entries = new List<NormalizedValueEntry>();
            for (int i = 0; i < fields.Length; i += 2) entries.Add(new NormalizedValueEntry { Name = (string)fields[i], Value = (NormalizedValue)fields[i + 1] });
            return NormalizedValue.CreateObject(entries);
        }
        private static void AssertBalance(GameState state, int player, int available, int reserved, int owned)
        {
            var balance = RouteTollEscrowService.QueryBalance(state, player);
            Assert.That(balance.Available, Is.EqualTo(available)); Assert.That(balance.Reserved, Is.EqualTo(reserved));
            Assert.That(balance.Owned, Is.EqualTo(owned));
        }
        private static GameMapDefinition WideMap(int routes)
        {
            var map = StaticMapDefinitions.CreateThreePlayerMap();
            map.Routes = Enumerable.Range(1, routes).Select(i => new MapRouteDefinition {
                RouteId = "R" + i, FromLocationId = "A-01", ToLocationId = "A-02",
                CoveredLocationIds = new List<string> { "A-01", "A-02" } }).ToList();
            return map;
        }
    }
}
