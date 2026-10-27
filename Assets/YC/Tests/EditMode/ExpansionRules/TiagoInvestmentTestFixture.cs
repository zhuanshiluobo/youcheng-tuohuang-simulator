using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YC.Application.Sessions;
using YC.Domain.Enterprises;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 明确的隔离输入；只装载真实D正文，不用C#处理器替代任何卡面效果，不计正常局。
    internal sealed class TiagoInvestmentTestFixture : IDisposable
    {
        internal const string Script = "lua/entrepreneurs/tiago.lua", Ability = "entrepreneur.D09.investment";
        internal readonly IsolatedTestArtifacts Artifacts = IsolatedTestArtifacts.BeginCase();
        internal ExternalContentPack Pack;
        internal GameState State;
        internal EffectRegistry Registry;
        internal RoundExecutionService Rounds;
        internal EffectTreeExecutor Engine => Rounds.CreateCommandExecutor(State);
        internal string Carrier;
        private readonly string privateInputRoot, frozenSource, frozenSourceByteHash, frozenSourceHash, handlerVersion;
        private string preparedRoot;
        private readonly HashSet<string> recoveredRolls = new HashSet<string>();
        internal string Source => frozenSource;

        internal TiagoInvestmentTestFixture()
        {
            string installed = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            privateInputRoot = Artifacts.CopyContent(installed, "014本case完整私有基线");
            byte[] sourceBytes = File.ReadAllBytes(Path.Combine(privateInputRoot, Script));
            frozenSource = new UTF8Encoding(false, true).GetString(sourceBytes);
            frozenSourceByteHash = ByteHash(sourceBytes);
            frozenSourceHash = LuaContentHasher.ComputeSha256(frozenSource);
            var abilityDefinition = JObject.Parse(File.ReadAllText(Path.Combine(privateInputRoot, "entrepreneur/entrepreneur.ability.D09.json")));
            handlerVersion = (string)abilityDefinition["abilityHandlers"].Single(h => (string)h["handlerId"] == Ability)["version"];
            Artifacts.WriteLabeledText("014本case冻结Lua原文关联", Newtonsoft.Json.JsonConvert.SerializeObject(new {
                baselineInputRoot = privateInputRoot, script = Script, sourceFileByteSha256 = frozenSourceByteHash,
                invocationSourceSha256 = frozenSourceHash, source = frozenSource }));
        }
        private static string ByteHash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        internal string PreparePack()
        {
            if (Pack != null) return preparedRoot;
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(privateInputRoot, "pack.json")));
            var handlers = (JArray)manifest["settlementHandlers"];
            foreach (string id in new[] { Ability, "entrepreneur.D01.exchange", "entrepreneur.D02.exchange" })
                Assert.That(handlers.Count(h => (string)h["handlerId"] == id + ".availability" && (string)h["script"] == Script &&
                    (string)h["entryPoint"] == (id == Ability ? "TiagoInvestmentAvailability" : "TiagoExchangeAvailability")), Is.EqualTo(1),
                    "正式manifest缺少availability登记：" + id);
            string root = privateInputRoot; // 不替测试补manifest；正式登记缺失或陈旧必须由唯一整合者修正。
            var loadedPack = ExternalContentPack.Load(root);
            string loadedSource = loadedPack.GetScript(Script);
            string hostSourceByteHash = ByteHash(File.ReadAllBytes(Path.Combine(root, Script)));
            string hostSourceHash = LuaContentHasher.ComputeSha256(loadedSource);
            Artifacts.RecordContent("014正式解析器装载", root, loadedPack.ContentHash);
            Artifacts.WriteLabeledText("014同基线Host装载关联", Newtonsoft.Json.JsonConvert.SerializeObject(new {
                baselineInputRoot = privateInputRoot, hostInputRoot = root, contentPackHash = loadedPack.ContentHash, script = Script,
                frozenSourceByteSha256 = frozenSourceByteHash, hostSourceByteSha256 = hostSourceByteHash,
                frozenInvocationSourceSha256 = frozenSourceHash, hostInvocationSourceSha256 = hostSourceHash,
                sourceTextMatches = string.Equals(loadedSource, frozenSource, StringComparison.Ordinal) }));
            Assert.That(hostSourceByteHash, Is.EqualTo(frozenSourceByteHash), "Host输入的Lua字节必须来自本case冻结基线");
            Assert.That(loadedSource, Is.EqualTo(frozenSource), "Host装载的Lua原文必须与隔离调用冻结原文一致");
            Assert.That(hostSourceHash, Is.EqualTo(frozenSourceHash), "Host装载的Lua源码摘要必须与隔离调用一致");
            Pack = loadedPack.CreateActiveSnapshot(new GameRuleConfiguration {
                MapId = StaticMapDefinitions.ThreePlayerMapId, PlayerCount = 2, RuleModeId = "entrepreneur",
                EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" },
                PlayerStageSchedulingMode = PlayerStageSchedulingMode.Sequential });
            Assert.That(Pack.ActiveDefinitions.Any(d => d.DefinitionId == "entrepreneur.ability.D09"), Is.True);
            Artifacts.RecordContent("014正式活动目录装配", root, Pack.ContentHash);
            Artifacts.WriteLabeledText("014装载与活动内容身份", Newtonsoft.Json.JsonConvert.SerializeObject(new {
                inputRoot = root, installedContentPackHash = loadedPack.ContentHash, activeContentPackHash = Pack.ContentHash,
                limitation = "正式活动目录装配用于隔离领域用例；不是菜单开局或正常游戏证据" }));
            preparedRoot = root; return root;
        }
        internal static LuaSnapshotValue Snapshot(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return LuaSnapshotValue.Null;
            if (token is JObject obj) return LuaSnapshotValue.Object(obj.Properties().Select(p => new LuaSnapshotEntry(p.Name, Snapshot(p.Value))).ToList());
            if (token is JArray array) return LuaSnapshotValue.Array(array.Select(Snapshot).ToList());
            if (token.Type == JTokenType.Integer) return LuaSnapshotValue.Integer(token.Value<long>());
            if (token.Type == JTokenType.Boolean) return LuaSnapshotValue.Boolean(token.Value<bool>());
            return LuaSnapshotValue.String(token.Value<string>());
        }
        internal static LuaSnapshotValue Snapshot(NormalizedValue value)
        {
            if (value == null || value.Kind == NormalizedValueKind.Null) return LuaSnapshotValue.Null;
            if (value.Kind == NormalizedValueKind.Array) return LuaSnapshotValue.Array(value.Items.Select(Snapshot).ToList());
            if (value.Kind == NormalizedValueKind.Object) return LuaSnapshotValue.Object(value.Properties.Select(p => new LuaSnapshotEntry(p.Name, Snapshot(p.Value))).ToList());
            if (value.Kind == NormalizedValueKind.Integer) return LuaSnapshotValue.Integer(value.IntegerValue);
            if (value.Kind == NormalizedValueKind.Boolean) return LuaSnapshotValue.Boolean(value.BooleanValue);
            return LuaSnapshotValue.String(value.Kind == NormalizedValueKind.StableReference ? value.ReferenceId : value.StringValue);
        }
        internal LuaInvocationResult Invoke(string ability = Ability, string[] faces = null, NormalizedValue continuation = null, NormalizedValue result = null, string outcome = "completed", Action<JObject> alterIsolatedContent = null, JArray tokenInventory = null)
        {
            // 这是仅检验Lua构树的只读快照，不是伪造随机结果的正常游戏验收。
            var instances = new JArray(); var choices = new JArray();
            foreach (string face in faces ?? Array.Empty<string>())
            {
                int i = int.Parse(face.Substring(2)); int round = ((i - 1) / 2) * 2 + 1;
                string def = "entrepreneur.round.D." + round, item = "card." + round;
                instances.Add(new JObject { ["id"] = item, ["definitionId"] = def, ["controller"] = "p1", ["owner"] = "p1", ["zoneId"] = "active", ["entrepreneurAbilityItemId"] = "carrier" });
                choices.Add(new JObject { ["selectedCandidateId"] = "candidate." + face, ["activeFaceId"] = face,
                    ["candidates"] = new JArray(new JObject { ["candidateId"] = "candidate." + face, ["faceId"] = face, ["entityItemId"] = item, ["definitionId"] = def }) });
            }
            instances.Add(new JObject { ["id"] = "carrier", ["definitionId"] = "entrepreneur.ability.D09", ["controller"] = "p1", ["owner"] = "p1", ["zoneId"] = "active", ["roundChoices"] = choices });
            for (int i = 1; i <= 3; i++) instances.Add(new JObject { ["id"] = "die." + i, ["definitionId"] = "entrepreneur.component.die", ["typeId"] = "core.die", ["owner"] = "p1", ["controller"] = "p1", ["zoneId"] = "supply" });
            var snapshot = LuaFacadeSnapshot.FromInvocation("p1", 0, 100, 2, handlerVersion);
            var isolatedContent = new JObject { ["instances"] = instances, ["definitions"] = new JArray(), ["zones"] = new JArray(
                new JObject { ["id"] = "active", ["templateId"] = "core.zone.entrepreneur_active" },
                new JObject { ["id"] = "reputation", ["parentId"] = "carrier", ["localZoneId"] = "reputation" },
                new JObject { ["id"] = "supply", ["owner"] = "p1", ["localZoneId"] = "supply" },
                new JObject { ["id"] = "retired", ["templateId"] = "core.zone.box_retired" }) };
            alterIsolatedContent?.Invoke(isolatedContent);
            snapshot.GlobalContent = Snapshot(isolatedContent);
            var sources = new JArray(IsolatedExchangeSources(false)[1].DeepClone());
            var actualCarrier = isolatedContent["instances"].OfType<JObject>().Single(i => (string)i["id"] == "carrier");
            foreach (JObject group in actualCarrier["roundChoices"])
            {
                var candidate = group["candidates"].OfType<JObject>().SingleOrDefault(c => (string)c["candidateId"] == (string)group["selectedCandidateId"] && (string)c["faceId"] == (string)group["activeFaceId"]);
                if (candidate == null) continue;
                var card = isolatedContent["instances"].OfType<JObject>().Single(i => (string)i["id"] == (string)candidate["entityItemId"]);
                bool active = isolatedContent["zones"].OfType<JObject>().Any(z => (string)z["id"] == (string)card["zoneId"] && (string)z["templateId"] == "core.zone.entrepreneur_active");
                if (!active || (string)card["definitionId"] != (string)candidate["definitionId"] || (string)card["controller"] != "p1" ||
                    (string)card["owner"] != "p1" || (string)card["entrepreneurAbilityItemId"] != "carrier") continue;
                sources.Add(IsolatedSelectedSource((string)candidate["faceId"], (string)card["id"], (string)card["definitionId"]));
            }
            foreach (JObject proof in sources) { proof["handlerHash"] = frozenSourceHash; proof["declarationVersion"] = handlerVersion; }
            var sourceFacts = new JObject { ["protocolVersion"] = 1, ["stateRevision"] = 0, ["contentHash"] = "qy014.isolated.sources",
                ["authorizedPlayerId"] = "p1", ["ruleOwnerSourceKey"] = "carrier.source", ["ruleOwnerItemId"] = "carrier",
                ["sources"] = sources, ["grants"] = new JArray(), ["callableActions"] = new JArray(),
                ["transferableTokens"] = tokenInventory ?? IsolatedTokens(8), ["transferableTokenCount"] = (tokenInventory ?? IsolatedTokens(8)).Count };

            snapshot.EventPayload = continuation == null ? Snapshot(new JObject { ["abilityId"] = ability, ["sourceItemId"] = "carrier" }) :
                LuaSnapshotValue.Object(new[] { new LuaSnapshotEntry("continuationContext", Snapshot(continuation)), new LuaSnapshotEntry("normalizedResult", Snapshot(result)), new LuaSnapshotEntry("outcome", LuaSnapshotValue.String(outcome)) });
            Artifacts.WriteLabeledText("014隔离Lua只读调用输入", Newtonsoft.Json.JsonConvert.SerializeObject(new {
                ability, frozenSourceHash, isolatedContent, sourceFacts, snapshot.EventPayload,
                limitation = "分支构树输入；指定结果和内容快照不计正常骰子、正式选面或界面验收" }));
            return new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("entrepreneur.ability.D09", ability, ability, handlerVersion, frozenSource, frozenSourceHash),
                new LuaInvocationContext("qy014.isolated", continuation == null ? "AbilityInvoked" : "EffectCompleted", "p1", 0, handlerVersion, 2, 0, 100, snapshot) { SourceFacts = Snapshot(sourceFacts) });
        }
        internal static JObject IsolatedSelectedSource(string face, string item, string definition)
        {
            return new JObject { ["sourceKey"] = "opaque." + item + "." + face, ["itemId"] = item, ["definitionId"] = definition,
                ["abilityId"] = "entrepreneur.face." + face, ["activeFaceId"] = face, ["declarationVersion"] = "1.3.0", ["handlerHash"] = "isolated.handler",
                ["authorizedPlayerId"] = "p1", ["authorizationRef"] = "selection:select." + item, ["effectiveZoneId"] = "active", ["ruleOwnerItemId"] = item,
                ["abilityOwnerItemId"] = "carrier", ["abilityOwnerDefinitionId"] = "entrepreneur.ability.D09",
                ["selectionReceipt"] = new JObject { ["protocolVersion"] = 1, ["requestId"] = "request." + item,
                    ["ownerEffectId"] = "select." + item, ["candidateId"] = "candidate." + face, ["faceId"] = face }, ["grantingSourceRefs"] = new JArray() };
        }
        internal static JArray IsolatedTokens(int count, string carrier = "carrier", string owner = "p1", string zone = "reputation")
        {
            var result = new JArray();
            for (int i = 1; i <= count; i++) result.Add(new JObject { ["itemId"] = carrier + ".marker." + i.ToString("D3"),
                ["typeId"] = "core.player_marker", ["physicalKind"] = "Cube", ["zoneId"] = zone, ["carrierItemId"] = carrier,
                ["localZoneId"] = "reputation", ["originalSupplyZoneId"] = "supply", ["ownerSubject"] = owner, ["controller"] = owner,
                ["ownerKind"] = "Player", ["ownerPlayerId"] = owner, ["ownerCompanyId"] = "", ["controllerKind"] = "Player",
                ["controllerPlayerId"] = owner, ["controllerCompanyId"] = "", ["localState"] = new JObject(), ["localStateDigest"] = "isolated.empty",
                ["hasOpenDeparture"] = false, ["operations"] = new JArray("transfer.original_supply", "transfer.retired") });
            return result;
        }
        internal static JArray IsolatedExchangeSources(bool discount, string face = "D01")
        {
            JObject Selected(string key, string item, string definition, string selectedFace, string ability) => new JObject {
                ["sourceKey"] = key, ["itemId"] = item, ["definitionId"] = definition, ["abilityId"] = ability,
                ["activeFaceId"] = selectedFace, ["declarationVersion"] = "1.3.0", ["handlerHash"] = "isolated.handler",
                ["authorizedPlayerId"] = "p1", ["authorizationRef"] = "selection:select." + item,
                ["effectiveZoneId"] = "p1.active", ["ruleOwnerItemId"] = item,
                ["abilityOwnerItemId"] = "carrier", ["abilityOwnerDefinitionId"] = "entrepreneur.ability.D09",
                ["selectionReceipt"] = new JObject { ["protocolVersion"] = 1, ["requestId"] = "request." + item,
                    ["ownerEffectId"] = "select." + item, ["candidateId"] = "candidate." + selectedFace, ["faceId"] = selectedFace },
                ["grantingSourceRefs"] = new JArray() };
            var result = new JArray(Selected("owner", "card.1", "entrepreneur.round.D.1", face, "entrepreneur." + face + ".exchange"));
            var carrier = Selected("carrier.source", "carrier", "entrepreneur.ability.D09", "", Ability);
            carrier["selectionReceipt"] = null; carrier["authorizationRef"] = "controller:1"; result.Add(carrier);
            // 让目标落在第二页以后，验证作者确实遍历分页；这些仅是明确标记的规则隔离输入。
            for (int i = 0; i < 4; i++) result.Add(Selected("unrelated." + i, "unrelated." + i, "diy.unrelated", "other", "diy.other." + i));
            if (discount) result.Add(Selected("discount", "card.5", "entrepreneur.round.D.5", "D05", "entrepreneur.face.D05"));
            return result;
        }
        internal LuaInvocationResult ExchangeAvailability(JArray sources, int markers = 2, string player = "p1", JArray tokens = null)
        {
            var sourceItems = (JArray)sources.DeepClone();
            foreach (JObject proof in sourceItems) { proof["handlerHash"] = frozenSourceHash; proof["declarationVersion"] = handlerVersion; }
            var owner = (JObject)sourceItems[0];
            var facts = new JObject { ["source"] = new JObject { ["itemId"] = "card.1", ["abilityId"] = owner["abilityId"],
                ["persistentActive"] = true, ["markerCount"] = markers }, ["cleanup"] = new JObject() };
            var sourceFacts = new JObject { ["protocolVersion"] = 1, ["stateRevision"] = 0, ["contentHash"] = "qy014.isolated.sources",
                ["authorizedPlayerId"] = player, ["ruleOwnerSourceKey"] = "owner", ["ruleOwnerItemId"] = "card.1",
                ["sources"] = sourceItems, ["grants"] = new JArray(), ["callableActions"] = new JArray(), ["transferableTokenCount"] = (tokens ?? IsolatedTokens(markers)).Count, ["transferableTokens"] = tokens ?? IsolatedTokens(markers) };
            Artifacts.WriteLabeledText("014兑换纯规则隔离输入", Newtonsoft.Json.JsonConvert.SerializeObject(new { facts, sourceFacts,
                limitation = "用于正式Lua纯资格规则定位；手写来源快照不替代公共来源服务或真实Host正例" }));
            var context = new LuaInvocationContext("qy014.isolated.availability", "SettlementAvailability", player, 0, handlerVersion, 2, 0, 0) {
                EntryPoint = "TiagoExchangeAvailability", ResponseKind = "settlementAvailability", SourceFacts = Snapshot(sourceFacts),
                SettlementFacts = Object("source", Object("itemId", NormalizedValue.CreateString("card.1"), "abilityId", NormalizedValue.CreateString((string)owner["abilityId"]),
                    "persistentActive", NormalizedValue.CreateBoolean(true), "markerCount", NormalizedValue.CreateInteger(markers)), "cleanup", Object()),
                SettlementContext = new SettlementWindowContext { Id = "qy014.isolated.window", Kind = SettlementEntryService.PlayerCleanup,
                    OwnerEffectId = "owner", PlayerId = int.Parse(player.Substring(1)), StateRevision = 0 } };
            var result = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("entrepreneur.round.D.1", (string)owner["abilityId"],
                "entrepreneur." + (string)owner["activeFaceId"] + ".exchange.availability", handlerVersion, frozenSource, frozenSourceHash), context);
            Artifacts.WriteLabeledText("014兑换纯规则隔离结果", Newtonsoft.Json.JsonConvert.SerializeObject(new { result.Status, result.Diagnostic, result.SettlementAvailability }));
            return result;
        }

        internal static void AssertTokenCost(LuaEffectSpec transfer, int count, string destination)
        {
            Assert.That(transfer.EffectTypeId, Is.EqualTo(ItemTransferEffectExecutor.TypeId)); var args = transfer.RawArguments;
            Assert.That(Number(args, "apiVersion"), Is.EqualTo(3)); Assert.That(Number(args, "requiredCount"), Is.EqualTo(count));
            Assert.That(Text(args, "operationProfileId"), Is.EqualTo("core.player_marker_storage"));
            Assert.That(Text(Value(args, "expectedSource"), "zoneId"), Is.EqualTo("reputation"));
            Assert.That(Value(args, "itemIds").Items.Count, Is.EqualTo(count));
            Assert.That(Value(args, "itemIds").Items.Select(v => v.StringValue).Distinct().Count(), Is.EqualTo(count));
            if (destination == "retired") Assert.That(Text(Value(args, "destination"), "zoneId"), Is.EqualTo("retired"));
            else Assert.That(Text(Value(args, "destination"), "kind"), Is.EqualTo("original_supply"));
        }
        internal void AssertCommittedTokenCost(EffectNodeRuntimeState node, int count, string destination)
        {
            Assert.That(IsTokenTransfer(node), Is.True); Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed));
            var ids = Value(node.NormalizedArguments, "itemIds").Items.Select(v => v.StringValue).ToArray();
            Assert.That(ids.Length, Is.EqualTo(count)); Assert.That(ids.Distinct().Count(), Is.EqualTo(count));
            Assert.That(Number(node.NormalizedArguments, "requiredCount"), Is.EqualTo(count));
            Assert.That(Number(node.NormalizedResult, "movedCount"), Is.EqualTo(count)); Assert.That(Flag(node.NormalizedResult, "changed"), Is.True);
            Assert.That(Value(node.NormalizedResult, "movedItemIds").Items.Select(v => v.StringValue), Is.EqualTo(ids));
            var receipt = Value(node.ContinuationData, "transferBatchReceipt");
            Assert.That(Number(receipt, "protocolVersion"), Is.EqualTo(3)); Assert.That(Number(receipt, "movedCount"), Is.EqualTo(count));
            Assert.That(Text(receipt, "operationId"), Is.EqualTo(node.EffectId + ":move"));
            Assert.That(Text(receipt, "requestDigest"), Does.StartWith("yc.token-transfer.sha256.v1:"));
            Assert.That(receipt.Properties.Count, Is.EqualTo(4));
            foreach (string id in ids)
            {
                var position = node.PositionChanges.Single(c => c.ItemId == id);
                var cardZone = State.ItemZones.Zones.Single(z => z.ParentId == Carrier && z.LocalZoneId == "reputation");
                Assert.That(position.From.ZoneId, Is.EqualTo(cardZone.ZoneId));
                string target = destination == "retired" ? State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.box_retired").ZoneId :
                    State.ItemZones.Items.Single(i => i.ItemId == id).OriginalSupplyZoneId;
                Assert.That(position.To.ZoneId, Is.EqualTo(target));
                Assert.That(State.ItemZones.Items.Single(i => i.ItemId == id).ZoneId, Is.EqualTo(target));
            }
            Assert.That(State.EffectRuntime.InteractionRequests.Any(q => q.OwnerEffectId == node.EffectId), Is.False);
        }
        internal static LuaEffectSpec Investment(LuaInvocationResult invocation, bool quick = false)
        {
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            return quick ? invocation.Effects.Single().NestedEffectGroups[1].Single() : invocation.Effects.Single();
        }
        internal LuaInvocationResult Rolled(string[] faces, int[] dice, string ability = Ability)
        {
            var initial = Invoke(ability, faces); var roll = Investment(initial, ability != Ability).NestedEffectGroups[1].Single();
            LuaInvocationResult next = null;
            for (int i = 0; i < dice.Length; i++)
            {
                next = Invoke(ability, faces, roll.ContinuationContext, Object("diceResult", NormalizedValue.CreateInteger(dice[i])));
                Assert.That(next.IsSuccess, Is.True, next.Diagnostic); roll = next.Effects.Single();
            }
            Assert.That(roll.EffectTypeId, Is.EqualTo(LuaDomainEffectTypeIds.Choice));
            return Invoke(ability, faces, roll.ContinuationContext, NormalizedValue.CreateString("0"));
        }
        internal void CreateHost(int gold = 50, int markerSupply = 29)
        {
            PreparePack(); var map = StaticMapDefinitions.CreateThreePlayerMap(); var query = new MapQueryService(map);
            Registry = new EffectRegistry { ContentPackHash = Pack.ContentHash };
            ResourceEffectExecutor.Register(Registry); GenericLuaEffectExecutor.Register(Registry); ItemTransferEffectExecutor.Register(Registry);
            InfluenceEffectExecutor.Register(Registry, new InfluenceService(query)); ContentPrimitiveEffectExecutor.Register(Registry, query, Registry.InfluenceService);
            AbilitySourceLuaCatalog.Register(Registry, Pack);
            var initial = new GameState { GameId = "qy014.host.isolated", ContentPackHash = Pack.ContentHash, MapId = map.MapId,
                Round = 1, MaxRounds = 8, ActionRound = 1, CurrentPlayerId = 1, StartPlayerId = 1, Phase = GamePhase.Cleanup, LaunchPlayerOrder = new List<int> { 1, 2 },
                RuleConfiguration = new GameRuleConfiguration { RuleModeId = "standard", MapId = map.MapId, PlayerCount = 2, ContentPackHash = Pack.ContentHash, PlayerStageSchedulingMode = PlayerStageSchedulingMode.Sequential, EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Name = "隔离甲", Resources = new ResourceSet { GoldVoucher = gold } }, new PlayerState { PlayerId = 2, Name = "隔离乙" } },
                ContentDefinitions = new GameContentDefinitions { AbilitySources = Registry.AbilityDeclarations.ToList(), Facilities = Pack.CreateFacilities().ToList(), Characters = Pack.CreateCharacters().ToList(),
                    UsageTracks = Pack.ActiveDefinitions.SelectMany(d => d.UsageTracks).ToList(), Awards = ContentLifecycleLuaCatalog.CreateAwards(Pack), CityStyles = Pack.CreateCityStyles().ToList(), SpecialActions = Pack.CreateSpecialActions().ToList(),
                    Entrepreneurs = EntrepreneurContentCatalog.Read(Pack.Definitions), Components = Pack.Definitions.Where(d => d.ContentType == "component").Select(d => new ComponentContentDefinition {
                        DefinitionId = d.DefinitionId, TypeId = d.ItemTypeId, QuantityPolicy = (string)d.Data["quantityPolicy"], CubeAppearance = d.CubeAppearance }).ToList() } };
            initial.RandomRuntime = GameplayRandomRuntime.Create(initial.GameId);
            var recipe = new List<ItemRecipeEntry> {
                new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = markerSupply, PerPlayer = true, InitialZoneRole = "supply" },
                new ItemRecipeEntry { EntryId = "ability", DefinitionId = "entrepreneur.ability.D09", TypeId = "entrepreneur.ability_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "entrepreneur_active" },
                new ItemRecipeEntry { EntryId = "dice", DefinitionId = "entrepreneur.component.die", TypeId = "core.die", Quantity = 3, PerPlayer = true, InitialZoneRole = "supply" } };
            State = new ItemZoneStateFactory().CreateForSetup(initial, map, recipe, Pack.Definitions.Where(d => d.CardZones.Count > 0).Select(d => new ItemCardZoneDeclaration { DefinitionId = d.DefinitionId, CardZones = d.CardZones }).ToList());
            State.RuleConfiguration.RuleModeId = "entrepreneur";
            Carrier = State.ItemZones.Items.Single(i => i.DefinitionId == "entrepreneur.ability.D09" && i.OwnerSubject.PlayerId == 1).ItemId;
            Registry.ContentDefinitions = GameStateCloneService.DeepClone(State.ContentDefinitions);
            Rounds = new RoundExecutionService(Registry);
            var chain = new RoundMainChainBuilder().Build(State.GameId, "", 1, new List<int> { 1, 2 }, 1);
            var main = chain.Nodes.Single(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow && n.PlayerId == 1);
            State.EffectRuntime.MainNodes.AddRange(chain.Nodes);
            foreach (var previous in chain.Nodes.Where(n => n.MainlineIndex < main.MainlineIndex)) previous.Status = EffectNodeStatus.Completed;
            State.EffectRuntime.ActiveMainNodeId = main.NodeId; State.EffectRuntime.FirstMainNodeId = chain.FirstNodeId; State.EffectRuntime.CurrentRoundExecutionId = chain.RoundExecutionId;
            State.EffectRuntime.CurrentRoundNumber = 1; State.EffectRuntime.CurrentRoundStartPlayerId = 1; State.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            main.ExecutionEffectId = Engine.CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId, Object("mainNodeId", NormalizedValue.CreateString(main.NodeId)), 1)); main.Status = EffectNodeStatus.Running; Drain();
        }
        internal void CreateSelectedHost(string[] faces, int initialReputation = 0, ResourceSet startingResources = null)
        {
            PreparePack(); var map = StaticMapDefinitions.CreateThreePlayerMap(); var query = new MapQueryService(map);
            Registry = new EffectRegistry { ContentPackHash = Pack.ContentHash };
            ResourceEffectExecutor.Register(Registry); GenericLuaEffectExecutor.Register(Registry); ItemTransferEffectExecutor.Register(Registry);
            InfluenceEffectExecutor.Register(Registry, new InfluenceService(query)); ContentPrimitiveEffectExecutor.Register(Registry, query, Registry.InfluenceService);
            AbilitySourceLuaCatalog.Register(Registry, Pack); ExpansionSetupFlow.Register(Registry);
            EnterpriseEffectExecutor.Register(Registry, LuaContentCatalog.CreateEnterpriseDefinitions(Registry, Pack.ActiveDefinitions, Pack.GetScript, Pack));
            var definitions = GameContentDefinitions.CreateRegisteredStandard();
            definitions.AbilitySources = Registry.AbilityDeclarations.ToList(); definitions.Entrepreneurs = EntrepreneurContentCatalog.Read(Pack.Definitions);
            definitions.Facilities = Pack.CreateFacilities().ToList(); definitions.Characters = Pack.CreateCharacters().ToList();
            definitions.UsageTracks = Pack.ActiveDefinitions.SelectMany(d => d.UsageTracks).ToList();
            definitions.Awards = ContentLifecycleLuaCatalog.CreateAwards(Pack); definitions.CityStyles = Pack.CreateCityStyles().ToList();
            definitions.SpecialActions = Pack.CreateSpecialActions().ToList();
            definitions.Components = Pack.Definitions.Where(d => d.ContentType == "component").Select(d => new ComponentContentDefinition {
                DefinitionId = d.DefinitionId, TypeId = d.ItemTypeId, QuantityPolicy = (string)d.Data["quantityPolicy"], CubeAppearance = d.CubeAppearance }).ToList();
            var recipe = GameSetupRecipe.CreateRegisteredStandard(map.MapId); recipe.EnterpriseEntrance = true;
            recipe.Entries.AddRange(GameStateCloneService.DeepClone(Pack.SetupRecipes.Single(r => r.RecipeId == "entrepreneur").Entries));
            foreach (var enterprise in Pack.ActiveDefinitions.Where(d => d.ContentType == "enterprise"))
                recipe.Add(enterprise.DefinitionId, enterprise.DefinitionId, "core.board_card", "reserve");
            recipe.CardZoneDeclarations = Pack.ActiveDefinitions.Where(d => d.CardZones.Count > 0).Select(d => new ItemCardZoneDeclaration {
                DefinitionId = d.DefinitionId, CardZones = d.CardZones }).ToList();
            var config = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = Pack.ContentHash, PlayerStageSchedulingMode = PlayerStageSchedulingMode.Sequential };
            State = GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1, new List<PlayerSeat> {
                new PlayerSeat { PlayerId = 1, PlayerName = "隔离甲" }, new PlayerSeat { PlayerId = 2, PlayerName = "隔离乙" } },
                config, 47, recipe, definitions, new List<int> { 1, 2 }, "qy014.formal-source.isolated", null);
            // 以下均为第一条规则Journal之前的隔离库存基线；不是正常开局或Lua取得声望的证明。
            State.RuleConfiguration.RuleModeId = "entrepreneur";
            State.RuleConfiguration.EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" };
            State.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            State.FindPlayer(1).Resources = startingResources?.Clone() ?? new ResourceSet { GoldVoucher = 200, Originium = 10, OriginiumShard = 10, Iron = 10 };
            string reservedAbility = State.ItemZones.Items.Single(i => i.DefinitionId == "entrepreneur.ability.D09").ItemId;
            var reputation = State.ItemZones.Zones.Single(z => z.ParentId == reservedAbility && z.LocalZoneId == "reputation");
            var stock = PlayerAreaPolicy.Supply(State, 1, "core.player_marker").Take(initialReputation).ToList();
            Assert.That(stock.Count, Is.EqualTo(initialReputation));
            foreach (var marker in stock)
            {
                var item = State.ItemZones.Items.Single(i => i.ItemId == marker.ItemId);
                State.ItemZones.Orders.Find(o => o.ZoneId == item.ZoneId)?.ItemIds.Remove(item.ItemId);
                item.ZoneId = reputation.ZoneId; item.SlotId = "";
                var order = State.ItemZones.Orders.Find(o => o.ZoneId == reputation.ZoneId);
                if (order != null) order.ItemIds.Add(item.ItemId);
            }
            Registry.ContentDefinitions = GameStateCloneService.DeepClone(definitions);
            Artifacts.WriteLabeledText("014选面测试初始隔离基线", Newtonsoft.Json.JsonConvert.SerializeObject(new { initialReputation, State,
                limitation = "预置库存仅用于隔离边界；后续选面/行动/付款/随机/恢复全部走真实Host，正常覆盖为0" }));
            Rounds = new RoundExecutionService(Registry);
            Engine.CreateRoot(ExpansionSetupFlow.PreparationSpec(1, false)); Drain();
            for (int step = 0; step < 40 && State.EffectRuntime.InteractionRequests.Any(q => q.Status == "open"); step++)
            {
                var request = State.EffectRuntime.InteractionRequests.First(q => q.Status == "open");
                string choice;
                if (request.InteractionTypeId == EntrepreneurFlow.PreviewInteraction) choice = "continue";
                else if (request.InteractionTypeId == EntrepreneurFlow.ChooseInteraction)
                    choice = request.AnsweringPlayerId == 1 ? "entrepreneur.ability.D09" : "entrepreneur.ability.T09";
                else
                {
                    Assert.That(request.InteractionTypeId, Is.EqualTo(ExpansionSetupFlow.EnterpriseChoiceInteraction));
                    choice = request.CandidateIds.First();
                }
                AnswerSetup(request, choice);
            }
            Assert.That(State.EntrepreneurSetup.Stage, Is.EqualTo("complete"));
            Assert.That(State.EffectRuntime.InteractionRequests.Any(q => q.Status == "open"), Is.False);
            Carrier = State.EntrepreneurSetup.Players.Single(p => p.PlayerId == 1).AbilityItemId;
            foreach (string face in faces)
            {
                var own = State.EntrepreneurSetup.Players.Single(p => p.PlayerId == 1);
                int round = own.RoundGroups.Single(g => g.Candidates.Any(c => c.FaceId == face)).RoundNumber;
                State.Round = round; // 隔离回合前置；不冒充正常回合推进。
                Engine.CreateRoot(ExpansionSetupFlow.RoundPreparationSpec(1)); Drain();
                for (int step = 0; step < 80 && State.EffectRuntime.InteractionRequests.Any(q => q.Status == "open"); step++)
                {
                    var request = State.EffectRuntime.InteractionRequests.First(q => q.Status == "open");
                    if (request.InteractionTypeId == ExpansionSetupFlow.SupplyRetirementInteraction) AnswerSetup(request, request.CandidateIds.First());
                    else if (request.InteractionTypeId == EntrepreneurFlow.FaceInteraction)
                    {
                        Assert.That(request.InteractionTypeId, Is.EqualTo(EntrepreneurFlow.FaceInteraction));
                        var player = State.EntrepreneurSetup.Players.Single(p => p.PlayerId == request.AnsweringPlayerId);
                        var group = player.RoundGroups.Single(g => g.RoundNumber == round);
                        string candidate = request.AnsweringPlayerId == 1 ? group.Candidates.Single(c => c.FaceId == face).CandidateId : group.Candidates.First().CandidateId;
                        AnswerSetup(request, candidate);
                    }
                    else AnswerSetup(request, request.CandidateIds.First());
                }
                Assert.That(State.EffectRuntime.InteractionRequests.Any(q => q.Status == "open"), Is.False);
                var selected = State.EntrepreneurSetup.Players.Single(p => p.PlayerId == 1).RoundGroups.Single(g => g.RoundNumber == round);
                var card = State.ItemZones.Items.Single(i => i.ItemId == selected.Candidates.Single(c => c.CandidateId == selected.SelectedCandidateId).EntityItemId);
                Assert.That(AbilitySourceSelection.HasSelectedFaceReceipt(State, card, face, 1), Is.True);
                Artifacts.WriteLabeledText("014正式选面收据-" + face, Newtonsoft.Json.JsonConvert.SerializeObject(new { selected, card.ItemId,
                    sources = Registry.QuerySources(State, 1).Where(x => x.ItemId == card.ItemId).ToList() }));
            }
        }
        private void AnswerSetup(InteractionRequest request, string answer)
        {
            string id = request.GetStableInteractionId(); RoundExecutionService.RefreshConcurrentInteractionRevisions(State);
            var current = State.EffectRuntime.InteractionRequests.Single(q => q.GetStableInteractionId() == id);
            var selected = current.InteractionTypeId == ExpansionSetupFlow.EnterpriseChoiceInteraction
                ? current.CandidateIds.Take(current.MinSelections).ToArray() : new[] { answer };
            var value = current.AnswerSchema == "candidate_ids" || current.AnswerSchema == "candidate_id_array"
                ? Strings(selected) : NormalizedValue.CreateString(selected.Single());
            bool accepted = Engine.TrySubmitInteraction(id, current.AnsweringPlayerId, current.StateRevision, value, out string reason);
            if (!accepted) Artifacts.WriteLabeledText("准备交互真实拒绝现场", Newtonsoft.Json.JsonConvert.SerializeObject(State));
            Assert.That(accepted, Is.True, reason); Drain();
        }
        internal void OpenSelectedWindow(bool cleanup)
        {
            State.Phase = cleanup ? GamePhase.Cleanup : GamePhase.ActionRound1; State.ActionRound = 1; State.CurrentPlayerId = 1; State.StartPlayerId = 1;
            var chain = new RoundMainChainBuilder().Build(State.GameId, "", State.Round, new List<int> { 1, 2 }, 1, false, PlayerStageSchedulingMode.Sequential);
            Assert.That(State.EffectRuntime.MainNodes, Is.Empty, "每个隔离用例只建立一个待验窗口");
            State.EffectRuntime.MainNodes.AddRange(chain.Nodes);
            var main = chain.Nodes.Single(n => n.NodeTypeId == (cleanup ? RoundMainlineNodeTypeIds.PlayerCleanupWindow : RoundMainlineNodeTypeIds.PlayerActionWindow)
                && n.PlayerId == 1 && (cleanup || n.ActionRound == 1));
            foreach (var previous in chain.Nodes.Where(n => n.MainlineIndex < main.MainlineIndex)) previous.Status = EffectNodeStatus.Completed;
            State.EffectRuntime.ActiveMainNodeId = main.NodeId; State.EffectRuntime.FirstMainNodeId = chain.FirstNodeId;
            State.EffectRuntime.CurrentRoundExecutionId = chain.RoundExecutionId; State.EffectRuntime.CurrentRoundNumber = State.Round;
            State.EffectRuntime.CurrentRoundStartPlayerId = 1; State.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            main.ExecutionEffectId = Engine.CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId, Object("mainNodeId", NormalizedValue.CreateString(main.NodeId)), 1));
            main.Status = EffectNodeStatus.Running; Drain();
        }
        internal SettlementWindowRuntimeState CleanupWindow => State.EffectRuntime.EffectNodes.Single(n => n.SettlementWindow != null &&
            n.SettlementWindow.Context.PlayerId == 1 && !n.SettlementWindow.IsClosed).SettlementWindow;
        internal CharacterSettlementOption ExchangeOption => Registry.GetSettlementOptions(State, 1).Single(o => o.CleanupRequest != null && o.SourceItemId != Carrier);
        internal string StartExchange(string command)
        {
            var option = ExchangeOption;
            Assert.That(option.IsEnabled, Is.True, option.DisabledReasonCode);
            Assert.That(Engine.TryInvokeCleanupSource(1, command, option.CleanupRequest, out bool replay, out string id, out string reason), Is.True, reason);
            Assert.That(replay, Is.False); Drain(); return id;
        }
        internal string StartQuick(string ability, string command)
        {
            var option = Registry.QueryActions(State, 1).Single(o => o.ActionId == ability);
            Assert.That(option.Enabled, Is.True, option.DisabledReason);
            Assert.That(Engine.TryInvokeAbilityAction(1, option.OptionId, option.RequestRef.InteractionId, option.RequestRef.FactsDigest,
                option.VersionRef.ContentPackHash, command, Object(), out string id, out string reason), Is.True, reason); Drain(); return id;
        }
        internal int Reputation => State.ItemZones.Items.Count(i => State.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.ParentId == Carrier && z.LocalZoneId == "reputation"));
        internal void Confirm(bool accept)
        {
            var q = Open;
            var answer = q.AnswerSchema == "candidate_id" || q.AnswerSchema == "candidate_id_array" || q.AnswerSchema == "candidate_ids"
                ? NormalizedValue.CreateString(accept ? (q.CandidateIds.Contains("continue") ? "continue" : "pay") : "decline")
                : NormalizedValue.CreateBoolean(accept);
            Assert.That(Engine.TrySubmitInteraction(q.GetStableInteractionId(), 1, q.StateRevision,
                answer, out string reason), Is.True, reason); Drain();
            Assert.That(State.EffectRuntime.InteractionRequests.Where(r => r.Status == "open").Any(r =>
                IsTokenTransfer(Engine.GetNode(r.OwnerEffectId))), Is.False, "通用token费用不产生实例选择页");
        }
        internal static bool IsTokenTransfer(EffectNodeRuntimeState node) => node.EffectTypeId == ItemTransferEffectExecutor.TypeId && Number(node.NormalizedArguments, "apiVersion") == 3;

        internal void StartInvestment()
        {
            var option = Registry.GetSettlementOptions(State, 1).Single(o => o.SourceItemId == Carrier);
            Assert.That(Engine.TryInvokeCharacterSettlementOption(1, option.OptionId, "", option.StateRevision, out _, out string reason), Is.True, reason); Drain();
        }
        internal InteractionRequest Open => State.EffectRuntime.InteractionRequests.Last(r => r.Status == "open" && r.AnsweringPlayerId == 1);
        internal void Answer(string answer, bool recoverRolls = false)
        {
            var q = Open; Assert.That(Engine.TrySubmitInteraction(q.GetStableInteractionId(), 1, q.StateRevision,
                NormalizedValue.CreateString(answer), out string reason), Is.True, reason);
            if (recoverRolls) AdvanceSavingEachRoll(); else Drain();
        }
        private void AdvanceSavingEachRoll()
        {
            for (int step = 0; step < 2000; step++)
            {
                bool advanced = Engine.Advance();
                foreach (var roll in State.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == LuaDomainEffectTypeIds.RollDice && Number(n.NormalizedResult,"diceResult") >= 1 && Number(n.NormalizedResult,"diceResult") <= 6).ToList())
                    if (recoveredRolls.Add(roll.EffectId))
                    {
                        string random = Newtonsoft.Json.JsonConvert.SerializeObject(State.RandomRuntime);
                        string result = roll.NormalizedResult.ToDeterministicString();
                        Restore("真实第" + recoveredRolls.Count + "骰提交后尚未下一骰");
                        Assert.That(Engine.GetNode(roll.EffectId).NormalizedResult.ToDeterministicString(), Is.EqualTo(result));
                        Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(State.RandomRuntime), Is.EqualTo(random));
                    }
                if (!advanced) { Drain(); return; }
            }
            Assert.Fail("逐骰隔离推进超过有限步数");
        }
        internal void Drain()
        { var r = Engine.RunUntilQuiescent(); if (r.Faulted) Artifacts.WriteLabeledText("014首次故障", Newtonsoft.Json.JsonConvert.SerializeObject(State)); Assert.That(r.Faulted, Is.False, State.EffectRuntime.LastFaultMessage); RoundExecutionService.RefreshConcurrentInteractionRevisions(State); }
        internal void Restore(string label)
        { var recovered = Artifacts.Recover(State, label, Registry); Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic); State = recovered.State; }
        internal void ToPointChoice(int reputation = 0, bool recoverEachRoll = false)
        {
            for (int i = 0; i < 12; i++)
            {
                var q = Open; var owner = Engine.GetNode(q.OwnerEffectId);
                if (q.CandidateIds.Contains("continue")) Answer("continue", recoverEachRoll);
                else if (owner.Payment != null) Answer(PaymentResources.Candidate(owner.Payment.PaymentCost), recoverEachRoll);
                else if (owner.EffectTypeId == LuaDomainEffectTypeIds.Choice && Text(owner.ContinuationContext, "stage") == "reputation_selected") Answer(reputation.ToString(System.Globalization.CultureInfo.InvariantCulture), recoverEachRoll);
                else { Assert.That(q.CandidateIds.All(s => int.TryParse(s, out var n) && n >= 0 && n <= 7), Is.True); return; }
            }
            Assert.Fail("未到达正式点数请求");
        }
        public void Dispose()
        {
            if (State != null) Artifacts.WriteLabeledText("014最终真实状态", Newtonsoft.Json.JsonConvert.SerializeObject(State));
            Artifacts.Finish();
        }
    }
}
