using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 故障局原档与原命令的隔离规则定位；不计正常界面或联机验收。
    public sealed class DiyEnterpriseRecordedFaultTests
    {
        [TestCase(false)] [TestCase(true)]
        public void SyntheticGroupUpgradeResolvesBothLastAwardsInSequence(bool useOriginalScripts)
        {
            string Argument(string key) => Environment.GetCommandLineArgs()
                .FirstOrDefault(value => value.StartsWith(key + "=", StringComparison.Ordinal))?.Substring(key.Length + 1);
            string fixturePath = Argument("--yc-road-award-fixture"), reportRoot = Argument("--yc-road-award-report");
            if (fixturePath == null || reportRoot == null) Assert.Ignore("仅显式提供隔离存档和报告路径时运行。");
            string allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestResults")) + Path.DirectorySeparatorChar;
            string Safe(string path)
            {
                string full = Path.GetFullPath(path);
                if (!full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("夹具必须位于 TestResults。");
                return full;
            }
            string Hash(string path)
            {
                using var stream = File.OpenRead(path); using var sha = SHA256.Create();
                return string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
            }
            fixturePath = Safe(fixturePath); reportRoot = Safe(reportRoot);
            Directory.CreateDirectory(reportRoot);
            string report = Path.Combine(reportRoot, useOriginalScripts ? "group-original.json" : "group-fixed.json");
            Assert.That(File.Exists(report), Is.False);
            var fixture = JObject.Parse(File.ReadAllText(fixturePath));
            string savePath = Safe((string)fixture["SourceSave"]);
            Assert.That(Hash(savePath), Is.EqualTo((string)fixture["SourceSaveSha256"]));
            var save = JsonConvert.DeserializeObject<MatchSaveData>((string)JObject.Parse(File.ReadAllText(savePath))["Payload"]);
            Assert.That(save.GameId, Is.EqualTo((string)fixture["GameId"]));
            var settings = (JsonSerializerSettings)typeof(HostRecoveryService)
                .GetField("ArchiveSettings", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var recorded = JObject.Parse(save.ArchiveJson)["Snapshot"]["State"].ToObject<GameState>(JsonSerializer.Create(settings));
            ItemZoneLegacyProjection.Bind(recorded, true);
            var originalOrder = recorded.EffectRuntime.PlayerOrderSnapshot.ToList();
            var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
            if (useOriginalScripts)
            {
                var overrides = new Dictionary<string, string>();
                foreach (string award in new[] { "roads", "score" })
                {
                    string relative = "lua/awards/infected.award." + award + ".acquired.lua";
                    var frozen = ((JArray)fixture["Contents"]).Single(value => ((string)value["Relative"]).Replace('\\', '/') == relative);
                    string path = Safe((string)frozen["Copy"]);
                    Assert.That(Hash(path), Is.EqualTo((string)frozen["SHA256"]));
                    overrides.Add(relative, File.ReadAllText(path));
                }
                pack = pack.CreateWithOverrides(overrides);
            }
            // 仅规则夹具：新建独立棋盘并重建故障前等级、奖池和玩家顺序；
            // 这不是实际存档恢复，也不重放玛恩纳的原始命令。
            var configuration = recorded.RuleConfiguration.Clone();
            configuration.ContentPackHash = string.Empty;
            var catalog = ActiveContentCatalogBuilder.Build(pack, configuration);
            configuration.ContentPackHash = catalog.ContentPackHash;
            var seats = Enumerable.Range(1, 4).Select(player => new PlayerSeat { PlayerId = player, PlayerName = "夹具" + player }).ToList();
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1, seats, configuration, 197,
                catalog.SetupRecipe, catalog.ContentDefinitions, originalOrder, "fixture.maylander.last.awards", null, catalog);
            state.Phase = GamePhase.ActionRound1; state.Round = 3; state.ActionRound = 1; state.CurrentPlayerId = 1;
            state.EffectRuntime.PlayerOrderSnapshot = originalOrder;
            void Position(ItemInstanceState item, string destination)
            {
                foreach (var order in state.ItemZones.Orders) if (order.ItemIds.Remove(item.ItemId)) order.OrderRevision++;
                item.ZoneId = destination; item.SlotId = "";
                var destinationOrder = state.ItemZones.Orders.SingleOrDefault(value => value.ZoneId == destination);
                if (destinationOrder != null) { destinationOrder.ItemIds.Add(item.ItemId); destinationOrder.OrderRevision++; }
            }
            string enterpriseId = state.ItemZones.Items.Single(item => item.DefinitionId == "infected.enterprise.maylander_foundation").ItemId;
            string displayId = state.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.enterprise_display").ZoneId;
            Position(state.ItemZones.Items.Single(item => item.ItemId == enterpriseId), displayId);
            int[] levels = { 3, 5, 4, 3 };
            foreach (int player in Enumerable.Range(1, 4))
            {
                var track = state.ItemZones.Zones.SingleOrDefault(zone => zone.ParentId == enterpriseId && zone.LocalZoneId == "cooperation_" + player);
                string trackId;
                if (track == null)
                {
                    var declaration = new CardZoneDefinition { LocalZoneId = "cooperation_" + player, ZoneTemplateId = "core.zone.board_marker_track",
                        AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, AcceptedTypeIds = new List<string> { "core.player_marker" },
                        CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Track, VisibilityPolicyId = ItemZoneVisibility.Public,
                        EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents" };
                    Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, enterpriseId, declaration, out trackId, out string reason), Is.True, reason);
                }
                else trackId = track.ZoneId;
                var supply = state.ItemZones.Zones.Single(zone => zone.LocalZoneId == "supply" && zone.OwnerPlayerId == player);
                var marker = state.ItemZones.Items.First(item => item.TypeId == "core.player_marker" && item.OwnerSubject.PlayerId == player && item.ZoneId == supply.ZoneId);
                Position(marker, trackId); marker.SlotId = "level_" + levels[player - 1];
            }
            string poolId = state.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.reward_pool" && zone.LocalZoneId == "awards").ZoneId;
            string retiredId = state.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.box_retired").ZoneId;
            foreach (var item in state.ItemZones.Items.Where(value => value.TypeId == "core.award_card").ToList())
                Position(item, item.DefinitionId == "infected.award.roads" || item.DefinitionId == "infected.award.score" ? poolId : retiredId);
            var mapDefinition = StaticMapDefinitions.Resolve(state.MapId);
            state.Map.OpenLocationIds.AddRange(mapDefinition.Locations.Where(location => !state.Map.OpenLocationIds.Contains(location.LocationId)).Select(location => location.LocationId));
            Assert.That(new ItemZoneQuery().TryValidate(state, out string stateReason), Is.True, stateReason);
            int initialZoneCount = state.ItemZones.Zones.Count;
            var registry = new EffectRegistry();
            catalog.Register(registry);
            var map = new MapQueryService(mapDefinition);
            var influence = new InfluenceService(map);
            ContentPrimitiveEffectExecutor.Register(registry, map, influence);
            GenericLuaEffectExecutor.Register(registry);
            ResourceEffectExecutor.Register(registry);
            InfluenceEffectExecutor.Register(registry, influence);
            var trace = new List<object>();
            var roadSelections = new List<string>();
            var rewardRecipients = new List<int>();
            string rootId = "";
            try
            {
                var enterprise = state.ItemZones.Items.Single(item => item.DefinitionId == "infected.enterprise.maylander_foundation");
                var roadAward = state.ItemZones.Items.Single(item => item.DefinitionId == "infected.award.roads");
                var scoreAward = state.ItemZones.Items.Single(item => item.DefinitionId == "infected.award.score");
                var pool = state.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.reward_pool" && zone.LocalZoneId == "awards");
                Assert.That(new[] { roadAward.ZoneId, scoreAward.ZoneId }, Is.All.EqualTo(pool.ZoneId));
                var query = new EnterpriseStateQuery(registry.EnterpriseDefinitions);
                Assert.That(Enumerable.Range(1, 4).Select(player => query.GetLevel(state, player, enterprise.ItemId)), Is.EqualTo(new[] { 3, 5, 4, 3 }));
                int goldBefore = state.FindPlayer(1).Resources.GoldVoucher, score4Before = state.FindPlayer(4).Score, score3Before = state.FindPlayer(3).Score;
                var engine = new EffectTreeExecutor(state, registry);
                var spec = EffectSpec.Create(EnterpriseEffectExecutor.UpgradeAllPlayersTypeId,
                    Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"),
                        "enterpriseItemId", NormalizedValue.CreateString(enterprise.ItemId), "levels", NormalizedValue.CreateInteger(1)), 1);
                rootId = engine.CreateRoot(spec);
                engine.RunUntilQuiescent();
                for (int step = 0; step < 12 && state.EffectRuntime.Status == EffectRuntimeStatus.Active; step++)
                {
                    var request = state.EffectRuntime.InteractionRequests.LastOrDefault(value => value.Status == "open" && !value.IsInternalMainlineInteraction());
                    if (request == null) break;
                    string answer = "";
                    if (request.InteractionTypeId == "reward.choose")
                    {
                        rewardRecipients.Add(request.AnsweringPlayerId);
                        answer = request.AnsweringPlayerId == 1 ? roadAward.ItemId : scoreAward.ItemId;
                        Assert.That(request.AnsweringPlayerId, Is.EqualTo(rewardRecipients.Count == 1 ? 1 : 4));
                        Assert.That(request.CandidateIds, Does.Contain(answer));
                        if (request.AnsweringPlayerId == 4) Assert.That(request.CandidateIds, Is.EqualTo(new[] { scoreAward.ItemId }));
                    }
                    else if (request.InteractionTypeId == "effect.route.choose")
                    {
                        Assert.That(request.AnsweringPlayerId, Is.EqualTo(1));
                        answer = request.CandidateIds.First(route => !state.Map.Influences.Any(item => item.RouteId == route));
                        Assert.That(roadSelections, Does.Not.Contain(answer));
                        roadSelections.Add(answer);
                    }
                    else Assert.Fail("未预期的隔离交互：" + request.InteractionTypeId);
                    trace.Add(new { request.InteractionTypeId, request.AnsweringPlayerId, Candidates = request.CandidateIds.ToArray(), Answer = answer });
                    Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision,
                        NormalizedValue.CreateString(answer), out string reason), Is.True, reason);
                    engine.RunUntilQuiescent();
                }
                // 小范围新局夹具中的旧脚本也应通过；较大的真实区域投影由另一组测试证明超预算。
                Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active), state.EffectRuntime.LastFaultMessage);
                Assert.That(engine.GetNode(rootId).Status, Is.EqualTo(EffectNodeStatus.Completed));
                Assert.That(rewardRecipients, Is.EqualTo(new[] { 1, 4 }));
                Assert.That(roadSelections.Count, Is.EqualTo(2));
                Assert.That(roadSelections.All(route => state.Map.RoadRouteIds.Contains(route)), Is.True);
                Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(goldBefore + 20));
                Assert.That(state.FindPlayer(4).Score, Is.EqualTo(score4Before + 1));
                Assert.That(state.FindPlayer(3).Score, Is.EqualTo(score3Before + 2));
                string retired = state.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.box_retired").ZoneId;
                Assert.That(state.ItemZones.Items.Single(item => item.ItemId == roadAward.ItemId).ZoneId, Is.EqualTo(retired));
                Assert.That(state.ItemZones.Items.Single(item => item.ItemId == scoreAward.ItemId).ZoneId, Is.EqualTo(retired));
                Assert.That(state.EffectRuntime.RuleEvents.Where(value => value.EventType == RewardAcquisitionFlow.AcquiredEventTypeId).Select(value => value.PlayerId), Is.EqualTo(new[] { 1, 4 }));
                Assert.That(Enumerable.Range(1, 4).Select(player => query.GetLevel(state, player, enterprise.ItemId)), Is.EqualTo(new[] { 4, 5, 5, 4 }));
            }
            finally
            {
                using var writer = new StreamWriter(new FileStream(report, FileMode.CreateNew, FileAccess.Write));
                writer.Write(JsonConvert.SerializeObject(new { Scope = "独立新局夹具重建故障前的等级、奖池和玩家顺序，从群升节点进行规则定位；不是实际恢复或正常联机验收",
                    SourceGameId = save.GameId, FixtureGameId = state.GameId, OriginalScripts = useOriginalScripts, ContentHash = catalog.ContentPackHash, RootId = rootId,
                    OriginalPlayerOrder = originalOrder, InitialZoneCount = initialZoneCount,
                    RuntimeStatus = state.EffectRuntime.Status.ToString(), state.EffectRuntime.LastFaultCode, state.EffectRuntime.LastFaultMessage,
                    RootStatus = state.EffectRuntime.EffectNodes.FirstOrDefault(value => value.EffectId == rootId)?.Status.ToString(),
                    Trace = trace, Roads = roadSelections, Recipients = rewardRecipients }, Formatting.Indented));
            }
            Assert.That(Hash(savePath), Is.EqualTo((string)fixture["SourceSaveSha256"]));
        }

        [TestCase("roads", 1)] [TestCase("roads", 4)]
        [TestCase("score", 1)] [TestCase("score", 4)]
        public void RecordedAwardAcquisitionRunsWithinBudgetForBothUpgradeRecipients(string award, int player)
        {
            string Argument(string key) => Environment.GetCommandLineArgs()
                .FirstOrDefault(value => value.StartsWith(key + "=", StringComparison.Ordinal))?.Substring(key.Length + 1);
            string fixturePath = Argument("--yc-road-award-fixture"), reportRoot = Argument("--yc-road-award-report");
            if (fixturePath == null || reportRoot == null) Assert.Ignore("仅显式提供隔离存档和报告路径时运行。");
            string allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestResults")) + Path.DirectorySeparatorChar;
            string Safe(string path)
            {
                string full = Path.GetFullPath(path);
                if (!full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("查询夹具必须位于 TestResults。");
                return full;
            }
            string Hash(string path)
            {
                using var stream = File.OpenRead(path); using var sha = SHA256.Create();
                return string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
            }
            fixturePath = Safe(fixturePath); reportRoot = Safe(reportRoot);
            Directory.CreateDirectory(reportRoot);
            string report = Path.Combine(reportRoot, award + "-player-" + player + ".json");
            Assert.That(File.Exists(report), Is.False);
            var fixture = JObject.Parse(File.ReadAllText(fixturePath));
            string savePath = Safe((string)fixture["SourceSave"]);
            Assert.That(Hash(savePath), Is.EqualTo((string)fixture["SourceSaveSha256"]));
            var save = JsonConvert.DeserializeObject<MatchSaveData>((string)JObject.Parse(File.ReadAllText(savePath))["Payload"]);
            Assert.That(save.GameId, Is.EqualTo((string)fixture["GameId"]));
            var settings = (JsonSerializerSettings)typeof(HostRecoveryService)
                .GetField("ArchiveSettings", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var state = JObject.Parse(save.ArchiveJson)["Snapshot"]["State"].ToObject<GameState>(JsonSerializer.Create(settings));
            ItemZoneLegacyProjection.Bind(state, true);
            var awardItem = state.ItemZones.Items.Single(item => item.DefinitionId == "infected.award." + award);
            var visible = new ItemZoneQuery().Project(state, GameStateViewer.Player(player));
            var builder = typeof(MoonSharpLuaRuntimeHost).Assembly.GetType("YC.Infrastructure.Lua.LuaQuerySnapshotBuilder", true);
            var content = (LuaSnapshotValue)builder.GetMethod("Content", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { state, visible, new List<LuaSnapshotValue>() });
            var zones = content.Get("zones").Items;
            var minimal = zones.Where(zone => zone.Get("templateId").StringValue == "core.zone.box_retired" ||
                zone.Get("templateId").StringValue == "core.zone.player_rewards" && zone.Get("owner").StringValue == "p" + player).ToList();
            Assert.That(minimal.Count, Is.EqualTo(2));
            string fileName = "infected.award." + award + ".acquired.lua";
            var frozen = ((JArray)fixture["Contents"]).Single(value => ((string)value["Relative"]).EndsWith(fileName, StringComparison.Ordinal));
            string originalPath = Safe((string)frozen["Copy"]);
            Assert.That(Hash(originalPath), Is.EqualTo((string)frozen["SHA256"]));
            string oldSource = File.ReadAllText(originalPath);
            string currentSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core/lua/awards", fileName));
            LuaInvocationResult Invoke(string source, LuaSnapshotValue input)
            {
                var actor = state.FindPlayer(player);
                var snapshot = LuaFacadeSnapshot.FromInvocation("p" + player, actor.Score, actor.Resources.GoldVoucher, state.Players.Count, "1.1.0");
                snapshot.GlobalContent = input;
                snapshot.EventPayload = LuaSnapshotValue.Object(new[] { new LuaSnapshotEntry("rewardItemId", LuaSnapshotValue.String(awardItem.ItemId)) });
                var context = new LuaInvocationContext("fixture.award", "reward.acquired", "p" + player,
                    state.EffectRuntime.StateRevision, "1.1.0", state.Players.Count, actor.Score, actor.Resources.GoldVoucher, snapshot);
                var script = new LuaScriptDefinition("infected.award." + award, "infected.award." + award + ".acquired",
                    "content.infected.award." + award + ".acquired", "1.1.0", source, LuaContentHasher.ComputeSha256(source));
                return new MoonSharpLuaRuntimeHost().Invoke(script, context);
            }
            var small = LuaSnapshotValue.Object(new[] { new LuaSnapshotEntry("zones", LuaSnapshotValue.Array(minimal)) });
            var oldSmall = Invoke(oldSource, small); var newSmall = Invoke(currentSource, small);
            var oldActual = Invoke(oldSource, content); var currentActual = Invoke(currentSource, content);
            bool sameEffects = oldSmall.IsSuccess && newSmall.IsSuccess &&
                JsonConvert.SerializeObject(oldSmall.Effects) == JsonConvert.SerializeObject(newSmall.Effects);
            using (var stream = new StreamWriter(new FileStream(report, FileMode.CreateNew, FileAccess.Write)))
                stream.Write(JsonConvert.SerializeObject(new { Scope = "故障前自然存档的区域投影与模拟获得事件，仅作隔离 Lua 诊断",
                    save.GameId, Player = player, Award = award, VisibleZoneCount = zones.Count, RelevantZoneCount = minimal.Count,
                    OldFailure = oldActual.FailureCode.ToString(), OldDiagnostic = oldActual.Diagnostic,
                    CurrentFailure = currentActual.FailureCode.ToString(), CurrentDiagnostic = currentActual.Diagnostic,
                    OldSmallSucceeded = oldSmall.IsSuccess, CurrentSmallSucceeded = newSmall.IsSuccess, SameSmallEffects = sameEffects }, Formatting.Indented));
            Assert.That(oldSmall.IsSuccess, Is.True, oldSmall.Diagnostic);
            Assert.That(newSmall.IsSuccess, Is.True, newSmall.Diagnostic);
            Assert.That(sameEffects, Is.True, "过滤前后必须保留相同的收益、选择及回收效果。");
            Assert.That(currentActual.IsSuccess, Is.True, currentActual.Diagnostic);
            Assert.That(Hash(savePath), Is.EqualTo((string)fixture["SourceSaveSha256"]));
        }

        [TestCase(1)] [TestCase(2)]
        public void AwardQuickActionKeepsCorrectZonesUnderNormalInstructionBudget(int player)
        {
            LuaSnapshotValue Zone(string id, string template, string owner) => LuaSnapshotValue.Object(new[] {
                new LuaSnapshotEntry("id", LuaSnapshotValue.String(id)),
                new LuaSnapshotEntry("templateId", LuaSnapshotValue.String(template)),
                new LuaSnapshotEntry("owner", LuaSnapshotValue.String(owner)) });
            var zones = Enumerable.Range(0, 500).Select(index => Zone("fixture.zone." + index, "core.zone.map_location", "none")).ToList();
            zones.Insert(70, Zone("fixture.rewards.p1", "core.zone.player_rewards", "p1"));
            zones.Insert(250, Zone("fixture.retired", "core.zone.box_retired", "none"));
            zones.Insert(410, Zone("fixture.rewards.p2", "core.zone.player_rewards", "p2"));
            var snapshot = LuaFacadeSnapshot.FromInvocation("p" + player, 0, 0, 2, "1.1.0");
            snapshot.GlobalContent = LuaSnapshotValue.Object(new[] { new LuaSnapshotEntry("zones", LuaSnapshotValue.Array(zones)) });
            snapshot.EventPayload = LuaSnapshotValue.Object(new[] { new LuaSnapshotEntry("rewardItemId", LuaSnapshotValue.String("fixture.award")) });
            var context = new LuaInvocationContext("fixture.award", "AbilityInvoked", "p" + player, 0, "1.1.0", 2, 0, 0, snapshot);
            string source = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core/lua/awards/infected.award.enterprise_special.quick.lua"));
            var script = new LuaScriptDefinition("infected.award.enterprise_special", "infected.award.enterprise_special.quick",
                "content.infected.award.enterprise_special.quick", "1.1.0", source, LuaContentHasher.ComputeSha256(source));
            var result = new MoonSharpLuaRuntimeHost().Invoke(script, context);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(result.Effects.Select(effect => effect.EffectTypeId), Is.EqualTo(new[] { "effect.enterprise.activate_special", "effect.item.move" }));
            YC.Domain.State.NormalizedValue Value(YC.Domain.State.NormalizedValue value, string key) => value.Properties.Single(entry => entry.Name == key).Value;
            var activate = result.Effects[0].RawArguments; var move = result.Effects[1].RawArguments;
            Assert.That(Value(activate, "player").StringValue, Is.EqualTo(player.ToString()));
            Assert.That(Value(activate, "unlockedByPlayer").StringValue, Is.EqualTo(player.ToString()));
            Assert.That(Value(Value(move, "selector"), "itemIds").Items.Single().StringValue, Is.EqualTo("fixture.award"));
            Assert.That(Value(Value(Value(move, "selector"), "expectedSource"), "zoneId").StringValue, Is.EqualTo("fixture.rewards.p" + player));
            Assert.That(Value(Value(move, "destination"), "zoneId").StringValue, Is.EqualTo("fixture.retired"));
        }

        [Test]
        public void RecordedAwardOriginalStateFilteredScriptProbe()
        {
            string Argument(string key) => Environment.GetCommandLineArgs()
                .FirstOrDefault(value => value.StartsWith(key + "=", StringComparison.Ordinal))?.Substring(key.Length + 1);
            string fixturePath = Argument("--yc-enterprise-fault-fixture"), reportPath = Argument("--yc-enterprise-fault-report");
            if (fixturePath == null || reportPath == null) Assert.Ignore("仅显式提供隔离原档、原命令及新报告路径时运行。");
            string results = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestResults")) + Path.DirectorySeparatorChar;
            string Safe(string path)
            {
                string full = Path.GetFullPath(path);
                if (!full.StartsWith(results, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("故障定位路径不在 TestResults。");
                return full;
            }
            string Hash(string path)
            {
                using var stream = File.OpenRead(path);
                using var sha = SHA256.Create();
                return string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
            }
            fixturePath = Safe(fixturePath); reportPath = Safe(reportPath);
            Assert.That(File.Exists(reportPath), Is.False, "不得覆盖故障定位报告。");
            var fixture = JObject.Parse(File.ReadAllText(fixturePath));
            string savePath = Safe((string)fixture["sourceSave"]), commandsPath = Safe((string)fixture["successCommands"]), faultPath = Safe((string)fixture["faultCommand"]);
            Assert.That(Hash(savePath), Is.EqualTo((string)fixture["sourceSaveSha256"]));
            Assert.That(Hash(commandsPath), Is.EqualTo((string)fixture["successCommandsSha256"]));
            Assert.That(Hash(faultPath), Is.EqualTo((string)fixture["faultCommandSha256"]));
            var save = JsonConvert.DeserializeObject<MatchSaveData>((string)JObject.Parse(File.ReadAllText(savePath))["Payload"]);
            Assert.That(save.GameId, Is.EqualTo((string)fixture["gameId"]));
            Assert.That(save.ContentHash, Is.EqualTo((string)fixture["contentHash"]));
            // 原档锁定旧 core；只读使用当时冻结构建，不能把新脚本伪装成旧内容。
            string oldCore = Path.Combine(Directory.GetCurrentDirectory(), "Builds/DIYFinalB7-20261112/tuohuang_Data/StreamingAssets/Content/core");
            string oldScript = Path.Combine(oldCore, "lua/awards/infected.award.enterprise_special.quick.lua");
            var buildManifest = JObject.Parse(File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "TestResults/DIY-exit-recovery-build-r7-20261112/game-manifest.json")));
            string oldScriptHash = (string)((JArray)buildManifest["files"]).Single(value => (string)value["path"] == "tuohuang_Data/StreamingAssets/Content/core/lua/awards/infected.award.enterprise_special.quick.lua")["sha256"];
            Assert.That(Hash(oldScript), Is.EqualTo(oldScriptHash));
            var runtimeType = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
            runtimeType.GetMethod("ConfigureRegistered", BindingFlags.Public | BindingFlags.Static).Invoke(null,
                new object[] { oldCore, Path.Combine(Path.GetDirectoryName(fixturePath), "隔离内容/Presentation") });
            var bootstrapper = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
            var restored = bootstrapper.GetMethod("Restore", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { null, save });
            var session = (GameSession)restored.GetType().GetField("Session").GetValue(restored);
            Assert.That(session.State.EffectRuntime.StateRevision, Is.EqualTo((int)fixture["initialRevision"]));
            var trace = new List<object>();
            object probeInfo = null; bool probeOnly = false;
            try
            {
                var commands = File.ReadLines(commandsPath).Where(line => !string.IsNullOrWhiteSpace(line)).Select(JObject.Parse).ToArray();
                Assert.That(commands.Length, Is.EqualTo(11));
                foreach (var original in commands)
                {
                    Assert.That(session.State.EffectRuntime.StateRevision, Is.EqualTo((int)original["BeforeRevision"]));
                    var command = original["Command"].ToObject<GameCommand>();
                    var result = session.Submit(command);
                    trace.Add(new { command.CommandId, Before = (int)original["BeforeRevision"], After = session.State.EffectRuntime.StateRevision,
                        result.Succeeded, Reason = result.Validation?.Reason, RuntimeStatus = session.State.EffectRuntime.Status.ToString() });
                    Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
                    Assert.That(session.State.EffectRuntime.StateRevision, Is.EqualTo((int)original["AfterRevision"]));
                    Assert.That(session.State.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active), session.State.EffectRuntime.LastFaultMessage);
                }
                var originalFault = JObject.Parse(File.ReadAllText(faultPath));
                var faultCommand = originalFault["OriginalUiCommand"].ToObject<GameCommand>();
                Assert.That(faultCommand.CommandId, Is.EqualTo((string)fixture["faultCommandId"]));
                Assert.That(session.State.EffectRuntime.StateRevision, Is.EqualTo((int)fixture["faultBeforeRevision"]));
                string candidatePath = Argument("--yc-enterprise-probe-source");
                if (candidatePath != null)
                {
                    candidatePath = Safe(candidatePath);
                    string originalPath = Path.Combine(Directory.GetCurrentDirectory(), "Builds/DIYFinalB7-20261112/tuohuang_Data/StreamingAssets/Content/core/lua/awards/infected.award.enterprise_special.quick.lua");
                    string originalSource = File.ReadAllText(originalPath), candidateSource = File.ReadAllText(candidatePath);
                    var actor = session.State.FindPlayer(faultCommand.PlayerId);
                    var registry = (YC.Domain.Effects.EffectRegistry)restored.GetType().GetField("EffectRegistry").GetValue(restored);
                    var option = registry.QueryActions(session.State, faultCommand.PlayerId).Single(value => value.OptionId == faultCommand.Parameters["optionId"]);
                    var award = session.State.ItemZones.Items.Single(item => item.ItemId == option.SourceRef.ItemId);
                    var visible = new ItemZoneQuery().Project(session.State, GameStateViewer.Player(faultCommand.PlayerId));
                    var builder = typeof(MoonSharpLuaRuntimeHost).Assembly.GetType("YC.Infrastructure.Lua.LuaQuerySnapshotBuilder", true);
                    var content = (LuaSnapshotValue)builder.GetMethod("Content", BindingFlags.NonPublic | BindingFlags.Static)
                        .Invoke(null, new object[] { session.State, visible, new List<LuaSnapshotValue>() });
                    var zones = content.Get("zones").Items;
                    var minimal = zones.Where(zone => zone.Get("templateId").StringValue == "core.zone.box_retired" ||
                        zone.Get("templateId").StringValue == "core.zone.player_rewards" && zone.Get("owner").StringValue == "p" + faultCommand.PlayerId).ToList();
                    LuaInvocationResult Invoke(string source, LuaSnapshotValue contentSnapshot)
                    {
                        var snapshot = LuaFacadeSnapshot.FromInvocation("p" + faultCommand.PlayerId, actor.Score,
                            actor.Resources.GoldVoucher, session.State.Players.Count, "1.1.0");
                        snapshot.GlobalContent = contentSnapshot;
                        snapshot.EventPayload = LuaSnapshotValue.Object(new[] { new LuaSnapshotEntry("rewardItemId", LuaSnapshotValue.String(award.ItemId)) });
                        var context = new LuaInvocationContext("fixture.award", "AbilityInvoked", "p" + faultCommand.PlayerId,
                            session.State.EffectRuntime.StateRevision, "1.1.0", session.State.Players.Count, actor.Score,
                            actor.Resources.GoldVoucher, snapshot);
                        var script = new LuaScriptDefinition("infected.award.enterprise_special", "infected.award.enterprise_special.quick",
                            "content.infected.award.enterprise_special.quick", "1.1.0", source, LuaContentHasher.ComputeSha256(source));
                        return new MoonSharpLuaRuntimeHost().Invoke(script, context);
                    }
                    var smallContent = LuaSnapshotValue.Object(new[] { new LuaSnapshotEntry("zones", LuaSnapshotValue.Array(minimal)) });
                    var oldSmall = Invoke(originalSource, smallContent); var newSmall = Invoke(candidateSource, smallContent);
                    var oldActual = Invoke(originalSource, content); var newActual = Invoke(candidateSource, content);
                    bool sameEffects = oldSmall.IsSuccess && newSmall.IsSuccess &&
                        JsonConvert.SerializeObject(oldSmall.Effects) == JsonConvert.SerializeObject(newSmall.Effects);
                    probeInfo = new { VisibleZoneCount = zones.Count, RelevantZoneCount = minimal.Count,
                        OldActualStatus = oldActual.Status.ToString(), OldActualFailure = oldActual.FailureCode.ToString(), oldActual.Diagnostic,
                        NewActualStatus = newActual.Status.ToString(), NewActualFailure = newActual.FailureCode.ToString(), NewActualDiagnostic = newActual.Diagnostic,
                        SmallInputsProduceIdenticalEffects = sameEffects, NewActualEffects = newActual.Effects };
                    Assert.That(oldActual.FailureCode, Is.EqualTo(LuaFailureCode.InstructionBudgetExceeded), oldActual.Diagnostic);
                    Assert.That(newActual.IsSuccess, Is.True, newActual.Diagnostic);
                    Assert.That(sameEffects, Is.True, "过滤前后须在相同合法区域上产生完全相同的效果。");
                    string formalSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core/lua/awards/infected.award.enterprise_special.quick.lua"));
                    Assert.That(LuaContentHasher.ComputeSha256(formalSource), Is.EqualTo(LuaContentHasher.ComputeSha256(candidateSource)), "正式脚本应与已核验的候选源码一致。");
                    probeOnly = true; return;
                }
                var faultResult = session.Submit(faultCommand);
                trace.Add(new { faultCommand.CommandId, Before = (int)fixture["faultBeforeRevision"], After = session.State.EffectRuntime.StateRevision,
                    faultResult.Succeeded, Reason = faultResult.Validation?.Reason, RuntimeStatus = session.State.EffectRuntime.Status.ToString() });
                Assert.That(faultResult.Succeeded, Is.True, faultResult.Validation?.Reason);
                Assert.That(session.State.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active), session.State.EffectRuntime.LastFaultMessage);
                Assert.That(session.State.EffectRuntime.InteractionRequests.Any(request => request.Status == "open" &&
                    request.AnsweringPlayerId == faultCommand.PlayerId && request.InteractionTypeId == "enterprise.special"), Is.True,
                    "持有授勋且有已解锁企业特效时，应打开本人合法的企业特效选择。 ");
            }
            finally
            {
                var runtime = session.State.EffectRuntime;
                var faulted = runtime.EffectNodes.Where(node => node.Status == EffectNodeStatus.Faulted).ToArray();
                var chain = new HashSet<string>(faulted.Select(node => node.EffectId));
                foreach (var failed in faulted)
                    for (var parent = failed; parent != null; parent = runtime.EffectNodes.Find(node => node.EffectId == parent.ParentEffectId)) chain.Add(parent.EffectId);
                using var stream = new StreamWriter(new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write));
                stream.Write(JsonConvert.SerializeObject(new { Scope = "原档与原命令隔离定位，非正常页面验收", save.GameId,
                    runtime.StateRevision, RuntimeStatus = runtime.Status.ToString(), runtime.LastFaultCode, runtime.LastFaultMessage,
                    ProbeOnly = probeOnly, Trace = trace, Probe = probeInfo,
                    FaultChain = runtime.EffectNodes.Where(node => chain.Contains(node.EffectId) || chain.Contains(node.ParentEffectId)).ToArray() }, Formatting.Indented));
            }
        }
    }
}
