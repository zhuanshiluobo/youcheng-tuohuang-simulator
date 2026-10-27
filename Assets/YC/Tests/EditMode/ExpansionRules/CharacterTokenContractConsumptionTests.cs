using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
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
    // QY-007 R2独占。快照用例验证真实Lua规则；下方正式装配用例使用隔离GameState。
    // 不复制/改造manifest，不运行正常对局，不把隔离输入或源码用例数当运行通过。
    public sealed class CharacterTokenContractConsumptionTests
    {
        private static IsolatedTestArtifacts artifacts;
        private static GameState observedState;
        [SetUp] public void BeginEvidence() { observedState = null; artifacts = IsolatedTestArtifacts.BeginCase(); }
        [TearDown] public void EndEvidence()
        {
            if (observedState != null) artifacts.WriteLabeledText("007实际Host状态与请求", Newtonsoft.Json.JsonConvert.SerializeObject(observedState));
            artifacts.Finish(); artifacts = null; observedState = null;
        }
        private const string Card = "qy007.dorothy", Storage = "qy007.markers", Hash = "qy007.source.hash", Scope = "opaque.qy007.scope";
        private const int Revision = 23;
        private static readonly string Root = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
        private static LuaSnapshotValue S(string value) => LuaSnapshotValue.String(value);
        private static LuaSnapshotValue N(int value) => LuaSnapshotValue.Integer(value);
        private static LuaSnapshotValue B(bool value) => LuaSnapshotValue.Boolean(value);
        private static LuaSnapshotValue A(params LuaSnapshotValue[] values) => LuaSnapshotValue.Array(values);
        private static LuaSnapshotEntry F(string key, LuaSnapshotValue value) => new LuaSnapshotEntry(key, value);
        private static LuaSnapshotValue O(params LuaSnapshotEntry[] fields) => LuaSnapshotValue.Object(fields);
        private static LuaSnapshotValue Snapshot(NormalizedValue value)
        {
            if (value == null || value.Kind == NormalizedValueKind.Null) return LuaSnapshotValue.Null;
            switch (value.Kind)
            {
                case NormalizedValueKind.String: return S(value.StringValue);
                case NormalizedValueKind.Integer: return LuaSnapshotValue.Integer(value.IntegerValue);
                case NormalizedValueKind.Boolean: return B(value.BooleanValue);
                case NormalizedValueKind.Array: return A(value.Items.Select(Snapshot).ToArray());
                case NormalizedValueKind.Object: return O(value.Properties.Select(v => F(v.Name, Snapshot(v.Value))).ToArray());
                default: throw new InvalidOperationException("夹具未声明的续接值种类：" + value.Kind);
            }
        }
        private static LuaSnapshotValue Token(string id, string type = "core.player_marker", string owner = "p1",
            string supply = "qy007.supply.p1", string digest = "empty", string controller = "p1", bool departure = false,
            string carrier = Card, string zone = Storage) => O(
                F("itemId", S(id)), F("typeId", S(type)), F("physicalKind", S("Cube")), F("zoneId", S(zone)),
                F("carrierItemId", S(carrier)), F("localZoneId", S("markers")), F("originalSupplyZoneId", S(supply)),
                F("ownerSubject", S(owner)), F("controller", S(controller)), F("ownerKind", S("Player")),
                F("ownerPlayerId", S(owner)), F("ownerCompanyId", S("")), F("controllerKind", S("Player")),
                F("controllerPlayerId", S(controller)), F("controllerCompanyId", S("")),
                F("localState", digest == "empty" ? O() : O(F("amount", N(2)))), F("localStateDigest", S(digest)),
                F("hasOpenDeparture", B(departure)), F("operations", A(S("transfer.original_supply"))));
        private static LuaSnapshotValue Content() => O(F("zones", A(O(F("id", S(Storage)),
            F("templateId", S("core.zone.card_cube_storage")), F("parentId", S(Card)), F("localZoneId", S("markers"))))));
        private static LuaSnapshotValue Facts(LuaSnapshotValue[] tokens, string scope = Scope, string hash = Hash) => O(
            F("protocolVersion", N(1)), F("stateRevision", N(Revision)), F("authorizedPlayerId", S("p1")),
            F("ruleOwnerSourceKey", S(scope)), F("ruleOwnerItemId", S(Card)), F("contentHash", S(hash)),
            F("transferableTokenCount", N(tokens.Length)), F("transferableTokens", A(tokens)));
        private static LuaInvocationResult Invoke(string role, string eventType, LuaSnapshotValue payload,
            LuaSnapshotValue content, LuaSnapshotValue facts = null)
        {
            var definition = JObject.Parse(File.ReadAllText(Path.Combine(Root, "character", role + ".json")));
            var ability = definition["abilities"].Single(a => (string)a["mode"] == "tactic");
            string code = File.ReadAllText(Path.Combine(Root, (string)ability["script"])), version = (string)ability["version"];
            var snapshot = LuaFacadeSnapshot.FromInvocation("p1", 0, 0, 3, version);
            snapshot.GlobalContent = content; snapshot.EventPayload = payload;
            artifacts.WriteLabeledText("007实际Lua输入", Newtonsoft.Json.JsonConvert.SerializeObject(new { role, eventType, payload, content, facts,
                script = (string)ability["script"], version, scriptSHA256 = LuaContentHasher.ComputeSha256(code) }));
            var result = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition(role, (string)ability["abilityId"],
                (string)ability["subscriptionId"], version, code, LuaContentHasher.ComputeSha256(code)),
                new LuaInvocationContext("qy007.r2.snapshot", eventType, "p1", Revision, version, 3, 0, 0, snapshot)
                { SourceFacts = facts ?? LuaSnapshotValue.Null });
            artifacts.WriteLabeledText("007实际Lua输出", Newtonsoft.Json.JsonConvert.SerializeObject(result));
            return result;
        }
        private static LuaEffectSpec Only(LuaInvocationResult result)
        {
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(result.Effects.Count, Is.EqualTo(1)); return result.Effects.Single();
        }
        private static LuaEffectSpec Cleanup(params LuaSnapshotValue[] tokens) => Only(Invoke("operator.dorothy",
            "PlayerCleanupStarted", O(F("cardInstanceRef", S(Card))), Content(), Facts(tokens)));
        private static LuaEffectSpec Left(LuaEffectSpec condition)
        {
            Assert.That(condition.EffectTypeId, Is.EqualTo(EffectTypeIds.Condition));
            Assert.That(condition.NestedEffectGroups[0].Count, Is.EqualTo(1));
            Assert.That(condition.NestedEffectGroups[1].Count, Is.EqualTo(1));
            return condition.NestedEffectGroups[0].Single();
        }
        private static string[] Ids(LuaEffectSpec spec, string field) => Value(spec.RawArguments, field).Items.Select(v => v.StringValue).ToArray();
        private static void ExactTransfer(LuaEffectSpec spec, params string[] ids)
        {
            Assert.That(spec.EffectTypeId, Is.EqualTo(ItemTransferEffectExecutor.TypeId));
            Assert.That(Number(spec.RawArguments, "apiVersion"), Is.EqualTo(3));
            Assert.That(Number(spec.RawArguments, "requiredCount"), Is.EqualTo(2));
            Assert.That(Text(spec.RawArguments, "operationProfileId"), Is.EqualTo("core.cube_storage"));
            Assert.That(Text(Value(spec.RawArguments, "expectedSource"), "zoneId"), Is.EqualTo(Storage));
            Assert.That(Text(Value(spec.RawArguments, "destination"), "kind"), Is.EqualTo("original_supply"));
            Assert.That(Ids(spec, "itemIds"), Is.EqualTo(ids));
        }
        private static LuaInvocationResult Answer(LuaEffectSpec choice, LuaSnapshotValue[] tokens, string[] ids,
            string scope = Scope, string hash = Hash, string outcome = "completed") => Invoke("operator.dorothy", "EffectCompleted",
                O(F("continuationContext", Snapshot(choice.ContinuationContext)), F("outcome", S(outcome)),
                    F("normalizedResult", A(ids.Select(S).ToArray()))), Content(), Facts(tokens, scope, hash));

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(7)]
        public void EquivalentInventoryHasOneExactTransferAndShortfallNeverBecomesAnEmptyLeftSide(int count)
        {
            var tokens = Enumerable.Range(0, count).Select(i => Token("p1." + i)).ToArray();
            var condition = Cleanup(tokens); ExactTransfer(Left(condition), tokens.Take(2).Select(t => t.Get("itemId").StringValue).ToArray());
            Assert.That(condition.NestedEffectGroups[1].Single().EffectTypeId, Is.EqualTo(InfluenceEffectTypeIds.PlaceInfluence));
        }
        [Test]
        public void AMeaningfulDifferenceOnTheThirdPageIsKeptWhileEquivalentDuplicatesAreBounded()
        {
            var tokens = Enumerable.Range(0, 8).Select(i => Token("p1." + i)).Concat(new[] {
                Token("z.gang", "entrepreneur.gang_marker", supply: "qy007.gang.reserve") }).ToArray();
            var choice = Left(Cleanup(tokens));
            Assert.That(choice.EffectTypeId, Is.EqualTo(LuaDomainEffectTypeIds.Choice));
            Assert.That(Number(choice.RawArguments, "minSelections"), Is.EqualTo(2));
            Assert.That(Number(choice.RawArguments, "maxSelections"), Is.EqualTo(2));
            Assert.That(Ids(choice, "options"), Is.EqualTo(new[] { "p1.0", "p1.1", "z.gang" }));
        }
        [TestCase("type")] [TestCase("owner")] [TestCase("supply")] [TestCase("state")]
        [TestCase("controller")] [TestCase("departure")]
        public void RuleRelevantDifferencesKeepALuaChoice(string difference)
        {
            var other = Token("c", type: difference == "type" ? "entrepreneur.gang_marker" : "core.player_marker",
                owner: difference == "owner" ? "p2" : "p1", supply: difference == "supply" ? "qy007.other.supply" : "qy007.supply.p1",
                digest: difference == "state" ? "changed" : "empty", controller: difference == "controller" ? "p2" : "p1",
                departure: difference == "departure");
            Assert.That(Left(Cleanup(Token("a"), Token("b"), other)).EffectTypeId, Is.EqualTo(LuaDomainEffectTypeIds.Choice));
        }
        [Test]
        public void ExactlyTwoHeterogeneousTokensHaveOnlyOneCombinationAndNoChoice()
        {
            ExactTransfer(Left(Cleanup(Token("a", owner: "p2", supply: "qy007.supply.p2"),
                Token("b", "entrepreneur.gang_marker", supply: "qy007.gang.reserve"))), "a", "b");
        }
        [Test]
        public void SelectedMixedTokensProduceOneBatchAndNoPrematurePlacement()
        {
            var tokens = new[] { Token("a"), Token("b"), Token("c", "entrepreneur.gang_marker", supply: "qy007.gang.reserve") };
            var choice = Left(Cleanup(tokens));
            ExactTransfer(Only(Answer(choice, tokens, new[] { "c", "a" })), "c", "a");
        }
        [TestCase("missing", "dorothy_token_selection_changed")]
        [TestCase("duplicate", "dorothy_token_selection_invalid")]
        [TestCase("scope", "dorothy_source_changed")]
        [TestCase("hash", "source_facts_content_mismatch")]
        public void InvalidContinuationCannotReplaceTheChosenTokensOrConstructATransfer(string damage, string diagnostic)
        {
            var tokens = new[] { Token("a"), Token("b"), Token("c", owner: "p2", supply: "qy007.supply.p2") };
            var choice = Left(Cleanup(tokens));
            var result = Answer(choice, damage == "missing" ? tokens.Take(2).ToArray() : tokens,
                damage == "duplicate" ? new[] { "a", "a" } : new[] { "a", "c" },
                scope: damage == "scope" ? "different.source" : Scope, hash: damage == "hash" ? "different.hash" : Hash);
            Assert.That(result.IsSuccess, Is.False); Assert.That(result.Diagnostic, Does.Contain(diagnostic));
            Assert.That(result.Effects, Is.Empty);
        }
        [Test]
        public void OtherCarriersCannotFillThisCardsShortfall()
        {
            ExactTransfer(Left(Cleanup(Token("a"), Token("b", carrier: "other.card", zone: "other.zone"),
                Token("c", carrier: "other.card", zone: "other.zone"))), "a");
        }
        [Test]
        public void FailedContinuationDoesNotFallBackToLoadOrPlace()
        {
            var tokens = new[] { Token("a"), Token("b"), Token("c", owner: "p2") };
            var result = Answer(Left(Cleanup(tokens)), tokens, new[] { "a", "c" }, outcome: "failed");
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(result.Effects, Is.Empty);
        }
        [TestCase(1, false)] [TestCase(2, true)]
        public void DorothyAvailabilityUsesAuthorizedTransferableCount(int count, bool enabled)
        {
            string code = File.ReadAllText(Path.Combine(Root, "lua/settlements/operator.dorothy.tactic.lua"));
            var invocation = new LuaInvocationContext("qy007.availability", "Settlement", "p1", Revision, "2.0.0", 2, 0, 0) {
                EntryPoint = "availability", ResponseKind = "settlementAvailability",
                SettlementContext = new SettlementWindowContext { Id = "qy007.window", Kind = "player_cleanup", OwnerEffectId = "qy007.cleanup", PlayerId = 1, StateRevision = Revision },
                SettlementFacts = Object("source", Object("persistentActive", NormalizedValue.CreateBoolean(true),
                    "transferableTokenCount", NormalizedValue.CreateInteger(count), "markerCount", NormalizedValue.CreateInteger(99))) };
            var result = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("operator.dorothy", "operator.dorothy.tactic",
                "operator.dorothy.tactic.settlement.availability", "2.0.0", code, LuaContentHasher.ComputeSha256(code)), invocation);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(result.SettlementAvailability.Enabled, Is.EqualTo(enabled));
        }
        [TestCase("p2")] [TestCase("p3")]
        public void SwireReturnReceiptKeepsTheSelectedOriginalOwnerAsTheUpgradeBeneficiary(string originalOwner)
        {
            var context = O(F("source", S("qy007.swire")), F("stage", S("marker_returned")));
            var gained = Only(Invoke("infected.character.swire", "EffectCompleted", O(F("continuationContext", context),
                F("outcome", S("completed")), F("normalizedResult", O(F("markerItemId", S("marker." + originalOwner)),
                    F("originalOwner", S(originalOwner))))), O()));
            Assert.That(gained.Amount, Is.EqualTo(6));
            Assert.That(Text(gained.ContinuationContext, "originalOwner"), Is.EqualTo(originalOwner));
            var enterpriseContext = O(F("source", S("qy007.swire")), F("stage", S("self_upgraded")), F("originalOwner", S(originalOwner)), F("upgradeCandidates", A(S("qy007.enterprise"))));
            var upgrades = Invoke("infected.character.swire", "EffectCompleted", O(F("continuationContext", enterpriseContext),
                F("outcome", S("completed")), F("normalizedResult", O(F("selectedEnterpriseItemId", S("qy007.enterprise"))))), O());
            Assert.That(upgrades.IsSuccess, Is.True, upgrades.Diagnostic); Assert.That(upgrades.Effects.Count, Is.EqualTo(1));
            Assert.That(Text(upgrades.Effects[0].RawArguments, "targetPlayer"), Is.EqualTo(originalOwner.Substring(1)), "目标玩家须匹配原主；原语参数已经规范化为十进制玩家ID。");
        }

        [TestCase(false)] [TestCase(true)]
        public void ActualRegisteredDorothyCleanupMovesOneBatchBeforeOpeningPlacement(bool mixed)
            => RunRegisteredDorothyCleanup(mixed);

        // 64由005在原真实入口上增加恢复/授权观测；不改007的Lua和必要选择。
        [TestCase("missing_binding")][TestCase("wrong_handler")][TestCase("wrong_hash")][TestCase("wrong_trigger")]
        [TestCase("wrong_source")][TestCase("forged_grant")][TestCase("revoked_subscription")][TestCase("source_left")]
        public void PersistentChoiceRecoveryChecksProvenanceAndCurrentSourceSeparately(string damage)
            => RunRegisteredDorothyCleanup(true, damage);

        // 66/P01：直接修改真实Host JSON，避免typed往返把缺省版本补回1。
        [TestCase("original")][TestCase("missing_version")][TestCase("string_version")][TestCase("version_2")]
        public void PersistentChoiceRawProtocolIsCheckedBeforeDeserialization(string variant)
            => RunRegisteredDorothyCleanup(true, "raw:" + variant);

        private void RunRegisteredDorothyCleanup(bool mixed, string damage = "")
        {
            // 使用正式活动目录/角色声明与真实调用链；配方和库存布置仅为隔离规则定位。
            var map = StaticMapDefinitions.CreateThreePlayerMap();
            var configuration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2,
                EnabledExpansionIds = new List<string> { "core", "enterprise" } };
            var pack = ExternalContentPack.Load(Root).CreateActiveSnapshot(configuration); configuration.ContentPackHash = pack.ContentHash;
            var registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry);
            ItemTransferEffectExecutor.Register(registry); CharacterCoverEffectExecutor.Register(registry); CharacterLifecycleService.Register(registry);
            var query = new MapQueryService(map); var influence = new InfluenceService(query);
            InfluenceEffectExecutor.Register(registry, influence); ContentPrimitiveEffectExecutor.Register(registry, query, influence);
            CharacterCardLuaCatalog.Register(registry, pack); CityStyleLuaCatalog.Register(registry, pack);
            ContentLifecycleLuaCatalog.Register(registry, pack); AbilitySourceLuaCatalog.Register(registry, pack);
            var state = new ItemZoneStateFactory().CreateForSetup(new GameState {
                GameId = "qy007.dorothy.r2.isolated", MapId = map.MapId, ContentPackHash = pack.ContentHash, RuleConfiguration = configuration,
                Round = 1, ActionRound = 1, Phase = GamePhase.ActionRound1, CurrentPlayerId = 1, StartPlayerId = 1,
                LaunchPlayerOrder = new List<int> { 1, 2 }, Players = new List<PlayerState> {
                    new PlayerState { PlayerId = 1, Name = "隔离甲" }, new PlayerState { PlayerId = 2, Name = "隔离乙" } },
                ContentDefinitions = new GameContentDefinitions { Characters = pack.CreateCharacters().ToList(), Facilities = pack.CreateFacilities().ToList(),
                    Components = pack.Definitions.Where(d => d.ContentType == "component").Select(d => new ComponentContentDefinition {
                        DefinitionId = d.DefinitionId, TypeId = d.ItemTypeId, QuantityPolicy = (string)d.Data["quantityPolicy"], CubeAppearance = d.CubeAppearance }).ToList(),
                    AbilitySources = registry.AbilityDeclarations.ToList(), UsageTracks = pack.ActiveDefinitions.SelectMany(d => d.UsageTracks).ToList(),
                    CityStyles = pack.CreateCityStyles().ToList(), SpecialActions = pack.CreateSpecialActions().ToList(), Awards = ContentLifecycleLuaCatalog.CreateAwards(pack) }
            }, map, new[] {
                new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = 6, PerPlayer = true, InitialZoneRole = "supply" },
                new ItemRecipeEntry { EntryId = "dorothy", DefinitionId = "operator.dorothy", TypeId = "core.operator_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" },
                new ItemRecipeEntry { EntryId = "gang", DefinitionId = "entrepreneur.component.gang_marker", TypeId = "entrepreneur.gang_marker", Quantity = 1, PerPlayer = true, InitialZoneRole = "reserve" }
            }, new[] { new ItemCardZoneDeclaration { DefinitionId = "operator.dorothy", CardZones = pack.Definitions.Single(d => d.DefinitionId == "operator.dorothy").CardZones } });
            observedState = state;
            EffectTreeExecutor Engine() => new EffectTreeExecutor(state, registry);
            void Restore(string label)
            {
                var recovered = artifacts.Recover(state, label, registry);
                Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
                state = recovered.State; observedState = state;
            }
            void Drain() => Assert.That(Engine().RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            void Submit(params string[] ids)
            {
                var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
                var answer = ids.Length == 1 ? NormalizedValue.CreateString(ids[0]) : Strings(ids);
                Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId,
                    request.StateRevision, answer, out string error), Is.True, error); Drain();
            }
            string card = state.ItemZones.Items.Single(i => i.DefinitionId == "operator.dorothy" && i.OwnerSubject.PlayerId == 1).ItemId;
            Engine().CreateRoot(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Drain(); Submit(card);
            Engine().CreateRoot(CharacterLifecycleService.CreateUseSpec(1, card, "tactic")); Drain(); Submit("2");
            string storage = state.ItemZones.Zones.Single(z => z.ParentId == card && z.LocalZoneId == "markers").ZoneId;
            if (mixed) state.ItemZones.Items.Single(i => i.TypeId == "entrepreneur.gang_marker" && i.OwnerSubject.PlayerId == 1).ZoneId = storage;
            var stored = state.ItemZones.Items.Where(i => i.ZoneId == storage).ToDictionary(i => i.ItemId, i => i.OriginalSupplyZoneId);
            var chain = new RoundMainChainBuilder().Build(state.GameId, "", 1, new[] { 1, 2 }, 1);
            var main = chain.Nodes.Single(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow && n.PlayerId == 1);
            state.EffectRuntime.MainNodes.AddRange(chain.Nodes);
            foreach (var previous in chain.Nodes.Where(n => n.MainlineIndex < main.MainlineIndex)) previous.Status = EffectNodeStatus.Completed;
            state.EffectRuntime.ActiveMainNodeId = main.NodeId; state.EffectRuntime.FirstMainNodeId = chain.FirstNodeId;
            state.EffectRuntime.CurrentRoundExecutionId = chain.RoundExecutionId; state.EffectRuntime.CurrentRoundNumber = 1;
            state.EffectRuntime.CurrentRoundStartPlayerId = 1; state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 }; state.Phase = GamePhase.Cleanup;
            var rounds = new RoundExecutionService(registry);
            string root = rounds.CreateCommandExecutor(state).CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId,
                Object("mainNodeId", NormalizedValue.CreateString(main.NodeId)), 1));
            main = state.EffectRuntime.MainNodes.Single(n => n.NodeId == main.NodeId); main.ExecutionEffectId = root; main.Status = EffectNodeStatus.Running;
            Drain(); var option = registry.GetSettlementOptions(state, 1).Single(o => o.SourceItemId == card);
            Assert.That(Engine().TryInvokeCharacterSettlementOption(1, option.OptionId, "", option.StateRevision, out _, out string reason), Is.True, reason);
            Drain(); Submit("continue");
            if (mixed)
            {
                Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == storage), Is.EqualTo(3));
                string gang = state.ItemZones.Items.Single(i => i.ZoneId == storage && i.TypeId == "entrepreneur.gang_marker").ItemId;
                string playerMarker = state.ItemZones.Items.First(i => i.ZoneId == storage && i.TypeId == "core.player_marker").ItemId;
                var choice = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
                Assert.That(choice.MinSelections, Is.EqualTo(2)); Assert.That(choice.MaxSelections, Is.EqualTo(2));
                Assert.That(choice.InteractionTypeId, Is.EqualTo("lua.choice"));
                var bindingRoot = CharacterLifecycleService.PersistentSourceFactsRoot(state, Engine().GetNode(choice.OwnerEffectId));
                Assert.That(bindingRoot, Is.Not.Null); Assert.That(bindingRoot.PersistentSourceFacts.SourceRef.ItemId, Is.EqualTo(card));
                if (damage.StartsWith("raw:", StringComparison.Ordinal))
                {
                    var json = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
                    var raw = (JObject)json["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"]
                        .Single(n => (string)n["EffectId"] == bindingRoot.EffectId)["PersistentSourceFacts"];
                    if (damage == "raw:missing_version") raw.Property("ProtocolVersion").Remove();
                    if (damage == "raw:string_version") raw["ProtocolVersion"] = "1";
                    if (damage == "raw:version_2") raw["ProtocolVersion"] = 2;
                    string archive = json.ToString();
                    artifacts.WriteLabeledText("005原始局部版本-" + damage.Substring(4), archive);
                    var recovered = HostRecoveryService.RecoverJson(archive, registry);
                    Assert.That(recovered.Succeeded, Is.EqualTo(damage == "raw:original"), recovered.Diagnostic);
                    if (damage != "raw:original") Assert.That(recovered.Diagnostic, Does.Contain("persistent_source_facts.v1:raw_fields_missing_or_invalid"));
                    else Assert.That(CharacterLifecycleService.PersistentSourceFactsRoot(recovered.State,
                        recovered.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == choice.OwnerEffectId)).PersistentSourceFacts.SourceRef.ItemId, Is.EqualTo(card));
                    return;
                }
                if (damage.Length > 0)
                {
                    var archive = HostRecoveryService.CreateArchive(state);
                    var damagedState = archive.Snapshot.State;
                    var damagedRoot = damagedState.EffectRuntime.EffectNodes.Single(n => n.EffectId == bindingRoot.EffectId);
                    var binding = damagedRoot.PersistentSourceFacts;
                    if (damage == "missing_binding") damagedRoot.PersistentSourceFacts = null;
                    if (damage == "wrong_handler") binding.HandlerId = "forged.handler";
                    if (damage == "wrong_hash") binding.HandlerHash = "forged.hash";
                    if (damage == "wrong_trigger") binding.TriggerEventId = "forged.event";
                    if (damage == "wrong_source") binding.SourceRef.ItemId = "forged.item";
                    if (damage == "forged_grant") binding.SourceRef.GrantingSourceRefs.Add("forged.grant");
                    if (damage == "revoked_subscription") damagedState.CharacterUses.Single(u => u.UseId == binding.UseId).Modes.Single(m => m.AbilityId == binding.SourceRef.AbilityId).SubscriptionActive = false;
                    if (damage == "source_left")
                    {
                        damagedState.ItemZones.Items.Single(i => i.ItemId == card).ZoneId = FindZone(damagedState, "hand", 1).ZoneId;
                        CharacterLifecycleService.DetachExitedSubscriptions(damagedState, card);
                    }
                    string json = HostRecoveryService.SerializeArchive(archive);
                    artifacts.WriteLabeledText("005持续选择损坏来源存档-" + damage, json);
                    var denied = HostRecoveryService.RecoverJson(json, registry);
                    if (damage == "revoked_subscription" || damage == "source_left")
                    {
                        Assert.That(denied.Succeeded, Is.True, denied.Diagnostic);
                        Assert.That(registry.IsCurrentSourceFactsSource(denied.State, binding.SourceRef), Is.False);
                        Assert.That(SourceFactsLuaCatalog.Create(denied.State, registry, binding.SourceRef).Kind, Is.EqualTo(LuaSnapshotValueKind.Null));
                        var recoveredChoice = denied.State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
                        var inactiveEngine = new EffectTreeExecutor(denied.State, registry);
                        Assert.That(inactiveEngine.TrySubmitInteraction(recoveredChoice.GetStableInteractionId(), recoveredChoice.AnsweringPlayerId,
                            recoveredChoice.StateRevision, Strings(new[] { gang, playerMarker }), out string inactiveReason), Is.True, inactiveReason);
                        Assert.That(inactiveEngine.RunUntilQuiescent().Faulted, Is.True);
                        Assert.That(denied.State.EffectRuntime.LastFaultMessage, Does.Contain("source_facts_unavailable"));
                        Assert.That(denied.State.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ItemTransferEffectExecutor.TypeId &&
                            Number(n.NormalizedArguments, "apiVersion") == 3), Is.False);
                        Assert.That(denied.State.ItemZones.Items.Count(i => i.ZoneId == storage), Is.EqualTo(3));
                    }
                    else
                    {
                        Assert.That(denied.Succeeded, Is.False);
                        Assert.That(denied.Diagnostic, Does.Contain(damage == "missing_binding" ? "source_facts_binding_missing" : "source_facts_binding_invalid"));
                    }
                    Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == storage), Is.EqualTo(3));
                    return;
                }
                Restore("005必要3选2回答前恢复");
                string choiceId = choice.GetStableInteractionId(); int revision = choice.StateRevision;
                Submit(gang, playerMarker);
                string unchanged = RuleStateDigestService.Compute(state);
                Assert.That(Engine().TrySubmitInteraction(choiceId, 1, revision, Strings(new[] { gang, playerMarker }), out _), Is.False);
                Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(unchanged));
            }
            var moved = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == ItemTransferEffectExecutor.TypeId && Number(n.NormalizedArguments, "apiVersion") == 3);
            Assert.That(moved.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(Number(moved.NormalizedResult, "movedCount"), Is.EqualTo(2));
            var idsMoved = Value(moved.NormalizedResult, "movedItemIds").Items.Select(v => v.StringValue).ToArray();
            Assert.That(idsMoved.Distinct().Count(), Is.EqualTo(2));
            foreach (string id in idsMoved) Assert.That(state.ItemZones.Items.Single(i => i.ItemId == id).ZoneId, Is.EqualTo(stored[id]));
            Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == storage), Is.EqualTo(mixed ? 1 : 0));
            Assert.That(ItemTransferService.TryValidateTokenState(state, moved, out reason), Is.True, reason);
            Assert.That(state.EffectRuntime.RuleEvents.Any(e => e.EventType == "InfluenceRemoved" || e.EventType == "InfluenceReplaced"), Is.False);
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.InteractionTypeId.Contains("payment_items")), Is.False);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == card).ZoneId, Is.EqualTo(FindZone(state, "persistent", 1).ZoneId));
            var pending = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == pending.OwnerEffectId).EffectTypeId,
                Is.EqualTo(InfluenceEffectTypeIds.PlaceInfluence), "唯一剩余请求应是右式放置");
            Restore("005两枚真实转移后放置前恢复");
            int placedBefore = state.Map.Influences.Count;
            var placement = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Submit(placement.CandidateIds.First());
            Assert.That(state.Map.Influences.Count, Is.EqualTo(placedBefore + 1));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == ItemTransferEffectExecutor.TypeId && Number(n.NormalizedArguments, "apiVersion") == 3), Is.EqualTo(1));
            Restore("005放置完成后恢复不重复转移"); Drain();
            Assert.That(state.Map.Influences.Count, Is.EqualTo(placedBefore + 1));
            Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == storage), Is.EqualTo(mixed ? 1 : 0));
        }
    }
}
