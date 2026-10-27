using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 私有声明驱动真实SourceUse、Lua、实例、取得事件及恢复，不计正常游戏或正式正文验收。
    public sealed class RewardGrantTests
    {
        private const string Token = "infected.component.gavial_token", Reward = "entrepreneur.award.M01", Ability = "qy007.grant.fixture";
        private IsolatedTestArtifacts artifacts;
        private EffectRegistry registry;
        private GameState lastState;
        [SetUp] public void Start() => artifacts = IsolatedTestArtifacts.BeginCase();
        [TearDown] public void Finish()
        {
            if (lastState != null) artifacts.WriteLabeledText("最终真实发放现场", JsonConvert.SerializeObject(lastState));
            artifacts?.Finish(); lastState = null;
        }
        private GameState Prepare(string mutation = "", bool duplicate = false, bool badCardZone = false, bool leaveSourceBeforeGrant = false, bool movableSource = false)
        {
            string installed = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(installed, "pack.json")));
            manifest["enabledExpansionIds"] = new JArray("core", "enterprise", "infected");
            var token = JObject.Parse(File.ReadAllText(Path.Combine(installed, "component/" + Token + ".json")));
            // B2退出用例使用私有、声明与实例一致的普通方块来源；通用MoveItem不授权地图Token业务。
            string sourceType = leaveSourceBeforeGrant ? "core.player_marker" : movableSource ? "core.cube" : "infected.gavial_token";
            if (sourceType != "infected.gavial_token")
            {
                token["itemTypeId"] = sourceType; token["data"]["instanceTypeId"] = sourceType;
                token["cubeAppearance"] = new JObject { ["color"] = new JObject { ["mode"] = "owner_player", ["name"] = "", ["rgb"] = "" } };
            }
            token["abilitySources"] = JArray.FromObject(new[] { new AbilitySourceDeclaration { AbilityId = Ability, Kind = "event", HandlerRef = Ability,
                DeclarationVersion = "1.0.0", AuthorizationPolicy = "owner", PresentationRef = Token,
                EffectiveZones = new List<string> { leaveSourceBeforeGrant ? "core.zone.player_supply" : "core.zone.box_reserve" }, ObservedEvents = new List<string> { "RoundStarted" },
                AllowedOperations = new List<string> { "reward.grant:" + Reward + ":core.award_card:1:other:core.zone.player_rewards" } } });
            token["abilityHandlers"] = JArray.FromObject(new[] { new ExternalAbilityHandler { HandlerId = Ability, Version = "1.0.0", Script = "lua/qy007_grant_fixture.lua" } });
            string body = "local a={apiVersion=1,player=ctx.playerId,definitionId='" + Reward + "',typeId='core.award_card',count=1,recipientPlayer='p2',destination='core.zone.player_rewards',operationId='grant-2'}; ";
            if (mutation == "self") body += "a.recipientPlayer='p1'; ";
            if (mutation == "count") body += "a.count=2; ";
            if (mutation == "type") body += "a.typeId='core.contract_card'; ";
            if (mutation == "definition") body += "a.definitionId='unregistered.reward'; ";
            if (mutation == "actor") body += "a.player='p2'; ";
            if (mutation == "destination") body += "a.destination='core.zone.reward_pool'; ";
            string lua = "return function(ctx) if ctx.playerId~='p1' then return {} end; " + body +
                (duplicate ? "return {Effect.GrantReward(a),Effect.GrantReward(a)} end" : "local b={apiVersion=1,player=ctx.playerId,definitionId='" + Reward + "',typeId='core.award_card',count=1,recipientPlayer='p3',destination='core.zone.player_rewards',operationId='grant-3'}; return {Effect.GrantReward(a),Effect.GrantReward(b)} end");
            if (leaveSourceBeforeGrant)
                lua = lua.Replace("return {Effect.GrantReward(a),Effect.GrantReward(b)}", @"local reserve=Global.Content.FindZones({templateId='core.zone.player_supply',owner=ctx.playerId})[1]
local retired=Global.Content.FindZones({templateId='core.zone.box_retired'})[1]
return {Effect.MoveItem({apiVersion=1,player=ctx.playerId,itemRefs={{itemId=ctx.payload.sourceItemId}},
expectedSource={zoneId=reserve.id},destination={zoneId=retired.id},insertion={kind='unordered'},
operationProfileId='core.cube_storage',requiredCount=1}),Effect.GrantReward(a),Effect.GrantReward(b)}");
            var reward = JObject.Parse(File.ReadAllText(Path.Combine(installed, "award/" + Reward + ".json")));
            reward["applicableModes"] = new JArray("standard"); reward["requiredCapabilities"] = new JArray();
            string input = artifacts.CopyContent(installed, "声明发放私有输入", new Dictionary<string, string> {
                ["pack.json"] = manifest.ToString(), ["component/" + Token + ".json"] = token.ToString(),
                ["award/" + Reward + ".json"] = reward.ToString(), ["lua/qy007_grant_fixture.lua"] = lua });
            var pack = ExternalContentPack.Load(input); artifacts.RecordContent("发放实际内容", input, pack.ContentHash);
            registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
            GenericLuaEffectExecutor.Register(registry); ResourceEffectExecutor.Register(registry);
            ContentLifecycleLuaCatalog.Register(registry, pack); AbilitySourceLuaCatalog.Register(registry, pack);
            var map = StaticMapDefinitions.CreateThreePlayerMap();
            var source = new GameState { GameId = "qy007.grant." + Guid.NewGuid().ToString("N"), ContentPackHash = pack.ContentHash, MapId = map.MapId,
                Phase = GamePhase.ActionRound1, Round = 1, ActionRound = 1, CurrentPlayerId = 1, StartPlayerId = 1,
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 3, ContentPackHash = pack.ContentHash },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 }, new PlayerState { PlayerId = 3 } },
                ContentDefinitions = new GameContentDefinitions { Awards = ContentLifecycleLuaCatalog.CreateAwards(pack),
                    AbilitySources = registry.AbilityDeclarations.ToList(), UsageTracks = pack.ActiveDefinitions.SelectMany(d => d.UsageTracks).ToList(),
                    Components = pack.ActiveDefinitions.Where(d => d.ContentType == "component").Select(d => new ComponentContentDefinition {
                        DefinitionId = d.DefinitionId, TypeId = d.ItemTypeId, QuantityPolicy = (string)d.Data["quantityPolicy"], CubeAppearance = d.CubeAppearance }).ToList() } };
            source.RandomRuntime = GameplayRandomRuntime.Create(source.GameId);
            var state = new ItemZoneStateFactory().CreateForSetup(source, map, new List<ItemRecipeEntry> {
                new ItemRecipeEntry { EntryId = "source", DefinitionId = Token, TypeId = sourceType, Quantity = 1, PerPlayer = true, InitialZoneRole = leaveSourceBeforeGrant ? "supply" : "reserve" } });
            state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2, 3 };
            if (badCardZone) state.ContentDefinitions.Awards.Single(d => d.DefinitionId == Reward).CardZones.Add(new CardZoneDefinition { LocalZoneId = "bad", ZoneTemplateId = "unregistered.zone" });
            registry.Register(new EffectRegistration("qy007.grant.start", c => EffectStepResult.Completed().AddEvent(new EffectEventRequest {
                EventType = "RoundStarted", PlayerId = 1, Payload = Object() })));
            lastState = state; return state;
        }
        private EffectTreeExecutor Begin(GameState state)
        {
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(EffectSpec.Create("qy007.grant.start", playerId: 1)); return engine;
        }
        private void Until(EffectTreeExecutor engine, Func<bool> predicate)
        {
            for (int i = 0; !predicate() && i < 500; i++) Assert.That(engine.Advance(), Is.True);
            Assert.That(predicate(), Is.True);
        }
        [Test] public void ActualDeclaredSourceGrantsTwoOpponentsRealHeldRewardsWithoutRandomDraws()
        {
            var state = Prepare(); string random = JsonConvert.SerializeObject(state.RandomRuntime); var engine = Begin(state);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var rewards = state.ItemZones.Items.Where(i => i.DefinitionId == Reward).ToList();
            Assert.That(rewards.Count, Is.EqualTo(2)); Assert.That(rewards.Select(i => i.ControllerSubject.PlayerId).OrderBy(i => i), Is.EqualTo(new[] { 2, 3 }));
            Assert.That(rewards.All(i => ContentLifecycleEffectExecutor.IsHeld(state, i, i.ControllerSubject.PlayerId)), Is.True);
            Assert.That(state.EffectRuntime.AbilitySourceUses.Count, Is.EqualTo(3));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "reward.acquired"), Is.EqualTo(2));
            Assert.That(JsonConvert.SerializeObject(state.RandomRuntime), Is.EqualTo(random));
            Assert.That(RewardGrantEffectExecutor.TryValidate(state, registry, out string reason), Is.True, reason);
            var recovered = artifacts.Recover(state, "全部发放后冷恢复", registry); Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            Assert.That(new EffectTreeExecutor(recovered.State, registry).RunUntilQuiescent().Faulted, Is.False);
            Assert.That(recovered.State.ItemZones.Items.Count(i => i.DefinitionId == Reward), Is.EqualTo(2));
        }
        [TestCase(false)] [TestCase(true)] public void RecoveryBeforeAndAfterAtomicGrantNeverRecreatesAnInstance(bool committed)
        {
            var state = Prepare(); var engine = Begin(state);
            Until(engine, () => state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == RewardGrantEffectExecutor.TypeId && (committed ? n.RewardGrant != null : n.RewardGrant == null)));
            var savedIds = state.ItemZones.Items.Where(i => i.DefinitionId == Reward).Select(i => i.ItemId).ToList();
            var recovered = artifacts.Recover(state, committed ? "已发未取得事件" : "发放前续接", registry); Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            engine = new EffectTreeExecutor(recovered.State, registry); Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, recovered.State.EffectRuntime.LastFaultMessage);
            Assert.That(recovered.State.ItemZones.Items.Count(i => i.DefinitionId == Reward), Is.EqualTo(2));
            Assert.That(savedIds.All(id => recovered.State.ItemZones.Items.Any(i => i.ItemId == id)), Is.True);
            Assert.That(recovered.State.EffectRuntime.RuleEvents.Count(e => e.EventType == "reward.acquired"), Is.EqualTo(2));
        }
        [TestCase("self")] [TestCase("count")] [TestCase("type")] [TestCase("definition")]
        public void DeclarationRejectsRecipientCountTypeAndDefinitionEscalation(string mutation)
        {
            var state = Prepare(mutation, duplicate: true); string random = JsonConvert.SerializeObject(state.RandomRuntime);
            var engine = Begin(state); engine.RunUntilQuiescent();
            Assert.That(state.ItemZones.Items.Any(i => i.DefinitionId == Reward), Is.False);
            var grants = state.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == RewardGrantEffectExecutor.TypeId).ToList();
            Assert.That(grants.Count, Is.EqualTo(2));
            Assert.That(grants.All(n => n.Status == EffectNodeStatus.Failed && n.FailureReason == "reward_grant_permission_denied"), Is.True);
            Assert.That(JsonConvert.SerializeObject(state.RandomRuntime), Is.EqualTo(random));
            artifacts.WriteLabeledText("许可拒绝现场", JsonConvert.SerializeObject(state));
        }
        [TestCase("actor")] [TestCase("destination")]
        public void LuaRejectsWrongActorAndDestinationBeforeBodyRuns(string mutation)
        {
            var state = Prepare(mutation, duplicate: true); var engine = Begin(state); engine.RunUntilQuiescent();
            Assert.That(state.ItemZones.Items.Any(i => i.DefinitionId == Reward), Is.False);
            Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.PausedFault));
            artifacts.WriteLabeledText("宿主拒绝现场", JsonConvert.SerializeObject(state));
        }
        [Test] public void ReusedOperationWithinOneRealInvocationCannotIssueAnotherReward()
        {
            var state = Prepare(duplicate: true); var engine = Begin(state); engine.RunUntilQuiescent();
            Assert.That(state.ItemZones.Items.Count(i => i.DefinitionId == Reward), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.RewardGrant != null), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == RewardGrantEffectExecutor.TypeId && n.Status == EffectNodeStatus.Failed), Is.True);
        }
        [Test] public void InvalidChildZoneCannotPublishAnyPartOfTheGrant()
        {
            var state = Prepare(duplicate: true, badCardZone: true); var engine = Begin(state); engine.RunUntilQuiescent();
            Assert.That(state.ItemZones.Items.Any(i => i.DefinitionId == Reward), Is.False);
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.RewardGrant != null), Is.False);
            artifacts.WriteLabeledText("原子预检失败无实例", JsonConvert.SerializeObject(state));
        }
        [TestCase("ItemIds", false)] [TestCase("SourceRef", false)] [TestCase("RequestDigest", false)]
        [TestCase("ItemIds", true)] [TestCase("SourceRef", true)] [TestCase("RequestDigest", true)]
        public void RawReceiptFieldsAreRequiredAndStateAfterCannotOmitThem(string field, bool checkpoint)
        {
            var state = Prepare(); var before = HostRecoveryService.CreateArchive(state); var engine = Begin(state);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            HostRecoveryService.AppendJournal(before, state);
            var archive = JObject.Parse(HostRecoveryService.SerializeArchive(checkpoint ? before : HostRecoveryService.CreateArchive(state)));
            var raw = checkpoint ? (JObject)((JArray)archive["Journal"]["Entries"]).Last(v => v["StateAfter"] is JObject)["StateAfter"] : (JObject)archive["Snapshot"]["State"];
            var n = ((JArray)raw["EffectRuntime"]["EffectNodes"]).OfType<JObject>().First(v => v["RewardGrant"] is JObject);
            ((JObject)n["RewardGrant"]).Remove(field);
            Assert.That(RewardGrantEffectExecutor.HasRawFields(raw), Is.False);
            var result = HostRecoveryService.RecoverJson(archive.ToString(), registry);
            Assert.That(result.Succeeded, Is.False); Assert.That(result.Diagnostic, Does.Contain("reward_grant.v1:raw_fields_missing"));
            artifacts.WriteLabeledText("损坏原始发放收据归档", archive.ToString());
        }

        private JObject Qy007B2Archive(GameState state, HostSessionArchiveDto before, bool checkpoint)
        {
            if (checkpoint) HostRecoveryService.AppendJournal(before, state);
            return JObject.Parse(HostRecoveryService.SerializeArchive(checkpoint ? before : HostRecoveryService.CreateArchive(state)));
        }
        private static JObject Qy007B2RawState(JObject archive, bool checkpoint) => checkpoint
            ? (JObject)((JArray)archive["Journal"]["Entries"]).Last(e => e["StateAfter"] is JObject)["StateAfter"]
            : (JObject)archive["Snapshot"]["State"];
        private void Qy007B2Recover(JObject archive, bool expected, string label)
        {
            string json = archive.ToString(); artifacts.WriteLabeledText(label + "原归档", json);
            var result = HostRecoveryService.RecoverJson(json, registry);
            artifacts.WriteLabeledText(label + "恢复结果", JsonConvert.SerializeObject(new { result.Succeeded, result.Diagnostic, result.State }));
            Assert.That(result.Succeeded, Is.EqualTo(expected), result.Diagnostic);
            if (expected) lastState = result.State;
            else Assert.That(result.Diagnostic, Is.Not.Empty);
        }
        [TestCase("source", false)] [TestCase("reward", false)] [TestCase("both", false)]
        [TestCase("source", true)] [TestCase("reward", true)] [TestCase("both", true)]
        public void Qy007B2HistoricalGrantSurvivesRealSourceExitAndRewardTransfer(string history, bool checkpoint)
        {
            var state = Prepare(movableSource: true); var engine = Begin(state);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var before = HostRecoveryService.CreateArchive(state);
            string[] ids = state.ItemZones.Items.Where(i => i.DefinitionId == Reward).Select(i => i.ItemId).OrderBy(i => i).ToArray();
            string random = JsonConvert.SerializeObject(state.RandomRuntime);
            ItemTransferEffectExecutor.Register(registry);
            string retired = state.ItemZones.Zones.Single(z => z.LocalZoneId == "retired").ZoneId;
            if (history != "reward")
            {
                var source = state.ItemZones.Items.Single(i => i.DefinitionId == Token && i.OwnerSubject.PlayerId == 1);
                string move = engine.CreateRoot(MoveSpec(1, new[] { source.ItemId }, source.ZoneId, retired, "unordered", 1, "core.cube_storage"));
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
                Assert.That(engine.GetNode(move).Status, Is.EqualTo(EffectNodeStatus.Completed));
                Assert.That(registry.QuerySources(state, 1).Any(s => s.AbilityId == Ability), Is.False);
            }
            if (history != "source")
            {
                var reward = state.ItemZones.Items.Single(i => i.DefinitionId == Reward && i.OwnerSubject.PlayerId == 2);
                string move = engine.CreateRoot(MoveSpec(2, new[] { reward.ItemId }, reward.ZoneId, retired, "unordered", 1, "core.reward_card_transfer"));
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
                Assert.That(engine.GetNode(move).Status, Is.EqualTo(EffectNodeStatus.Completed));
                Assert.That(ContentLifecycleEffectExecutor.IsHeld(state, state.ItemZones.Items.Single(i => i.ItemId == reward.ItemId), 2), Is.False);
            }
            Qy007B2Recover(Qy007B2Archive(state, before, checkpoint), true, "历史发放真实退出" + history + checkpoint);
            var restoredEngine = new EffectTreeExecutor(lastState, registry);
            Assert.That(restoredEngine.RunUntilQuiescent().Faulted, Is.False, lastState.EffectRuntime.LastFaultMessage);
            Assert.That(lastState.ItemZones.Items.Where(i => i.DefinitionId == Reward).Select(i => i.ItemId).OrderBy(i => i), Is.EqualTo(ids));
            Assert.That(lastState.EffectRuntime.RuleEvents.Count(e => e.EventType == "reward.acquired"), Is.EqualTo(2));
            Assert.That(JsonConvert.SerializeObject(lastState.RandomRuntime), Is.EqualTo(random));
        }
        [Test] public void Qy007B2SourceThatLeavesThroughLuaBeforeGrantCannotUseHistoricalAuthorityForANewGrant()
        {
            var state = Prepare(leaveSourceBeforeGrant: true); ItemTransferEffectExecutor.Register(registry);
            var engine = Begin(state); engine.RunUntilQuiescent();
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == ItemTransferEffectExecutor.TypeId && n.Status == EffectNodeStatus.Completed), Is.EqualTo(1));
            var grants = state.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == RewardGrantEffectExecutor.TypeId).ToList();
            Assert.That(grants.Count, Is.EqualTo(2));
            Assert.That(grants.All(n => n.Status == EffectNodeStatus.Failed && n.FailureReason == "reward_grant_permission_denied"), Is.True);
            Assert.That(state.ItemZones.Items.Any(i => i.DefinitionId == Reward), Is.False);
            Assert.That(state.EffectRuntime.RuleEvents.Any(e => e.EventType == "reward.acquired"), Is.False);
            artifacts.WriteLabeledText("实际Lua先退出来源后拒绝新发放", JsonConvert.SerializeObject(state));
        }
        private static IEnumerable<TestCaseData> Qy007B2GrantCorruptions()
        {
            foreach (bool checkpoint in new[] { false, true })
            foreach (string field in new[] { "AuthorityEffectId", "RecipientPlayerId", "DefinitionVersion", "DestinationZoneId", "Count", "CountMaximum",
                "DefinitionId", "TypeId", "OperationId", "RequestDigest", "SourceRef.DefinitionId", "SourceRef.AbilityId",
                "SourceRef.ItemId", "SourceRef.AuthorizationRef", "SourceRef.AuthorizedPlayerId", "SourceRef.GrantingSourceRefs",
                "VersionRef.ContractVersion", "VersionRef.ContentPackHash", "VersionRef.HandlerHash", "VersionRef.DefinitionVersion", "VersionRef.ExecutorVersion",
                "raw:SourceRef.SourceKey", "raw:VersionRef.HandlerHash", "type:Count", "extra:Receipt" })
                yield return new TestCaseData(field, checkpoint);
        }
        [TestCaseSource(nameof(Qy007B2GrantCorruptions))]
        public void Qy007B2GrantPermissionAndHistoryReceiptCorruptionIsRejectedFromLegalArchives(string field, bool checkpoint)
        {
            var state = Prepare(); var before = HostRecoveryService.CreateArchive(state); var engine = Begin(state);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var legal = Qy007B2Archive(state, before, checkpoint);
            Qy007B2Recover(legal, true, "发放损坏负例合法对照" + field + checkpoint);
            var bad = (JObject)legal.DeepClone(); var raw = Qy007B2RawState(bad, checkpoint);
            var node = ((JArray)raw["EffectRuntime"]["EffectNodes"]).OfType<JObject>().First(n => n["RewardGrant"] is JObject);
            var receipt = (JObject)node["RewardGrant"];
            if (field.StartsWith("raw:", StringComparison.Ordinal))
            {
                string[] parts = field.Substring(4).Split('.'); ((JObject)receipt[parts[0]]).Property(parts[1]).Remove();
            }
            else if (field == "type:Count") receipt["Count"] = "1";
            else if (field == "extra:Receipt") receipt["ungrantedPermission"] = true;
            else
            {
                string[] parts = field.Split('.'); var target = parts.Length == 1 ? receipt : (JObject)receipt[parts[0]]; string key = parts.Last();
                if (key == "RecipientPlayerId" || key == "AuthorizedPlayerId") target[key] = 1 == target[key].Value<int>() ? 2 : 1;
                else if (key == "Count") target[key] = 0;
                else if (key == "CountMaximum") target["Count"] = 17;
                else if (key == "GrantingSourceRefs") ((JArray)target[key]).Add("forged.granting-source");
                else target[key] = "forged." + key;
            }
            Assert.That(RewardGrantEffectExecutor.HasRawFields(raw), Is.EqualTo(!field.Contains(":")), "先区分raw形状错误和domain身份错误");
            Qy007B2Recover(bad, false, "发放权限历史损坏拒绝" + field + checkpoint);
            Assert.That(state.ItemZones.Items.Count(i => i.DefinitionId == Reward), Is.EqualTo(2), "只改归档JSON，不改权威实例或补发");
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "reward.acquired"), Is.EqualTo(2));
        }
    }
}
