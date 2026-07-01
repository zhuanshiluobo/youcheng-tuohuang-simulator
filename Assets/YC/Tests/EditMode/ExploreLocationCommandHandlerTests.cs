using NUnit.Framework;
using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Exploration;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class ExploreLocationCommandHandlerTests
    {
        [Test]
        public void ExploreLocation_WithDefaultPath_RevealsEventPlacesTokenGrantsRewardPlacesInfluenceAndMarksMainActionComplete()
        {
            var state = CreateThreePlayerActionState();
            state.Decks.EventDeckYellow.Add("event_yellow_01");
            var handler = CreateThreePlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "mine-b",
                OptionIds = { "1" }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(12));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            Assert.That(state.Map.ResourceTokens, Has.Count.EqualTo(1));
            Assert.That(state.Map.ResourceTokens[0].LocationId, Is.EqualTo("mine-b"));
            Assert.That(state.Map.ResourceTokens[0].ResourceType, Is.EqualTo(ResourceType.Originium));
            Assert.That(state.Map.Influences.Exists(influence =>
                influence.PlayerId == 1 &&
                influence.SlotId == InfluenceService.GetLocationSlotId("mine-b", 0)), Is.True);
            Assert.That(state.Decks.EventDeckYellow, Is.Empty);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.True);
            Assert.That(result.Events[0].Kind, Is.EqualTo(GameEventKind.CardMoved));
            Assert.That(result.Events[0].Data["cardName"], Is.EqualTo("大型源岩场"));
        }

        [Test]
        public void ExploreLocation_WhenTargetIsNotOpen_AllowsExplorationAndOpensLocation()
        {
            var state = CreateThreePlayerActionState();
            state.Map.OpenLocationIds.Remove("mine-b");
            state.Decks.EventDeckYellow.Add("event_yellow_01");
            var handler = CreateThreePlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "mine-b",
                OptionIds = { "0" }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Map.OpenLocationIds, Does.Contain("mine-b"));
            Assert.That(state.Map.ResourceTokens[0].LocationId, Is.EqualTo("mine-b"));
        }

        [Test]
        public void ExploreLocation_WhenRouteHasOpponentInfluence_PaysOpponent()
        {
            var state = CreateThreePlayerActionState();
            state.FindPlayer(1).Resources.GoldVoucher = 5;
            state.Decks.EventDeckYellow.Add("event_yellow_06");
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = InfluenceService.GetRouteSlotId("route-a-b", 0),
                RouteId = "route-a-b"
            });
            var handler = CreateThreePlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "mine-b",
                OptionIds = { "0" }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(3));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(3));
        }

        [Test]
        public void ExploreLocation_WithEventOptionParameter_UsesSelectedEventOption()
        {
            var state = CreateThreePlayerActionState();
            state.Decks.EventDeckYellow.Add("event_yellow_01");
            var handler = CreateThreePlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "mine-b",
                Parameters =
                {
                    { ExploreLocationCommandHandler.EventOptionIdParameter, "1" }
                }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(12));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            Assert.That(result.Events[0].Data["selectedOptionIndex"], Is.EqualTo("1"));
        }

        [Test]
        public void ExploreLocation_WithoutEventOption_PaysDrawsPlacesTokenAndOpensPendingChoice()
        {
            var state = CreateThreePlayerActionState();
            state.Decks.EventDeckYellow.Add("event_yellow_01");
            var handler = CreateThreePlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "mine-b"
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(8));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(0));
            Assert.That(state.Map.ResourceTokens, Has.Count.EqualTo(1));
            Assert.That(state.Map.Influences, Is.Empty);
            Assert.That(state.Decks.EventDeckYellow, Is.Empty);
            Assert.That(state.PendingChoice, Is.Not.Null);
            Assert.That(state.PendingChoice.ChoiceType, Is.EqualTo(ExploreLocationCommandHandler.ExploreEventChoiceType));
            Assert.That(state.PendingChoice.CardId, Is.EqualTo("event_yellow_01"));
            Assert.That(state.PendingChoice.TargetId, Is.EqualTo("mine-b"));
            Assert.That(state.PendingCardSession, Is.Not.Null);
            Assert.That(state.PendingCardSession.ChoiceType, Is.EqualTo(ExploreLocationCommandHandler.ExploreEventChoiceType));
            Assert.That(state.PendingCardSession.CardId, Is.EqualTo("event_yellow_01"));
            Assert.That(state.PendingCardSession.TargetId, Is.EqualTo("mine-b"));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
            Assert.That(result.Events.Exists(e => e.Kind == GameEventKind.ChoiceOpened), Is.True);
        }

        [Test]
        public void ExploreLocation_WithoutEventOption_WhenFirstOptionRequiresInfluenceTarget_OpensPendingChoice()
        {
            var state = CreateThreePlayerActionState();
            state.Decks.EventDeckYellow.Add("event_yellow_04");
            var handler = CreateThreePlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "mine-b"
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(8));
            Assert.That(state.Map.ResourceTokens.Exists(token => token.LocationId == "mine-b"), Is.True);
            Assert.That(state.Map.Influences, Is.Empty);
            Assert.That(state.PendingChoice, Is.Not.Null);
            Assert.That(state.PendingChoice.CardId, Is.EqualTo("event_yellow_04"));
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.False);
        }

        [Test]
        public void ResolvePendingChoice_ForExplorationEvent_AppliesRewardPlacesInfluenceAndMarksMainActionComplete()
        {
            var state = CreateThreePlayerActionState();
            state.Decks.EventDeckYellow.Add("event_yellow_01");
            var handler = CreateThreePlayerHandler();
            handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "mine-b"
            });
            Assert.That(state.PendingCardSession, Is.Not.Null);
            state.PendingChoice = null;

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = 1,
                OptionIds = { "1" }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.PendingChoice, Is.Null);
            Assert.That(state.PendingCardSession, Is.Null);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(12));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            Assert.That(state.Map.Influences.Exists(influence =>
                influence.PlayerId == 1 &&
                influence.SlotId == InfluenceService.GetLocationSlotId("mine-b", 0)), Is.True);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.True);
            Assert.That(result.Events[0].Kind, Is.EqualTo(GameEventKind.ChoiceResolved));
        }

        [Test]
        public void ExploreLocation_WithImmediateEventOption_MatchesPendingChoiceResolution()
        {
            var immediateState = CreateThreePlayerActionState();
            immediateState.Decks.EventDeckYellow.Add("event_yellow_01");
            var pendingState = CreateThreePlayerActionState();
            pendingState.Decks.EventDeckYellow.Add("event_yellow_01");
            var handler = CreateThreePlayerHandler();

            var immediateResult = handler.Handle(immediateState, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "mine-b",
                OptionIds = { "1" }
            });
            handler.Handle(pendingState, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "mine-b"
            });
            var pendingResult = handler.Handle(pendingState, new GameCommand
            {
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = 1,
                OptionIds = { "1" }
            });

            Assert.That(immediateResult.Succeeded, Is.True);
            Assert.That(pendingResult.Succeeded, Is.True);
            Assert.That(immediateState.FindPlayer(1).Resources.GoldVoucher,
                Is.EqualTo(pendingState.FindPlayer(1).Resources.GoldVoucher));
            Assert.That(immediateState.FindPlayer(1).Score, Is.EqualTo(pendingState.FindPlayer(1).Score));
            Assert.That(immediateState.Map.ResourceTokens, Has.Count.EqualTo(pendingState.Map.ResourceTokens.Count));
            Assert.That(immediateState.Map.Influences, Has.Count.EqualTo(pendingState.Map.Influences.Count));
            Assert.That(immediateState.FindPlayer(1).ActedMainActionThisTurn, Is.True);
            Assert.That(pendingState.FindPlayer(1).ActedMainActionThisTurn, Is.True);
        }

        [Test]
        public void ExploreLocation_WithPaymentRecipientsParameter_PaysChosenOpponent()
        {
            var state = CreateThreePlayerActionState();
            state.FindPlayer(1).Resources.GoldVoucher = 5;
            state.Decks.EventDeckYellow.Add("event_yellow_06");
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = InfluenceService.GetRouteSlotId("route-a-b", 0),
                RouteId = "route-a-b"
            });
            var handler = CreateThreePlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "mine-b",
                Parameters =
                {
                    { ExploreLocationCommandHandler.EventOptionIdParameter, "0" },
                    { ExploreLocationCommandHandler.PaymentRecipientsParameter, "route-a-b=2" }
                }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(3));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(2));
        }

        [Test]
        public void ExploreLocation_WithMultipleOpponentInfluencesOnRoute_PaysChosenRecipient()
        {
            var state = CreateThreePlayerActionState();
            state.FindPlayer(1).Resources.GoldVoucher = 5;
            state.Decks.EventDeckYellow.Add("event_yellow_06");
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = InfluenceService.GetRouteSlotId("route-a-b", 0),
                RouteId = "route-a-b"
            });
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 3,
                SlotId = InfluenceService.GetRouteSlotId("route-a-b", 1),
                RouteId = "route-a-b"
            });
            var handler = CreateThreePlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "mine-b",
                Parameters =
                {
                    { ExploreLocationCommandHandler.EventOptionIdParameter, "0" },
                    { ExploreLocationCommandHandler.PaymentRecipientsParameter, "route-a-b=3" }
                }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(3));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(state.FindPlayer(3).Resources.GoldVoucher, Is.EqualTo(2));
        }

        [Test]
        public void ExploreLocation_WithMultipleShortestPaths_PrefersSystemTollOverOpponentToll()
        {
            var state = CreateEqualShortestPathState();
            state.Decks.EventDeckYellow.Add("event_yellow_06");
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = InfluenceService.GetRouteSlotId("route-start-opponent", 0),
                RouteId = "route-start-opponent"
            });
            var handler = CreateEqualShortestPathHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "target",
                OptionIds = { "0" }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(6));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(0));
        }

        [Test]
        public void FindDefaultPathChoices_WithEqualOpponentTolls_ReturnsBothChoices()
        {
            var state = CreateEqualShortestPathState();
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = InfluenceService.GetRouteSlotId("route-start-opponent", 0),
                RouteId = "route-start-opponent"
            });
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 3,
                SlotId = InfluenceService.GetRouteSlotId("route-start-system", 0),
                RouteId = "route-start-system"
            });
            var mapQuery = new MapQueryService(CreateEqualShortestPathMap());
            var influenceService = new InfluenceService(mapQuery);
            var explorationService = new ExplorationService(
                mapQuery,
                influenceService,
                new EventDeckService(),
                new ResourceTokenService());

            var choices = explorationService.FindDefaultPathChoices(state, 1, "target");

            var hasPlayer2Path = false;
            var hasPlayer3Path = false;
            for (var i = 0; i < choices.Count; i++)
            {
                hasPlayer2Path = hasPlayer2Path || choices[i].RouteIds.Contains("route-start-opponent");
                hasPlayer3Path = hasPlayer3Path || choices[i].RouteIds.Contains("route-start-system");
            }

            Assert.That(choices, Has.Count.EqualTo(2));
            Assert.That(hasPlayer2Path, Is.True);
            Assert.That(hasPlayer3Path, Is.True);
        }

        [Test]
        public void ExploreLocation_WithInvalidPaymentRecipient_FailsWithoutChangingResources()
        {
            var state = CreateThreePlayerActionState();
            state.FindPlayer(1).Resources.GoldVoucher = 5;
            state.Decks.EventDeckYellow.Add("event_yellow_06");
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = InfluenceService.GetRouteSlotId("route-a-b", 0),
                RouteId = "route-a-b"
            });
            var handler = CreateThreePlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "mine-b",
                Parameters =
                {
                    { ExploreLocationCommandHandler.EventOptionIdParameter, "0" },
                    { ExploreLocationCommandHandler.PaymentRecipientsParameter, "route-a-b=3" }
                }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(5));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(state.Decks.EventDeckYellow, Has.Count.EqualTo(1));
            Assert.That(state.Map.ResourceTokens, Is.Empty);
        }

        [Test]
        public void ExploreLocation_WithMalformedPaymentRecipient_FailsWithoutChangingResources()
        {
            var state = CreateThreePlayerActionState();
            state.FindPlayer(1).Resources.GoldVoucher = 5;
            state.Decks.EventDeckYellow.Add("event_yellow_06");
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = InfluenceService.GetRouteSlotId("route-a-b", 0),
                RouteId = "route-a-b"
            });
            var handler = CreateThreePlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "mine-b",
                Parameters =
                {
                    { ExploreLocationCommandHandler.EventOptionIdParameter, "0" },
                    { ExploreLocationCommandHandler.PaymentRecipientsParameter, "route-a-b=player2" }
                }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(5));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(state.Decks.EventDeckYellow, Has.Count.EqualTo(1));
            Assert.That(state.Map.ResourceTokens, Is.Empty);
        }

        [Test]
        public void ExploreLocation_WithOwnInfluenceOnRoute_PaysNoRouteCost()
        {
            var state = CreateThreePlayerActionState();
            state.FindPlayer(1).Resources.GoldVoucher = 0;
            state.Decks.EventDeckYellow.Add("event_yellow_06");
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = InfluenceService.GetRouteSlotId("route-a-b", 0),
                RouteId = "route-a-b"
            });
            var handler = CreateThreePlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "mine-b",
                OptionIds = { "0" }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(3));
        }

        [Test]
        public void ExploreLocation_WithInsufficientGold_FailsWithoutDrawingOrPlacingToken()
        {
            var state = CreateThreePlayerActionState();
            state.FindPlayer(1).Resources.GoldVoucher = 1;
            state.Decks.EventDeckYellow.Add("event_yellow_06");
            var handler = CreateThreePlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "mine-b",
                OptionIds = { "0" }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InsufficientResource));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(1));
            Assert.That(state.Decks.EventDeckYellow, Has.Count.EqualTo(1));
            Assert.That(state.Map.ResourceTokens, Is.Empty);
            Assert.That(state.Map.Influences, Is.Empty);
        }

        [Test]
        public void ExploreLocation_WhenTargetAlreadyHasResourceToken_Fails()
        {
            var state = CreateThreePlayerActionState();
            state.Decks.EventDeckYellow.Add("event_yellow_06");
            state.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = "mine-b",
                ResourceType = ResourceType.Iron,
                Amount = 1
            });
            var handler = CreateThreePlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "mine-b"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.Decks.EventDeckYellow, Has.Count.EqualTo(1));
        }

        [Test]
        public void ExploreLocation_RedZoneBeforeOpenRound_Fails()
        {
            var state = CreateFourPlayerActionState();
            state.Round = 3;
            state.FindPlayer(1).CityLocationId = "D-02";
            state.Map.OpenLocationIds.Add("F-01");
            state.Decks.EventDeckRed.Add("event_red_01");
            var handler = CreateFourPlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "F-01"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.ClosedLocation));
            Assert.That(state.Decks.EventDeckRed, Has.Count.EqualTo(1));
            Assert.That(state.Map.ResourceTokens, Is.Empty);
        }

        [Test]
        public void ExploreLocation_OnFourPlayerMapWithSingleLocalPlayer_RedZoneOpensAtRound4()
        {
            var state = CreateFourPlayerSingleLocalPlayerActionState();
            state.Round = 4;
            state.Decks.EventDeckRed.Add("event_red_01");
            var handler = CreateFourPlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "F-01",
                OptionIds = { "0" }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Map.ResourceTokens, Has.Count.EqualTo(1));
            Assert.That(state.Map.ResourceTokens[0].LocationId, Is.EqualTo("F-01"));
            Assert.That(state.Decks.EventDeckRed, Is.Empty);
        }

        [Test]
        public void ExploreLocation_ToF03_UsesMinimumGoldVoucherRouteAcrossPaidRouteGroups()
        {
            var state = CreateFourPlayerActionState();
            state.Round = 4;
            state.FindPlayer(1).CityLocationId = "A-02";
            state.FindPlayer(1).Resources.GoldVoucher = 2;
            state.Decks.EventDeckRed.Add("event_red_06");
            AddRouteInfluence(state, 1, "A2");
            AddRouteInfluence(state, 1, "R7");
            AddRouteInfluence(state, 1, "E1");
            var handler = CreateFourPlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "F-03",
                OptionIds = { "0" }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(state.Map.ResourceTokens, Has.Count.EqualTo(1));
            Assert.That(state.Map.ResourceTokens[0].LocationId, Is.EqualTo("F-03"));
            Assert.That(state.Decks.EventDeckRed, Is.Empty);
        }

        [Test]
        public void ExploreLocation_WithOpponentRewardEffect_GrantsResourceToAllOpponents()
        {
            var state = CreateFourPlayerActionState();
            state.Round = 4;
            state.FindPlayer(1).CityLocationId = "D-02";
            state.Map.OpenLocationIds.Add("F-01");
            state.Decks.EventDeckRed.Add("event_red_05");
            var handler = CreateFourPlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "F-01",
                OptionIds = { "2" }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(3));
            Assert.That(state.FindPlayer(3).Resources.GoldVoucher, Is.EqualTo(3));
            Assert.That(state.FindPlayer(4).Resources.GoldVoucher, Is.EqualTo(3));
        }

        [Test]
        public void ExploreLocation_WithPlaceInfluenceEffect_PlacesChosenEventInfluence()
        {
            var state = CreateFourPlayerActionState();
            state.Round = 4;
            state.FindPlayer(1).CityLocationId = "D-02";
            state.Map.OpenLocationIds.Add("F-01");
            state.Decks.EventDeckRed.Add("event_red_01");
            var handler = CreateFourPlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "F-01",
                OptionIds = { "2" },
                Parameters =
                {
                    { ExploreLocationCommandHandler.EventInfluenceSlotIdParameter, InfluenceService.GetRouteSlotId("F1", 0) }
                }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Map.Influences.Exists(influence =>
                influence.PlayerId == 1 &&
                influence.SlotId == InfluenceService.GetLocationSlotId("F-01", 0)), Is.True);
            Assert.That(state.Map.Influences.Exists(influence =>
                influence.PlayerId == 1 &&
                influence.SlotId == InfluenceService.GetRouteSlotId("F1", 0)), Is.True);
        }

        [Test]
        public void ExploreLocation_WithPlaceInfluenceEffect_WhenCombinedCostInsufficient_FailsWithoutChangingState()
        {
            var state = CreateThreePlayerActionState();
            state.FindPlayer(1).Resources.GoldVoucher = 5;
            state.Decks.EventDeckYellow.Add("event_yellow_04");
            var handler = CreateThreePlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "mine-b",
                OptionIds = { "0" },
                Parameters =
                {
                    { ExploreLocationCommandHandler.EventInfluenceSlotIdParameter, InfluenceService.GetRouteSlotId("route-a-b", 0) }
                }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InsufficientResource));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(5));
            Assert.That(state.Decks.EventDeckYellow, Has.Count.EqualTo(1));
            Assert.That(state.Map.ResourceTokens, Is.Empty);
            Assert.That(state.Map.Influences, Is.Empty);
        }

        [Test]
        public void ResolvePendingChoice_WithPlaceInfluenceEffect_WhenTargetSlotOccupied_FailsAndKeepsPendingChoice()
        {
            var state = CreateFourPlayerActionState();
            state.Round = 4;
            state.FindPlayer(1).CityLocationId = "D-02";
            state.Map.OpenLocationIds.Add("F-01");
            state.Decks.EventDeckRed.Add("event_red_01");
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = InfluenceService.GetRouteSlotId("F1", 0),
                RouteId = "F1"
            });
            var handler = CreateFourPlayerHandler();
            handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "F-01"
            });
            var goldBeforeResolve = state.FindPlayer(1).Resources.GoldVoucher;

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = 1,
                OptionIds = { "2" },
                Parameters =
                {
                    { ExploreLocationCommandHandler.EventInfluenceSlotIdParameter, InfluenceService.GetRouteSlotId("F1", 0) }
                }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.OccupiedSlot));
            Assert.That(state.PendingChoice, Is.Not.Null);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(goldBeforeResolve));
            Assert.That(state.Map.Influences.Exists(influence =>
                influence.PlayerId == 1 &&
                influence.SlotId == InfluenceService.GetLocationSlotId("F-01", 0)), Is.False);
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.False);
        }

        [Test]
        public void ExploreLocation_WithExplicitPath_UsesChosenRouteSequence()
        {
            var state = CreateThreePlayerActionState();
            state.FindPlayer(2).CityLocationId = string.Empty;
            state.Map.OpenLocationIds.Add("harbor-c");
            state.Decks.EventDeckYellow.Add("event_yellow_06");
            var handler = CreateThreePlayerHandler();

            var command = new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "harbor-c",
                OptionIds = { "1" },
                Parameters =
                {
                    { ExploreLocationCommandHandler.PathLocationIdsParameter, "city-a,mine-b,harbor-c" },
                    { ExploreLocationCommandHandler.RouteIdsParameter, "route-a-b,route-b-c" }
                }
            };

            var result = handler.Handle(state, command);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(6));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(4));
            Assert.That(state.Map.ResourceTokens[0].LocationId, Is.EqualTo("harbor-c"));
        }

        [Test]
        public void ExploreLocation_WithExplicitPath_WhenTargetHasOpponentCity_FailsWithoutMutatingState()
        {
            var state = CreateThreePlayerActionState();
            state.Map.OpenLocationIds.Add("harbor-c");
            state.Decks.EventDeckYellow.Add("event_yellow_06");
            var handler = CreateThreePlayerHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ExploreLocation,
                PlayerId = 1,
                TargetId = "harbor-c",
                OptionIds = { "1" },
                Parameters =
                {
                    { ExploreLocationCommandHandler.PathLocationIdsParameter, "city-a,mine-b,harbor-c" },
                    { ExploreLocationCommandHandler.RouteIdsParameter, "route-a-b,route-b-c" }
                }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(10));
            Assert.That(state.Decks.EventDeckYellow, Has.Count.EqualTo(1));
            Assert.That(state.Map.ResourceTokens, Is.Empty);
            Assert.That(state.Map.Influences, Is.Empty);
        }

        private static ExploreLocationCommandHandler CreateThreePlayerHandler()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder());
            var influenceService = new InfluenceService(mapQuery);
            var explorationService = new ExplorationService(
                mapQuery,
                influenceService,
                new EventDeckService(),
                new ResourceTokenService());
            return new ExploreLocationCommandHandler(explorationService);
        }

        private static ExploreLocationCommandHandler CreateFourPlayerHandler()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var influenceService = new InfluenceService(mapQuery);
            var explorationService = new ExplorationService(
                mapQuery,
                influenceService,
                new EventDeckService(),
                new ResourceTokenService());
            return new ExploreLocationCommandHandler(explorationService);
        }

        private static ExploreLocationCommandHandler CreateEqualShortestPathHandler()
        {
            var mapQuery = new MapQueryService(CreateEqualShortestPathMap());
            var influenceService = new InfluenceService(mapQuery);
            var explorationService = new ExplorationService(
                mapQuery,
                influenceService,
                new EventDeckService(),
                new ResourceTokenService());
            return new ExploreLocationCommandHandler(explorationService);
        }

        private static GameState CreateThreePlayerActionState()
        {
            return new GameState
            {
                Phase = GamePhase.ActionRound1,
                Round = 1,
                ActionRound = 1,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Red,
                        CityLocationId = "city-a",
                        Resources =
                        {
                            GoldVoucher = 10
                        }
                    },
                    new PlayerState
                    {
                        PlayerId = 2,
                        Color = PlayerColor.Blue,
                        CityLocationId = "harbor-c"
                    },
                    new PlayerState
                    {
                        PlayerId = 3,
                        Color = PlayerColor.Green
                    }
                },
                Map =
                {
                    OpenLocationIds = { "city-a", "mine-b" }
                }
            };
        }

        private static GameState CreateEqualShortestPathState()
        {
            return new GameState
            {
                Phase = GamePhase.ActionRound1,
                Round = 1,
                ActionRound = 1,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Red,
                        CityLocationId = "start",
                        Resources =
                        {
                            GoldVoucher = 10
                        }
                    },
                    new PlayerState
                    {
                        PlayerId = 2,
                        Color = PlayerColor.Blue
                    },
                    new PlayerState
                    {
                        PlayerId = 3,
                        Color = PlayerColor.Green
                    }
                },
                Map =
                {
                    OpenLocationIds = { "start" }
                }
            };
        }

        private static GameState CreateFourPlayerActionState()
        {
            return new GameState
            {
                Phase = GamePhase.ActionRound1,
                Round = 1,
                ActionRound = 1,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Red,
                        Resources =
                        {
                            GoldVoucher = 10
                        }
                    },
                    new PlayerState { PlayerId = 2, Color = PlayerColor.Blue },
                    new PlayerState { PlayerId = 3, Color = PlayerColor.Green },
                    new PlayerState { PlayerId = 4, Color = PlayerColor.Yellow }
                }
            };
        }

        private static GameState CreateFourPlayerSingleLocalPlayerActionState()
        {
            return new GameState
            {
                Phase = GamePhase.ActionRound1,
                Round = 1,
                ActionRound = 1,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                MapId = StaticMapDefinitions.FourPlayerMapId,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Red,
                        CityLocationId = "D-02",
                        Resources =
                        {
                            GoldVoucher = 10
                        }
                    }
                }
            };
        }

        private static GameMapDefinition CreateEqualShortestPathMap()
        {
            return new GameMapDefinition
            {
                MapId = "equal-shortest-path-test",
                MinPlayers = 2,
                MaxPlayers = 3,
                Locations = new List<MapLocationDefinition>
                {
                    new MapLocationDefinition
                    {
                        LocationId = "start",
                        ResourceType = ResourceType.Iron,
                        CanDockCity = true,
                        ResourceSlotCount = 1,
                        InfluenceSlotCount = 1
                    },
                    new MapLocationDefinition
                    {
                        LocationId = "opponent-mid",
                        ResourceType = ResourceType.Iron,
                        CanDockCity = true,
                        ResourceSlotCount = 1,
                        InfluenceSlotCount = 1
                    },
                    new MapLocationDefinition
                    {
                        LocationId = "system-mid",
                        ResourceType = ResourceType.Iron,
                        CanDockCity = true,
                        ResourceSlotCount = 1,
                        InfluenceSlotCount = 1
                    },
                    new MapLocationDefinition
                    {
                        LocationId = "target",
                        ResourceType = ResourceType.Iron,
                        CanDockCity = true,
                        ResourceSlotCount = 1,
                        InfluenceSlotCount = 1
                    }
                },
                Routes = new List<MapRouteDefinition>
                {
                    new MapRouteDefinition
                    {
                        RouteId = "route-start-opponent",
                        FromLocationId = "start",
                        ToLocationId = "opponent-mid",
                        InfluenceSlotCount = 1,
                        BaseCost = 2
                    },
                    new MapRouteDefinition
                    {
                        RouteId = "route-opponent-target",
                        FromLocationId = "opponent-mid",
                        ToLocationId = "target",
                        InfluenceSlotCount = 1,
                        BaseCost = 2
                    },
                    new MapRouteDefinition
                    {
                        RouteId = "route-start-system",
                        FromLocationId = "start",
                        ToLocationId = "system-mid",
                        InfluenceSlotCount = 1,
                        BaseCost = 2
                    },
                    new MapRouteDefinition
                    {
                        RouteId = "route-system-target",
                        FromLocationId = "system-mid",
                        ToLocationId = "target",
                        InfluenceSlotCount = 1,
                        BaseCost = 2
                    }
                }
            };
        }

        private static void AddRouteInfluence(GameState state, int playerId, string routeId)
        {
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = playerId,
                SlotId = InfluenceService.GetRouteSlotId(routeId, 0),
                RouteId = routeId
            });
        }
    }
}
