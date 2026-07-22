using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Gameplay;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class SpecialActionServiceTests
    {
        [Test]
        public void IndustrialHub_SpendsOriginalBudgetThenGrantsTwoAndKeepsLevelTwoCountUntilCleanup()
        {
            var context = CreateContext();
            var state = CreateState();
            var player = state.FindPlayer(1);
            player.Resources.GoldVoucher = 6;
            AddUnlockedMarker(
                player,
                SpecialActionDatabase.SourceStoneIndustrialHub,
                CityStyleDatabase.SourceStoneIndustrialHub,
                CityStyleMarkerAreas.UsesTwo,
                2);

            var result = context.SpecialActionService.Begin(
                state,
                1,
                SpecialActionDatabase.SourceStoneIndustrialHub,
                "marker",
                "command");

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Completed, Is.True);
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(player.RemainingMainActionsThisTurn, Is.EqualTo(2));
            Assert.That(player.CompletedMainActionsThisTurn, Is.EqualTo(1));
            Assert.That(player.ActedMainActionThisTurn, Is.False);
            Assert.That(player.CharacterCardLockedThisTurn, Is.True);
            Assert.That(player.DeclaredCityStyles[0].MarkerArea, Is.EqualTo(SpecialActionMarkerAreas.UsedFromTwo));
            Assert.That(player.DeclaredCityStyles[0].RemainingSpecialActionUses, Is.EqualTo(2));
        }

        [Test]
        public void MilitaryArea_RequiresMaximumExecutableCountAndPlacesAtomically()
        {
            var context = CreateContext();
            var state = CreateState();
            var player = state.FindPlayer(1);
            player.InfluenceSupply = 1;
            AddUnlockedMarker(
                player,
                SpecialActionDatabase.MilitaryIndustrialArea,
                CityStyleDatabase.MilitaryIndustrialArea,
                CityStyleMarkerAreas.Unused,
                1);
            player.DeclaredCityStyles.Add(new CityStyleDeclarationState
            {
                InfluenceMarkerId = "later-marker",
                CityStyleId = CityStyleDatabase.MilitaryIndustrialArea,
                MarkerArea = CityStyleMarkerAreas.Declared
            });

            var begin = context.SpecialActionService.Begin(
                state,
                1,
                SpecialActionDatabase.MilitaryIndustrialArea,
                "marker",
                "command");
            Assert.That(begin.Succeeded, Is.True);
            Assert.That(begin.Completed, Is.False);

            var legalSlots = context.OptionQuery.GetLegalInfluencePlacementSlotIds(state, 1);
            Assert.That(legalSlots, Is.Not.Empty);
            var resolve = context.SpecialActionService.ResolveMilitaryTargets(
                state,
                1,
                new[] { legalSlots[0] });

            Assert.That(resolve.Succeeded, Is.True);
            Assert.That(resolve.Completed, Is.True);
            Assert.That(player.InfluenceSupply, Is.EqualTo(0));
            Assert.That(state.Map.Influences.Exists(item => item.SlotId == legalSlots[0] && item.PlayerId == 1), Is.True);
            Assert.That(player.RemainingMainActionsThisTurn, Is.EqualTo(0));
            Assert.That(state.PendingSpecialAction, Is.Null);
        }

        [Test]
        public void Mobilization_RemovesOpponentEvenWhenReplacementCannotBePlaced()
        {
            var context = CreateContext();
            var state = CreateState();
            var player = state.FindPlayer(1);
            player.InfluenceSupply = 0;
            AddUnlockedMarker(
                player,
                SpecialActionDatabase.MobilizationSupportSystem,
                CityStyleDatabase.MobilizationSupportSystem,
                CityStyleMarkerAreas.Unused,
                1);
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = InfluenceService.GetLocationSlotId("A-02", 0),
                LocationId = "A-02"
            });
            state.FindPlayer(2).InfluenceSupply = 10;

            Assert.That(context.SpecialActionService.Begin(
                state,
                1,
                SpecialActionDatabase.MobilizationSupportSystem,
                "marker",
                "command").Succeeded, Is.True);
            var result = context.SpecialActionService.ResolveMobilizationTarget(
                state,
                1,
                InfluenceService.GetLocationSlotId("A-02", 0));

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Map.Influences.Exists(item => item.SlotId == InfluenceService.GetLocationSlotId("A-02", 0)), Is.False);
            Assert.That(state.FindPlayer(2).InfluenceSupply, Is.EqualTo(11));
            Assert.That(player.InfluenceSupply, Is.EqualTo(0));
            Assert.That(player.RemainingMainActionsThisTurn, Is.EqualTo(0));
        }

        [Test]
        public void CompositePower_HandlerPreservesSessionAcrossPaymentMoveAndRoutePlacement()
        {
            var context = CreateContext();
            var state = CreateState();
            var player = state.FindPlayer(1);
            player.Resources.OriginiumShard = 1;
            player.Resources.Originium = 2;
            player.Resources.Iron = 1;
            state.Decks.EventDeckYellow.Add("event_yellow_01");
            AddUnlockedMarker(
                player,
                SpecialActionDatabase.CompositePowerSystem,
                CityStyleDatabase.CompositePowerSystem,
                CityStyleMarkerAreas.Unused,
                1);
            var handler = new UseSpecialActionCommandHandler(
                context.SpecialActionService,
                context.OptionQuery,
                new MoveCityCommandHandler(context.MovementService));

            var begin = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.UseSpecialAction,
                PlayerId = 1,
                SourceId = "marker",
                TargetId = SpecialActionDatabase.CompositePowerSystem,
                Parameters =
                {
                    { UseSpecialActionCommandHandler.OriginiumAmountParameter, "2" },
                    { UseSpecialActionCommandHandler.IronAmountParameter, "1" }
                }
            });
            Assert.That(begin.Succeeded, Is.True);
            Assert.That(state.PendingSpecialAction.Step, Is.EqualTo(SpecialActionPendingSteps.AwaitFreeMoveTarget));
            var sessionId = state.PendingSpecialAction.SessionId;
            Assert.That(player.Resources.OriginiumShard, Is.EqualTo(0));
            Assert.That(player.Resources.Originium, Is.EqualTo(0));
            Assert.That(player.Resources.Iron, Is.EqualTo(0));
            Assert.That(state.PendingSpecialAction.SessionId, Is.EqualTo(sessionId));

            var move = handler.Handle(state, CreateResolveCommand(sessionId, new Dictionary<string, string>
            {
                { UseSpecialActionCommandHandler.TargetLocationIdParameter, "D-01" }
            }));
            Assert.That(move.Succeeded, Is.True);
            Assert.That(player.CityLocationId, Is.EqualTo("D-01"));
            Assert.That(state.PendingCardSession, Is.Not.Null);
            Assert.That(state.PendingSpecialAction.Step, Is.EqualTo(SpecialActionPendingSteps.AwaitMoveEvent));
            Assert.That(state.PendingSpecialAction.SessionId, Is.EqualTo(sessionId));

            var eventChoiceCommand = CreateResolveCommand(sessionId, new Dictionary<string, string>());
            eventChoiceCommand.OptionIds.Add("0");
            var eventChoice = handler.Handle(state, eventChoiceCommand);
            Assert.That(eventChoice.Succeeded, Is.True);
            Assert.That(state.PendingCardSession, Is.Null);
            Assert.That(state.PendingSpecialAction.Step, Is.EqualTo(SpecialActionPendingSteps.AwaitRouteInfluence));
            Assert.That(state.PendingSpecialAction.SessionId, Is.EqualTo(sessionId));

            var routeSlots = context.OptionQuery.GetLegalRouteInfluenceSlotIds(
                state,
                1,
                state.PendingSpecialAction.TraversedRouteId);
            Assert.That(routeSlots, Is.Not.Empty);
            var routePlacement = handler.Handle(state, CreateResolveCommand(sessionId, new Dictionary<string, string>
            {
                { UseSpecialActionCommandHandler.RouteInfluenceSlotIdParameter, routeSlots[0] }
            }));

            Assert.That(routePlacement.Succeeded, Is.True);
            Assert.That(state.PendingSpecialAction, Is.Null);
            Assert.That(player.Resources.OriginiumShard, Is.EqualTo(0));
            Assert.That(player.Resources.Originium, Is.EqualTo(5), "事件选项奖励应在特殊行动会话继续前正常结算。");
            Assert.That(player.Resources.Iron, Is.EqualTo(0));
            Assert.That(player.RemainingMainActionsThisTurn, Is.EqualTo(0));
            Assert.That(state.Map.Influences.Exists(item => item.SlotId == routeSlots[0] && item.PlayerId == 1), Is.True);
        }

        [TestCase(0, 3)]
        [TestCase(1, 2)]
        [TestCase(2, 1)]
        [TestCase(3, 0)]
        public void CompositePower_BeginAtomicallyPaysSelectedCombinationAndOpensMove(
            int originiumAmount,
            int ironAmount)
        {
            var context = CreateContext();
            var state = CreateState();
            var player = state.FindPlayer(1);
            player.Resources.OriginiumShard = 1;
            player.Resources.Originium = 3;
            player.Resources.Iron = 3;
            AddUnlockedMarker(
                player,
                SpecialActionDatabase.CompositePowerSystem,
                CityStyleDatabase.CompositePowerSystem,
                CityStyleMarkerAreas.Unused,
                1);
            var handler = CreateHandler(context);

            var result = handler.Handle(
                state,
                CreateCompositeBeginCommand(originiumAmount.ToString(), ironAmount.ToString()));

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.PendingSpecialAction, Is.Not.Null);
            Assert.That(state.PendingSpecialAction.Step, Is.EqualTo(SpecialActionPendingSteps.AwaitFreeMoveTarget));
            Assert.That(state.PendingSpecialAction.PaidOriginium, Is.EqualTo(originiumAmount));
            Assert.That(state.PendingSpecialAction.PaidOriginiumShard, Is.EqualTo(1));
            Assert.That(state.PendingSpecialAction.PaidIron, Is.EqualTo(ironAmount));
            Assert.That(player.Resources.Originium, Is.EqualTo(3 - originiumAmount));
            Assert.That(player.Resources.OriginiumShard, Is.Zero);
            Assert.That(player.Resources.Iron, Is.EqualTo(3 - ironAmount));
            Assert.That(player.DeclaredCityStyles[0].MarkerArea, Is.EqualTo(CityStyleMarkerAreas.Used));
            Assert.That(player.RemainingMainActionsThisTurn, Is.EqualTo(1));
        }

        [TestCase("-1", "4")]
        [TestCase("2", "0")]
        [TestCase("4", "0")]
        public void CompositePower_InvalidInitialPayment_IsRejectedWithoutWriteBack(
            string originiumAmount,
            string ironAmount)
        {
            var context = CreateContext();
            var state = CreateState();
            var player = state.FindPlayer(1);
            player.Resources.OriginiumShard = 1;
            player.Resources.Originium = 4;
            player.Resources.Iron = 4;
            AddUnlockedMarker(
                player,
                SpecialActionDatabase.CompositePowerSystem,
                CityStyleDatabase.CompositePowerSystem,
                CityStyleMarkerAreas.Unused,
                1);
            var before = JsonUtility.ToJson(state);

            var result = CreateHandler(context).Handle(
                state,
                CreateCompositeBeginCommand(originiumAmount, ironAmount));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(JsonUtility.ToJson(state), Is.EqualTo(before));
        }

        [Test]
        public void CompositePower_SelectedUnaffordableCombination_IsRejectedWithoutWriteBack()
        {
            var context = CreateContext();
            var state = CreateState();
            var player = state.FindPlayer(1);
            player.Resources.OriginiumShard = 1;
            player.Resources.Originium = 0;
            player.Resources.Iron = 3;
            AddUnlockedMarker(
                player,
                SpecialActionDatabase.CompositePowerSystem,
                CityStyleDatabase.CompositePowerSystem,
                CityStyleMarkerAreas.Unused,
                1);
            var before = JsonUtility.ToJson(state);

            var result = CreateHandler(context).Handle(state, CreateCompositeBeginCommand("1", "2"));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InsufficientResource));
            Assert.That(JsonUtility.ToJson(state), Is.EqualTo(before));
        }

        [Test]
        public void CompositePower_WithoutRequiredShard_IsRejectedWithoutWriteBack()
        {
            var context = CreateContext();
            var state = CreateState();
            var player = state.FindPlayer(1);
            player.Resources.OriginiumShard = 0;
            player.Resources.Originium = 3;
            player.Resources.Iron = 3;
            AddUnlockedMarker(
                player,
                SpecialActionDatabase.CompositePowerSystem,
                CityStyleDatabase.CompositePowerSystem,
                CityStyleMarkerAreas.Unused,
                1);
            var before = JsonUtility.ToJson(state);

            var result = CreateHandler(context).Handle(state, CreateCompositeBeginCommand("2", "1"));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InsufficientResource));
            Assert.That(JsonUtility.ToJson(state), Is.EqualTo(before));
        }

        [Test]
        public void CompositePower_WithNoMoveTarget_PaysAndCompletesInInitialCommand()
        {
            var map = new GameMapDefinition { MapId = "composite-no-target", MinPlayers = 1, MaxPlayers = 4 };
            map.Locations.Add(new MapLocationDefinition
            {
                LocationId = "ONLY",
                CanDockCity = true,
                InfluenceSlotCount = 1
            });
            var context = CreateContext(map);
            var state = new GameState
            {
                Phase = GamePhase.ActionRound1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        CityLocationId = "ONLY",
                        InfluenceSupply = 3,
                        Resources = new ResourceSet
                        {
                            Originium = 2,
                            OriginiumShard = 1,
                            Iron = 1
                        }
                    }
                },
                Map = { OpenLocationIds = { "ONLY" } }
            };
            var player = state.FindPlayer(1);
            AddUnlockedMarker(
                player,
                SpecialActionDatabase.CompositePowerSystem,
                CityStyleDatabase.CompositePowerSystem,
                CityStyleMarkerAreas.Unused,
                1);
            Assert.That(context.OptionQuery.GetLegalFreeMoveTargetIds(state, 1), Is.Empty);

            var result = CreateHandler(context).Handle(state, CreateCompositeBeginCommand("2", "1"));

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.PendingSpecialAction, Is.Null);
            Assert.That(player.Resources.Originium, Is.Zero);
            Assert.That(player.Resources.OriginiumShard, Is.Zero);
            Assert.That(player.Resources.Iron, Is.Zero);
            Assert.That(player.DeclaredCityStyles[0].MarkerArea, Is.EqualTo(CityStyleMarkerAreas.Used));
            Assert.That(player.RemainingMainActionsThisTurn, Is.Zero);
            Assert.That(player.CompletedMainActionsThisTurn, Is.EqualTo(1));
        }

        private static GameCommand CreateResolveCommand(string sessionId, IDictionary<string, string> values)
        {
            var command = new GameCommand
            {
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = 1
            };
            command.Parameters[UseSpecialActionCommandHandler.SessionIdParameter] = sessionId;
            foreach (var pair in values)
            {
                command.Parameters[pair.Key] = pair.Value;
            }

            return command;
        }

        private static GameCommand CreateCompositeBeginCommand(string originiumAmount, string ironAmount)
        {
            var command = new GameCommand
            {
                Kind = GameCommandKind.UseSpecialAction,
                PlayerId = 1,
                SourceId = "marker",
                TargetId = SpecialActionDatabase.CompositePowerSystem
            };
            command.Parameters[UseSpecialActionCommandHandler.OriginiumAmountParameter] = originiumAmount;
            command.Parameters[UseSpecialActionCommandHandler.IronAmountParameter] = ironAmount;
            return command;
        }

        private static UseSpecialActionCommandHandler CreateHandler(TestContext context)
        {
            return new UseSpecialActionCommandHandler(
                context.SpecialActionService,
                context.OptionQuery,
                new MoveCityCommandHandler(context.MovementService));
        }

        private static TestContext CreateContext()
        {
            return CreateContext(StaticMapDefinitions.CreateFourPlayerMap());
        }

        private static TestContext CreateContext(GameMapDefinition map)
        {
            var mapQuery = new MapQueryService(map);
            var influenceService = new InfluenceService(mapQuery);
            var movementService = new CityMovementService(
                mapQuery,
                influenceService,
                new TravelCostService(mapQuery));
            var lifecycle = new SpecialActionLifecycleService();
            var budget = new MainActionBudgetService();
            var optionQuery = new SpecialActionOptionQueryService(
                mapQuery,
                influenceService,
                movementService,
                lifecycle,
                budget);
            return new TestContext
            {
                MovementService = movementService,
                OptionQuery = optionQuery,
                SpecialActionService = new SpecialActionService(
                    optionQuery,
                    lifecycle,
                    influenceService,
                    new FacilityInfluenceEffectService(influenceService),
                    budget)
            };
        }

        private static GameState CreateState()
        {
            var state = new GameState
            {
                Phase = GamePhase.ActionRound1,
                CurrentPlayerId = 1,
                Round = 4,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Name = "Player 1",
                        Color = PlayerColor.Red,
                        CityLocationId = "A-01"
                    },
                    new PlayerState
                    {
                        PlayerId = 2,
                        Name = "Player 2",
                        Color = PlayerColor.Blue,
                        CityLocationId = "B-01"
                    }
                },
                Map =
                {
                    OpenLocationIds = { "A-01", "A-02", "B-01" },
                    ResourceTokens =
                    {
                        new ResourceTokenState
                        {
                            LocationId = "A-01",
                            ResourceType = ResourceType.Iron,
                            Amount = 1
                        },
                        new ResourceTokenState
                        {
                            LocationId = "A-02",
                            ResourceType = ResourceType.Iron,
                            Amount = 1
                        }
                    }
                }
            };
            return state;
        }

        private static void AddUnlockedMarker(
            PlayerState player,
            string specialActionId,
            string cityStyleId,
            string markerArea,
            int remainingUses)
        {
            player.DeclaredCityStyleIds.Add(cityStyleId);
            player.DeclaredCityStyles.Add(new CityStyleDeclarationState
            {
                InfluenceMarkerId = "marker",
                CityStyleId = cityStyleId,
                MarkerArea = markerArea,
                UnlockedSpecialActionId = specialActionId,
                RemainingSpecialActionUses = remainingUses
            });
        }

        private sealed class TestContext
        {
            public CityMovementService MovementService;
            public SpecialActionOptionQueryService OptionQuery;
            public SpecialActionService SpecialActionService;
        }
    }
}
