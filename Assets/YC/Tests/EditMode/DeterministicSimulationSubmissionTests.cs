using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class DeterministicSimulationSubmissionTests
    {
        [Test]
        public void NetworkInitializationWithoutRegistryCannotFallBackOrMutateSimulation()
        {
            using (var client = new LaunchFixture(LaunchMode.Client))
            {
                var type = Type.GetType("YC.Presentation.CommandSubmissionController, Assembly-CSharp", true);
                var controller = Activator.CreateInstance(type, new object[] { client.Session, client.Context, 1, null,
                    new Action(() => { }), new Action<string>(_ => { }), new Action<string>(_ => { }), null, null });
                using ((IDisposable)controller)
                {
                    string before = JsonConvert.SerializeObject(client.Session.State);
                    var error = Assert.Throws<TargetInvocationException>(() => type.GetMethod("Initialize", Type.EmptyTypes).Invoke(controller, null));
                    Assert.That(error.InnerException, Is.InstanceOf<InvalidOperationException>());
                    Assert.That(type.GetProperty("CanSubmitForLocalPlayer").GetValue(controller), Is.False);
                    Assert.That(JsonConvert.SerializeObject(client.Session.State), Is.EqualTo(before));
                    Assert.That(client.Session.View, Is.Null);
                }
            }
        }

        [Test]
        public void GameplayEntryKeepsFrozenStartAndInstallsDeferredClientWithItsActualRegistry()
        {
            using (var host = new LaunchFixture(LaunchMode.Host))
            using (var client = new LaunchFixture(LaunchMode.Client))
            using (var local = new LaunchFixture(LaunchMode.Local))
            {
                Assert.That(host.Session.State.GameId, Is.EqualTo(host.InitialGameId));
                Assert.That(host.Session.State.EventDeckSeed, Is.EqualTo(42));
                Assert.That(host.Session.State.LaunchPlayerOrder, Is.EqualTo(new[] { 1, 2 }));
                Assert.That(host.Session.State.RuleConfiguration.PlayerStageSchedulingMode, Is.EqualTo(PlayerStageSchedulingMode.Concurrent));
                Assert.That(local.Session.State.RuleConfiguration.PlayerStageSchedulingMode, Is.EqualTo(PlayerStageSchedulingMode.Sequential));
                Assert.That(host.Session.State.EffectRuntime.MainNodes, Is.Not.Empty);
                Assert.That(client.Session.State.EffectRuntime.MainNodes, Is.Empty);
                Assert.That(client.Session.State.EffectRuntime.EffectNodes, Is.Empty);
                Assert.That(client.Session.State.ItemZones.Items, Is.Empty);
                Assert.That(JsonConvert.SerializeObject(client.Session.State.RandomRuntime), Is.EqualTo(client.InitialRandomJson));
                var installer = new InitialSimulationCheckpointInstaller(client.Session, client.Registry, GameStateViewer.Player(1));
                var archive = HostRecoveryService.SerializeArchive(host.Session.CreateHostArchive(host.Session.State.ContentPackHash));
                Assert.That(installer.TryInstall("initial.consumer-check", archive, out var installed), Is.True, installed?.Diagnostic);
                Assert.That(installed.AlreadyInstalled, Is.False);
                Assert.That(RuleStateDigestService.Compute(client.Session.State), Is.EqualTo(RuleStateDigestService.Compute(host.Session.State)));
                Assert.That(client.Session.View.ViewerPlayerId, Is.EqualTo(1));
                var digest = RuleStateDigestService.Compute(client.Session.State);
                Assert.That(installer.TryInstall("initial.consumer-check", archive, out var duplicate), Is.True);
                Assert.That(duplicate.AlreadyInstalled, Is.True);
                Assert.That(RuleStateDigestService.Compute(client.Session.State), Is.EqualTo(digest));
            }
        }

        [Test]
        public void ExactRefreshRejectionKeepsTheOriginalErrorAndDoesNotRebuildTheSelection()
        {
            var session = new GameSession(new GameState { GameId = "explicit-refresh-game" });
            var request = new InteractionRequestProjection { VisibleToViewer = true, Status = "open", InteractionId = "refresh-selection",
                SourceNodeId = "source", OwnerEffectId = "source", AnsweringPlayerId = 2, InteractionTypeId = "setup.basic_character.discard",
                CandidateSetId = "candidates", CandidateSetVersion = 1, MinSelections = 1, MaxSelections = 1,
                AnswerSchema = "candidate_id", StateRevision = 4, CandidateIds = new List<string> { "candidate" } };
            var command = EffectInteractionCommands.Answer(request, 2, request.CandidateIds);
            var intent = new InteractionCommandIntent(); Assert.That(intent.Begin(session.State.GameId, request, command, true), Is.True);
            var type = Type.GetType("YC.Presentation.CommandSubmissionController, Assembly-CSharp", true);
            object controller = null; int notifications = 0; bool refreshRequired = false; CommandErrorCode received = CommandErrorCode.None;
            Action<string, string, bool, CommandErrorCode> callback = (game, id, accepted, error) =>
            {
                notifications++; received = error;
                refreshRequired = (bool)type.GetProperty("RequiresExplicitRefreshForCurrentResult").GetValue(controller);
                Assert.That(intent.Resolve(game, id, accepted, error, refreshRequired), Is.True);
            };
            controller = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new object[] { session, null, 1, null, (Action)(() => { }), (Action<string>)(_ => { }), (Action<string>)(_ => { }), null, callback }, null);
            using ((IDisposable)controller)
            {
                var tracker = (SubmittedInteractionCommands)type.GetField("submittedCommands", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                tracker.Record(session.State.GameId, command);
                var resolve = type.GetMethod("ResolveSubmitted", BindingFlags.Instance | BindingFlags.NonPublic);
                var result = resolve.Invoke(controller, new object[] { command.CommandId, 2, session.State.GameId, false, CommandErrorCode.StateRevisionMismatch, true });
                Assert.That(result, Is.EqualTo(true)); Assert.That(notifications, Is.EqualTo(1));
                Assert.That(received, Is.EqualTo(CommandErrorCode.StateRevisionMismatch)); Assert.That(refreshRequired, Is.True);
                Assert.That(intent.IsBusy, Is.False); request.StateRevision++;
                Assert.That(intent.TryRebuild(session.State.GameId, request, string.Empty, out _), Is.False);
                Assert.That(type.GetProperty("RequiresExplicitRefreshForCurrentResult").GetValue(controller), Is.EqualTo(false));
                Assert.That(resolve.Invoke(controller, new object[] { command.CommandId, 2, session.State.GameId, false, received, true }), Is.EqualTo(false));
                Assert.That(notifications, Is.EqualTo(1));
            }
        }

        private sealed class LaunchFixture : IDisposable
        {
            private readonly PropertyInfo instance;
            private readonly object previous;
            private readonly GameObject gameObject;
            public const string GameId = "simulation-gameplay-entry";
            public readonly string InitialGameId = GameId;
            public readonly string InitialRandomJson;
            public readonly object Context;
            public readonly GameSession Session;
            public readonly EffectRegistry Registry;
            public LaunchFixture(LaunchMode mode)
            {
                var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
                var bootstrapper = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
                instance = contextType.GetProperty("Instance"); previous = instance.GetValue(null); instance.GetSetMethod(true).Invoke(null, new object[] { null });
                gameObject = new GameObject("启动消费者检查"); var context = Context = gameObject.AddComponent(contextType);
                var random = GameplayRandomRuntime.Create(GameId); InitialRandomJson = JsonConvert.SerializeObject(random);
                var config = new GameRuleConfiguration { MapId = StaticMapDefinitions.ThreePlayerMapId, PlayerCount = 2,
                    EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } };
                contextType.GetMethods().Single(m => m.Name == "Configure" && m.GetParameters().Length == 9).Invoke(context,
                    new object[] { mode, 1, "simulation-start", new List<PlayerSeat> { new PlayerSeat { PlayerId = 1 }, new PlayerSeat { PlayerId = 2 } },
                        config, new[] { 1, 2 }, GameId, 42, random });
                try
                {
                    var result = bootstrapper.GetMethod("BuildForGameplay").Invoke(null, new object[] { context, false, false });
                    Session = (GameSession)result.GetType().GetField("Session").GetValue(result);
                    Registry = (EffectRegistry)result.GetType().GetField("EffectRegistry").GetValue(result);
                }
                catch { Dispose(); throw; }
            }
            public void Dispose() { UnityEngine.Object.DestroyImmediate(gameObject); instance.GetSetMethod(true).Invoke(null, new[] { previous }); }
        }
    }
}
