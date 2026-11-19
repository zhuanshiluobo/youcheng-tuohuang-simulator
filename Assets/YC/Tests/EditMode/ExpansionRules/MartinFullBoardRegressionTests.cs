using System.Linq;
using System.Collections.Generic;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 隔离复现：正式马丁脚本和实体设施；人工摆放只用于构造满板前提，不计正常对局覆盖。
    public sealed class MartinFullBoardRegressionTests : MartinContentTestFixture
    {
        private GameState FullBoard(bool withInfluence, out ItemInstanceState target)
        {
            var state = State("entrepreneur.round.M.7", true);
            Assert.That(Pack.ActiveDefinitions.Count(value => value.DefinitionId == "entrepreneur.round.M.7"), Is.EqualTo(1), "隔离内容须含正式第七回合定义");
            Assert.That(state.ItemZones.Items.Count(value => value.DefinitionId == "entrepreneur.round.M.7" && value.ControllerSubject.PlayerId == 1), Is.EqualTo(1), "隔离玩家须有正式第七回合牌");
            ActivateFace(state, "entrepreneur.round.M.7", "M12");
            for (int slot = 0; slot < BuildFacilityService.CityBoardSlotCount; slot++)
            {
                string definition = slot < 6 ? "building_014" : "building_019";
                var item = Card(state, definition);
                Assert.That(FacilityCardDatabase.Get(state, item.DefinitionId).Score, Is.EqualTo(0));
                Position(state, item, Cell(state, slot).CellZoneId);
            }
            target = Supply(state, "building_022", 0);
            Assert.That(FacilityCardDatabase.Get(state, target.DefinitionId).Score, Is.GreaterThan(0));
            if (withInfluence) OwnInfluence(state);
            Assert.That(BuildFacilityService.FindFirstEmptyCityBoardSlot(state, 1), Is.EqualTo(-1));
            return state;
        }

        [TestCase(true)] [TestCase(false)]
        public void MainActionPaymentQuoteUsesActualLayeredSlotRulesWhenBoardIsFull(bool withInfluence)
        {
            var state = FullBoard(withInfluence, out var target);
            var arguments = Object("paymentMode", NormalizedValue.CreateString("gold"),
                "expectedSourceZoneId", NormalizedValue.CreateString(target.ZoneId));
            var legalSlots = Registry.FacilityBuildQueries.ResolveSlots(state, 1, target.ItemId);
            Assert.That(legalSlots.Count > 0, Is.EqualTo(withInfluence));
            Assert.That(ResourcePaymentPreparationService.TryQuote(state, 1, "build", target.ItemId, arguments,
                out var cost, out var reason, Registry), Is.EqualTo(withInfluence), reason);
            if (withInfluence)
            {
                Assert.That(cost.GoldVoucher, Is.EqualTo(FacilityCardDatabase.Get(state, target.DefinitionId).GoldVoucherCost));
                Assert.That(ResourcePaymentPreparationService.TryQuote(state, 1, "build", target.ItemId, arguments,
                    out _, out reason, buildQueries: Registry.FacilityBuildQueries), Is.True, reason);
                arguments.Properties.Add(new NormalizedValueEntry { Name = "cityBoardSlotIndex", Value = NormalizedValue.CreateInteger(0) });
                arguments.Properties.Sort((left, right) => System.StringComparer.Ordinal.Compare(left.Name, right.Name));
                Assert.That(ResourcePaymentPreparationService.TryQuote(state, 1, "build", target.ItemId, arguments,
                    out _, out reason, Registry), Is.True, reason);
                arguments.Properties.Single(value => value.Name == "cityBoardSlotIndex").Value = NormalizedValue.CreateInteger(12);
                Assert.That(ResourcePaymentPreparationService.TryQuote(state, 1, "build", target.ItemId, arguments,
                    out _, out _, Registry), Is.False, "非法槽位仍不可支付。");
            }
        }

        [Test]
        public void MainActionFullBoardPaysAndPlacesUsingRealOverlayObligation()
        {
            var state = FullBoard(true, out var target);
            var builder = new RoundMainChainBuilder();
            var order = new List<int> { 1, 2, 3 };
            var chain = builder.Build(state.GameId, builder.CreateRoundExecutionId(state.GameId, 2, order), 2, order, 1);
            var window = chain.Nodes.Single(node => node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow &&
                node.ActionRound == 2 && node.PlayerId == 1);
            foreach (var node in chain.Nodes.Where(node => node.MainlineIndex < window.MainlineIndex)) node.Status = EffectNodeStatus.Completed;
            var runtime = state.EffectRuntime;
            runtime.CurrentRoundExecutionId = chain.RoundExecutionId; runtime.CurrentRoundNumber = 2;
            runtime.CurrentRoundStartPlayerId = 1; runtime.PlayerOrderSnapshot = order; runtime.FirstMainNodeId = chain.FirstNodeId;
            runtime.MainNodes.AddRange(chain.Nodes); runtime.ActiveMainNodeId = window.NodeId; window.Status = EffectNodeStatus.Ready;
            var advanced = new RoundExecutionService(Registry).Advance(state);
            Assert.That(advanced.IsValid, Is.True, advanced.Reason);
            int initialGold = state.FindPlayer(1).Resources.GoldVoucher;
            int initialBudget = state.FindPlayer(1).RemainingMainActionsThisTurn;
            string oldTop = Cell(state, 0).TopItemId;
            var arguments = Object("paymentMode", NormalizedValue.CreateString("gold"),
                "expectedSourceZoneId", NormalizedValue.CreateString(target.ZoneId));
            var engine = new EffectTreeExecutor(state, Registry);
            Assert.That(engine.TryPrepareActionPayment(1, "build", target.ItemId, arguments, runtime.StateRevision,
                "fixture.full-board.prepare", out string owner, out _, out _, out string reason), Is.True, reason);
            var command = new GameCommand { CommandId = "fixture.full-board.build", PlayerId = 1,
                Kind = GameCommandKind.BuildFacility, TargetId = target.ItemId };
            command.Parameters[BuildFacilityCommandHandler.PaymentModeParameter] = "gold";
            command.Parameters[BuildFacilityCommandHandler.ExpectedSourceZoneIdParameter] = target.ZoneId;
            command.Parameters[BuildFacilityCommandHandler.PaymentPreparationOwnerEffectIdParameter] = owner;
            var result = new BuildFacilityCommandHandler(new BuildFacilityService(), new RoundAdvanceService(), Registry).Handle(state, command);
            Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo(FacilityBuildEffectExecutor.PlacementInteractionType));
            Answer(state, NormalizedValue.CreateString("0"));
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("effect.influence.remove.target"));
            Answer(state, NormalizedValue.CreateString(Open(state).CandidateIds.First()));
            Assert.That(Cell(state, 0).TopItemId, Is.EqualTo(target.ItemId));
            Assert.That(Cell(state, 0).CoveredItemIds, Does.Contain(oldTop));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher,
                Is.EqualTo(initialGold - FacilityCardDatabase.Get(state, target.DefinitionId).GoldVoucherCost));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(initialBudget - 1));
        }

        [Test]
        public void M12FullBoardEffectCanUseRealLayerRulesAndCompleteOverlay()
        {
            var state = FullBoard(true, out var target);
            var player = state.FindPlayer(1);
            int pure = player.Resources.PureOriginium, gold = player.Resources.GoldVoucher;
            int remaining = player.RemainingMainActionsThisTurn;
            string originalTop = Cell(state, 0).TopItemId;
            Action(state, "M12");
            Answer(state, NormalizedValue.CreateString("continue"));
            var request = Open(state);
            Assert.That(request.InteractionTypeId, Is.EqualTo(FacilitySelectionEffectExecutor.NormalBuildInteractionType));
            Assert.That(request.CandidateIds, Does.Contain(target.ItemId));
            Assert.That(state.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(pure - 1));
            var visible = GameStateViewProjector.ProjectForPlayer(state, 1, Registry);
            var factory = System.Type.GetType("YC.Presentation.PlayerUiReadStateFactory, Assembly-CSharp", true);
            var publicState = (GameState)factory.GetMethod("Create", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Invoke(null, new object[] { state, visible, 1 });
            Assert.That(publicState, Is.Not.Null);
            var options = new BuildFacilityOptionQueryService(new BuildFacilityService(), Registry.FacilityBuildCandidates)
                .QueryForEffectBuild(publicState, 1, visible, request.OwnerEffectId);
            var option = options.Single(value => value.FacilityId == target.ItemId);
            Assert.That(option.PreparationValidation.IsValid, Is.True, option.PreparationValidation.Reason);
            Assert.That(option.SlotOptions.Any(value => value.CityBoardSlotIndex == 0 && value.IsLegal), Is.True);
            PrepareGoldBuild(state, target.ItemId, target.ZoneId);
            Answer(state, NormalizedValue.CreateString("pay"));
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo(FacilityBuildEffectExecutor.PlacementInteractionType));
            Answer(state, NormalizedValue.CreateString("0"));
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("effect.influence.remove.target"));
            Answer(state, NormalizedValue.CreateString(Open(state).CandidateIds.First()));
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo("effect.influence.place.target"));
            Answer(state, NormalizedValue.CreateString(Open(state).CandidateIds.First()));
            Assert.That(Cell(state, 0).TopItemId, Is.EqualTo(target.ItemId));
            Assert.That(Cell(state, 0).CoveredItemIds, Does.Contain(originalTop));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher,
                Is.EqualTo(gold - FacilityCardDatabase.Get(state, target.DefinitionId).GoldVoucherCost));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(remaining));
            Assert.That(state.EffectRuntime.AbilityActionInvocations.Single(value => value.ActionId == "entrepreneur.face.M12.action").Outcome,
                Is.EqualTo("completed"));
        }
    }
}
