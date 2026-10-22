using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class InitialSimulationCheckpointTests
    {
        private sealed class Built
        {
            public GameSession Session;
            public EffectRegistry Registry;
        }
        private sealed class Fixture
        {
            public readonly string GameId = "initial-checkpoint-match";
            public readonly int Seed = 42;
            public readonly List<PlayerSeat> Players = new List<PlayerSeat>
            { new PlayerSeat { PlayerId = 1, PlayerName = "玩家1" }, new PlayerSeat { PlayerId = 2, PlayerName = "玩家2" }, new PlayerSeat { PlayerId = 3, PlayerName = "玩家3" } };
            public readonly GameRuleConfiguration Configuration = new GameRuleConfiguration { MapId = StaticMapDefinitions.ThreePlayerMapId, PlayerCount = 3 };
            public readonly List<int> Order = new List<int> { 1, 2, 3 };
            public readonly GameplayRandomState Random;
            public readonly Built Host;
            public readonly string ArchiveJson;
            public Fixture()
            {
                var start = new GameState { GameId = GameId, EventDeckSeed = Seed, RandomRuntime = GameplayRandomRuntime.Create(GameId) };
                GameplayRandomRuntime.Shuffle(Order, start, GameplayRandomRuntime.LaunchOrder); Random = start.RandomRuntime;
                Host = Build(LaunchMode.Host, false);
                Configuration.ContentPackHash = Host.Session.State.ContentPackHash;
                ArchiveJson = HostRecoveryService.SerializeArchive(Host.Session.CreateHostArchive());
            }
            public Built Build(LaunchMode mode, bool defer, bool missingStart = false)
            {
                var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
                var bootstrapperType = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
                var instance = contextType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public);
                var previous = instance.GetValue(null); var setter = instance.GetSetMethod(true);
                GameObject host = null;
                try
                {
                    setter.Invoke(null, new object[] { null }); host = new GameObject("InitialSimulationCheckpointCheck");
                    var context = host.AddComponent(contextType);
                    contextType.GetMethods().Single(m => m.Name == "Configure" && m.GetParameters().Length == 9).Invoke(context,
                        new object[] { mode, 2, "initial-checkpoint-room", Players, Configuration, Order, GameId, Seed, Random.Clone() });
                    if (missingStart) contextType.GetField("InitialRandomState").SetValue(context, null);
                    // 默认路径复用原公开三参数反射入口；新模式按准确四参数签名显式采用。
                    var build = defer
                        ? bootstrapperType.GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic, null,
                            new[] { contextType, typeof(bool), typeof(bool), typeof(bool) }, null)
                        : bootstrapperType.GetMethod("Build", BindingFlags.Static | BindingFlags.Public);
                    var value = build.Invoke(null, defer ? new object[] { context, false, false, true } : new object[] { context, false, false });
                    return new Built { Session = (GameSession)value.GetType().GetField("Session").GetValue(value),
                        Registry = (EffectRegistry)value.GetType().GetField("EffectRegistry").GetValue(value) };
                }
                finally
                {
                    if (host != null) UnityEngine.Object.DestroyImmediate(host);
                    setter.Invoke(null, new[] { previous });
                }
            }
        }
        private static Fixture fixture;
        private static Fixture Shared => fixture ?? (fixture = new Fixture());
        public static int InitialArchiveUtf8Bytes => Encoding.UTF8.GetByteCount(Shared.ArchiveJson);

        [Test]
        public void DeferredBuildKeepsAcceptedStartSkeletonAndDefaultBuildStillStartsEntrance()
        {
            var f = Shared; var client = f.Build(LaunchMode.Client, true);
            Assert.That(client.Session.State.GameId, Is.EqualTo(f.GameId));
            Assert.That(client.Session.State.EventDeckSeed, Is.EqualTo(f.Seed));
            Assert.That(client.Session.State.ItemZones.Items, Is.Empty);
            Assert.That(client.Session.State.EffectRuntime.EffectNodes, Is.Empty);
            Assert.That(client.Session.State.EffectRuntime.Journal, Is.Empty);
            Assert.That(client.Session.State.EffectRuntime.StateRevision, Is.EqualTo(0));
            Assert.That(JsonConvert.SerializeObject(client.Session.State.RandomRuntime), Is.EqualTo(JsonConvert.SerializeObject(f.Random)));
            Assert.That(client.Registry, Is.Not.Null);
            Assert.That(f.Host.Session.State.ItemZones.Items.Count > 0, Is.True);
            Assert.That(f.Host.Session.State.EffectRuntime.EffectNodes.Count > 0, Is.True);
            Assert.That(f.Host.Session.State.EffectRuntime.Journal.Count > 0, Is.True);
            bool rejected = false;
            try { f.Build(LaunchMode.Client, true, missingStart: true); }
            catch (TargetInvocationException error) { rejected = error.InnerException is InvalidOperationException; }
            Assert.That(rejected, Is.True);
        }

        [Test]
        public void InstallsCompleteStateOnceAndOnlyProjectsTheRequestedPlayer()
        {
            var f = Shared; var client = f.Build(LaunchMode.Client, true);
            var installer = new InitialSimulationCheckpointInstaller(client.Session, client.Registry, GameStateViewer.Player(2));
            Assert.That(installer.TryInstall("checkpoint-1", f.ArchiveJson, out var result), Is.True, result?.Diagnostic);
            Assert.That(result.Succeeded, Is.True); Assert.That(result.AlreadyInstalled, Is.False);
            Assert.That(GameStateCloneService.AreEquivalent(client.Session.State, f.Host.Session.State), Is.True);
            Assert.That(client.Session.State, Is.Not.SameAs(f.Host.Session.State));
            Assert.That(client.Session.State.EffectRuntime, Is.Not.SameAs(f.Host.Session.State.EffectRuntime));
            Assert.That(client.Session.State.FindPlayer(1).HandCardIds.Count > 0, Is.True);
            Assert.That(client.Session.View.ViewerPlayerId, Is.EqualTo(2));
            Assert.That(client.Session.View.Players.Single(p => p.PlayerId == 1).HandCardIds, Is.Empty);
            Assert.That(client.Session.View.Players.Single(p => p.PlayerId == 2).HandCardIds.Count > 0, Is.True);
            var view = client.Session.View;
            GameplayRandomRuntime.Next(client.Session.State, GameplayRandomRuntime.Dice, 6);
            client.Session.State.EffectRuntime.StateRevision++;
            var progressed = GameStateCloneService.DeepClone(client.Session.State);
            Assert.That(installer.TryInstall("checkpoint-1", f.ArchiveJson, out var duplicate), Is.True);
            Assert.That(duplicate.AlreadyInstalled, Is.True);
            Assert.That(duplicate.ArchiveDigest, Is.EqualTo(result.ArchiveDigest));
            Assert.That(client.Session.View, Is.SameAs(view));
            Assert.That(GameStateCloneService.AreEquivalent(client.Session.State, progressed), Is.True);
            Assert.That(installer.TryInstall("another-id", f.ArchiveJson, out _), Is.False);
            Assert.That(installer.TryInstall("checkpoint-1", f.ArchiveJson + " ", out _), Is.False);
            Assert.That(GameStateCloneService.AreEquivalent(client.Session.State, progressed), Is.True);
        }

        [Test]
        public void BadArchivesAndStartConflictsDoNotPolluteStateViewOrRandom()
        {
            var f = Shared; var client = f.Build(LaunchMode.Client, true);
            var installer = new InitialSimulationCheckpointInstaller(client.Session, client.Registry, GameStateViewer.Player(2));
            var view = client.Session.RefreshLocalProjection(GameStateViewer.Player(2));
            var before = GameStateCloneService.DeepClone(client.Session.State); var random = client.Session.State.RandomRuntime;
            var wrongGame = JObject.Parse(f.ArchiveJson); wrongGame["Snapshot"]["State"]["GameId"] = "other-match";
            wrongGame["Snapshot"]["State"]["RandomRuntime"]["GameId"] = "other-match";
            var wrongContent = JObject.Parse(f.ArchiveJson); wrongContent["Snapshot"]["ContentHash"] = "other-content";
            var wrongRules = JObject.Parse(f.ArchiveJson); wrongRules["Snapshot"]["State"]["RuleConfiguration"]["RuleContractVersion"] = "other-rules";
            var wrongOrder = JObject.Parse(f.ArchiveJson); ((JArray)wrongOrder["Snapshot"]["State"]["LaunchPlayerOrder"])[0] = 999;
            var wrongSeed = JObject.Parse(f.ArchiveJson); wrongSeed["Snapshot"]["State"]["EventDeckSeed"] = 43;
            var missingRandom = JObject.Parse(f.ArchiveJson); ((JObject)missingRandom["Snapshot"]["State"]).Remove("RandomRuntime");
            var rollback = JObject.Parse(f.ArchiveJson); var earlier = GameplayRandomRuntime.Create(f.GameId);
            GameplayRandomRuntime.Next(earlier, f.GameId, f.Seed, GameplayRandomRuntime.LaunchOrder, 3);
            var streams = (JArray)rollback["Snapshot"]["State"]["RandomRuntime"]["Streams"];
            streams.Single(s => (string)s["StreamId"] == GameplayRandomRuntime.LaunchOrder).Replace(JObject.FromObject(earlier.Streams.Single()));
            var failedState = JObject.Parse(f.ArchiveJson); failedState["Snapshot"]["LastCommitSequence"] = -1;
            var failure = HostRecoveryService.RecoverJson(failedState.ToString(), client.Registry);
            Assert.That(failure.Succeeded, Is.False); Assert.That(failure.State, Is.Not.Null);
            foreach (string archive in new[] { "{broken", wrongGame.ToString(), wrongContent.ToString(), wrongRules.ToString(),
                wrongOrder.ToString(), wrongSeed.ToString(), missingRandom.ToString(), rollback.ToString(), failedState.ToString() })
            {
                Assert.That(installer.TryInstall("checkpoint-1", archive, out var result), Is.False);
                Assert.That(result.Succeeded, Is.False);
                Assert.That(client.Session.View, Is.SameAs(view));
                Assert.That(client.Session.State.RandomRuntime, Is.SameAs(random));
                Assert.That(GameStateCloneService.AreEquivalent(client.Session.State, before), Is.True);
            }
            Assert.That(installer.TryInstall("checkpoint-1", f.ArchiveJson, out _), Is.True);
        }

        [Test]
        public void InstallerRejectsReceiverProgressAndCannotBecomeRuntimeRecovery()
        {
            var f = Shared; var client = f.Build(LaunchMode.Client, true);
            var installer = new InitialSimulationCheckpointInstaller(client.Session, client.Registry, GameStateViewer.Player(2));
            client.Session.State.EffectRuntime.StateRevision++;
            var before = GameStateCloneService.DeepClone(client.Session.State);
            Assert.That(installer.TryInstall("checkpoint-1", f.ArchiveJson, out _), Is.False);
            Assert.That(GameStateCloneService.AreEquivalent(client.Session.State, before), Is.True);
            var runtimeInstaller = new InitialSimulationCheckpointInstaller(f.Host.Session, f.Host.Registry, GameStateViewer.Player(2));
            var hostBefore = GameStateCloneService.DeepClone(f.Host.Session.State);
            Assert.That(runtimeInstaller.TryInstall("checkpoint-1", f.ArchiveJson, out _), Is.False);
            Assert.That(GameStateCloneService.AreEquivalent(f.Host.Session.State, hostBefore), Is.True);
        }
    }
}
