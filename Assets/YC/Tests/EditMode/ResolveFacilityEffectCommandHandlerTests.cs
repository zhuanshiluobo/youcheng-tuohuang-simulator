using System.Collections.Generic;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Exploration;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class ResolveFacilityEffectCommandHandlerTests
    {
        [Test]
        public void SellResources_ValidAmounts_DeductsResourcesAndAddsGold()
        {
            var fixture = CreateFixture();
            var player = fixture.State.FindPlayer(1);
            player.Resources = new ResourceSet
            {
                Originium = 2,
                OriginiumShard = 3,
                Iron = 1,
                PureOriginium = 1
            };
            OpenEffect(fixture.State, "building_019");

            var result = fixture.Handler.Handle(fixture.State, ResolveCommand(fixture.State,
                FacilityPendingChoiceTypes.ConfirmOption,
                new Dictionary<string, string>
                {
                    { ResolveFacilityEffectCommandHandler.OriginiumAmountParameter, "2" },
                    { ResolveFacilityEffectCommandHandler.OriginiumShardAmountParameter, "1" },
                    { ResolveFacilityEffectCommandHandler.IronAmountParameter, "1" },
                    { ResolveFacilityEffectCommandHandler.PureOriginiumAmountParameter, "1" }
                }));

            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(player.Resources.Originium, Is.Zero);
            Assert.That(player.Resources.OriginiumShard, Is.EqualTo(2));
            Assert.That(player.Resources.Iron, Is.Zero);
            Assert.That(player.Resources.PureOriginium, Is.Zero);
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(28));
            Assert.That(fixture.State.PendingCardSession, Is.Null);
        }

        [Test]
        public void SellResources_OverSelling_FailsWithoutChangingResourcesOrPendingChoice()
        {
            var fixture = CreateFixture();
            var player = fixture.State.FindPlayer(1);
            player.Resources.Originium = 1;
            OpenEffect(fixture.State, "building_019");
            var pending = fixture.State.PendingCardSession;

            var result = fixture.Handler.Handle(fixture.State, ResolveCommand(fixture.State,
                FacilityPendingChoiceTypes.ConfirmOption,
                new Dictionary<string, string>
                {
                    { ResolveFacilityEffectCommandHandler.OriginiumAmountParameter, "2" }
                }));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(player.Resources.Originium, Is.EqualTo(1));
            Assert.That(player.Resources.GoldVoucher, Is.Zero);
            Assert.That(fixture.State.PendingCardSession, Is.SameAs(pending));
        }

        [Test]
        public void SellResources_NegativeAmount_FailsWithoutChangingResourcesOrPendingChoice()
        {
            var fixture = CreateFixture();
            var player = fixture.State.FindPlayer(1);
            player.Resources.Originium = 1;
            player.Resources.GoldVoucher = 7;
            OpenEffect(fixture.State, "building_019");
            var pending = fixture.State.PendingCardSession;

            var result = fixture.Handler.Handle(fixture.State, ResolveCommand(fixture.State,
                FacilityPendingChoiceTypes.ConfirmOption,
                new Dictionary<string, string>
                {
                    { ResolveFacilityEffectCommandHandler.OriginiumAmountParameter, "-1" }
                }));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(result.Validation.Reason, Is.EqualTo("出售数量必须是非负整数。"));
            Assert.That(player.Resources.Originium, Is.EqualTo(1));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(7));
            Assert.That(fixture.State.PendingCardSession, Is.SameAs(pending));
        }

        [Test]
        public void MiningPowerShovel_DistributionMustTotalExactlyFive()
        {
            var fixture = CreateFixture();
            OpenEffect(fixture.State, "building_029");

            var invalid = fixture.Handler.Handle(fixture.State, ResolveCommand(fixture.State,
                FacilityPendingChoiceTypes.ConfirmOption,
                new Dictionary<string, string>
                {
                    { ResolveFacilityEffectCommandHandler.OriginiumAmountParameter, "2" },
                    { ResolveFacilityEffectCommandHandler.OriginiumShardAmountParameter, "2" }
                }));
            Assert.That(invalid.Succeeded, Is.False);
            Assert.That(fixture.State.PendingCardSession, Is.Not.Null);

            var valid = fixture.Handler.Handle(fixture.State, ResolveCommand(fixture.State,
                FacilityPendingChoiceTypes.ConfirmOption,
                new Dictionary<string, string>
                {
                    { ResolveFacilityEffectCommandHandler.OriginiumAmountParameter, "2" },
                    { ResolveFacilityEffectCommandHandler.OriginiumShardAmountParameter, "2" },
                    { ResolveFacilityEffectCommandHandler.IronAmountParameter, "1" }
                }));

            Assert.That(valid.Succeeded, Is.True, valid.Validation.Reason);
            Assert.That(fixture.State.FindPlayer(1).Resources.Originium, Is.EqualTo(2));
            Assert.That(fixture.State.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(2));
            Assert.That(fixture.State.FindPlayer(1).Resources.Iron, Is.EqualTo(1));
        }

        [Test]
        public void LogisticsHub_BuildsSelectedUnusedExtensionForFreeAndScoresMinusOne()
        {
            var fixture = CreateFixture();
            OpenEffect(fixture.State, "building_012");

            var result = fixture.Handler.Handle(fixture.State, ResolveCommand(fixture.State,
                FacilityCardDatabase.ExtensionHubBlue,
                new Dictionary<string, string>
                {
                    { BuildFacilityCommandHandler.CityBoardSlotIndexParameter, "3" }
                }));

            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(fixture.State.FindPlayer(1).Score, Is.EqualTo(-1));
            Assert.That(fixture.State.FindPlayer(1).BuiltFacilityIds,
                Does.Contain(FacilityCardDatabase.ExtensionHubBlue));
            Assert.That(fixture.State.Map.Facilities.Exists(placement =>
                placement.FacilityCardId == FacilityCardDatabase.ExtensionHubBlue &&
                placement.CityBoardSlotIndex == 3), Is.True);
        }

        [Test]
        public void AdditionalBuild_UsesOriginalSupplyOptionsAndResolvesNewFacilityEntry()
        {
            var fixture = CreateFixture();
            fixture.State.Decks.FacilitySupply.Add("building_010");
            fixture.State.Decks.FacilitySupply.Add("building_014");
            fixture.State.Decks.FacilityDeck.Add("building_015");
            var player = fixture.State.FindPlayer(1);
            player.Resources = new ResourceSet { Originium = 1, OriginiumShard = 1, Iron = 1 };
            OpenEffect(fixture.State, "building_010");

            var result = fixture.Handler.Handle(fixture.State, ResolveCommand(fixture.State,
                "building_014",
                new Dictionary<string, string>
                {
                    { BuildFacilityCommandHandler.CityBoardSlotIndexParameter, "2" },
                    { BuildFacilityCommandHandler.PaymentModeParameter, BuildFacilityService.PaymentModeResources }
                }));

            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(player.BuiltFacilityIds, Does.Contain("building_014"));
            Assert.That(player.Resources.OriginiumShard, Is.EqualTo(6));
            Assert.That(fixture.State.Decks.FacilitySupply[1], Is.EqualTo("building_015"));
        }

        [Test]
        public void ReplaceInfluence_ResolvesMercenaryCommandPendingChoice()
        {
            var fixture = CreateFixture();
            var slotId = InfluenceService.GetRouteSlotId("A1", 0);
            fixture.State.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = slotId,
                RouteId = "A1"
            });
            OpenEffect(fixture.State, "building_034");

            var result = fixture.Handler.Handle(
                fixture.State,
                ResolveCommand(fixture.State, slotId, null));

            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(fixture.Influence.FindInfluence(fixture.State, slotId).PlayerId, Is.EqualTo(1));
        }

        [Test]
        public void HighPerformancePower_FreeMoveWaivesCostAndDeploysInfluenceOnTraversedRoute()
        {
            var fixture = CreateFixture();
            var player = fixture.State.FindPlayer(1);
            player.Resources.OriginiumShard = 0;
            fixture.State.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = "A-02",
                ResourceType = ResourceType.Iron,
                Amount = 1
            });
            OpenEffect(fixture.State, "building_025");
            var routeSlotId = InfluenceService.GetRouteSlotId("A1", 0);

            var result = fixture.Handler.Handle(fixture.State, ResolveCommand(fixture.State,
                FacilityPendingChoiceTypes.ConfirmOption,
                new Dictionary<string, string>
                {
                    { ResolveFacilityEffectCommandHandler.TargetLocationIdParameter, "A-02" },
                    { ResolveFacilityEffectCommandHandler.RouteInfluenceSlotIdParameter, routeSlotId }
                }));

            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(player.CityLocationId, Is.EqualTo("A-02"));
            Assert.That(player.Resources.OriginiumShard, Is.Zero, "设施免费移动不应支付正常移动费用。");
            Assert.That(fixture.Influence.FindInfluence(fixture.State, routeSlotId), Is.Not.Null);
            Assert.That(fixture.Influence.FindInfluence(fixture.State, routeSlotId).PlayerId, Is.EqualTo(1));
            Assert.That(fixture.State.PendingCardSession, Is.Null);
        }

        [Test]
        public void HighPerformancePower_WhenTraversedRouteHasNoFreeSlot_MoveStillSucceeds()
        {
            var fixture = CreateFixture();
            var player = fixture.State.FindPlayer(1);
            player.Resources.OriginiumShard = 0;
            fixture.State.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = "A-02",
                ResourceType = ResourceType.Iron,
                Amount = 1
            });
            var routeSlotId = InfluenceService.GetRouteSlotId("A1", 0);
            fixture.State.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = routeSlotId,
                RouteId = "A1"
            });
            OpenEffect(fixture.State, "building_025");

            var result = fixture.Handler.Handle(fixture.State, ResolveCommand(fixture.State,
                FacilityPendingChoiceTypes.ConfirmOption,
                new Dictionary<string, string>
                {
                    { ResolveFacilityEffectCommandHandler.TargetLocationIdParameter, "A-02" },
                    { ResolveFacilityEffectCommandHandler.RouteInfluenceSlotIdParameter, routeSlotId }
                }));

            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(player.CityLocationId, Is.EqualTo("A-02"));
            Assert.That(player.Resources.OriginiumShard, Is.Zero);
            Assert.That(fixture.State.Map.Influences.FindAll(influence =>
                influence.RouteId == "A1"), Has.Count.EqualTo(1),
                "经过航道没有空位时应保留移动结果并跳过附加部署。");
            Assert.That(fixture.State.PendingCardSession, Is.Null);
        }

        [Test]
        public void HighPerformancePower_EventMoveReusesNormalEventsWithoutConsumingMainAction()
        {
            var fixture = CreateFixture();
            fixture.State.Decks.EventDeckGreen.Add("event_green_01");
            OpenEffect(fixture.State, "building_025");

            var begin = fixture.Handler.Handle(fixture.State, ResolveCommand(
                fixture.State,
                FacilityPendingChoiceTypes.ConfirmOption,
                new Dictionary<string, string>
                {
                    { ResolveFacilityEffectCommandHandler.TargetLocationIdParameter, "A-02" }
                }));

            Assert.That(begin.Succeeded, Is.True, begin.Validation.Reason);
            Assert.That(begin.Events.Exists(item => item.Kind == GameEventKind.CityMoved), Is.True);
            Assert.That(begin.Events.Exists(item => item.Kind == GameEventKind.CardMoved), Is.True);
            Assert.That(begin.Events.Exists(item => item.Kind == GameEventKind.ChoiceOpened), Is.True);
            Assert.That(fixture.State.FindPlayer(1).ActedMainActionThisTurn, Is.False);

            var resolved = fixture.MoveHandler.Handle(fixture.State, new GameCommand
            {
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = 1,
                OptionIds = { "0" }
            });

            Assert.That(resolved.Succeeded, Is.True, resolved.Validation.Reason);
            Assert.That(fixture.State.FindPlayer(1).ActedMainActionThisTurn, Is.False);
        }

        [Test]
        public void VehicleWarehouse_ExploreBranchUsesNormalExploreCostAndOpensEventChoice()
        {
            var fixture = CreateFixture();
            var player = fixture.State.FindPlayer(1);
            player.Resources.GoldVoucher = 5;
            fixture.State.Decks.EventDeckGreen.Add("event_green_01");
            var routeSlotId = InfluenceService.GetRouteSlotId("A1", 0);
            fixture.State.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = routeSlotId,
                RouteId = "A1"
            });
            OpenEffect(fixture.State, "building_039");

            var result = fixture.Handler.Handle(fixture.State, ResolveCommand(fixture.State,
                FacilityPendingChoiceTypes.ExploreOption,
                new Dictionary<string, string>
                {
                    { ResolveFacilityEffectCommandHandler.TargetLocationIdParameter, "A-02" }
                }));

            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(3), "载具仓库的探索分支仍应支付正常航道路费。");
            Assert.That(fixture.State.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(fixture.State.Map.ResourceTokens.Exists(token => token.LocationId == "A-02"), Is.True);
            Assert.That(fixture.State.Decks.EventDeckGreen, Is.Empty);
            Assert.That(fixture.State.PendingCardSession, Is.Not.Null);
            Assert.That(fixture.State.PendingCardSession.ScenarioId,
                Is.Not.EqualTo(FacilityPendingChoiceTypes.ScenarioId));
            Assert.That(fixture.State.PendingCardSession.CardId, Is.EqualTo("event_green_01"));
            Assert.That(result.Events.Exists(item => item.Kind == GameEventKind.CardMoved), Is.True);
            Assert.That(result.Events.Exists(item => item.Kind == GameEventKind.ResourceChanged), Is.True);
            Assert.That(result.Events.Exists(item => item.Kind == GameEventKind.ChoiceOpened), Is.True);
            Assert.That(player.ActedMainActionThisTurn, Is.False,
                "设施入场提供的探索不应额外消耗一次主要行动。");

            var resolved = fixture.ExploreHandler.Handle(fixture.State, new GameCommand
            {
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = 1,
                OptionIds = { "0" }
            });
            Assert.That(resolved.Succeeded, Is.True, resolved.Validation.Reason);
            Assert.That(player.ActedMainActionThisTurn, Is.False,
                "赠送探索在事件选择结算后也不应额外消耗主要行动。");
        }

        [Test]
        public void EscortDispatchCenter_WhenOneOfTwoRequestedSlotsIsIllegal_FailsAtomically()
        {
            var fixture = CreateFixture();
            var legalSlotId = InfluenceService.GetRouteSlotId("A1", 0);
            OpenEffect(fixture.State, "building_037");

            var result = fixture.Handler.Handle(fixture.State, ResolveCommand(fixture.State,
                FacilityPendingChoiceTypes.ConfirmOption,
                new Dictionary<string, string>
                {
                    {
                        ResolveFacilityEffectCommandHandler.InfluenceSlotIdsParameter,
                        legalSlotId + ",not-a-valid-influence-slot"
                    }
                }));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixture.Influence.FindInfluence(fixture.State, legalSlotId), Is.Null);
            Assert.That(fixture.State.FindPlayer(1).InfluenceSupply, Is.EqualTo(30));
            Assert.That(fixture.State.PendingCardSession, Is.Not.Null);
        }

        [Test]
        public void VehicleWarehouse_RemoveThenDispatch_UsesOneAtomicInfluenceTransaction()
        {
            var fixture = CreateFixture();
            var removeSlot = InfluenceService.GetRouteSlotId("A1", 0);
            var sourceSlot = InfluenceService.GetRouteSlotId("B1", 0);
            var targetSlot = InfluenceService.GetRouteSlotId("B2", 0);
            fixture.State.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = removeSlot,
                RouteId = "A1"
            });
            fixture.State.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = sourceSlot,
                RouteId = "B1"
            });
            OpenEffect(fixture.State, "building_039");

            var result = fixture.Handler.Handle(fixture.State, ResolveCommand(
                fixture.State,
                FacilityPendingChoiceTypes.RemoveDispatchOption,
                new Dictionary<string, string>
                {
                    { ResolveFacilityEffectCommandHandler.RemoveInfluenceSlotIdParameter, removeSlot },
                    { ResolveFacilityEffectCommandHandler.SourceInfluenceSlotIdParameter, sourceSlot },
                    { ResolveFacilityEffectCommandHandler.TargetInfluenceSlotIdParameter, targetSlot }
                }));

            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(fixture.Influence.FindInfluence(fixture.State, removeSlot), Is.Null);
            Assert.That(fixture.Influence.FindInfluence(fixture.State, sourceSlot), Is.Null);
            Assert.That(fixture.Influence.FindInfluence(fixture.State, targetSlot), Is.Not.Null);
            Assert.That(fixture.State.PendingCardSession, Is.Null);
        }

        [Test]
        public void VehicleWarehouse_WhenDispatchIsIllegal_DoesNotKeepTheRemoval()
        {
            var fixture = CreateFixture();
            var removeSlot = InfluenceService.GetRouteSlotId("A1", 0);
            var sourceSlot = InfluenceService.GetRouteSlotId("B1", 0);
            fixture.State.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = removeSlot,
                RouteId = "A1"
            });
            fixture.State.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = sourceSlot,
                RouteId = "B1"
            });
            OpenEffect(fixture.State, "building_039");
            var pending = fixture.State.PendingCardSession;

            var result = fixture.Handler.Handle(fixture.State, ResolveCommand(
                fixture.State,
                FacilityPendingChoiceTypes.RemoveDispatchOption,
                new Dictionary<string, string>
                {
                    { ResolveFacilityEffectCommandHandler.RemoveInfluenceSlotIdParameter, removeSlot },
                    { ResolveFacilityEffectCommandHandler.SourceInfluenceSlotIdParameter, sourceSlot },
                    { ResolveFacilityEffectCommandHandler.TargetInfluenceSlotIdParameter, "not-a-slot" }
                }));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(fixture.Influence.FindInfluence(fixture.State, removeSlot), Is.Not.Null);
            Assert.That(fixture.Influence.FindInfluence(fixture.State, sourceSlot), Is.Not.Null);
            Assert.That(fixture.State.PendingCardSession, Is.SameAs(pending));
        }

        [Test]
        public void AuxiliaryEnergy_ReplaysAdjacentFixedResourceEntryEffect()
        {
            var fixture = CreateFixture();
            fixture.State.Map.Facilities.Add(new FacilityPlacement
            {
                PlayerId = 1,
                FacilityCardId = "building_014",
                CityBoardSlotIndex = 1
            });
            var player = fixture.State.FindPlayer(1);
            OpenEffect(fixture.State, "building_004", 4);

            var result = fixture.Handler.Handle(
                fixture.State,
                ResolveCommand(fixture.State, "1", null));

            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(player.Resources.OriginiumShard, Is.EqualTo(6));
            Assert.That(fixture.State.PendingCardSession, Is.Null);
        }

        [Test]
        public void ResolveFacilityEffect_WithStaleSessionId_IsRejectedWithoutChangingCurrentSession()
        {
            var fixture = CreateFixture();
            OpenEffect(fixture.State, "building_019");
            var staleSessionId = fixture.State.PendingCardSession.SessionId;
            OpenEffect(fixture.State, "building_029");
            var currentSession = fixture.State.PendingCardSession;
            var command = ResolveCommand(fixture.State,
                FacilityPendingChoiceTypes.ConfirmOption,
                new Dictionary<string, string>
                {
                    { ResolveFacilityEffectCommandHandler.OriginiumAmountParameter, "5" }
                });
            command.Parameters[ResolveFacilityEffectCommandHandler.PendingSessionIdParameter] = staleSessionId;

            var result = fixture.Handler.Handle(fixture.State, command);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(fixture.State.PendingCardSession, Is.SameAs(currentSession));
            Assert.That(fixture.State.FindPlayer(1).Resources.Originium, Is.Zero);
        }

        [Test]
        public void ResolveFacilityEffect_WithoutSessionId_IsRejectedWithoutChangingCurrentSession()
        {
            var fixture = CreateFixture();
            OpenEffect(fixture.State, FacilityCardDatabase.MiningPowerShovel);
            var currentSession = fixture.State.PendingCardSession;
            var command = new GameCommand
            {
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = 1,
                OptionIds = new List<string> { FacilityPendingChoiceTypes.ConfirmOption }
            };
            command.Parameters[ResolveFacilityEffectCommandHandler.OriginiumAmountParameter] = "5";

            var result = fixture.Handler.Handle(fixture.State, command);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(fixture.State.PendingCardSession, Is.SameAs(currentSession));
            Assert.That(fixture.State.FindPlayer(1).Resources.Originium, Is.Zero);
        }

        private static Fixture CreateFixture()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var influence = new InfluenceService(mapQuery);
            var eventDeck = new EventDeckService();
            var resourceTokens = new ResourceTokenService();
            var movement = new CityMovementService(
                mapQuery,
                influence,
                new TravelCostService(mapQuery),
                eventDeck,
                resourceTokens);
            var exploration = new ExplorationService(mapQuery, influence, eventDeck, resourceTokens);
            var exploreHandler = new ExploreLocationCommandHandler(exploration);
            var moveHandler = new MoveCityCommandHandler(movement);
            var build = new BuildFacilityService();
            var state = new GameState
            {
                Phase = GamePhase.ActionRound1,
                CurrentPlayerId = 1,
                Round = 4
            };
            state.Players.Add(new PlayerState { PlayerId = 1, Name = "P1", CityLocationId = "A-01" });
            state.Players.Add(new PlayerState { PlayerId = 2, Name = "P2" });
            return new Fixture
            {
                State = state,
                Influence = influence,
                MoveHandler = moveHandler,
                ExploreHandler = exploreHandler,
                Handler = new ResolveFacilityEffectCommandHandler(
                    build,
                    new FacilityEntryEffectService(),
                    influence,
                    moveHandler,
                    exploreHandler,
                    mapQuery,
                    new YC.Domain.Economy.ResourceSaleService())
            };
        }

        private static void OpenEffect(GameState state, string facilityId, int cityBoardSlotIndex = 0)
        {
            new FacilityEntryEffectService().Resolve(
                state,
                state.FindPlayer(1),
                FacilityCardDatabase.Get(facilityId),
                cityBoardSlotIndex);
            Assert.That(state.PendingCardSession, Is.Not.Null, facilityId + " should open a pending effect.");
        }

        private static GameCommand ResolveCommand(
            GameState state,
            string optionId,
            Dictionary<string, string> parameters)
        {
            var command = new GameCommand
            {
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = 1,
                OptionIds = new List<string> { optionId }
            };
            command.Parameters[ResolveFacilityEffectCommandHandler.PendingSessionIdParameter] =
                state.PendingCardSession.SessionId;
            if (parameters != null)
            {
                foreach (var pair in parameters)
                {
                    command.Parameters[pair.Key] = pair.Value;
                }
            }

            return command;
        }

        private sealed class Fixture
        {
            public GameState State;
            public InfluenceService Influence;
            public MoveCityCommandHandler MoveHandler;
            public ExploreLocationCommandHandler ExploreHandler;
            public ResolveFacilityEffectCommandHandler Handler;
        }
    }
}
