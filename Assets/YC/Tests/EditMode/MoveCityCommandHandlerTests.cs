using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class MoveCityCommandHandlerTests
    {
        [Test]
        public void MoveCity_ToAdjacentOpenLocation_Succeeds()
        {
            var state = CreateState();
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "A-02"
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-02"));
            Assert.That(state.FindPlayer(1).HasMovedCityThisRound, Is.True);
            Assert.That(result.Events[0].Kind, Is.EqualTo(GameEventKind.CityMoved));
        }

        [Test]
        public void MoveCity_ToNonAdjacentLocation_FailsWithoutChangingState()
        {
            var state = CreateState();
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "C-01"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.NoRoute));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-01"));
            Assert.That(state.FindPlayer(1).HasMovedCityThisRound, Is.False);
        }

        [Test]
        public void MoveCity_ToLocationOccupiedByAnotherCity_FailsWithoutChangingState()
        {
            var state = CreateState();
            state.FindPlayer(2).CityLocationId = "A-02";
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "A-02"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.OccupiedSlot));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-01"));
            Assert.That(state.FindPlayer(2).CityLocationId, Is.EqualTo("A-02"));
        }

        [Test]
        public void MoveCity_ToLocationWithoutResourceToken_RevealsEventPlacesTokenAndOpensPendingChoice()
        {
            var state = CreateState();
            RemoveResourceToken(state, "A-02");
            var cardId = AddEventCardForLocation(state, "A-02");
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "A-02"
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-02"));
            Assert.That(state.Map.ResourceTokens.Exists(token => token.LocationId == "A-02"), Is.True);
            Assert.That(state.PendingChoice, Is.Not.Null);
            Assert.That(state.PendingChoice.ChoiceType, Is.EqualTo(MoveCityCommandHandler.MoveCityEventChoiceType));
            Assert.That(state.PendingChoice.CardId, Is.EqualTo(cardId));
            Assert.That(state.PendingChoice.TargetId, Is.EqualTo("A-02"));
            Assert.That(state.PendingCardSession, Is.Not.Null);
            Assert.That(state.PendingCardSession.ChoiceType, Is.EqualTo(MoveCityCommandHandler.MoveCityEventChoiceType));
            Assert.That(state.PendingCardSession.CardId, Is.EqualTo(cardId));
            Assert.That(state.PendingCardSession.TargetId, Is.EqualTo("A-02"));
            Assert.That(result.Events.Exists(e => e.Kind == GameEventKind.CardMoved), Is.True);
            Assert.That(result.Events.Exists(e => e.Kind == GameEventKind.ChoiceOpened), Is.True);
        }

        [Test]
        public void MoveCity_WithPendingMoveEventChoice_AppliesRewardAndClearsPendingChoice()
        {
            var state = CreateState();
            RemoveResourceToken(state, "A-02");
            var cardId = AddEventCardForLocation(state, "A-02");
            var handler = CreateHandler();
            handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "A-02"
            });
            Assert.That(state.PendingCardSession, Is.Not.Null);
            state.PendingChoice = null;

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "A-02",
                OptionIds = { "0" }
            });

            var reward = EventCardDatabase.Get(cardId).ChoiceRewards[0];
            var player = state.FindPlayer(1);
            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.PendingChoice, Is.Null);
            Assert.That(state.PendingCardSession, Is.Null);
            Assert.That(player.Resources.Originium, Is.EqualTo(reward.Originium));
            Assert.That(player.Resources.OriginiumShard, Is.EqualTo(reward.OriginiumShard));
            Assert.That(player.Resources.Iron, Is.EqualTo(reward.Iron));
            Assert.That(player.Resources.PureOriginium, Is.EqualTo(reward.PureOriginium));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(reward.GoldVoucher));
            Assert.That(player.ActedMainActionThisTurn, Is.True);
            Assert.That(result.Events[0].Kind, Is.EqualTo(GameEventKind.ChoiceResolved));
        }

        [Test]
        public void MoveCity_WithPendingMoveEventChoice_ResolvePendingChoiceAppliesRewardAndClearsPendingChoice()
        {
            var state = CreateState();
            RemoveResourceToken(state, "A-02");
            var cardId = AddEventCardForLocation(state, "A-02");
            var handler = CreateHandler();
            handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "A-02"
            });
            Assert.That(state.PendingCardSession, Is.Not.Null);
            state.PendingChoice = null;

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = 1,
                OptionIds = { "0" }
            });

            var reward = EventCardDatabase.Get(cardId).ChoiceRewards[0];
            var player = state.FindPlayer(1);
            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.PendingChoice, Is.Null);
            Assert.That(state.PendingCardSession, Is.Null);
            Assert.That(player.Resources.Originium, Is.EqualTo(reward.Originium));
            Assert.That(player.Resources.OriginiumShard, Is.EqualTo(reward.OriginiumShard));
            Assert.That(player.Resources.Iron, Is.EqualTo(reward.Iron));
            Assert.That(player.Resources.PureOriginium, Is.EqualTo(reward.PureOriginium));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(reward.GoldVoucher));
            Assert.That(player.ActedMainActionThisTurn, Is.True);
        }

        [Test]
        public void MoveCity_WithPendingPlaceInfluenceEventChoice_PlacesChosenEventInfluence()
        {
            var state = CreateState();
            state.Round = 4;
            state.FindPlayer(1).CityLocationId = "D-02";
            state.FindPlayer(1).Resources.OriginiumShard = 3;
            state.FindPlayer(1).Resources.GoldVoucher = 0;
            RemoveResourceToken(state, "F-01");
            state.Decks.EventDeckRed.Add("event_red_01");
            var handler = CreateHandler();
            handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "F-01"
            });

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = 1,
                OptionIds = { "2" },
                Parameters =
                {
                    { MoveCityCommandHandler.EventInfluenceSlotIdParameter, InfluenceService.GetRouteSlotId("F1", 0) }
                }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.PendingChoice, Is.Null);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(6));
            Assert.That(state.Map.Influences.Exists(influence =>
                influence.PlayerId == 1 &&
                influence.SlotId == InfluenceService.GetRouteSlotId("F1", 0)), Is.True);
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.True);
        }

        [Test]
        public void MoveCity_WithPendingPlaceInfluenceEventChoice_WhenSlotMissing_FailsWithoutResolvingChoice()
        {
            var state = CreateState();
            state.Round = 4;
            state.FindPlayer(1).CityLocationId = "D-02";
            state.FindPlayer(1).Resources.OriginiumShard = 3;
            RemoveResourceToken(state, "F-01");
            state.Decks.EventDeckRed.Add("event_red_01");
            var handler = CreateHandler();
            handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "F-01"
            });
            var influenceCountBeforeResolve = state.Map.Influences.Count;

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = 1,
                OptionIds = { "2" }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.PendingChoice, Is.Not.Null);
            Assert.That(state.Map.Influences, Has.Count.EqualTo(influenceCountBeforeResolve));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.False);
        }

        [Test]
        public void MoveCity_WithImmediatePaidPlaceInfluenceEvent_WhenCombinedCostInsufficient_FailsBeforeMoving()
        {
            var state = CreateState();
            state.FindPlayer(1).CityLocationId = "B-02";
            state.FindPlayer(1).Resources.OriginiumShard = 3;
            state.FindPlayer(1).Resources.GoldVoucher = 3;
            RemoveResourceToken(state, "B-03");
            state.Decks.EventDeckYellow.Add("event_yellow_04");
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "B-03",
                OptionIds = { "0" },
                Parameters =
                {
                    { MoveCityCommandHandler.EventInfluenceSlotIdParameter, InfluenceService.GetRouteSlotId("C2", 0) }
                }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InsufficientResource));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("B-02"));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(3));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(3));
            Assert.That(state.Decks.EventDeckYellow, Has.Count.EqualTo(1));
            Assert.That(state.Map.ResourceTokens.Exists(token => token.LocationId == "B-03"), Is.False);
            Assert.That(state.Map.Influences, Is.Empty);
        }

        [Test]
        public void MoveCity_WhenPlayerAlreadyTookMainAction_FailsWithoutChangingState()
        {
            var state = CreateState();
            state.FindPlayer(1).ActedMainActionThisTurn = true;
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "A-02"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-01"));
        }

        [Test]
        public void MoveCity_WhenNotCurrentPlayer_FailsWithoutChangingState()
        {
            var state = CreateState();
            state.CurrentPlayerId = 2;
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "A-02"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.NotCurrentPlayer));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-01"));
        }

        [Test]
        public void MoveCity_WhenAlreadyMovedInPreviousActionRound_SucceedsInSecondActionRound()
        {
            var state = CreateState();
            state.Phase = GamePhase.ActionRound2;
            state.ActionRound = 2;
            state.FindPlayer(1).CityLocationId = "A-02";
            state.FindPlayer(1).HasMovedCityThisRound = true;
            state.FindPlayer(1).ActedMainActionThisTurn = false;
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "A-01"
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-01"));
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.True);
            Assert.That(state.FindPlayer(1).HasMovedCityThisRound, Is.True);
        }

        [Test]
        public void MoveCity_InWrongPhase_FailsWithoutChangingState()
        {
            var state = CreateState();
            state.Phase = GamePhase.Entrance;
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "A-02"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.WrongPhase));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-01"));
        }

        private static MoveCityCommandHandler CreateHandler()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var influenceService = new InfluenceService(mapQuery);
            var travelCostService = new TravelCostService(mapQuery);
            var movementService = new CityMovementService(mapQuery, influenceService, travelCostService);
            return new MoveCityCommandHandler(movementService);
        }

        private static GameState CreateState()
        {
            return new GameState
            {
                Phase = GamePhase.ActionRound1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Name = "Player 1",
                        Color = PlayerColor.Red,
                        CityLocationId = "A-01",
                        Resources =
                        {
                            OriginiumShard = 3
                        }
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
        }

        private static string AddEventCardForLocation(GameState state, string locationId)
        {
            var eventColor = StaticMapDefinitions.GetEventColor(locationId);
            switch (eventColor)
            {
                case EventColor.Green:
                    state.Decks.EventDeckGreen.Add("event_green_01");
                    return "event_green_01";
                case EventColor.Yellow:
                    state.Decks.EventDeckYellow.Add("event_yellow_01");
                    return "event_yellow_01";
                case EventColor.Red:
                    state.Decks.EventDeckRed.Add("event_red_01");
                    return "event_red_01";
                default:
                    return string.Empty;
            }
        }

        private static void RemoveResourceToken(GameState state, string locationId)
        {
            for (var i = 0; i < state.Map.ResourceTokens.Count; i++)
            {
                if (state.Map.ResourceTokens[i].LocationId != locationId)
                {
                    continue;
                }

                state.Map.ResourceTokens.RemoveAt(i);
                return;
            }
        }
    }
}
