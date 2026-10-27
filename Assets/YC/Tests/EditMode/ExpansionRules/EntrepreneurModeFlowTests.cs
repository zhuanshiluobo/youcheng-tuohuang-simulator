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
    /// <summary>全部为隔离机制测试。测试处理器明确不代表五位企业家的卡面正文或正常游戏验收。</summary>
    public sealed class EntrepreneurModeFlowTests
    {
        private IsolatedTestArtifacts artifacts;
        private ExternalContentPack pack;
        private EffectRegistry registry;
        private List<EntrepreneurDefinition> official;
        private GameSetupRecipe entrepreneurRecipe;
        [SetUp] public void Begin()
        {
            artifacts = IsolatedTestArtifacts.BeginCase();
            string root = artifacts.CopyContent(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"), "004正式安装内容副本");
            pack = ExternalContentPack.Load(root); artifacts.RecordContent("004安装内容", root, pack.ContentHash);
            official = EntrepreneurContentCatalog.Read(pack.Definitions);
            entrepreneurRecipe = pack.SetupRecipes.Single(r => r.RecipeId == "entrepreneur");
        }
        [TearDown] public void End() => artifacts?.Finish();
        private static GameRuleConfiguration Configuration(int count = 3, string mode = "standard") => new GameRuleConfiguration { PlayerCount = count, RuleModeId = mode,
            EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" }, MapId = count == 4 ? StaticMapDefinitions.FourPlayerMapId : StaticMapDefinitions.ThreePlayerMapId };
        [Test] public void Qy007B2OfficialPaymentDeclarationsLoadThroughRegisteredCapabilitiesAndKeepG0()
        {
            var composite = pack.Definitions.Single(d => d.DefinitionId == "city_style_composite_power_system");
            var reward = pack.Definitions.Single(d => d.DefinitionId == "entrepreneur.award.M01");
            var passenger = pack.Definitions.Single(d => d.DefinitionId == "operator.passenger");
            Assert.That(composite.RequiredCapabilities, Does.Contain("resource_payment_total.v2"));
            Assert.That(reward.RequiredCapabilities, Does.Contain("resource_payment_sources.v2"));
            Assert.That(passenger.RequiredCapabilities, Does.Contain("character_subscription.link.v1"));
            foreach (var definition in new[] { composite, reward, passenger })
                Assert.That(RuleModeReadiness.Missing(definition.RequiredCapabilities), Is.Empty, definition.DefinitionId);
            var active = ActiveContentCatalogBuilder.Build(pack, Configuration());
            Assert.That(active.Definitions.Any(d => d.DefinitionId == composite.DefinitionId), Is.True);
            Assert.That(active.Definitions.Any(d => d.DefinitionId == passenger.DefinitionId), Is.True);
            Assert.That(active.SetupRecipe.Entries.Any(e => e.DefinitionId == reward.DefinitionId), Is.False, "指定发放奖励不能因公共能力登记进入开局配方");
            var missing = RuleModeReadiness.Missing(RuleModeReadiness.EntrepreneurRequirements).Split(',');
            Assert.That(missing, Is.EquivalentTo(new[] { "entrepreneur_content.T.v1", "entrepreneur_content.A.v1", "entrepreneur_content.D.v1",
                "entrepreneur_content.P.v1", "entrepreneur_content.M.v1", "entrepreneur_ui.v1", "host_recovery.v5", "room_protocol.v6" }));
            var rejected = Assert.Throws<InvalidOperationException>(() => ActiveContentCatalogBuilder.Build(pack, Configuration(mode: "entrepreneur")));
            foreach (string capability in missing) Assert.That(rejected.Message, Does.Contain(capability));
            artifacts.WriteLabeledText("正式付款能力装配与G0保留", JsonConvert.SerializeObject(new { active.ContentPackHash, missing,
                composite = composite.RequiredCapabilities, reward = reward.RequiredCapabilities, passenger = passenger.RequiredCapabilities }));
        }
        [Test] public void OfficialDefinitionsCountPhysicalEntitiesFacesAndImagesSeparately()
        {
            Assert.That(official.Count, Is.EqualTo(5)); Assert.That(official.SelectMany(d => d.RoundGroups).Count(), Is.EqualTo(20));
            var faces = official.SelectMany(d => d.RoundGroups).SelectMany(g => g.Candidates).ToList();
            Assert.That(faces.Count, Is.EqualTo(40)); Assert.That(faces.Select(f => f.EntityDefinitionId).Distinct().Count(), Is.EqualTo(20));
            Assert.That(pack.Definitions.Count(d => d.ContentType == "entrepreneur_round" && d.Enabled), Is.EqualTo(20));
            Assert.That(pack.Definitions.Where(d => d.Tags.Contains("example")).All(d => !d.Enabled), Is.True);
            Assert.That(faces.All(f => f.ArtworkSha256.Length == 64), Is.True);
            Assert.That(official.SelectMany(d => d.RoundGroups).All(g => g.Candidates.Single(f => f.CandidateId == g.PreviewCandidateId).SourceId.Length > 0), Is.True);
            artifacts.WriteLabeledText("004正式定义对账", JsonConvert.SerializeObject(official, Formatting.Indented));
        }
        [Test] public void OfficialRecipesDoNotIssueRewardOrDuplicateBaseTower()
        {
            var components = official.SelectMany(d => d.Components).ToList();
            Assert.That(components.Single(c => c.TypeId == "core.die").Quantity, Is.EqualTo(3));
            Assert.That(components.Single(c => c.TypeId == "entrepreneur.gang_marker").Quantity, Is.EqualTo(4));
            Assert.That(components.Where(c => c.CarrierLocalZoneId == "blueprints").Select(c => c.Quantity), Is.EqualTo(new[] { 3, 3, 3 }));
            Assert.That(components.Single(c => c.SourceRole == "supply").Quantity, Is.EqualTo(2));
            Assert.That(entrepreneurRecipe.Entries.Any(e => e.DefinitionId == "entrepreneur.award.M01"), Is.False);
            var tower = pack.Definitions.Single(d => d.DefinitionId == "entrepreneur.facility.additional_core_tower");
            Assert.That((bool)tower.Data["unique"], Is.False); Assert.That((bool)tower.Data["reserveOnly"], Is.False);
            Assert.That((string)tower.Data["effectId"], Is.EqualTo("setup_core_command_tower")); Assert.That(tower.ArtworkSpriteId, Is.EqualTo("facility_045"));
            Assert.That(pack.Definitions.Single(d => d.DefinitionId == "reserve_001").Data["unique"].Value<bool>(), Is.True);
        }
        [Test] public void ProductionModeRejectsRealMissingMechanismsAndDoesNotRequireNormalGameEvidence()
        {
            var config = Configuration(mode: "entrepreneur"); Assert.That(config.TryValidate(out _), Is.True);
            var ex = Assert.Throws<InvalidOperationException>(() => ActiveContentCatalogBuilder.Build(pack, config));
            Assert.That(ex.Message, Does.StartWith("rule_mode_not_ready:")); Assert.That(ex.Message, Does.Contain("entrepreneur_ui.v1"));
            Assert.That(RuleModeReadiness.EntrepreneurRequirements.Any(c => c.Contains("019") || c.Contains("acceptance")), Is.False);
            artifacts.WriteLabeledText("004G0正式拒绝", ex.Message);
        }
        [TestCase("enterprise")][TestCase("infected")]
        public void ModeRequiresBothDependencies(string missing)
        {
            var config = Configuration(mode: "entrepreneur"); config.EnabledExpansionIds.Remove(missing);
            Assert.That(config.TryValidate(out string reason), Is.False); Assert.That(reason, Is.EqualTo("missing_entrepreneur_dependency"));
        }
        [Test] public void StandardModeDoesNotAssembleEntrepreneurDefinitionsOrAccessories()
        {
            var config = new GameRuleConfiguration { PlayerCount = 2 };
            var catalog = ActiveContentCatalogBuilder.Build(pack, config);
            Assert.That(catalog.ContentDefinitions.Entrepreneurs, Is.Empty);
            Assert.That(catalog.Definitions.Any(d => d.ContentType.StartsWith("entrepreneur")), Is.False);
            Assert.That(catalog.SetupRecipe.Entries.Any(e => e.DefinitionId.StartsWith("entrepreneur.")), Is.False);
        }
        [TestCase(2,2)][TestCase(3,1)][TestCase(4,0)]
        public void InfectedSupplyRulesSurviveEntrepreneurMode(int players, int expected)
        {
            var state = new GameState { RuleConfiguration = Configuration(players, "entrepreneur") };
            Assert.That(InfectedRoundRules.IsEnabled(state), Is.True); Assert.That(InfectedRoundRules.GetSupplyRetirementCount(state, players), Is.EqualTo(expected));
        }
        private GameState IsolatedState(int players, bool firstImmediateFails = false, bool diy = false)
        {
            registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
            ResourceEffectExecutor.Register(registry); ExpansionSetupFlow.Register(registry); ItemTransferEffectExecutor.Register(registry);
            registry.Register("qy004.isolated.fail", c => EffectStepResult.Failed("isolated_expected_failure"), EffectExecutorKind.Atomic);
            registry.RegisterAbilityHandler("qy004.isolated.identity", "qy004.identity/1", _ => throw new InvalidOperationException("只读来源声明不执行正文"));
            var declarations = new List<AbilitySourceDeclaration>();
            foreach (var d in official)
            {
                declarations.Add(new AbilitySourceDeclaration { DefinitionId = d.DefinitionId, AbilityId = d.DefinitionId + ".identity", Kind = "setup", HandlerRef = "qy004.isolated.identity",
                    DeclarationVersion = d.Version, AuthorizationPolicy = "controller", EffectiveZones = new List<string> { "core.zone.entrepreneur_active" }, PresentationRef = d.DefinitionId });
                foreach (var group in d.RoundGroups)
                {
                    if (diy && group.RoundNumber == 3)
                    {
                        var third = GameStateCloneService.DeepClone(group.Candidates[0]); third.CandidateId += ".diy-third"; third.FaceId += ".diy-third";
                        third.RuleRefs = new List<string> { third.CandidateId + ".rule" }; third.DisplayOrder = 2; group.Candidates.Add(third);
                    }
                    foreach (var f in group.Candidates)
                    {
                        declarations.Add(new AbilitySourceDeclaration { DefinitionId = f.EntityDefinitionId, AbilityId = f.RuleRefs[0], Kind = "setup", HandlerRef = "qy004.isolated.identity", ActiveFaceId = f.FaceId,
                            DeclarationVersion = d.Version, AuthorizationPolicy = "active_selection", EffectiveZones = new List<string> { "core.zone.entrepreneur_active" }, PresentationRef = f.EntityDefinitionId });
                        foreach (var handler in f.ImmediateHandlerRefs)
                            declarations.Add(new AbilitySourceDeclaration { DefinitionId = f.EntityDefinitionId, AbilityId = f.RuleRefs[0] + ".entry." + (f.ImmediateHandlerRefs.IndexOf(handler) + 1), Kind = "setup", HandlerRef = handler, ActiveFaceId = f.FaceId,
                                DeclarationVersion = d.Version, AuthorizationPolicy = "active_selection", EffectiveZones = new List<string> { "core.zone.entrepreneur_active" }, PresentationRef = f.EntityDefinitionId });
                    }
                }
            }
            foreach (string h in official.SelectMany(d => d.RoundGroups).SelectMany(g => g.Candidates).SelectMany(f => f.ImmediateHandlerRefs).Distinct())
            {
                string handler = h;
                registry.RegisterAbilityHandler(handler, "qy004.isolated.immediate/1:" + handler, call => new List<EffectSpec> {
                    firstImmediateFails && handler.EndsWith(".immediate.1") ? EffectSpec.Create("qy004.isolated.fail", playerId: call.SourceRef.AuthorizedPlayerId) : ResourceEffectSpecFactory.GainScore(call.SourceRef.AuthorizedPlayerId, 1) });
            }
            registry.FreezeAbilityCatalog(declarations);
            var enterpriseDefinitions = pack.Definitions.Where(d => d.ExpansionId == "enterprise" && (d.ContentType == "enterprise" || d.ContentType == "department")).ToList();
            foreach (var e in enterpriseDefinitions) e.RuntimeId = e.DefinitionId;
            registry.EnterpriseDefinitions = LuaContentCatalog.CreateEnterpriseDefinitions(registry, enterpriseDefinitions, pack.GetScript, pack);
            var recipe = GameSetupRecipe.CreateRegisteredStandard(players == 4 ? StaticMapDefinitions.FourPlayerMapId : StaticMapDefinitions.ThreePlayerMapId);
            recipe.EnterpriseEntrance = true; recipe.Entries.AddRange(GameStateCloneService.DeepClone(entrepreneurRecipe.Entries));
            foreach (var e in enterpriseDefinitions.Where(e => e.ContentType == "enterprise")) recipe.Add(e.DefinitionId, e.DefinitionId, "core.board_card", "reserve");
            recipe.CardZoneDeclarations = pack.Definitions.Where(d => d.ContentType == "entrepreneur_ability").Select(d => new ItemCardZoneDeclaration { DefinitionId = d.DefinitionId, CardZones = d.CardZones }).ToList();
            var definitions = GameContentDefinitions.CreateRegisteredStandard(); definitions.Entrepreneurs = GameStateCloneService.DeepClone(official); definitions.AbilitySources = declarations;
            definitions.Facilities.AddRange(pack.Definitions.Where(d => d.ContentType == "facility" && d.ApplicableModes.Contains("entrepreneur")).Select(d => { var value = d.Data.ToObject<YC.Domain.Facilities.FacilityCardDefinition>(); value.FacilityId = d.DefinitionId; return value; }));
            definitions.Components = pack.Definitions.Where(d => d.ContentType == "component").Select(d => new ComponentContentDefinition { DefinitionId = d.DefinitionId, TypeId = d.ItemTypeId, CubeAppearance = d.CubeAppearance, QuantityPolicy = (string)d.Data["quantityPolicy"] }).ToList();
            var config = new GameRuleConfiguration { MapId = players == 4 ? StaticMapDefinitions.FourPlayerMapId : StaticMapDefinitions.ThreePlayerMapId, PlayerCount = players, ContentPackHash = pack.ContentHash };
            var seats = Enumerable.Range(1, players).Select(p => new PlayerSeat { PlayerId = p, PlayerName = "隔离玩家" + p }).ToList();
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1, seats, config, 47, recipe, definitions, Enumerable.Range(1, players).ToList(), "qy004.isolated." + players, null);
            // 隔离机制定位：正式Host G0已单独断言拒绝，以下不是正常开局或G1证据。
            state.RuleConfiguration.RuleModeId = "entrepreneur"; state.RuleConfiguration.EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" };
            state.EffectRuntime.PlayerOrderSnapshot = Enumerable.Range(1, players).ToList();
            registry.ContentDefinitions = GameStateCloneService.DeepClone(definitions);
            return state;
        }
        private void Run(EffectTreeExecutor engine)
        { var result = engine.RunUntilQuiescent(); if (result.Faulted) artifacts.WriteLabeledText("故障原始状态", JsonConvert.SerializeObject(engine.State)); Assert.That(result.Faulted, Is.False, engine.State.EffectRuntime.LastFaultMessage); }
        private void AnswerRequest(EffectTreeExecutor engine, InteractionRequest request, string answer)
        {
            RoundExecutionService.RefreshConcurrentInteractionRevisions(engine.State);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, NormalizedValue.CreateArray(new List<NormalizedValue> { NormalizedValue.CreateString(answer) }), out string reason), Is.True, reason); Run(engine);
        }
        private GameState Restore(GameState state, string label)
        { artifacts.WriteLabeledText(label + "-原始状态", JsonConvert.SerializeObject(state)); var result = artifacts.Recover(state, label, registry); Assert.That(result.Succeeded, Is.True, result.Diagnostic); return result.State; }
        private EffectTreeExecutor SetupPlayers(GameState state, string firstAbility = "entrepreneur.ability.M13", bool recover = false)
        {
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(ExpansionSetupFlow.PreparationSpec(1, false)); Run(engine);
            if (recover) engine = new EffectTreeExecutor(Restore(engine.State, "企业候选已抽只读查看"), registry);
            foreach (var preview in engine.State.EffectRuntime.InteractionRequests.Where(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.PreviewInteraction).ToList()) AnswerRequest(engine, preview, "continue");
            while (engine.State.EntrepreneurSetup.Stage != "complete")
            {
                var request = engine.State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.ChooseInteraction);
                if (recover) { engine = new EffectTreeExecutor(Restore(engine.State, "逆序选人待答"), registry); request = engine.State.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == request.GetStableInteractionId()); }
                string selection = engine.State.EntrepreneurSetup.Players.Count == 0 ? firstAbility : request.CandidateIds.First();
                AnswerRequest(engine, request, selection);
            }
            return engine;
        }
        [Test]
        public void InitialEnterpriseChoicesPublishNewVersionsButOldAnswersAndDetachedTasksRemainRejected()
        {
            var engine = SetupPlayers(IsolatedState(2));
            var state = engine.State;
            YC.Domain.Enterprises.EnterpriseEffectExecutor.Register(registry, registry.EnterpriseDefinitions);
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" &&
                r.InteractionTypeId == ExpansionSetupFlow.EnterpriseChoiceInteraction && r.AnsweringPlayerId == 1);
            string id = request.GetStableInteractionId(); int oldRevision = request.StateRevision;
            Assert.That(oldRevision, Is.LessThan(state.EffectRuntime.StateRevision));
            var answer = Strings(request.CandidateIds.Take(request.MinSelections));
            string before = JsonConvert.SerializeObject(state);
            Assert.That(engine.TrySubmitInteraction(id, 1, oldRevision, answer, out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            var detached = GameStateCloneService.DeepClone(state);
            var detachedOwner = detached.EffectRuntime.EffectNodes.Single(n => n.EffectId == request.OwnerEffectId);
            detached.EffectRuntime.EffectNodes.Single(n => n.EffectId == detachedOwner.ParentEffectId)
                .ChildSchedulingGroups.Single(g => g.StableGroupKey == "initial_enterprise_selections").ChildEffectIds.Remove(detachedOwner.EffectId);
            RoundExecutionService.RefreshConcurrentInteractionRevisions(detached);
            Assert.That(detached.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == id).StateRevision, Is.EqualTo(oldRevision));
            RoundExecutionService.RefreshConcurrentInteractionRevisions(state);
            int currentRevision = state.EffectRuntime.StateRevision;
            Assert.That(state.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == id).StateRevision, Is.EqualTo(currentRevision));
            Assert.That(engine.TrySubmitInteraction(id, 1, oldRevision, answer, out _), Is.False);
            Assert.That(engine.TrySubmitInteraction(id, 2, currentRevision, answer, out _), Is.False);
            Assert.That(engine.TrySubmitInteraction(id, 1, currentRevision, Strings(new[] { "not-a-candidate" }), out _), Is.False);
            Assert.That(engine.TrySubmitInteraction(id, 1, currentRevision, answer, out string reason), Is.True, reason); Run(engine);
            RoundExecutionService.RefreshConcurrentInteractionRevisions(state);
            var other = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" &&
                r.InteractionTypeId == ExpansionSetupFlow.EnterpriseChoiceInteraction && r.AnsweringPlayerId == 2);
            Assert.That(engine.TrySubmitInteraction(other.GetStableInteractionId(), 2, other.StateRevision,
                Strings(other.CandidateIds.Take(other.MinSelections)), out reason), Is.True, reason); Run(engine);
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open" && r.InteractionTypeId == ExpansionSetupFlow.EnterpriseChoiceInteraction), Is.False);
            Assert.That(engine.TrySubmitInteraction(id, 1, state.EffectRuntime.StateRevision, answer, out _), Is.False);
            artifacts.WriteLabeledText("真实独立企业选择版本发布及严格拒绝", JsonConvert.SerializeObject(state));
        }
        [TestCase(2)][TestCase(3)][TestCase(4)]
        public void ReverseDraftPreservesOriginalPrivateCandidatesAndOneTimeComponents(int players)
        {
            var engine = SetupPlayers(IsolatedState(players), recover: true); var state = engine.State;
            Assert.That(state.EntrepreneurSetup.Players.Select(p => p.PlayerId), Is.EqualTo(Enumerable.Range(1, players).Reverse()));
            Assert.That(state.EntrepreneurSetup.Players.All(p => p.AllocatedItemIds.Distinct().Count() == p.AllocatedItemIds.Count), Is.True);
            Assert.That(state.EntrepreneurSetup.EnterpriseAssignments.All(a => a.CandidateItemIds.Count == (players == 2 ? 4 : 2)), Is.True);
            Assert.That(state.EffectRuntime.InteractionRequests.Count(r => r.Status == "open" && r.InteractionTypeId == ExpansionSetupFlow.EnterpriseChoiceInteraction), Is.EqualTo(players));
            var own = EntrepreneurContent.Project(state, GameStateViewer.Player(1));
            Assert.That(own.OwnEnterpriseCandidateItemIds, Is.EqualTo(state.EntrepreneurSetup.EnterpriseAssignments.Single(a => a.PlayerId == 1).CandidateItemIds));
            Assert.That(EntrepreneurContent.Project(state, GameStateViewer.Spectator).OwnEnterpriseCandidateItemIds, Is.Empty);
            var martin = state.EntrepreneurSetup.Players.First();
            Assert.That(PlayerAreaPolicy.Supply(state, martin.PlayerId, "core.facility_card").Count, Is.EqualTo(1));
            Assert.That(state.ItemZones.Items.Count(i => i.TypeId == "entrepreneur.round_card" && i.OwnerSubject.PlayerId == martin.PlayerId), Is.EqualTo(4));
            var restored = Restore(state, "设置完成原企业选择待答"); Assert.That(restored.ItemZones.Items.Count, Is.EqualTo(state.ItemZones.Items.Count));
        }
        [Test] public void UnselectedPreviewDoesNotActivateSourcesOrCreateInstances()
        {
            var state = SetupPlayers(IsolatedState(2)).State; int count = state.ItemZones.Items.Count;
            var projected = EntrepreneurContent.Project(state, GameStateViewer.Player(1));
            Assert.That(projected.Players.SelectMany(p => p.RoundSlots).All(s => s.DisplayState == "unselected_preview" && s.SelectedCandidateId == "" && s.ActiveFaceId == "" && s.DisplayCandidateId.Length > 0), Is.True);
            Assert.That(registry.QuerySources(state).Any(s => s.ActiveFaceId.Length > 0), Is.False); Assert.That(state.ItemZones.Items.Count, Is.EqualTo(count));
        }
        [Test] public void DiyThirdCandidateAndMissingPreviewAreExplicit()
        {
            var state = SetupPlayers(IsolatedState(2, diy: true)).State;
            var player = state.EntrepreneurSetup.Players[0]; var definition = state.ContentDefinitions.Entrepreneurs.Single(d => d.DefinitionId == player.DefinitionId);
            definition.RoundGroups.Single(g => g.RoundNumber == 3).PreviewCandidateId = "";
            var slot = EntrepreneurContent.Project(state, GameStateViewer.Player(1)).Players.Single(p => p.PlayerId == player.PlayerId).RoundSlots.Single(g => g.RoundNumber == 3);
            Assert.That(slot.Candidates.Count, Is.EqualTo(3)); Assert.That(slot.DisplayCandidateId, Is.Empty); Assert.That(slot.PreviewDiagnostic, Does.StartWith("diy_preview_candidate_missing:"));
        }
        [Test] public void DamagedSelectionArchiveIsRejectedWithoutRecreatingAllocation()
        {
            var state = SetupPlayers(IsolatedState(2)).State;
            var json = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            var arrays = json.Descendants().OfType<JObject>().Where(o => o["EntrepreneurSetup"] is JObject).ToList();
            Assert.That(arrays.Count, Is.GreaterThan(0)); foreach (var o in arrays) ((JObject)o["EntrepreneurSetup"])["Players"][0]["SetupReceiptId"].Parent.Remove();
            var result = HostRecoveryService.RecoverJson(json.ToString(), registry); Assert.That(result.Succeeded, Is.False);
            artifacts.WriteLabeledText("缺失设置收据拒绝", result.Diagnostic);
        }
        private static void AdvanceTo(EffectTreeExecutor engine, Func<bool> condition)
        {
            for (int i = 0; i < 300 && !condition(); i++) Assert.That(engine.Advance(), Is.True, engine.LastDiagnostic);
            Assert.That(condition(), Is.True, engine.LastDiagnostic);
        }
        private EffectTreeExecutor RoundChoice(GameState state, int round)
        {
            state.Round = round;
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(ExpansionSetupFlow.RoundPreparationSpec(1)); Run(engine);
            while (engine.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open" && r.InteractionTypeId == ExpansionSetupFlow.SupplyRetirementInteraction))
            {
                Assert.That(engine.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.FaceInteraction), Is.False);
                var request = engine.State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == ExpansionSetupFlow.SupplyRetirementInteraction);
                AnswerRequest(engine, request, request.CandidateIds.First());
            }
            return engine;
        }
        [Test] public void FaceSelectionRestoresBeforeAnswerAfterActivationAndBetweenIndependentEntries()
        {
            var engine = SetupPlayers(IsolatedState(2), "entrepreneur.ability.T09");
            engine = RoundChoice(engine.State, 5);
            var earlier = engine.State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.FaceInteraction);
            AnswerRequest(engine, earlier, earlier.CandidateIds.First());
            engine = new EffectTreeExecutor(Restore(engine.State, "正式选面待答"), registry);
            var request = engine.State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.FaceInteraction);
            string candidate = request.CandidateIds.First();
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId + 10, Strings(new[] { candidate }), out _), Is.False);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, Strings(new[] { candidate }), out _), Is.True);
            AdvanceTo(engine, () => engine.State.EntrepreneurSetup.Players.Single(p => p.PlayerId == request.AnsweringPlayerId).RoundGroups.Single(g => g.RoundNumber == 5).SelectedCandidateId.Length > 0);
            engine = new EffectTreeExecutor(Restore(engine.State, "已选启用尚未立即"), registry);
            while (engine.State.EntrepreneurSetup.Players.Single(p => p.PlayerId == request.AnsweringPlayerId).RoundGroups.Single(g => g.RoundNumber == 5).ImmediateEntries.All(e => e.Status == "pending")) Assert.That(engine.Advance(), Is.True);
            engine = new EffectTreeExecutor(Restore(engine.State, "第一条独立立即已调度"), registry);
            AdvanceTo(engine, () => {
                var g = engine.State.EntrepreneurSetup.Players.Single(p => p.PlayerId == request.AnsweringPlayerId).RoundGroups.Single(x => x.RoundNumber == 5);
                return g.ImmediateEntries[0].EffectRootId.Length > 0 && engine.GetNode(g.ImmediateEntries[0].EffectRootId).Status == EffectNodeStatus.Completed && g.ImmediateEntries[1].Status == "pending";
            });
            engine = new EffectTreeExecutor(Restore(engine.State, "两条独立立即之间"), registry);
            Run(engine);
            var p2 = engine.State.EntrepreneurSetup.Players.Single(p => p.PlayerId == request.AnsweringPlayerId);
            var selected = p2.RoundGroups.Single(g => g.RoundNumber == 5);
            Assert.That(selected.ImmediateEntries, Has.Count.EqualTo(2)); Assert.That(selected.ImmediateEntries.All(e => e.Status == "completed"), Is.True);
            Assert.That(selected.ImmediateEntries.Select(e => e.EffectRootId).Distinct().Count(), Is.EqualTo(2));
            Assert.That(engine.State.EffectRuntime.EffectNodes.Where(n => selected.ImmediateEntries.Any(e => e.EffectRootId == n.EffectId)).All(n => n.ParentEffectId == selected.OwnerEffectId), Is.True);
            int score = engine.State.FindPlayer(p2.PlayerId).Score;
            engine = new EffectTreeExecutor(Restore(engine.State, "独立立即完成后"), registry); Run(engine);
            Assert.That(engine.State.FindPlayer(p2.PlayerId).Score, Is.EqualTo(score));
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, Strings(new[] { candidate }), out _), Is.False);
        }
        [Test] public void IndependentImmediateFailureDoesNotCancelItsSibling()
        {
            var engine = SetupPlayers(IsolatedState(2, firstImmediateFails: true), "entrepreneur.ability.T09"); engine = RoundChoice(engine.State, 5);
            var earlier = engine.State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.FaceInteraction);
            AnswerRequest(engine, earlier, earlier.CandidateIds.First());
            var request = engine.State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.FaceInteraction);
            AnswerRequest(engine, request, request.CandidateIds.First());
            var group = engine.State.EntrepreneurSetup.Players.Single(p => p.PlayerId == request.AnsweringPlayerId).RoundGroups.Single(g => g.RoundNumber == 5);
            Assert.That(group.ImmediateEntries.Select(e => e.Status), Is.EqualTo(new[] { "failed", "completed" }));
        }
        [Test] public void DiyThirdCandidateIsSelectableAndEvenRoundDoesNotSelect()
        {
            var engine = SetupPlayers(IsolatedState(2, diy: true)); engine = RoundChoice(engine.State, 3);
            var request = engine.State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.FaceInteraction);
            Assert.That(request.CandidateIds.Count, Is.EqualTo(3)); AnswerRequest(engine, request, request.CandidateIds.Single(id => id.EndsWith("diy-third")));
            var group = engine.State.EntrepreneurSetup.Players.Single(p => p.PlayerId == request.AnsweringPlayerId).RoundGroups.Single(g => g.RoundNumber == 3);
            Assert.That(group.SelectedCandidateId, Does.EndWith("diy-third"));
            foreach (var remaining in engine.State.EffectRuntime.InteractionRequests.Where(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.FaceInteraction).ToList()) AnswerRequest(engine, remaining, remaining.CandidateIds.Single(id => id.EndsWith("diy-third")));
            int requests = engine.State.EffectRuntime.InteractionRequests.Count(r => r.InteractionTypeId == EntrepreneurFlow.FaceInteraction);
            engine = RoundChoice(engine.State, 4);
            Assert.That(engine.State.EffectRuntime.InteractionRequests.Count(r => r.InteractionTypeId == EntrepreneurFlow.FaceInteraction), Is.EqualTo(requests));
            Assert.That(registry.QuerySources(engine.State).Where(s => s.ActiveFaceId.EndsWith("diy-third")).Select(s => s.ItemId).Distinct().Count(), Is.EqualTo(2));
            Restore(engine.State, "DIY第三候选及偶数回合保留旧面");
        }
        [Test] public void AllocationBreakpointDoesNotReissueFixedMarkers()
        {
            var state = IsolatedState(2); var engine = new EffectTreeExecutor(state, registry);
            engine.CreateRoot(ExpansionSetupFlow.PreparationSpec(1, false)); Run(engine);
            foreach (var preview in engine.State.EffectRuntime.InteractionRequests.Where(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.PreviewInteraction).ToList()) AnswerRequest(engine, preview, "continue");
            var request = engine.State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.ChooseInteraction);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, Strings(new[] { "entrepreneur.ability.T09" }), out _), Is.True);
            AdvanceTo(engine, () => engine.State.EntrepreneurSetup.Players.Any(p => p.Stage == "allocated"));
            var ids = engine.State.EntrepreneurSetup.Players.Single().AllocatedItemIds.ToList();
            engine = new EffectTreeExecutor(Restore(engine.State, "T固定两枚已发设置未完成"), registry); Run(engine);
            var player = engine.State.EntrepreneurSetup.Players.Single();
            Assert.That(player.AllocatedItemIds, Is.EqualTo(ids));
            var zone = engine.State.ItemZones.Zones.Single(z => z.ParentId == player.AbilityItemId && z.LocalZoneId == "markers");
            Assert.That(engine.State.ItemZones.Items.Count(i => i.ZoneId == zone.ZoneId), Is.EqualTo(2));
        }
        [Test] public void NormalMainlineMechanismReachesReadOnlyCandidatesAndRestores()
        {
            var state = IsolatedState(3); state.ContentDefinitions.EnterpriseDefinitionIds = pack.Definitions.Where(d => d.ExpansionId == "enterprise" && d.ContentType == "enterprise").Select(d => d.DefinitionId).ToList();
            registry.ContentDefinitions = GameStateCloneService.DeepClone(state.ContentDefinitions);
            var rounds = new RoundExecutionService(registry); var started = rounds.StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            Assert.That(state.EntrepreneurSetup?.Stage, Is.EqualTo("preview"));
            Assert.That(state.EffectRuntime.InteractionRequests.Count(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.PreviewInteraction), Is.EqualTo(3));
            Restore(state, "原入场主链企业家查看阶段");
        }
        [Test] public void ActualLuaImmediateHandlersReceiveTheirOwnDeclaredSourceIdentity()
        {
            var state = IsolatedState(2); var enterprise = registry.EnterpriseDefinitions;
            var patches = new Dictionary<string, string>();
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(pack.RootPath, "pack.json")));
            manifest["enabledExpansionIds"] = new JArray("core", "enterprise", "infected"); patches["pack.json"] = manifest.ToString();
            foreach (string relative in manifest["definitions"].Values<string>().Where(p => p.StartsWith("entrepreneur/")))
            {
                var json = JObject.Parse(File.ReadAllText(Path.Combine(pack.RootPath, relative)));
                string id = (string)json["definitionId"], identity = id + ".isolated.identity";
                json["applicableModes"] = new JArray("standard", "entrepreneur");
                var sources = new List<AbilitySourceDeclaration>(); var handlers = new List<ExternalAbilityHandler>();
                void Add(string ability, string handler, string face, int score)
                {
                    sources.Add(new AbilitySourceDeclaration { DefinitionId = id, AbilityId = ability, Kind = "setup", HandlerRef = handler, DeclarationVersion = "1.0.0", ActiveFaceId = face,
                        AuthorizationPolicy = face.Length > 0 ? "active_selection" : "controller", EffectiveZones = new List<string> { "core.zone.entrepreneur_active" }, PresentationRef = id });
                    if (handlers.Any(h => h.HandlerId == handler)) return;
                    string script = "lua/qy004." + handler + ".lua";
                    handlers.Add(new ExternalAbilityHandler { HandlerId = handler, Script = script, Version = "1.0.0" });
                    patches[script] = "return function(ctx) if ctx.payload.abilityId~='" + ability + "' then error('entry_source_identity_mismatch') end; return { Effect.GainScore({recipient=ctx.playerId,amount=" + score + "}) } end";
                }
                if ((string)json["contentType"] == "entrepreneur_ability") Add(id + ".identity", identity, "", 0);
                else foreach (var f in json["data"]["faces"].ToObject<List<RoundCandidateDefinition>>())
                {
                    // 身份来源不执行；每条立即都有单独声明及真实Lua，而不是共用第一项来源。
                    Add(f.RuleRefs[0], identity + "." + f.FaceId, f.FaceId, 0);
                    foreach (string handler in f.ImmediateHandlerRefs) Add(f.RuleRefs[0] + ".entry." + (f.ImmediateHandlerRefs.IndexOf(handler) + 1), handler, f.FaceId, f.ImmediateHandlerRefs.IndexOf(handler) + 1);
                }
                json["abilitySources"] = JArray.FromObject(sources); json["abilityHandlers"] = JArray.FromObject(handlers); patches[relative] = json.ToString();
            }
            string actualRoot = artifacts.CopyContent(pack.RootPath, "004真实Lua来源绑定隔离包", patches);
            var actual = ExternalContentPack.Load(actualRoot); artifacts.RecordContent("真实Lua来源内容", actualRoot, actual.ContentHash);
            registry = new EffectRegistry { ContentPackHash = actual.ContentHash, EnterpriseDefinitions = enterprise };
            ResourceEffectExecutor.Register(registry); ExpansionSetupFlow.Register(registry); ItemTransferEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry);
            AbilitySourceLuaCatalog.Register(registry, actual);
            state.ContentPackHash = state.RuleConfiguration.ContentPackHash = actual.ContentHash;
            state.ContentDefinitions.AbilitySources = registry.AbilityDeclarations.Select(GameStateCloneService.DeepClone).ToList();
            registry.ContentDefinitions = GameStateCloneService.DeepClone(state.ContentDefinitions);
            var engine = SetupPlayers(state, "entrepreneur.ability.T09"); engine = RoundChoice(engine.State, 5);
            var earlier = engine.State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.FaceInteraction);
            AnswerRequest(engine, earlier, earlier.CandidateIds.First());
            var request = engine.State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.FaceInteraction);
            int before = engine.State.FindPlayer(request.AnsweringPlayerId).Score; AnswerRequest(engine, request, request.CandidateIds.First());
            Assert.That(engine.State.FindPlayer(request.AnsweringPlayerId).Score - before, Is.EqualTo(3));
            Restore(engine.State, "真实Lua两条立即各自来源恢复");
        }
    }
}

