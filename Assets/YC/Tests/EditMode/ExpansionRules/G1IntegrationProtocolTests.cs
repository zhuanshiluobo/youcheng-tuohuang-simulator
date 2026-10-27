using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Scoring;
using YC.Infrastructure.Lua;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 新发布边界的必要集成验证。安装包只读加载一次，不复制Content，不计正常对局。
    public sealed class G1IntegrationProtocolTests
    {
        private ExternalContentPack pack;
        private IsolatedTestArtifacts artifacts;
        private GameState state;
        private EffectRegistry registry;
        private RoundExecutionService rounds;
        [OneTimeSetUp] public void Load() => pack = ExternalContentPack.Current;
        [SetUp] public void Begin() => artifacts = IsolatedTestArtifacts.BeginCase();
        [TearDown] public void End()
        {
            if (state != null) artifacts.WriteLabeledText("当前隔离现场", JsonConvert.SerializeObject(state));
            artifacts?.Finish();
        }
        private ActiveContentCatalog Start(string mode, string expansions, int players = 2, bool entrance = true)
        {
            var config = new GameRuleConfiguration { PlayerCount = players, MapId = StaticMapDefinitions.ThreePlayerMapId,
                RuleModeId = mode, EnabledExpansionIds = expansions.Split(',').ToList() };
            var catalog = ActiveContentCatalogBuilder.Build(pack, config);
            config.ContentPackHash = catalog.ContentPackHash;
            registry = new EffectRegistry(); catalog.Register(registry);
            PlayerEntranceEffectExecutor.Register(registry, new MapQueryService(StaticMapDefinitions.Resolve(config.MapId)));
            var seats = Enumerable.Range(1, players).Select(p => new PlayerSeat { PlayerId = p, PlayerName = "隔离玩家" + p }).ToList();
            state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1, seats, config, 0,
                catalog.SetupRecipe, catalog.ContentDefinitions, seats.Select(p => p.PlayerId).ToList(),
                "qy018.g1." + TestContext.CurrentContext.Test.ID, null);
            rounds = new RoundExecutionService(registry);
            if (entrance) { var result = rounds.StartEntrance(state); Assert.That(result.IsValid, Is.True, result.Reason); }
            artifacts.WriteLabeledText("正式装配身份", JsonConvert.SerializeObject(new { pack.RootPath, pack.ContentHash,
                catalog.ContentPackHash, GameProtocolVersions.Identity, config, copiedContent = false,
                installedScripts = pack.Scripts.Count, effectiveSources = registry.AbilityDeclarations.Count }));
            return catalog;
        }
        private GameState Restore(string label)
        {
            string digest = RuleStateDigestService.Compute(state);
            var result = artifacts.Recover(state, label, registry);
            Assert.That(result.Succeeded, Is.True, result.Diagnostic);
            Assert.That(RuleStateDigestService.Compute(result.State), Is.EqualTo(digest));
            return state = result.State;
        }
        private GameCommand Answer(InteractionRequest request, int player, string selected, string id)
        {
            var command = new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = player, CommandId = id,
                OptionIds = new List<string> { selected } };
            command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.GetStableInteractionId();
            command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString(CultureInfo.InvariantCulture);
            return command;
        }
        private GameSession Session()
        {
            var session = new GameSession(state);
            session.RegisterHandler(new AnswerInteractionCommandHandler(registry, rounds));
            return session;
        }
        [Test] public void ReadOnlyContentOverridesKeepOriginalIdentityAndUseTheSameValidation()
        {
            string before = pack.ContentHash;
            var same = pack.CreateWithOverrides(new Dictionary<string, string>());
            Assert.That(same.ContentHash, Is.EqualTo(before));
            var manifest = pack.Manifest;
            ((JArray)manifest["definitions"]).Add("award/qy018.absent.json");
            Assert.Throws<System.IO.InvalidDataException>(() => pack.CreateWithOverrides(new Dictionary<string, string> { ["pack.json"] = manifest.ToString() }));
            Assert.Throws<System.IO.InvalidDataException>(() => pack.CreateWithOverrides(new Dictionary<string, string> { ["../outside.json"] = "{}" }));
            Assert.That(pack.ContentHash, Is.EqualTo(before));
            Assert.That(pack.Manifest.ToString(), Is.Not.EqualTo(manifest.ToString()));
        }
        [TestCase("standard", "core")]
        [TestCase("standard", "core,enterprise")]
        [TestCase("standard", "core,enterprise,infected")]
        [TestCase("entrepreneur", "core,enterprise,infected")]
        public void FormalModeRegistrationCreatesCurrentStateAndColdRestores(string mode, string expansions)
        {
            Start(mode, expansions);
            Assert.That(state.EntitySchemaVersion, Is.EqualTo(2)); Assert.That(state.EffectRuntime.SchemaVersion, Is.EqualTo(4));
            RuleModeReadiness.ValidateReady(state);
            var archive = HostRecoveryService.CreateArchive(state);
            Assert.That(archive.Snapshot.SchemaVersion, Is.EqualTo(5)); Assert.That(archive.Journal.SchemaVersion, Is.EqualTo(5));
            Restore("正式入口当前版本冷恢复");
            if (mode == "entrepreneur")
            {
                Assert.That(state.EntrepreneurSetup.Stage, Is.EqualTo("preview"));
                Assert.That(state.ContentDefinitions.Entrepreneurs.Count, Is.EqualTo(5));
            }
            else Assert.That(state.ContentDefinitions.Entrepreneurs, Is.Empty);
        }

        [Test] public void JournalCheckpointsAndRawVersionDamageAreRejectedWithoutDefaults()
        {
            Start("entrepreneur", "core,enterprise,infected");
            var archive = HostRecoveryService.CreateArchive(state);
            var request = state.EffectRuntime.InteractionRequests.First(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.PreviewInteraction);
            var session = Session(); var command = Answer(request, request.AnsweringPlayerId, request.CandidateIds.First(), "qy018.journal.continue");
            var answer = session.Submit(command); state = session.State;
            Assert.That(answer.Succeeded, Is.True, answer.Validation.Reason);
            HostRecoveryService.AppendJournal(archive, state);
            Assert.That(archive.Journal.Entries.Count, Is.GreaterThan(0));
            string original = HostRecoveryService.SerializeArchive(archive);
            var restored = HostRecoveryService.RecoverJson(original, registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic);
            Assert.That(RuleStateDigestService.Compute(restored.State), Is.EqualTo(RuleStateDigestService.Compute(state)));
            artifacts.WriteLabeledText("真实提交及批末原档", original);
            var raw = JObject.Parse(original);
            var mutations = new Dictionary<string, Action<JObject>> {
                ["快照原始布尔缺失"] = j => ((JObject)j["Snapshot"]).Remove("RandomStateRequired"),
                ["快照旧实体版本"] = j => j["Snapshot"]["State"]["EntitySchemaVersion"] = 1,
                ["快照旧运行时版本"] = j => j["Snapshot"]["State"]["EffectRuntime"]["SchemaVersion"] = 3,
                ["快照层协议缺失"] = j => ((JObject)j["Snapshot"]["State"]["ItemZones"]).Remove("FacilityLayerVersion"),
                ["批末实体版本"] = j => j["Journal"]["Entries"].Last["StateAfter"]["EntitySchemaVersion"] = 1,
                ["批末运行时版本"] = j => j["Journal"]["Entries"].Last["StateAfter"]["EffectRuntime"]["SchemaVersion"] = 3,
                ["批末丢失"] = j => j["Journal"]["Entries"].Last["StateAfter"] = JValue.CreateNull(),
                ["提交序号损坏"] = j => j["Journal"]["Entries"][0]["CommitSequence"] = 999999,
                ["内容哈希损坏"] = j => j["Journal"]["Entries"][0]["ContentHash"] = "foreign",
            };
            foreach (var change in mutations)
            {
                var copy = (JObject)raw.DeepClone(); change.Value(copy); string input = copy.ToString();
                var result = HostRecoveryService.RecoverJson(input, registry);
                artifacts.WriteLabeledText(change.Key, JsonConvert.SerializeObject(new { result.Succeeded, result.FaultCode, result.Diagnostic }));
                Assert.That(result.Succeeded, Is.False, change.Key);
                Assert.That(copy.ToString(), Is.EqualTo(input));
            }
            Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(original));
        }

        [Test] public void ColdReconnectTransfersOnlyOwnCandidatesAndRejectsWrongOrConsumedRequests()
        {
            Start("entrepreneur", "core,enterprise,infected");
            string random = JsonConvert.SerializeObject(state.RandomRuntime);
            Restore("主客机准备请求断线冷恢复");
            var session = Session();
            foreach (int player in new[] { 1, 2 })
            {
                var view = session.ProjectFor(GameStateViewer.Player(player));
                byte[] payload = GameStateViewTransfer.Encode(JsonConvert.SerializeObject(view));
                var assembler = new GameStateViewTransfer.Assembler(); string json = null;
                foreach (var part in GameStateViewTransfer.Split(payload))
                    Assert.That(assembler.TryAppend(1, true, payload.Length, part.Offset, part.ToArray(), out json), Is.True);
                var client = JsonConvert.DeserializeObject<GameStateView>(json);
                var own = state.EntrepreneurSetup.EnterpriseAssignments.Single(a => a.PlayerId == player).CandidateItemIds;
                Assert.That(client.Entrepreneurs.OwnEnterpriseCandidates.Select(c => c.ItemId), Is.EqualTo(own));
                Assert.That(client.Entrepreneurs.OwnEnterpriseCandidates.Count, Is.EqualTo(4));
                var other = state.EntrepreneurSetup.EnterpriseAssignments.Single(a => a.PlayerId != player).CandidateItemIds;
                Assert.That(client.ItemZones.Items.Any(i => other.Contains(i.ItemId)), Is.False);
                Assert.That(client.Entrepreneurs.OwnEnterpriseCandidates.Any(i => other.Contains(i.ItemId)), Is.False);
                artifacts.WriteLabeledText("玩家" + player + "实际传输投影", json);
            }
            Assert.That(session.ProjectFor(GameStateViewer.Spectator).Entrepreneurs.OwnEnterpriseCandidates, Is.Empty);
            var request = state.EffectRuntime.InteractionRequests.First(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.PreviewInteraction);
            string before = RuleStateDigestService.Compute(state);
            var wrong = session.Submit(Answer(request, request.AnsweringPlayerId == 1 ? 2 : 1, request.CandidateIds.First(), "qy018.wrong-owner"));
            Assert.That(wrong.Succeeded, Is.False); Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(before));
            var command = Answer(request, request.AnsweringPlayerId, request.CandidateIds.First(), "qy018.original-answer");
            var result = session.Submit(command); state = session.State;
            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            string after = RuleStateDigestService.Compute(state);
            var old = session.Submit(Answer(request, request.AnsweringPlayerId, request.CandidateIds.First(), "qy018.consumed-request"));
            Assert.That(old.Succeeded, Is.False); Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(after));
            Assert.That(JsonConvert.SerializeObject(state.RandomRuntime), Is.EqualTo(random));
            Restore("正式回答后重连不重抽");
        }

        [Test] public void OldGlobalVersionsAreExplicitlyRejectedAndInputRemainsUntouched()
        {
            Start("entrepreneur", "core,enterprise,infected");
            const string legacyPath = "TestResults/IsolatedRuns/qy004-own-candidates-r1-a002-20261022/5b46544342e77fed-c804923bc7c34e9e86e2a8c48f31f45a/0004-继续前恢复-ac4eecfaf3.archive.json";
            byte[] historical = System.IO.File.ReadAllBytes(legacyPath);
            string historicalHash;
            using (var sha = System.Security.Cryptography.SHA256.Create()) historicalHash = string.Concat(sha.ComputeHash(historical).Select(b => b.ToString("x2")));
            Assert.That(historicalHash, Is.EqualTo("dfdb60266d903305f02f89a9a59757d0b227255ca058de887fdd4ee9283ec3fc"));
            var rejected = HostRecoveryService.RecoverJson(System.Text.Encoding.UTF8.GetString(historical), registry);
            Assert.That(rejected.Succeeded, Is.False); Assert.That(rejected.FaultCode, Is.EqualTo(HostRecoveryService.GlobalVersionUnsupported));
            Assert.That(System.IO.File.ReadAllBytes(legacyPath), Is.EqualTo(historical));
            artifacts.WriteLabeledText("真实Host4原档只读拒绝", JsonConvert.SerializeObject(new { legacyPath, historicalHash, rejected.FaultCode, rejected.Diagnostic }));
            var raw = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            foreach (int version in new[] { 1, 2, 3, 4, 6 })
            {
                var old = (JObject)raw.DeepClone(); old["Snapshot"]["SchemaVersion"] = old["Journal"]["SchemaVersion"] = version;
                string input = old.ToString(); var result = HostRecoveryService.RecoverJson(input, registry);
                Assert.That(result.Succeeded, Is.False); Assert.That(result.FaultCode, Is.EqualTo(HostRecoveryService.GlobalVersionUnsupported));
                Assert.That(old.ToString(), Is.EqualTo(input));
                artifacts.WriteLabeledText("旧或未知全局版本拒绝" + version, JsonConvert.SerializeObject(new { version, result.FaultCode, result.Diagnostic,
                    scope = "当前档副本的版本负例，不冒充真实历史档或证明历史迁移支持" }));
            }
        }

        [Test] public void ReadyContractStillRejectsAnUnimplementedFaceOrEmptyRuleTags()
        {
            var catalog = Start("entrepreneur", "core,enterprise,infected");
            var definitions = catalog.ContentDefinitions;
            var face = definitions.Entrepreneurs.First().RoundGroups.First().Candidates.First();
            definitions.AbilitySources.RemoveAll(s => s.DefinitionId == face.EntityDefinitionId && s.ActiveFaceId == face.FaceId);
            Assert.Throws<InvalidOperationException>(() => RuleModeReadiness.ValidateContent(definitions, catalog.SetupRecipe));
            definitions = catalog.ContentDefinitions;
            definitions.Entrepreneurs.First().RoundGroups.First().Candidates.First().RuleRefs.Clear();
            Assert.Throws<InvalidOperationException>(() => RuleModeReadiness.ValidateContent(definitions, catalog.SetupRecipe));
        }

        [Test] public void OriginalHotseatHostCanFreezeFormalModeAndOldRoomWireCannotJoin()
        {
            var providerType = Type.GetType("YC.Infrastructure.Multiplayer.BasicRoomContentCapabilityProvider, Assembly-CSharp", true);
            var provider = (IRoomContentCapabilityProvider)Activator.CreateInstance(providerType, new Func<ExternalContentPack>(() => pack));
            using var room = new RoomSessionService(provider, () => throw new Exception("本机模式不创建网络"));
            var snapshot = room.CreateRoomAsync("隔离房主", 3).GetAwaiter().GetResult();
            RoomConfigurationRequest Fill(RoomConfigurationRequest r, string id)
            { r.CommandId = id; r.RoomId = snapshot.RoomId; r.RequestingPlayerId = snapshot.LocalPlayerId; r.ExpectedConfigurationRevision = snapshot.Configuration.ConfigurationRevision; return r; }
            var add = (AddLocalRoomPlayerRequest)Fill(new AddLocalRoomPlayerRequest { SeatIndex = 1, PlayerName = "隔离玩家2" }, "qy018.room.add");
            Assert.That(room.AddLocalPlayerAsync(add).Result.Success, Is.True);
            snapshot = room.GetCurrentRoom();
            var change = (ChangeRoomConfigurationRequest)Fill(new ChangeRoomConfigurationRequest { MapId = StaticMapDefinitions.ThreePlayerMapId,
                RuleModeId = "entrepreneur", EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } }, "qy018.room.mode");
            var changed = room.ChangeConfigurationAsync(change).Result;
            Assert.That(changed.Success, Is.True, changed.ReasonCode); snapshot = room.GetCurrentRoom();
            var start = (StartConfiguredGameRequest)Fill(new StartConfiguredGameRequest(), "qy018.room.start");
            var launched = room.StartConfiguredGameAsync(start).Result;
            Assert.That(launched.Success, Is.True, launched.ReasonCode);
            snapshot = room.GetCurrentRoom(); Assert.That(snapshot.HasStarted, Is.True);
            Assert.That(snapshot.ProtocolVersion, Is.EqualTo(6));
            string wire = RoomStateSerializer.Serialize(snapshot);
            Assert.That(RoomStateSerializer.TryDeserialize(wire, out var restored, out string reason), Is.True, reason);
            Assert.That(restored.Configuration.RuleModeId, Is.EqualTo("entrepreneur"));
            var old = JObject.Parse(wire); old["ProtocolVersion"] = 5;
            Assert.That(RoomStateSerializer.TryDeserialize(old.ToString(), out _, out _), Is.False);
            artifacts.WriteLabeledText("原房间入口实际冻结与旧版拒绝", JsonConvert.SerializeObject(new { snapshot, wire, legacyRoom5Rejected = true,
                scope = "实际房间服务的隔离调用，不计可见菜单/正常游戏" }));
        }

        [Test] public void FinalSessionRestoresEachStageAndProjectsSpecialScoreBeforeShowingRealResultAsset()
        {
            Start("standard", "core", entrance: false);
            state.Phase = GamePhase.FinalScoring; state.Round = state.MaxRounds = 8;
            state.StartPlayerId = 1; state.CurrentPlayerId = -1;
            artifacts.WriteLabeledText("终局协议隔离输入声明", "使用正式安装内容；直接设置末轮仅定位协议。通用来源探针给2分，不是企业家卡面替身，不计正常局。");
            var queries = new ScoringModifierQueryService(new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)), registry);
            var service = new FinalScoringSessionService(queries, YC.Domain.Facilities.FacilityLayerEvidence.Validate);
            // 独立协议探针走真实来源授权与完成收据；不是正式卡面，也不计正常对局。
            var item = state.ItemZones.Items.First(i => i.OwnerSubject.PlayerId == 1 && i.OwnerSubject.Kind == YC.Domain.Items.ItemSubjectKind.Player);
            var zone = state.ItemZones.Zones.Single(z => z.ZoneId == item.ZoneId);
            var declaration = new AbilitySourceDeclaration { DefinitionId = item.DefinitionId, AbilityId = "qy018.protocol.income",
                Kind = "event", HandlerRef = "qy018.protocol.income", DeclarationVersion = "1.0.0", AuthorizationPolicy = "owner", PresentationRef = item.DefinitionId,
                EffectiveZones = new List<string> { zone.TemplateId }, ObservedEvents = new List<string> { "FinalScoringStarted" } };
            state.ContentPackHash = state.RuleConfiguration.ContentPackHash = LuaContentHasher.ComputeSha256(state.ContentPackHash + ":qy018.final.protocol.1");
            state.ContentDefinitions.AbilitySources = new List<AbilitySourceDeclaration> { declaration };
            registry = new EffectRegistry { ContentPackHash = state.ContentPackHash };
            ResourceEffectExecutor.Register(registry); AbilitySourceEffectExecutor.Register(registry);
            registry.RegisterAbilityHandler(declaration.HandlerRef, LuaContentHasher.ComputeSha256("qy018.final.protocol.GainScore(2)"),
                call => call.SourceRef.ItemId == item.ItemId ? new List<EffectSpec> { ResourceEffectSpecFactory.GainScore(1, 2) } : new List<EffectSpec>());
            registry.FreezeAbilityCatalog(new[] { declaration });
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("qy018.protocol.income.event", "FinalScoringStarted", "", "qy018.protocol.income.event",
                context => registry.QuerySources(context.State, 1, "event", "FinalScoringStarted").Where(source => source.ItemId == item.ItemId)
                    .Select(source => AbilitySourceEffectExecutor.Create(source, context.Event, declaration.HandlerRef, false, context.State.Round)).ToList()));
            queries = new ScoringModifierQueryService(new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)), registry);
            service = new FinalScoringSessionService(queries, YC.Domain.Facilities.FacilityLayerEvidence.Validate);
            const string type = "qy018.protocol.final";
            registry.Register(new EffectRegistration(type, context => {
                if (context.Node.FlowStage.Length == 0)
                {
                    var main = new MainlineNodeRuntimeState { NodeId = "qy018.final.main", NodeTypeId = RoundMainlineNodeTypeIds.RoundEnded,
                        ExecutionEffectId = context.Node.EffectId, RoundNumber = 8, CompletionRequested = true };
                    main.PublishedEventKeys.Add("RoundEnded"); context.State.EffectRuntime.MainNodes.Add(main);
                    context.State.EffectRuntime.ActiveMainNodeId = main.NodeId;
                    return EffectStepResult.Continue(FinalScoringSessionService.Prepare).AddEvent(new EffectEventRequest { EventType = "RoundEnded",
                        SourceEffectId = context.Node.EffectId, OwnerNodeId = context.Node.EffectId });
                }
                return service.Advance(context, context.State.EffectRuntime.MainNodes.Single(m => m.ExecutionEffectId == context.Node.EffectId),
                    () => new EffectEventRequest { EventType = "FinalScoringStarted" });
            }, EffectExecutorKind.IntrinsicFlow, ScoringModifierProtocol.Version));
            string id = new EffectTreeExecutor(state, registry).CreateRoot(EffectSpec.Create(type, playerId: 1));
            var seen = new HashSet<string>();
            UnityEngine.GameObject instance = null;
            try
            {
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>("Assets/YC/Presentation/Prefabs/FinalScore/FinalScore.prefab");
                Assert.That(prefab, Is.Not.Null); instance = UnityEngine.Object.Instantiate(prefab);
                var controllerType = Type.GetType("YC.Presentation.FinalScoreController, Assembly-CSharp", true);
                var controller = instance.GetComponentInChildren(controllerType, true);
                Assert.That(controller, Is.Not.Null);
                controllerType.GetMethod("RefreshFromState").Invoke(controller, new object[] { state });
                var shown = controllerType.GetField("gameOverDialogShown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.That((bool)shown.GetValue(controller), Is.False, "未结清的终局必须保持正式请求可答");
                for (int guard = 0; guard < 180; guard++)
                {
                    var owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
                    if (owner.FlowStage.StartsWith("final_", StringComparison.Ordinal) && seen.Add(owner.FlowStage)) Restore("终局阶段" + owner.FlowStage);
                    if (owner.Status == EffectNodeStatus.Completed) break;
                    var engine = new EffectTreeExecutor(state, registry);
                    Assert.That(engine.Advance(), Is.True, engine.LastDiagnostic);
                }
                Assert.That(state.FinalScoring.IsResolved, Is.True, state.EffectRuntime.LastFaultMessage);
                var score = state.FinalScoring.PlayerScores.Single(p => p.PlayerId == 1);
                Assert.That(score.SpecialScore, Is.EqualTo(2));
                Assert.That(score.TotalScore, Is.EqualTo(score.BaseScore + score.SpecialScore + score.FacilityScore + score.CityStyleScore + score.RegionScore + score.ResourceScore));
                var view = new GameSession(state).ProjectFor(GameStateViewer.Player(1));
                var transported = JsonConvert.DeserializeObject<GameStateView>(JsonConvert.SerializeObject(view));
                var client = GameStateViewProjector.ToClientState(transported);
                Assert.That(client.FinalScoring.PlayerScores.Single(p => p.PlayerId == 1).SpecialScore, Is.EqualTo(2));
                controllerType.GetMethod("RefreshFromState").Invoke(controller, new object[] { client });
                Assert.That((bool)shown.GetValue(controller), Is.True);
                Restore("终局完整分项冷恢复");
                var raw = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
                ((JObject)raw["Snapshot"]["State"]["FinalScoring"]["PlayerScores"][0]).Remove("SpecialScore");
                Assert.That(HostRecoveryService.RecoverJson(raw.ToString(), registry).Succeeded, Is.False);
                Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "FinalScoringStarted"), Is.EqualTo(1));
            }
            finally { if (instance != null) UnityEngine.Object.DestroyImmediate(instance); }
        }
    }
}
