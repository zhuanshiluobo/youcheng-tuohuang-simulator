using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class CharacterMapInteractionCoordinatorTests
    {
        [Test]
        public void LiskarmCleanup_HighlightsOnlyLegalInfluenceAndSubmitsAfterMapClick()
        {
            var state = CreateState(CharacterCardDatabase.Liskarm, GamePhase.Cleanup);
            var slotId = InfluenceService.GetRouteSlotId("A1", 0);
            state.Map.Influences.Add(InfluenceAt(1, slotId, "A1"));
            state.PendingCharacterEffect = new PendingCharacterEffectState
            {
                ChoiceType = CharacterPendingChoiceTypes.LiskarmCleanupRemoval,
                PlayerId = 1,
                CardId = state.FindPlayer(1).CoveredCharacterCardId,
                OptionIds = { slotId }
            };

            IReadOnlyList<WorkflowHighlight> highlights = null;
            IReadOnlyDictionary<string, string> submitted = null;
            var prompt = string.Empty;
            var coordinator = CreateCoordinator(
                state,
                values => submitted = values,
                (mode, values) => { },
                values => highlights = values,
                value => prompt = value);

            Assert.That(coordinator.Synchronize(), Is.True);
            Assert.That(highlights, Has.Count.EqualTo(1));
            Assert.That(highlights[0].TargetId, Is.EqualTo(slotId));
            Assert.That(prompt, Does.Contain("雷蛇").And.Contain("完成后才能结束本回合"));

            coordinator.TryHandleInfluenceSlotClicked("route:invalid:0");
            Assert.That(submitted, Is.Null);
            coordinator.TryHandleInfluenceSlotClicked(slotId);
            Assert.That(submitted[CharacterEffectParameterKeys.TargetInfluenceSlotId], Is.EqualTo(slotId));
        }

        [Test]
        public void LiskarmStrategy_SelectsTwoDistinctPlacementSlotsOnMapBeforeSubmitting()
        {
            var state = CreateState(CharacterCardDatabase.Liskarm, GamePhase.ActionRound1);
            IReadOnlyList<WorkflowHighlight> highlights = null;
            string submittedMode = null;
            IReadOnlyDictionary<string, string> submitted = null;
            var coordinator = CreateCoordinator(
                state,
                values => { },
                (mode, values) => { submittedMode = mode; submitted = values; },
                values => highlights = values,
                value => { });

            Assert.That(coordinator.TryBeginEffect(
                UseCharacterCardCommandHandler.Strategy,
                CharacterCardEffectKind.LiskarmSecurityProtocol), Is.True);
            Assert.That(highlights, Is.Not.Empty);
            var first = highlights[0].TargetId;
            coordinator.TryHandleInfluenceSlotClicked(first);
            Assert.That(submitted, Is.Null);
            Assert.That(highlights, Is.Not.Empty);
            Assert.That(highlights.Any(item => item.TargetId == first), Is.False);
            var second = highlights[0].TargetId;
            coordinator.TryHandleInfluenceSlotClicked(second);

            Assert.That(submittedMode, Is.EqualTo(UseCharacterCardCommandHandler.Strategy));
            Assert.That(submitted[CharacterEffectParameterKeys.PlacementSlotId1], Is.EqualTo(first));
            Assert.That(submitted[CharacterEffectParameterKeys.PlacementSlotId2], Is.EqualTo(second));
            Assert.That(submitted[CharacterEffectParameterKeys.OfferSecondEffect], Is.EqualTo("true"));
        }

        [Test]
        public void LiskarmTactic_ReplacesOpponentInfluenceThroughMapTarget()
        {
            var state = CreateState(CharacterCardDatabase.Liskarm, GamePhase.ActionRound1);
            state.Players.Add(new PlayerState { PlayerId = 2, Color = PlayerColor.Blue, InfluenceSupply = 29 });
            var opponentSlot = InfluenceService.GetRouteSlotId("A1", 0);
            state.Map.Influences.Add(InfluenceAt(2, opponentSlot, "A1"));
            IReadOnlyList<WorkflowHighlight> highlights = null;
            IReadOnlyDictionary<string, string> submitted = null;
            var coordinator = CreateCoordinator(
                state,
                values => { },
                (mode, values) => submitted = values,
                values => highlights = values,
                value => { });

            coordinator.TryBeginEffect(
                UseCharacterCardCommandHandler.Tactic,
                CharacterCardEffectKind.LiskarmControlPosition);

            Assert.That(highlights.Select(item => item.TargetId).ToArray(), Is.EqualTo(new[] { opponentSlot }));
            coordinator.TryHandleInfluenceSlotClicked(opponentSlot);
            Assert.That(submitted[CharacterEffectParameterKeys.TargetInfluenceSlotId], Is.EqualTo(opponentSlot));
        }

        [Test]
        public void TinManPendingMove_SelectsSourceThenTargetOnMap()
        {
            var state = CreateState(CharacterCardDatabase.TinMan, GamePhase.ActionRound1);
            var source = InfluenceService.GetRouteSlotId("A1", 0);
            state.Map.Influences.Add(InfluenceAt(1, source, "A1"));
            state.PendingCharacterEffect = new PendingCharacterEffectState
            {
                ChoiceType = CharacterPendingChoiceTypes.TinManDiscard,
                PlayerId = 1,
                CardId = state.FindPlayer(1).CoveredCharacterCardId,
                SourceCommandId = "tin-man-source-command",
                RemainingCardIds = { "discard-a" },
                OptionIds = { CharacterEffectChoiceIds.GainGold, CharacterEffectChoiceIds.MoveInfluence }
            };
            IReadOnlyList<WorkflowHighlight> highlights = null;
            IReadOnlyDictionary<string, string> submitted = null;
            var coordinator = CreateCoordinator(
                state,
                values => submitted = values,
                (mode, values) => { },
                values => highlights = values,
                value => { });

            Assert.That(coordinator.TryBeginTinManPendingMove(), Is.True);
            Assert.That(highlights.Select(item => item.TargetId).ToArray(), Does.Contain(source));
            coordinator.TryHandleInfluenceSlotClicked(source);
            var target = highlights[0].TargetId;
            Assert.That(target, Is.Not.EqualTo(source));
            coordinator.TryHandleInfluenceSlotClicked(target);

            Assert.That(submitted[CharacterEffectParameterKeys.Choice], Is.EqualTo(CharacterEffectChoiceIds.MoveInfluence));
            Assert.That(submitted[CharacterEffectParameterKeys.SourceInfluenceSlotId], Is.EqualTo(source));
            Assert.That(submitted[CharacterEffectParameterKeys.TargetInfluenceSlotId], Is.EqualTo(target));
            Assert.That(
                submitted[CharacterEffectParameterKeys.PendingCharacterEffectSourceCommandId],
                Is.EqualTo("tin-man-source-command"));
        }

        private static CharacterMapInteractionCoordinator CreateCoordinator(
            GameState state,
            System.Action<IReadOnlyDictionary<string, string>> submitPending,
            System.Action<string, IReadOnlyDictionary<string, string>> submitEffect,
            System.Action<IReadOnlyList<WorkflowHighlight>> setHighlights,
            System.Action<string> setPrompt)
        {
            return new CharacterMapInteractionCoordinator(
                () => state,
                () => 1,
                new CharacterCardPanelPresenter(),
                setHighlights,
                () => setHighlights(new List<WorkflowHighlight>()),
                submitEffect,
                submitPending,
                setPrompt);
        }

        private static GameState CreateState(string templateId, GamePhase phase)
        {
            var cardId = "character.red.p1." + templateId;
            return new GameState
            {
                MapId = StaticMapDefinitions.FourPlayerMapId,
                Phase = phase,
                Round = 1,
                ActionRound = phase == GamePhase.ActionRound1 ? 1 : 0,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Red,
                        InfluenceSupply = 30,
                        CoveredCharacterCardId = cardId,
                        CoveredCharacterCardIds = { cardId }
                    }
                }
            };
        }

        private static InfluencePlacement InfluenceAt(int playerId, string slotId, string routeId)
        {
            return new InfluencePlacement
            {
                PlayerId = playerId,
                SlotId = slotId,
                RouteId = routeId
            };
        }
    }
}
