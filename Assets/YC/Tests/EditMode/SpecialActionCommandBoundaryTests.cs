using NUnit.Framework;
using UnityEngine;
using YC.Application.Gameplay;
using YC.Domain.Cards;
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
    public sealed class SpecialActionCommandBoundaryTests
    {
        private const string EfficientMarkerId = "efficient-marker";
        private const string IndustrialHubMarkerId = "industrial-hub-marker";

        [Test]
        public void EfficientMove_TwoEventMoves_PreserveSessionUntilFinalSettlementAndSpendMainAction()
        {
            var context = CreateContext();
            var state = CreateEfficientMoveState(3);
            state.Decks.EventDeckYellow.Add("event_yellow_02");
            state.Decks.EventDeckYellow.Add("event_yellow_01");
            var player = state.FindPlayer(1);

            var begin = context.Handler.Handle(
                state,
                CreateBeginCommand(
                    "efficient-two-events-begin",
                    SpecialActionDatabase.EfficientMobileManagementSystem,
                    EfficientMarkerId));

            Assert.That(begin.Succeeded, Is.True, begin.Validation.Reason);
            Assert.That(state.PendingSpecialAction, Is.Not.Null);
            var sessionId = state.PendingSpecialAction.SessionId;
            Assert.That(state.PendingSpecialAction.Step, Is.EqualTo(SpecialActionPendingSteps.AwaitFreeMoveTarget));
            Assert.That(state.PendingSpecialAction.RemainingRepetitions, Is.EqualTo(2));
            AssertBudget(player, 1, 0, false);
            Assert.That(player.Resources.OriginiumShard, Is.Zero);
            Assert.That(player.CharacterCardLockedThisTurn, Is.True);
            Assert.That(player.DeclaredCityStyles[0].MarkerArea, Is.EqualTo(SpecialActionMarkerAreas.UsedFromTwo));

            var firstMove = context.Handler.Handle(
                state,
                CreateMoveResolution("efficient-first-move", sessionId, "D-01"));

            Assert.That(firstMove.Succeeded, Is.True, firstMove.Validation.Reason);
            Assert.That(player.CityLocationId, Is.EqualTo("D-01"));
            Assert.That(state.PendingCardSession, Is.Not.Null);
            Assert.That(state.PendingCardSession.CardId, Is.EqualTo("event_yellow_01"));
            Assert.That(state.PendingSpecialAction.SessionId, Is.EqualTo(sessionId));
            Assert.That(state.PendingSpecialAction.Step, Is.EqualTo(SpecialActionPendingSteps.AwaitMoveEvent));
            Assert.That(state.PendingSpecialAction.RemainingRepetitions, Is.EqualTo(1));
            AssertBudget(player, 1, 0, false);

            var firstEvent = context.Handler.Handle(
                state,
                CreateEventResolution("efficient-first-event", sessionId, "D-01"));

            Assert.That(firstEvent.Succeeded, Is.True, firstEvent.Validation.Reason);
            Assert.That(state.PendingCardSession, Is.Null);
            Assert.That(state.PendingSpecialAction, Is.Not.Null);
            Assert.That(state.PendingSpecialAction.SessionId, Is.EqualTo(sessionId));
            Assert.That(state.PendingSpecialAction.Step, Is.EqualTo(SpecialActionPendingSteps.AwaitFreeMoveTarget));
            Assert.That(state.PendingSpecialAction.RemainingRepetitions, Is.EqualTo(1));
            Assert.That(player.Resources.Originium, Is.EqualTo(5));
            Assert.That(context.OptionQuery.GetLegalFreeMoveTargetIds(state, 1), Does.Contain("A-03"));
            AssertBudget(player, 1, 0, false);

            var secondMove = context.Handler.Handle(
                state,
                CreateMoveResolution("efficient-second-move", sessionId, "A-03"));

            Assert.That(secondMove.Succeeded, Is.True, secondMove.Validation.Reason);
            Assert.That(player.CityLocationId, Is.EqualTo("A-03"));
            Assert.That(state.PendingCardSession, Is.Not.Null);
            Assert.That(state.PendingCardSession.CardId, Is.EqualTo("event_yellow_02"));
            Assert.That(state.PendingSpecialAction.SessionId, Is.EqualTo(sessionId));
            Assert.That(state.PendingSpecialAction.Step, Is.EqualTo(SpecialActionPendingSteps.AwaitMoveEvent));
            Assert.That(state.PendingSpecialAction.RemainingRepetitions, Is.Zero);
            AssertBudget(player, 1, 0, false);

            var secondEvent = context.Handler.Handle(
                state,
                CreateEventResolution("efficient-second-event", sessionId, "A-03"));

            Assert.That(secondEvent.Succeeded, Is.True, secondEvent.Validation.Reason);
            Assert.That(state.PendingCardSession, Is.Null);
            Assert.That(state.PendingSpecialAction, Is.Null);
            Assert.That(player.Resources.Originium, Is.EqualTo(10));
            Assert.That(player.CharacterCardLockedThisTurn, Is.True);
            Assert.That(player.UsedSpecialActionIdsThisRound,
                Is.EqualTo(new[] { SpecialActionDatabase.EfficientMobileManagementSystem }));
            Assert.That(player.DeclaredCityStyles[0].MarkerArea, Is.EqualTo(SpecialActionMarkerAreas.UsedFromTwo));
            Assert.That(player.DeclaredCityStyles[0].RemainingSpecialActionUses, Is.EqualTo(2));
            Assert.That(state.Map.ResourceTokens.Exists(token => token.LocationId == "D-01"), Is.True);
            Assert.That(state.Map.ResourceTokens.Exists(token => token.LocationId == "A-03"), Is.True);
            Assert.That(state.Decks.EventDeckYellow, Is.Empty);
            AssertBudget(player, 0, 1, true);
        }

        [Test]
        public void EfficientMove_WhenSecondTargetBecomesUnavailable_SkipsRemainingMoveAndCompletes()
        {
            var context = CreateContext();
            var state = CreateEfficientMoveState(0);
            var player = state.FindPlayer(1);
            player.CityLocationId = "D-01";
            player.CharacterCardLockedThisTurn = true;
            player.UsedSpecialActionIdsThisRound.Add(SpecialActionDatabase.EfficientMobileManagementSystem);
            player.DeclaredCityStyles[0].MarkerArea = SpecialActionMarkerAreas.UsedFromTwo;
            state.PendingSpecialAction = new PendingSpecialActionState
            {
                SessionId = "efficient-second-move-session",
                PlayerId = 1,
                SpecialActionId = SpecialActionDatabase.EfficientMobileManagementSystem,
                DeclarationMarkerId = EfficientMarkerId,
                SourceCommandId = "efficient-first-move-settled",
                Step = SpecialActionPendingSteps.AwaitFreeMoveTarget,
                RemainingRepetitions = 1,
                TraversedRouteId = "R1",
                ResolvedTargetIds = { "D-01" }
            };
            Assert.That(context.OptionQuery.GetLegalFreeMoveTargetIds(state, 1), Is.Empty);

            var secondMoveResolution = context.Handler.Handle(
                state,
                CreateResolutionCommand(
                    "efficient-skip-second-move",
                    state.PendingSpecialAction.SessionId,
                    string.Empty));

            Assert.That(secondMoveResolution.Succeeded, Is.True, secondMoveResolution.Validation.Reason);
            Assert.That(player.CityLocationId, Is.EqualTo("D-01"));
            Assert.That(state.PendingCardSession, Is.Null);
            Assert.That(state.PendingSpecialAction, Is.Null, "只剩第二段且没有合法目标时应自动跳过并完成行动。");
            Assert.That(player.Resources.OriginiumShard, Is.Zero);
            Assert.That(player.CharacterCardLockedThisTurn, Is.True);
            Assert.That(player.UsedSpecialActionIdsThisRound,
                Does.Contain(SpecialActionDatabase.EfficientMobileManagementSystem));
            AssertBudget(player, 0, 1, true);
        }

        [Test]
        public void ResolvePendingChoice_WithExpiredSpecialActionSessionId_IsRejectedAtomically()
        {
            var context = CreateContext();
            var state = CreateEfficientMoveState(3);
            state.Decks.EventDeckYellow.Add("event_yellow_01");
            var begin = context.Handler.Handle(
                state,
                CreateBeginCommand(
                    "efficient-expired-begin",
                    SpecialActionDatabase.EfficientMobileManagementSystem,
                    EfficientMarkerId));
            Assert.That(begin.Succeeded, Is.True, begin.Validation.Reason);
            var activeSessionId = state.PendingSpecialAction.SessionId;
            var before = JsonUtility.ToJson(state);

            var staleResolution = context.Handler.Handle(
                state,
                CreateMoveResolution("efficient-expired-resolve", "expired-" + activeSessionId, "D-01"));

            Assert.That(staleResolution.Succeeded, Is.False);
            Assert.That(staleResolution.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidSource));
            Assert.That(JsonUtility.ToJson(state), Is.EqualTo(before));
            Assert.That(state.PendingSpecialAction.SessionId, Is.EqualTo(activeSessionId));
            Assert.That(state.PendingSpecialAction.Step, Is.EqualTo(SpecialActionPendingSteps.AwaitFreeMoveTarget));
        }

        [Test]
        public void AwaitMoveEvent_RequiresMatchingSpecialActionSessionIdWithoutWriteBack()
        {
            var context = CreateContext();
            var state = CreateEfficientMoveState(3);
            state.Decks.EventDeckYellow.Add("event_yellow_01");
            Assert.That(context.Handler.Handle(
                state,
                CreateBeginCommand(
                    "efficient-event-session-begin",
                    SpecialActionDatabase.EfficientMobileManagementSystem,
                    EfficientMarkerId)).Succeeded, Is.True);
            var sessionId = state.PendingSpecialAction.SessionId;
            Assert.That(context.Handler.Handle(
                state,
                CreateMoveResolution("efficient-event-session-move", sessionId, "D-01")).Succeeded, Is.True);
            Assert.That(state.PendingSpecialAction.Step, Is.EqualTo(SpecialActionPendingSteps.AwaitMoveEvent));
            var before = JsonUtility.ToJson(state);

            var missingSession = new GameCommand
            {
                CommandId = "efficient-event-session-missing",
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = 1,
                TargetId = "D-01",
                OptionIds = { "0" }
            };
            var missingResult = context.Handler.Handle(state, missingSession);

            Assert.That(missingResult.Succeeded, Is.False);
            Assert.That(missingResult.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidSource));
            Assert.That(JsonUtility.ToJson(state), Is.EqualTo(before));

            var staleSession = CreateEventResolution(
                "efficient-event-session-stale",
                "stale-" + sessionId,
                "D-01");
            var staleResult = context.Handler.Handle(state, staleSession);

            Assert.That(staleResult.Succeeded, Is.False);
            Assert.That(staleResult.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidSource));
            Assert.That(JsonUtility.ToJson(state), Is.EqualTo(before));
            Assert.That(state.PendingSpecialAction.SessionId, Is.EqualTo(sessionId));
            Assert.That(state.PendingCardSession, Is.Not.Null);
        }

        [Test]
        public void UseSpecialAction_WhenFixedCostIsInsufficient_DoesNotConsumeMarkerBudgetOrResources()
        {
            var context = CreateContext();
            var state = CreateEfficientMoveState(2);
            state.Decks.EventDeckYellow.Add("event_yellow_01");
            var player = state.FindPlayer(1);
            var before = JsonUtility.ToJson(state);

            var result = context.Handler.Handle(
                state,
                CreateBeginCommand(
                    "efficient-insufficient-begin",
                    SpecialActionDatabase.EfficientMobileManagementSystem,
                    EfficientMarkerId));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InsufficientResource));
            Assert.That(JsonUtility.ToJson(state), Is.EqualTo(before));
            Assert.That(state.PendingSpecialAction, Is.Null);
            Assert.That(player.Resources.OriginiumShard, Is.EqualTo(2));
            Assert.That(player.DeclaredCityStyles[0].MarkerArea, Is.EqualTo(CityStyleMarkerAreas.UsesTwo));
            Assert.That(player.DeclaredCityStyles[0].RemainingSpecialActionUses, Is.EqualTo(2));
            Assert.That(player.UsedSpecialActionIdsThisRound, Is.Empty);
            Assert.That(player.CharacterCardLockedThisTurn, Is.False);
            AssertBudget(player, 1, 0, false);
        }

        [Test]
        public void UseSpecialAction_WhenRepeatedInSameRound_IsRejectedWithoutWriteBack()
        {
            var context = CreateContext();
            var state = CreateIndustrialHubState(12);
            var player = state.FindPlayer(1);

            var first = context.Handler.Handle(
                state,
                CreateBeginCommand(
                    "industrial-first-use",
                    SpecialActionDatabase.SourceStoneIndustrialHub,
                    IndustrialHubMarkerId));

            Assert.That(first.Succeeded, Is.True, first.Validation.Reason);
            Assert.That(state.PendingSpecialAction, Is.Null);
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(6));
            Assert.That(player.CharacterCardLockedThisTurn, Is.True);
            Assert.That(player.UsedSpecialActionIdsThisRound,
                Is.EqualTo(new[] { SpecialActionDatabase.SourceStoneIndustrialHub }));
            AssertBudget(player, 2, 1, false);
            var beforeRepeat = JsonUtility.ToJson(state);

            var repeated = context.Handler.Handle(
                state,
                CreateBeginCommand(
                    "industrial-repeated-use",
                    SpecialActionDatabase.SourceStoneIndustrialHub,
                    IndustrialHubMarkerId));

            Assert.That(repeated.Succeeded, Is.False);
            Assert.That(repeated.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidSource));
            Assert.That(JsonUtility.ToJson(state), Is.EqualTo(beforeRepeat));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(6));
            Assert.That(player.DeclaredCityStyles[0].MarkerArea, Is.EqualTo(SpecialActionMarkerAreas.UsedFromTwo));
            Assert.That(player.DeclaredCityStyles[0].RemainingSpecialActionUses, Is.EqualTo(2));
            AssertBudget(player, 2, 1, false);
        }

        private static TestContext CreateContext()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var influenceService = new InfluenceService(mapQuery);
            var movementService = new CityMovementService(
                mapQuery,
                influenceService,
                new TravelCostService(mapQuery),
                new EventDeckService(),
                new ResourceTokenService());
            var lifecycle = new SpecialActionLifecycleService();
            var budget = new MainActionBudgetService();
            var optionQuery = new SpecialActionOptionQueryService(
                mapQuery,
                influenceService,
                movementService,
                lifecycle,
                budget);
            var moveHandler = new MoveCityCommandHandler(movementService);
            return new TestContext
            {
                OptionQuery = optionQuery,
                Handler = new UseSpecialActionCommandHandler(
                    new SpecialActionService(
                        optionQuery,
                        lifecycle,
                        influenceService,
                        new FacilityInfluenceEffectService(influenceService),
                        budget),
                    optionQuery,
                    moveHandler)
            };
        }

        private static GameState CreateEfficientMoveState(int originiumShard)
        {
            var state = CreateActionState();
            var player = state.FindPlayer(1);
            player.Resources.OriginiumShard = originiumShard;
            AddUnlockedMarker(
                player,
                EfficientMarkerId,
                CityStyleDatabase.EfficientMobileManagementSystem,
                SpecialActionDatabase.EfficientMobileManagementSystem);
            return state;
        }

        private static GameState CreateIndustrialHubState(int goldVoucher)
        {
            var state = CreateActionState();
            var player = state.FindPlayer(1);
            player.Resources.GoldVoucher = goldVoucher;
            AddUnlockedMarker(
                player,
                IndustrialHubMarkerId,
                CityStyleDatabase.SourceStoneIndustrialHub,
                SpecialActionDatabase.SourceStoneIndustrialHub);
            return state;
        }

        private static GameState CreateActionState()
        {
            return new GameState
            {
                Phase = GamePhase.ActionRound1,
                Round = 4,
                ActionRound = 1,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
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
                    OpenLocationIds = { "A-01", "B-01" }
                }
            };
        }

        private static void AddUnlockedMarker(
            PlayerState player,
            string markerId,
            string cityStyleId,
            string specialActionId)
        {
            player.DeclaredCityStyleIds.Add(cityStyleId);
            player.DeclaredCityStyles.Add(new CityStyleDeclarationState
            {
                InfluenceMarkerId = markerId,
                CityStyleId = cityStyleId,
                MarkerArea = CityStyleMarkerAreas.UsesTwo,
                UnlockedSpecialActionId = specialActionId,
                RemainingSpecialActionUses = 2
            });
        }

        private static GameCommand CreateBeginCommand(
            string commandId,
            string specialActionId,
            string markerId)
        {
            var command = new GameCommand
            {
                CommandId = commandId,
                Kind = GameCommandKind.UseSpecialAction,
                PlayerId = 1,
                SourceId = markerId,
                TargetId = specialActionId
            };
            command.Parameters[UseSpecialActionCommandHandler.SpecialActionIdParameter] = specialActionId;
            command.Parameters[UseSpecialActionCommandHandler.DeclarationMarkerIdParameter] = markerId;
            return command;
        }

        private static GameCommand CreateMoveResolution(
            string commandId,
            string sessionId,
            string targetLocationId)
        {
            var command = CreateResolutionCommand(commandId, sessionId, targetLocationId);
            command.Parameters[UseSpecialActionCommandHandler.TargetLocationIdParameter] = targetLocationId;
            return command;
        }

        private static GameCommand CreateEventResolution(
            string commandId,
            string sessionId,
            string targetLocationId)
        {
            var command = CreateResolutionCommand(commandId, sessionId, targetLocationId);
            command.OptionIds.Add("0");
            return command;
        }

        private static GameCommand CreateResolutionCommand(
            string commandId,
            string sessionId,
            string targetId)
        {
            var command = new GameCommand
            {
                CommandId = commandId,
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = 1,
                SourceId = sessionId,
                TargetId = targetId
            };
            command.Parameters[UseSpecialActionCommandHandler.SessionIdParameter] = sessionId;
            return command;
        }

        private static void AssertBudget(PlayerState player, int remaining, int completed, bool acted)
        {
            Assert.That(player.RemainingMainActionsThisTurn, Is.EqualTo(remaining));
            Assert.That(player.CompletedMainActionsThisTurn, Is.EqualTo(completed));
            Assert.That(player.ActedMainActionThisTurn, Is.EqualTo(acted));
        }

        private sealed class TestContext
        {
            public UseSpecialActionCommandHandler Handler;
            public SpecialActionOptionQueryService OptionQuery;
        }
    }
}
