using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Rules;
using YC.Infrastructure.Lua;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.EditMode
{
    public sealed class RoomContentCapabilityCacheTests
    {
        private ExternalContentPack installed;
        private Type providerType;
        [OneTimeSetUp] public void LoadInstalledSnapshot()
        {
            installed = ExternalContentPack.Load(Path.GetFullPath("Assets/StreamingAssets/Content/core"));
            providerType = Type.GetType("YC.Infrastructure.Multiplayer.BasicRoomContentCapabilityProvider, Assembly-CSharp") ??
                AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YC.Infrastructure.Multiplayer.BasicRoomContentCapabilityProvider")).First(t => t != null);
        }

        private IRoomContentCapabilityProvider Provider(Func<ExternalContentPack> source = null) =>
            (IRoomContentCapabilityProvider)Activator.CreateInstance(providerType, new object[] { source ?? (() => installed) });
        private int Builds(IRoomContentCapabilityProvider provider) => (int)providerType.GetProperty("CatalogBuildCount", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(provider);
        private static GameRuleConfiguration Configuration(bool enterprise = false) => new GameRuleConfiguration
        { PlayerCount = 2, EnabledExpansionIds = enterprise ? new List<string> { "core", "enterprise" } : new List<string> { "core" } };
        private static T Request<T>(RoomState room, int player = 1) where T : RoomConfigurationRequest, new() => new T
        { CommandId = Guid.NewGuid().ToString("N"), RoomId = room.RoomId, RequestingPlayerId = player, ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision };

        [Test] public void RepeatedValidationHashAndRevisionChangesShareOneBuildWithoutMutatingInput()
        {
            var provider = Provider(); var configuration = Configuration(true);
            var hash = provider.GetContentPackHash(configuration);
            Assert.That(hash, Is.Not.Empty);
            configuration.ContentPackHash = hash;
            configuration.EnabledExpansionIds.Reverse();
            for (var i = 0; i < 100; i++)
            {
                configuration.ConfigurationRevision++;
                Assert.That(provider.TryValidate(configuration, out var reason), Is.True, reason);
                Assert.That(provider.GetContentPackHash(configuration), Is.EqualTo(hash));
            }
            Assert.That(Builds(provider), Is.EqualTo(1));
            Assert.That(configuration.EnabledExpansionIds, Is.EqualTo(new[] { "enterprise", "core" }));
            Assert.That(configuration.ContentPackHash, Is.EqualTo(hash));
        }

        [Test] public void EffectiveCountMapAndExpansionChangesBuildSeparateEntries()
        {
            var provider = Provider(); var configuration = Configuration();
            provider.GetContentPackHash(configuration);
            configuration.PlayerCount = 3; provider.GetContentPackHash(configuration);
            configuration.MapId = "map-four-players"; provider.GetContentPackHash(configuration);
            configuration.PlayerCount = 4; provider.GetContentPackHash(configuration);
            configuration.EnabledExpansionIds.Add("enterprise"); provider.GetContentPackHash(configuration);
            Assert.That(Builds(provider), Is.EqualTo(5));
            configuration.EnabledExpansionIds.Reverse(); provider.GetContentPackHash(configuration);
            Assert.That(Builds(provider), Is.EqualTo(5));
            provider.GetContentPackHash(Configuration());
            Assert.That(Builds(provider), Is.EqualTo(5), "返回相同有效配置可复用该快照的结果");
        }

        [TestCase("mode", "rule_mode_not_implemented")]
        [TestCase("map", "room_protocol_mismatch")]
        [TestCase("count", "unsupported_player_count")]
        [TestCase("fourPlayerMap", "map_requires_four_player")]
        [TestCase("schema", "content_version_mismatch")]
        [TestCase("ruleVersion", "content_version_mismatch")]
        [TestCase("dependency", "missing_enterprise_dependency")]
        [TestCase("duplicate", "content_not_ready")]
        [TestCase("unknownExpansion", "content_not_ready")]
        public void InvalidConfigurationCannotReuseSuccessfulEntry(string field, string expectedReason)
        {
            var provider = Provider(); var configuration = Configuration();
            configuration.ContentPackHash = provider.GetContentPackHash(configuration);
            switch (field)
            {
                case "mode": configuration.RuleModeId = "entrepreneur"; break;
                case "map": configuration.MapId = "missing-map"; break;
                case "count": configuration.PlayerCount = 5; break;
                case "fourPlayerMap": configuration.PlayerCount = 4; break;
                case "schema": configuration.SchemaVersion++; break;
                case "ruleVersion": configuration.RuleContractVersion = "unknown"; break;
                case "dependency": configuration.EnabledExpansionIds.Add("infected"); break;
                case "duplicate": configuration.EnabledExpansionIds.Add("core"); break;
                case "unknownExpansion": configuration.EnabledExpansionIds.Add("unknown"); break;
            }
            Assert.That(provider.TryValidate(configuration, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo(expectedReason));
            Assert.That(provider.GetContentPackHash(configuration), Is.Empty);
            Assert.That(Builds(provider), Is.EqualTo(1), "非法组合应在复用缓存前拒绝");
        }

        [Test] public void CallerFingerprintIsCheckedEveryTimeAndCannotPoisonCachedHash()
        {
            var provider = Provider(); var configuration = Configuration();
            configuration.ContentPackHash = "wrong-client-fingerprint";
            Assert.That(provider.TryValidate(configuration, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("content_version_mismatch"));
            var hash = provider.GetContentPackHash(configuration);
            Assert.That(hash, Is.Not.Empty);
            Assert.That(hash, Is.Not.EqualTo(configuration.ContentPackHash));
            configuration.ContentPackHash = hash;
            Assert.That(provider.TryValidate(configuration, out reason), Is.True, reason);
            configuration.ContentPackHash = "another-wrong-fingerprint";
            Assert.That(provider.TryValidate(configuration, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("content_version_mismatch"));
            Assert.That(Builds(provider), Is.EqualTo(1));
        }

        [Test] public void NewInstalledSnapshotInvalidatesSuccessAndHostStillRejectsOldContent()
        {
            var snapshot = installed; var provider = Provider(() => snapshot);
            var authority = new RoomConfigurationService(provider, localPlayers: true);
            var room = CreateRoom(authority, provider, false);
            Assert.That(authority.TryValidateLaunch(room, out var reason), Is.True, reason);
            var oldHash = room.Configuration.ContentPackHash;
            var previousBuilds = Builds(provider);
            var root = CopyContent();
            var script = installed.ActiveDefinitions.SelectMany(d => d.Abilities).First(a => !string.IsNullOrEmpty(a.Script)).Script;
            File.AppendAllText(Path.Combine(root, script), "\n-- room capability cache reload verification\n");
            snapshot = ExternalContentPack.Load(root);
            Assert.That(snapshot.ContentHash, Is.Not.EqualTo(installed.ContentHash));
            Assert.That(authority.TryFreezeForLaunch(room, Request<StartConfiguredGameRequest>(room), out _, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("content_version_mismatch"));
            Assert.That(room.HasStarted, Is.False);
            Assert.That(provider.GetContentPackHash(room.Configuration), Is.Not.EqualTo(oldHash));
            Assert.That(Builds(provider), Is.EqualTo(previousBuilds + 1));
            var fresh = CreateRoom(authority, provider, false);
            Assert.That(authority.TryFreezeForLaunch(fresh, Request<StartConfiguredGameRequest>(fresh), out _, out reason), Is.True, reason);
        }

        [Test] public void UnsupportedContentFailureIsCachedOnlyUntilSnapshotReplacement()
        {
            var root = CopyContent();
            var manifestPath = Path.Combine(root, "pack.json");
            var manifest = JObject.Parse(File.ReadAllText(manifestPath));
            ((JArray)manifest["requiredCapabilities"]).Add("unsupported.room-cache-test");
            File.WriteAllText(manifestPath, manifest.ToString());
            var snapshot = ExternalContentPack.Load(root); var provider = Provider(() => snapshot);
            var configuration = Configuration();
            for (var i = 0; i < 10; i++)
            {
                Assert.That(provider.TryValidate(configuration, out var reason), Is.False);
                Assert.That(reason, Is.EqualTo("content_not_ready"));
                Assert.That(provider.GetContentPackHash(configuration), Is.Empty);
            }
            Assert.That(Builds(provider), Is.EqualTo(1));
            snapshot = installed;
            Assert.That(provider.TryValidate(configuration, out var readyReason), Is.True, readyReason);
            Assert.That(Builds(provider), Is.EqualTo(2));
        }

        [Test] public void EqualHashNewSnapshotStillReplacesCacheAndMissingSnapshotCannotStart()
        {
            var snapshot = installed; var provider = Provider(() => snapshot); var configuration = Configuration();
            var hash = provider.GetContentPackHash(configuration);
            snapshot = null;
            Assert.That(provider.TryValidate(configuration, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("content_not_ready"));
            snapshot = ExternalContentPack.Load(installed.RootPath);
            Assert.That(provider.GetContentPackHash(configuration), Is.EqualTo(hash));
            Assert.That(Builds(provider), Is.EqualTo(2));
        }

        [Test] public void RepeatedNetworkPreparationAndLocalRoomChecksKeepDynamicGatesLive()
        {
            var provider = Provider(); var authority = new RoomConfigurationService(provider);
            var room = CreateRoom(authority, provider, true);
            var ready = Request<SetRoomReadyRequest>(room, 2); ready.IsUserReady = true;
            Assert.That(authority.SetReady(room, ready).Success, Is.True);
            var builds = Builds(provider);
            for (var i = 0; i < 100; i++) Assert.That(authority.TryValidateLaunch(room.Clone(), out var reason), Is.True, reason);
            room.Seats[1].TransportConnected = false;
            Assert.That(authority.TryValidateLaunch(room, out var disconnected), Is.False);
            Assert.That(disconnected, Is.EqualTo("player_not_ready"));
            room.Seats[1].TransportConnected = true;
            var cancel = Request<SetRoomReadyRequest>(room, 2);
            Assert.That(authority.SetReady(room, cancel).Success, Is.True);
            Assert.That(authority.TryFreezeForLaunch(room, Request<StartConfiguredGameRequest>(room), out _, out var unready), Is.False);
            Assert.That(unready, Is.EqualTo("player_not_ready"));
            var localAuthority = new RoomConfigurationService(provider, localPlayers: true);
            var local = CreateRoom(localAuthority, provider, false);
            for (var i = 0; i < 100; i++) Assert.That(localAuthority.TryValidateLaunch(local.Clone(), out var reason), Is.True, reason);
            var stale = Request<StartConfiguredGameRequest>(local); stale.ExpectedConfigurationRevision--;
            Assert.That(localAuthority.TryFreezeForLaunch(local, stale, out _, out var staleReason), Is.False);
            Assert.That(staleReason, Is.EqualTo("configuration_stale"));
            Assert.That(Builds(provider), Is.EqualTo(builds));
        }

        [Test] public void ConcurrentQueriesForImmutableSnapshotBuildOnce()
        {
            var provider = Provider(); var configuration = Configuration();
            var hashes = new string[8];
            Parallel.For(0, hashes.Length, i => hashes[i] = provider.GetContentPackHash(configuration));
            Assert.That(hashes.All(h => !string.IsNullOrEmpty(h) && h == hashes[0]), Is.True);
            Assert.That(Builds(provider), Is.EqualTo(1));
        }

        [TestCase(false)] [TestCase(true)]
        public void MeasureColdAndRepeatedCapabilityChecks(bool enterprise)
        {
            var configuration = Configuration(enterprise);
            var watch = Stopwatch.StartNew();
            var original = ActiveContentCatalogBuilder.Build(installed, configuration).ContentPackHash;
            var duplicate = ActiveContentCatalogBuilder.Build(installed, configuration).ContentPackHash;
            var uncached = watch.Elapsed.TotalMilliseconds;
            Assert.That(duplicate, Is.EqualTo(original));
            var provider = Provider(); configuration.ContentPackHash = original;
            watch.Restart();
            Assert.That(provider.TryValidate(configuration, out var reason), Is.True, reason);
            Assert.That(provider.GetContentPackHash(configuration), Is.EqualTo(original));
            var cold = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            for (var i = 0; i < 100; i++)
            {
                Assert.That(provider.TryValidate(configuration, out reason), Is.True, reason);
                Assert.That(provider.GetContentPackHash(configuration), Is.EqualTo(original));
            }
            var warm = watch.Elapsed.TotalMilliseconds;
            Assert.That(Builds(provider), Is.EqualTo(1));
            Console.WriteLine("ROOM_CACHE enterprise={0} uncachedPairMs={1:F3} coldPairMs={2:F3} warm100PairsMs={3:F3} builds=1", enterprise, uncached, cold, warm);
        }

        private static RoomState CreateRoom(RoomConfigurationService authority, IRoomContentCapabilityProvider provider, bool online)
        {
            var room = new RoomState { RoomId = Guid.NewGuid().ToString("N"), HostPlayerId = 1, LocalPlayerId = 1 };
            for (var id = 1; id <= 2; id++)
                Assert.That(authority.TryOccupySeat(room, new PlayerSeat
                {
                    PlayerId = id, PlayerName = "缓存验证玩家" + id, LobbyMemberPresent = true,
                    SteamId = online ? (ulong)(100 + id) : 0, NetworkClientId = (ulong)(id - 1),
                    IsReady = online, IdentityVerified = online, TransportConnected = online
                }, out var reason), Is.True, reason);
            foreach (var player in room.Seats)
            { player.ContentPackHash = provider.GetContentPackHash(room.Configuration); player.RuleContractVersion = room.Configuration.RuleContractVersion; }
            return room;
        }

        private string CopyContent()
        {
            var root = Path.GetFullPath(Path.Combine("Logs", "RoomContentCacheValidation", "fixtures", Guid.NewGuid().ToString("N")));
            foreach (var file in Directory.GetFiles(installed.RootPath, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(root, file.Substring(installed.RootPath.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target);
            }
            return root;
        }
    }
}
