using NUnit.Framework;
using YC.Application.Setup;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class SetupCommandHandlerTests
    {
        [Test]
        public void ChooseStartPlayer_Succeeds_SetsStartAndCurrentPlayerAndEntersEntrance()
        {
            var state = CreateState(GamePhase.Setup);
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ChooseStartPlayer,
                PlayerId = 1
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.StartPlayerId, Is.EqualTo(1));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.Entrance));
            Assert.That(result.Events, Has.Count.GreaterThanOrEqualTo(1));
            Assert.That(result.LogMessage, Does.Contain("start player"));
        }

        [Test]
        public void ChooseStartPlayer_InWrongPhase_FailsWithoutChangingState()
        {
            var state = CreateState(GamePhase.Entrance);
            state.StartPlayerId = 2;
            state.CurrentPlayerId = 2;
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ChooseStartPlayer,
                PlayerId = 1
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.WrongPhase));
            Assert.That(state.StartPlayerId, Is.EqualTo(2));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(2));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.Entrance));
        }

        [Test]
        public void ChooseInitialLocation_Succeeds_SetsCityLocationAndOpensLocation()
        {
            var state = CreateState(GamePhase.Entrance);
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ChooseInitialLocation,
                PlayerId = 1,
                TargetId = "city-a"
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("city-a"));
            Assert.That(state.Map.OpenLocationIds, Does.Contain("city-a"));
            AssertCoreCommandTowerPlaced(state, 1);
            Assert.That(result.Events, Has.Count.GreaterThanOrEqualTo(1));
            Assert.That(result.LogMessage, Does.Contain("city-a"));
        }

        [Test]
        public void ChooseInitialLocation_WithUndockableLocation_FailsWithoutChangingState()
        {
            var state = CreateState(GamePhase.Entrance);
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ChooseInitialLocation,
                PlayerId = 1,
                TargetId = "mine-b"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.Empty);
            Assert.That(state.Map.OpenLocationIds, Is.Empty);
        }

        [Test]
        public void ChooseInitialLocation_WithOtherPlayerCityAtLocation_FailsWithoutChangingState()
        {
            var state = CreateState(GamePhase.Entrance);
            state.FindPlayer(2).CityLocationId = "city-a";
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ChooseInitialLocation,
                PlayerId = 1,
                TargetId = "city-a"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.OccupiedSlot));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.Empty);
            Assert.That(state.FindPlayer(2).CityLocationId, Is.EqualTo("city-a"));
            Assert.That(state.Map.OpenLocationIds, Is.Empty);
        }

        [Test]
        public void ChooseInitialLocation_OnFourPlayerMap_WithAllowedEntranceLocation_Succeeds()
        {
            var state = CreateState(GamePhase.Entrance);
            var handler = CreateFourPlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ChooseInitialLocation,
                PlayerId = 1,
                TargetId = "G-01"
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("G-01"));
            Assert.That(state.Map.OpenLocationIds, Does.Contain("G-01"));
        }

        [Test]
        public void ChooseInitialLocation_OnFourPlayerMap_WithDisallowedEntranceLocation_FailsWithoutChangingState()
        {
            var state = CreateState(GamePhase.Entrance);
            var handler = CreateFourPlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ChooseInitialLocation,
                PlayerId = 1,
                TargetId = "G-02"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.Empty);
            Assert.That(state.Map.OpenLocationIds, Is.Empty);
        }

        [Test]
        public void ChooseInitialLocation_WhenFourPlayerEntranceCompletes_GrantsInitialGoldByTurnOrder()
        {
            var state = CreateFourPlayerState(GamePhase.Entrance);
            state.StartPlayerId = 2;
            state.CurrentPlayerId = 2;
            var handler = CreateFourPlayerHandler();

            handler.Handle(state, InitialLocationCommand(2, "G-01"));
            handler.Handle(state, InitialLocationCommand(3, "A-01"));
            handler.Handle(state, InitialLocationCommand(4, "A-02"));
            var result = handler.Handle(state, InitialLocationCommand(1, "B-01"));

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(10));
            Assert.That(state.FindPlayer(3).Resources.GoldVoucher, Is.EqualTo(12));
            Assert.That(state.FindPlayer(4).Resources.GoldVoucher, Is.EqualTo(14));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(18));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound1));
            Assert.That(state.Round, Is.EqualTo(1));
        }

        [Test]
        public void ChooseInitialLocation_InEntranceTurnOrder_AdvancesCurrentPlayer()
        {
            var state = CreateFourPlayerState(GamePhase.Entrance);
            state.StartPlayerId = 2;
            state.CurrentPlayerId = 2;
            var handler = CreateFourPlayerHandler();

            var result = handler.Handle(state, InitialLocationCommand(2, "G-01"));

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(2).CityLocationId, Is.EqualTo("G-01"));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(3));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.Entrance));
        }

        [Test]
        public void ChooseInitialLocation_WithSeatTurnOrder_UsesPlayerIdOrderForEntranceAndFirstAction()
        {
            var state = CreateFourPlayerState(GamePhase.Entrance);
            state.UseSeatTurnOrder = true;
            state.StartPlayerId = 1;
            state.CurrentPlayerId = 1;
            var handler = CreateFourPlayerHandler();

            var firstResult = handler.Handle(state, InitialLocationCommand(1, "G-01"));
            handler.Handle(state, InitialLocationCommand(2, "A-01"));
            handler.Handle(state, InitialLocationCommand(3, "A-02"));
            var finalResult = handler.Handle(state, InitialLocationCommand(4, "B-01"));

            Assert.That(firstResult.Succeeded, Is.True);
            Assert.That(finalResult.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("G-01"));
            Assert.That(state.FindPlayer(2).CityLocationId, Is.EqualTo("A-01"));
            Assert.That(state.FindPlayer(3).CityLocationId, Is.EqualTo("A-02"));
            Assert.That(state.FindPlayer(4).CityLocationId, Is.EqualTo("B-01"));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(10));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(12));
            Assert.That(state.FindPlayer(3).Resources.GoldVoucher, Is.EqualTo(14));
            Assert.That(state.FindPlayer(4).Resources.GoldVoucher, Is.EqualTo(18));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound1));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
        }

        [Test]
        public void ChooseInitialLocation_OutOfEntranceTurnOrder_FailsWithoutChangingState()
        {
            var state = CreateFourPlayerState(GamePhase.Entrance);
            state.StartPlayerId = 2;
            state.CurrentPlayerId = 2;
            var handler = CreateFourPlayerHandler();

            var result = handler.Handle(state, InitialLocationCommand(3, "G-01"));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.NotCurrentPlayer));
            Assert.That(state.FindPlayer(3).CityLocationId, Is.Empty);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(2));
            Assert.That(state.Map.OpenLocationIds, Is.Empty);
        }

        [Test]
        public void ResolveEntranceEvent_WithValidOption_GrantsRewardAndAdvancesEntrance()
        {
            var state = CreateFourPlayerState(GamePhase.Entrance);
            state.StartPlayerId = 1;
            state.CurrentPlayerId = 1;
            state.Decks.EventDeckGreen.Add("event_green_01");
            var handler = CreateFourPlayerHandler();

            var placeResult = handler.Handle(state, InitialLocationCommand(1, "G-01"));
            Assert.That(state.PendingCardSession, Is.Not.Null);
            Assert.That(state.PendingCardSession.ChoiceType, Is.EqualTo("entrance_event"));
            Assert.That(state.PendingCardSession.CardId, Is.EqualTo("event_green_01"));
            Assert.That(state.PendingCardSession.TargetId, Is.EqualTo("G-01"));
            state.PendingChoice = null;

            var resolveResult = handler.Handle(state, EntranceEventCommand(1, "0"));

            Assert.That(placeResult.Succeeded, Is.True);
            Assert.That(placeResult.Events[1].Message, Does.Contain("中立采石场"));
            Assert.That(placeResult.Events[1].Data["cardName"], Is.EqualTo("中立采石场"));
            Assert.That(resolveResult.Succeeded, Is.True);
            Assert.That(resolveResult.LogMessage, Does.Contain("中立采石场"));
            Assert.That(state.PendingChoice, Is.Null);
            Assert.That(state.PendingCardSession, Is.Null);
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(3));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(2));
            Assert.That(state.Map.ResourceTokens, Has.Count.EqualTo(1));
            Assert.That(state.Map.ResourceTokens[0].LocationId, Is.EqualTo("G-01"));
            Assert.That(state.Map.ResourceTokens[0].ResourceType, Is.EqualTo(ResourceType.Originium));
            Assert.That(state.Map.ResourceTokens[0].Amount, Is.EqualTo(1));
        }

        [Test]
        public void EventCardDatabase_AllCardsHaveDisplayText()
        {
            AssertCardsHaveDisplayText(EventCardDatabase.GreenCardIds);
            AssertCardsHaveDisplayText(EventCardDatabase.YellowCardIds);
            AssertCardsHaveDisplayText(EventCardDatabase.RedCardIds);
        }

        [Test]
        public void EventCardDatabase_StoresChoicePendingEffectsAsStructuredData()
        {
            var scoreCard = EventCardDatabase.Get("event_yellow_01");
            Assert.That(scoreCard.ChoicePendingEffects[1], Has.Count.EqualTo(1));
            Assert.That(scoreCard.ChoicePendingEffects[1][0].Kind, Is.EqualTo(EventEffectKind.GainScore));
            Assert.That(scoreCard.ChoicePendingEffects[1][0].Amount, Is.EqualTo(1));

            var routeCard = EventCardDatabase.Get("event_yellow_05");
            Assert.That(routeCard.ChoicePendingEffects[0], Has.Count.EqualTo(1));
            Assert.That(routeCard.ChoicePendingEffects[0][0].Kind, Is.EqualTo(EventEffectKind.PlaceInfluence));
            Assert.That(routeCard.ChoicePendingEffects[0][0].TargetScope, Is.EqualTo(EventEffectTargetScope.AdjacentRoute));

            var opponentRewardCard = EventCardDatabase.Get("event_red_05");
            Assert.That(opponentRewardCard.ChoicePendingEffects[2], Has.Count.EqualTo(1));
            Assert.That(opponentRewardCard.ChoicePendingEffects[2][0].Kind, Is.EqualTo(EventEffectKind.GrantResource));
            Assert.That(opponentRewardCard.ChoicePendingEffects[2][0].TargetScope, Is.EqualTo(EventEffectTargetScope.Opponents));
            Assert.That(opponentRewardCard.ChoicePendingEffects[2][0].ResourceType, Is.EqualTo(ResourceType.GoldVoucher));
            Assert.That(opponentRewardCard.ChoicePendingEffects[2][0].Amount, Is.EqualTo(3));
        }

        [Test]
        public void ResolveEntranceEvent_WithInvalidOption_FailsWithoutGrantingReward()
        {
            var state = CreateFourPlayerState(GamePhase.Entrance);
            state.StartPlayerId = 1;
            state.CurrentPlayerId = 1;
            state.Decks.EventDeckGreen.Add("event_green_01");
            var handler = CreateFourPlayerHandler();

            handler.Handle(state, InitialLocationCommand(1, "G-01"));
            var result = handler.Handle(state, EntranceEventCommand(1, "9"));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.PendingChoice, Is.Not.Null);
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.Zero);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
        }

        private static SetupCommandHandler CreateHandler()
        {
            return new SetupCommandHandler(new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder()));
        }

        private static SetupCommandHandler CreateFourPlayerHandler()
        {
            return new SetupCommandHandler(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));
        }

        private static GameState CreateState(GamePhase phase)
        {
            return new GameState
            {
                Phase = phase,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Name = "Player 1",
                        Color = PlayerColor.Red
                    },
                    new PlayerState
                    {
                        PlayerId = 2,
                        Name = "Player 2",
                        Color = PlayerColor.Blue
                    }
                }
            };
        }

        private static GameState CreateFourPlayerState(GamePhase phase)
        {
            return new GameState
            {
                Phase = phase,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Name = "Player 1",
                        Color = PlayerColor.Red
                    },
                    new PlayerState
                    {
                        PlayerId = 2,
                        Name = "Player 2",
                        Color = PlayerColor.Blue
                    },
                    new PlayerState
                    {
                        PlayerId = 3,
                        Name = "Player 3",
                        Color = PlayerColor.Green
                    },
                    new PlayerState
                    {
                        PlayerId = 4,
                        Name = "Player 4",
                        Color = PlayerColor.Yellow
                    }
                }
            };
        }

        private static GameCommand InitialLocationCommand(int playerId, string locationId)
        {
            return new GameCommand
            {
                Kind = GameCommandKind.ChooseInitialLocation,
                PlayerId = playerId,
                TargetId = locationId
            };
        }

        private static GameCommand EntranceEventCommand(int playerId, string optionId)
        {
            return new GameCommand
            {
                Kind = GameCommandKind.ResolveEntranceEvent,
                PlayerId = playerId,
                OptionIds = { optionId }
            };
        }

        private static void AssertCardsHaveDisplayText(System.Collections.Generic.IEnumerable<string> cardIds)
        {
            foreach (var cardId in cardIds)
            {
                var card = EventCardDatabase.Get(cardId);
                Assert.That(card, Is.Not.Null, cardId);
                Assert.That(card.Name, Is.Not.Null.And.Not.Empty, cardId);
                Assert.That(card.Description, Is.Not.Null.And.Not.Empty, cardId);
                Assert.That(card.RepresentativeResourceAmount, Is.GreaterThan(0), cardId);
                Assert.That(card.ChoicePendingEffects, Has.Count.EqualTo(card.ChoiceRewards.Count), cardId);
            }
        }

        private static void AssertCoreCommandTowerPlaced(GameState state, int playerId)
        {
            Assert.That(state.FindPlayer(playerId).BuiltFacilityIds, Does.Contain(FacilityCardDatabase.CoreCommandTower));

            var count = 0;
            for (var i = 0; i < state.Map.Facilities.Count; i++)
            {
                var placement = state.Map.Facilities[i];
                if (placement.PlayerId == playerId &&
                    placement.FacilityCardId == FacilityCardDatabase.CoreCommandTower &&
                    placement.CityBoardSlotIndex == BuildFacilityService.CoreCommandTowerCityBoardSlotIndex)
                {
                    count++;
                }
            }

            Assert.That(count, Is.EqualTo(1));
        }
    }
}
