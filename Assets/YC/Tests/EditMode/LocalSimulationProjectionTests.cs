using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class LocalSimulationProjectionTests
    {
        private static GameState FullState()
        {
            var state = new GameState
            {
                GameId = "projection-match", EntitySchemaVersion = 0, Phase = GamePhase.ActionRound1,
                CurrentPlayerId = 1, StartPlayerId = 1, Round = 2, EventDeckSeed = 42,
                RandomRuntime = GameplayRandomRuntime.Create("projection-match"), LaunchPlayerOrder = new List<int> { 1, 2 }
            };
            state.Players.Add(new PlayerState { PlayerId = 1, Name = "本人", HandCardIds = new List<string> { "private-hand-1" },
                DiscardCardIds = new List<string> { "private-discard-1" }, CoveredCharacterCardId = "private-cover-1" });
            state.Players.Add(new PlayerState { PlayerId = 2, Name = "其他玩家", HandCardIds = new List<string> { "private-hand-2" },
                DiscardCardIds = new List<string> { "private-discard-2" }, CoveredCharacterCardId = "private-cover-2" });
            state.GameBox.Cards.Add(new GameBoxCardState { CardId = "public-box", PrivateForPlayerId = -1 });
            state.GameBox.Cards.Add(new GameBoxCardState { CardId = "private-box-1", PrivateForPlayerId = 1 });
            state.GameBox.Cards.Add(new GameBoxCardState { CardId = "private-box-2", PrivateForPlayerId = 2 });
            state.Decks.FacilityDeck.Add("secret-deck-card");
            state.PendingChoice = new PendingChoiceState { ChoiceId = "pending-one", PlayerId = 1,
                ChoiceType = "private-choice", CardId = "private-hand-1", OptionIds = new List<string> { "private-answer-1" } };
            state.EffectRuntime.StateRevision = 17;
            state.EffectRuntime.ActiveMainNodeId = "private-node";
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "private-node", PlayerId = 1,
                EffectTypeId = "private-rule-type", Visibility = GameStateVisibilityPolicy.Owner });
            state.EffectRuntime.RuleEvents.Add(new RuleEvent { EventId = "private-event", EventType = "private-event-type", PlayerId = 1,
                Visibility = GameStateVisibilityPolicy.Owner, Payload = NormalizedValue.CreateString("private-rule-payload") });
            state.EffectRuntime.InteractionRequests.Add(new InteractionRequest { InteractionId = "private-interaction", RequestId = "private-interaction",
                InteractionTypeId = "private-interaction-type", AnsweringPlayerId = 1, Visibility = GameStateVisibilityPolicy.Owner,
                CandidateIds = new List<string> { "private-candidate" }, MinSelections = 1, MaxSelections = 1 });
            GameplayRandomRuntime.Next(state, GameplayRandomRuntime.Dice, 6);
            return state;
        }

        [Test]
        public void RefreshSavesReturnedViewAndPreservesCompleteStateAndPendingWork()
        {
            var state = FullState(); var session = new GameSession(state); var before = GameStateCloneService.DeepClone(state);
            var stateRoot = session.State; var runtime = state.EffectRuntime; var random = state.RandomRuntime; var pending = state.PendingChoice;
            var field = typeof(GameSession).GetField("pendingActionLog", BindingFlags.Instance | BindingFlags.NonPublic);
            var pendingLog = Activator.CreateInstance(field.FieldType, true); field.SetValue(session, pendingLog);
            for (int refresh = 0; refresh < 2; refresh++)
            {
                var view = session.RefreshLocalProjection(GameStateViewer.Player(1));
                Assert.That(view, Is.SameAs(session.View));
                Assert.That(session.State, Is.SameAs(stateRoot));
                Assert.That(state.EffectRuntime, Is.SameAs(runtime));
                Assert.That(state.RandomRuntime, Is.SameAs(random));
                Assert.That(state.PendingChoice, Is.SameAs(pending));
                Assert.That(field.GetValue(session), Is.SameAs(pendingLog));
                Assert.That(GameStateCloneService.AreEquivalent(state, before), Is.True);
                Assert.That(view.StateRevision, Is.EqualTo(17));
                Assert.That(view.Players.Single(p => p.PlayerId == 1).HandCardIds, Is.EqualTo(new[] { "private-hand-1" }));
                Assert.That(view.Players.Single(p => p.PlayerId == 2).HandCardIds, Is.Empty);
            }
            session.View.Players.Single(p => p.PlayerId == 1).HandCardIds.Clear();
            Assert.That(state.FindPlayer(1).HandCardIds.ToArray(), Is.EqualTo(new[] { "private-hand-1" }));
            Assert.That(state.FindPlayer(2).HandCardIds.ToArray(), Is.EqualTo(new[] { "private-hand-2" }));
            Assert.That(GameStateCloneService.AreEquivalent(state, before), Is.True);
        }

        [Test]
        public void RefreshSwitchesViewerWithExistingPrivateCardAndGameBoxPermissions()
        {
            var state = FullState(); var session = new GameSession(state); var before = GameStateCloneService.DeepClone(state);
            var first = session.RefreshLocalProjection(GameStateViewer.Player(1));
            Assert.That(first.GameBox.Cards.Select(card => card.CardId), Is.EquivalentTo(new[] { "public-box", "private-box-1" }));
            var second = session.RefreshLocalProjection(GameStateViewer.Player(2));
            Assert.That(second, Is.SameAs(session.View));
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.ViewerPlayerId, Is.EqualTo(2));
            Assert.That(second.Players.Single(p => p.PlayerId == 1).HandCardIds, Is.Empty);
            Assert.That(second.Players.Single(p => p.PlayerId == 1).DiscardCardIds, Is.Empty);
            Assert.That(second.Players.Single(p => p.PlayerId == 1).CoveredCharacterCardId, Is.Empty);
            Assert.That(second.Players.Single(p => p.PlayerId == 2).HandCardIds, Is.EqualTo(new[] { "private-hand-2" }));
            Assert.That(second.GameBox.Cards.Select(card => card.CardId), Is.EquivalentTo(new[] { "public-box", "private-box-2" }));
            string json = JsonConvert.SerializeObject(second);
            foreach (string secret in new[] { "private-answer-1", "private-candidate", "private-event", "private-rule-payload", "secret-deck-card" })
                Assert.That(json, Does.Not.Contain(secret));
            Assert.That(GameStateCloneService.AreEquivalent(state, before), Is.True);
        }

        [Test]
        public void SpectatorAndUnknownRefreshKeepPublicOnlyViewAndRetainFullSimulation()
        {
            var state = FullState(); var session = new GameSession(state); var before = GameStateCloneService.DeepClone(state);
            foreach (var viewer in new[] { GameStateViewer.Spectator, GameStateViewer.Unknown })
            {
                var view = session.RefreshLocalProjection(viewer);
                Assert.That(view, Is.SameAs(session.View));
                Assert.That(view.ViewerRole, Is.EqualTo(viewer.Role));
                Assert.That(view.GameBox.Cards.Select(card => card.CardId), Is.EqualTo(new[] { "public-box" }));
                foreach (var player in view.Players)
                {
                    Assert.That(player.HandCardIds, Is.Empty);
                    Assert.That(player.DiscardCardIds, Is.Empty);
                    Assert.That(player.CoveredCharacterCardId, Is.Empty);
                }
                string json = JsonConvert.SerializeObject(view);
                foreach (string secret in new[] { "private-hand-1", "private-hand-2", "private-box-1", "private-box-2", "private-answer-1", "secret-deck-card" })
                    Assert.That(json, Does.Not.Contain(secret));
                Assert.That(GameStateCloneService.AreEquivalent(state, before), Is.True);
            }
        }

        [Test]
        public void BootstrapperReturnsRegistryActuallyUsedByRegisteredHandlers()
        {
            var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
            var bootstrapperType = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
            var instance = contextType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            var oldInstance = instance.GetValue(null); var setter = instance.GetSetMethod(true);
            GameObject host = null;
            try
            {
                setter.Invoke(null, new object[] { null });
                host = new GameObject("LocalSimulationProjectionCheck");
                var context = host.AddComponent(contextType);
                var config = new GameRuleConfiguration { MapId = StaticMapDefinitions.ThreePlayerMapId, PlayerCount = 3 };
                var players = new List<PlayerSeat> { new PlayerSeat { PlayerId = 1 }, new PlayerSeat { PlayerId = 2 }, new PlayerSeat { PlayerId = 3 } };
                contextType.GetMethod("ConfigureNewLocal").Invoke(context, new object[] { 1, "projection-registry-room", players, config, (int?)42, null });
                var result = bootstrapperType.GetMethod("Build", BindingFlags.Static | BindingFlags.Public).Invoke(null, new[] { context, (object)false, false });
                var registry = (EffectRegistry)result.GetType().GetField("EffectRegistry").GetValue(result);
                var session = (GameSession)result.GetType().GetField("Session").GetValue(result);
                Assert.That(registry, Is.Not.Null);
                var handlers = (IEnumerable)typeof(GameSession).GetField("commandHandlers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(session);
                int references = 0;
                foreach (var handler in handlers)
                    foreach (var field in handler.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Where(f => f.FieldType == typeof(EffectRegistry)))
                    {
                        Assert.That(field.GetValue(handler), Is.SameAs(registry), handler.GetType().Name);
                        references++;
                    }
                Assert.That(references > 0, Is.True);
                var before = GameStateCloneService.DeepClone(session.State);
                var view = session.RefreshLocalProjection(GameStateViewer.Player(1));
                Assert.That(view, Is.SameAs(session.View));
                Assert.That(GameStateCloneService.AreEquivalent(session.State, before), Is.True);
            }
            finally
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
                setter.Invoke(null, new[] { oldInstance });
            }
        }
    }
}
