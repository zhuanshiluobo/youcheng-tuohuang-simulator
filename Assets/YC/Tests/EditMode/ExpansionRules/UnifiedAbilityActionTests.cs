using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Application.Presentation;
using YC.Domain.Presentation;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Influence;
using YC.Domain.SpecialActions;
using YC.Domain.Enterprises;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>隔离协议回归。实际JSON→生产装载器→Lua→真实Effect/付款/归档；不计正常游戏或画面验收。</summary>
    public sealed class UnifiedAbilityActionTests
    {
        private const string Token = "infected.component.gavial_token", Ability = "qy002.fixture.source", Quick = "qy002.fixture.quick", Special = "qy002.fixture.special", Caller = "qy002.fixture.caller";
        private ExternalContentPack pack;
        private EffectRegistry registry;
        private string fixtureRoot;
        private IsolatedTestArtifacts artifacts;
        private static NormalizedValue Args => Object();
        [SetUp] public void LoadActualContentAndIsolatedDeclaration()
        {
            artifacts = IsolatedTestArtifacts.BeginCase();
            string source = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            source=artifacts.CopyContent(source,"通用行动旧回归完整未修改基线");
            // 仅从完整私有基线读取、派生；不读取UI原档案。
            var replacements = new Dictionary<string, string>();
            string tokenPath = Path.Combine(source, "component/" + Token + ".json");
            var token = JObject.Parse(File.ReadAllText(tokenPath));
            var declaration = new AbilitySourceDeclaration { DefinitionId = Token, AbilityId = Ability, Kind = "quick", HandlerRef = "qy002.fixture.body", DeclarationVersion = "1.0.0",
                EffectiveZones = new List<string> { "core.zone.box_reserve" }, AuthorizationPolicy = "owner", PresentationRef = Token,
                AllowedOperations = new List<string> { "action.invoke:qy002.fixture.direct" }, Actions = new List<AbilityActionDeclaration> {
                    new AbilityActionDeclaration { ActionId = Quick, EntryKind = "quick", BodyHandlerRef = "qy002.fixture.body", EntryCostRef = "qy002.fixture.cost", PresentationRef = Token, DirectCallPolicy = "qy002.fixture.direct" },
                    new AbilityActionDeclaration { ActionId = Special, EntryKind = "special", BodyHandlerRef = "qy002.fixture.body", EntryCostRef = "qy002.fixture.cost", EntryAfterEffectsRef = "qy002.fixture.after", PresentationRef = Token },
                    new AbilityActionDeclaration { ActionId = Caller, EntryKind = "quick", BodyHandlerRef = "qy002.fixture.call", PresentationRef = Token },
                    new AbilityActionDeclaration { ActionId = "qy002.fixture.once", EntryKind = "quick", BodyHandlerRef = "qy002.fixture.body", LifecyclePolicy = "once", PresentationRef = Token } } };
            var querySource = new AbilitySourceDeclaration { DefinitionId = Token, AbilityId = "qy002.fixture.query", Kind = "ruleQuery", HandlerRef = "qy002.fixture.query", DeclarationVersion = "1.0.0",
                EffectiveZones = new List<string> { "core.zone.box_reserve" }, AuthorizationPolicy = "owner", PresentationRef = Token, QueryKinds = new List<string> { "scoringModifiers.v1" } };
            var eventSource = new AbilitySourceDeclaration { DefinitionId = Token, AbilityId = "qy002.fixture.event", Kind = "event", HandlerRef = "qy002.fixture.body", DeclarationVersion = "1.0.0",
                EffectiveZones = new List<string> { "core.zone.box_reserve" }, AuthorizationPolicy = "owner", PresentationRef = Token, ObservedEvents = new List<string> { "CollectionStarted" } };
            token["abilitySources"] = JArray.FromObject(new[] { declaration, querySource, eventSource });
            var handlers = new List<ExternalAbilityHandler>();
            foreach (var pair in new Dictionary<string, string> {
                ["body"] = "return function(ctx) if ctx.payload.abilityId=='qy002.fixture.event' and (ctx.eventType~='CollectionStarted' or ctx.payload.triggeringPlayerId~=1 or ctx.payload.triggerEventType~='CollectionStarted') then error('trigger_identity_lost') end; return { Effect.GainResource({recipient=ctx.playerId,resourceType='GoldVoucher',amount=5}) } end",
                ["cost"] = "return function(ctx) return { Effect.PayResource({player=ctx.playerId,cost={GoldVoucher=3}}) } end",
                ["after"] = "return function(ctx) return { Effect.GainScore({recipient=ctx.playerId,amount=1}) } end",
                ["call"] = "return function(ctx) return { Effect.InvokeAction({apiVersion=1,player=ctx.playerId,itemId=ctx.payload.sourceItemId,actionId='qy002.fixture.quick',callPolicyRef='qy002.fixture.direct'}) } end",
                ["query"] = "return function(ctx) local kind='fixture_fact'; if ctx.payload.facts.bad then kind='invalid' end; return {queryId=ctx.payload.queryId,basisDigest=ctx.payload.basisDigest,items={{kind=kind,playerId=ctx.payload.authorizedPlayerId}}} end" })
            {
                string relative = "lua/qy002_fixture_" + pair.Key + ".lua"; replacements[relative] = pair.Value;
                handlers.Add(new ExternalAbilityHandler { HandlerId = "qy002.fixture." + pair.Key, Script = relative, Version = "1.0.0" });
            }
            token["abilityHandlers"] = JArray.FromObject(handlers);
            token["actionCallPolicies"] = JArray.FromObject(new[] { new AbilityActionCallPolicy { PolicyRef = "qy002.fixture.direct", CallerAbilityId = Ability, CallerHandlerRef = "qy002.fixture.call", TargetActionId = Quick, OmitEntryCost = true, OmitEntryAfterEffects = true, OmitMainAction = true } });
            replacements["component/" + Token + ".json"] = token.ToString();
            var manifestPath = Path.Combine(source, "pack.json"); var manifest = JObject.Parse(File.ReadAllText(manifestPath));
            if (!((JArray)manifest["definitions"]).Values<string>().Contains("component/" + Token + ".json")) ((JArray)manifest["definitions"]).Add("component/" + Token + ".json");
            manifest["enabledExpansionIds"] = new JArray("core", "enterprise", "infected"); replacements["pack.json"] = manifest.ToString();
            fixtureRoot = artifacts.CopyContent(source, "统一行动基础内容", replacements);
            pack = ExternalContentPack.Load(fixtureRoot);
            artifacts.RecordContent("统一行动装载内容", fixtureRoot, pack.ContentHash);
        }
        [TearDown] public void RecordActualTestResult() => artifacts?.Finish();
        private GameState State(int gold = 20, bool realMap = false, int companyCount = 0, ExternalContentPack content = null)
        {
            var pack = content ?? this.pack;
            registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry); CharacterCoverEffectExecutor.Register(registry);
            CharacterCardLuaCatalog.Register(registry, pack); CityStyleLuaCatalog.Register(registry, pack); ContentLifecycleLuaCatalog.Register(registry, pack); AbilitySourceLuaCatalog.Register(registry, pack);
            var map = realMap ? StaticMapDefinitions.Resolve(StaticMapDefinitions.FourPlayerMapId) : new GameMapDefinition { MapId = "qy002.isolated", MinPlayers = 2, MaxPlayers = 2,
                Locations = new List<MapLocationDefinition> { new MapLocationDefinition { LocationId = "harbor", RegionId = "harbor_region", CanDockCity = true } },
                Regions = new List<MapRegionDefinition> { new MapRegionDefinition { RegionId = "harbor_region" } } };
            var source = new GameState { GameId = "qy002.isolated", ContentPackHash = pack.ContentHash, MapId = map.MapId,
                ContentDefinitions = new GameContentDefinitions { UsageTracks = pack.ActiveDefinitions.SelectMany(d => d.UsageTracks).ToList(), Facilities = pack.CreateFacilities().ToList(), Components = pack.ActiveDefinitions.Where(d => d.ContentType == "component").Select(d => new ComponentContentDefinition { DefinitionId = d.DefinitionId, TypeId = d.ItemTypeId, QuantityPolicy = (string)d.Data["quantityPolicy"], CubeAppearance = d.CubeAppearance }).ToList(), Characters = pack.CreateCharacters().ToList(), AbilitySources = registry.AbilityDeclarations.ToList(),
                    Awards = ContentLifecycleLuaCatalog.CreateAwards(pack), CityStyles = pack.CreateCityStyles().ToList(), SpecialActions = pack.CreateSpecialActions().ToList() },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Name = "甲", Resources = new ResourceSet { GoldVoucher = gold } }, new PlayerState { PlayerId = 2, Name = "乙" } },
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = pack.ContentHash } };
            var state = new ItemZoneStateFactory().CreateForSetup(source, map, new[] {
                new ItemRecipeEntry { EntryId = "token", DefinitionId = Token, TypeId = "infected.gavial_token", Quantity = 1, PerPlayer = true, InitialZoneRole = "reserve" },
                new ItemRecipeEntry { EntryId = "markers", DefinitionId = "component.marker", TypeId = "core.player_marker", Quantity = 6, PerPlayer = true, InitialZoneRole = "supply" },
                new ItemRecipeEntry { EntryId = "style", DefinitionId = "city_style_source_stone_industrial_hub", TypeId = "core.board_card", Quantity = 1, InitialZoneRole = "city_styles" },
                new ItemRecipeEntry { EntryId = "support", DefinitionId = "infected.award.permanent_support", TypeId = "core.award_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "reserve" },
                new ItemRecipeEntry { EntryId = "quickAward", DefinitionId = "infected.award.enterprise_special", TypeId = "core.award_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "reserve" },
                new ItemRecipeEntry { EntryId = "contract", DefinitionId = CompanyInfluenceRules.ContractDefinitionId, TypeId = "core.contract_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "reserve" },
                new ItemRecipeEntry { EntryId = "company", DefinitionId = "infected.component.company_marker", TypeId = "infected.company_marker", Quantity = companyCount, InitialZoneRole = "company_markers" },
                new ItemRecipeEntry { EntryId = "characters", DefinitionId = "texas", TypeId = "core.basic_character_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" } }.Concat(new[] { (0, "blue"), (3, "red"), (4, "blue"), (6, "yellow"), (7, "yellow"), (8, "red") }.Select(cell => new ItemRecipeEntry { EntryId = "qy003.style.facility." + cell.Item1, DefinitionId = pack.CreateFacilities().First(f => f.Color == cell.Item2).FacilityId, TypeId = "core.facility_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "city_cell_" + cell.Item1 })).ToList());
            state.Phase = GamePhase.ActionRound1; state.Round = 1; state.ActionRound = 1; state.StartPlayerId = 1; state.CurrentPlayerId = 1;
            return state;
        }
        private AbilityActionOption Option(GameState state, string action = Quick) => registry.QueryActions(state, 1).Single(o => o.ActionId == action);
        private EffectTreeExecutor Begin(GameState state, string command, out string owner, string action = Quick)
        { var engine = new EffectTreeExecutor(state, registry); owner = engine.CreateRoot(AbilityActionExecutor.Create(Option(state, action), command, Args)); return engine; }
        private void Run(EffectTreeExecutor engine) { var report = engine.RunUntilQuiescent(); Assert.That(report.Faulted, Is.False, engine.State.EffectRuntime.LastFaultMessage); }
        private GameState Restore(GameState state, string label)
        {
            var result = artifacts.Recover(state, label, registry); Assert.That(result.Succeeded, Is.True, result.Diagnostic); return result.State;
        }
        private static void Until(EffectTreeExecutor engine, Func<bool> condition)
        { for (int i = 0; i < 50 && !condition(); i++) Assert.That(engine.Advance(), Is.True, engine.LastDiagnostic); Assert.That(condition(), Is.True); }

        private const string BudgetPolicy = "qy002.r2.style.budget";
        private ExternalContentPack BudgetPack(bool omitMain)
        {
            string tokenPath = Path.Combine(fixtureRoot, "component/" + Token + ".json");
            string stylePath = Path.Combine(fixtureRoot, "city_style/city_style_source_stone_industrial_hub.json");
            string supportPath = Path.Combine(fixtureRoot, "award/infected.award.permanent_support.json");
            string tokenOriginal = File.ReadAllText(tokenPath), styleOriginal = File.ReadAllText(stylePath), supportOriginal = File.ReadAllText(supportPath);
            var token = JObject.Parse(tokenOriginal); var style = JObject.Parse(styleOriginal);
            string script = "lua/qy002_r2_call_style.lua";
            string callerScript = "return function(ctx) for _,o in ipairs(ctx.payload.callableActions) do if o.actionId=='special_action.city_style.source_stone_industrial_hub' then return {Effect.InvokeAction({apiVersion=1,player=ctx.playerId,itemId=o.itemId,actionId=o.actionId,callPolicyRef=o.callPolicyRef})} end end; return {} end";
            var policy = new AbilityActionCallPolicy { PolicyRef = BudgetPolicy, CallerAbilityId = "qy002.fixture.event", CallerHandlerRef = "qy002.r2.call_style", TargetActionId = SpecialActionDatabase.SourceStoneIndustrialHub, OmitMainAction = omitMain };
            var support = JObject.Parse(supportOriginal);
            if (omitMain)
            {
                // 只改隔离副本的正文；沿正式cleanup声明、真实持有物和结算窗口验证豁免。
                policy.CallerAbilityId = (string)support["abilitySources"][0]["abilityId"];
                policy.CallerHandlerRef = (string)support["abilitySources"][0]["handlerRef"];
                support["abilities"][0]["script"] = script;
                support["abilityHandlers"] = JArray.FromObject(new[] { new ExternalAbilityHandler { HandlerId = policy.CallerHandlerRef, Script = script, Version = "1.0.0" } });
                support["abilitySources"][0]["allowedOperations"] = new JArray("action.invoke:" + BudgetPolicy);
                support["actionCallPolicies"] = JArray.FromObject(new[] { policy });
            }
            else
            {
                var caller = (JObject)((JArray)token["abilitySources"]).Single(s => (string)s["AbilityId"] == "qy002.fixture.event");
                caller["HandlerRef"] = "qy002.r2.call_style"; caller["AllowedOperations"] = new JArray("action.invoke:" + BudgetPolicy);
                ((JArray)token["abilityHandlers"]).Add(JObject.FromObject(new ExternalAbilityHandler { HandlerId = "qy002.r2.call_style", Script = script, Version = "1.0.0" }));
                ((JArray)token["actionCallPolicies"]).Add(JObject.FromObject(policy));
            }
            style["abilitySources"][0]["actions"][0]["directCallPolicy"] = BudgetPolicy;
            string root = artifacts.CopyContent(fixtureRoot, "预算调用内容", new Dictionary<string, string> { ["component/" + Token + ".json"] = token.ToString(),
                ["city_style/city_style_source_stone_industrial_hub.json"] = style.ToString(), ["award/infected.award.permanent_support.json"] = support.ToString(), [script] = callerScript });
            var content = ExternalContentPack.Load(root); artifacts.RecordContent("预算调用装载内容", root, content.ContentHash); return content;
        }
        private GameState BudgetCall(int available, int restricted, bool omitMain, out EffectTreeExecutor engine, out string marker, out EffectNodeRuntimeState direct)
        {
            var state = State(content: BudgetPack(omitMain)); marker = DeclareStyleFixture(state, 1);
            var player = state.FindPlayer(1); player.RemainingMainActionsThisTurn = available; player.RestrictedMainActionsThisTurn = restricted; player.ActedMainActionThisTurn = available == 0;
            if (omitMain) GiftSupport(state);
            registry.Register("qy002.r2.signal", c => EffectStepResult.Completed().AddEvent(new EffectEventRequest { EventType = omitMain ? "PlayerCleanupStarted" : "CollectionStarted", PlayerId = 1 }));
            engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(EffectSpec.Create("qy002.r2.signal", playerId: 1));
            var executing = engine;
            if (omitMain)
            {
                Run(executing); var entry = registry.GetSettlementOptions(state, 1).Single();
                Assert.That(executing.TryInvokeCharacterSettlementOption(1, entry.OptionId, "", state.EffectRuntime.StateRevision, out _, out string error), Is.True, error);
            }
            Until(executing, () => state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == AbilityActionExecutor.DirectTypeId && n.PlayerId == 1));
            direct = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == AbilityActionExecutor.DirectTypeId && n.PlayerId == 1);
            return state;
        }
        [TestCase(0, 0, false)][TestCase(1, 1, false)][TestCase(1, 0, false)][TestCase(0, 0, true)]
        public void R2AuthorizedLuaCallMatchesUnwaivedBudgetAndDoesNotTrustDisabledPreview(int available, int restricted, bool waive)
        {
            var state = BudgetCall(available, restricted, waive, out var engine, out string marker, out var direct);
            bool allowed = waive || available > restricted;
            var preview = registry.QueryAuthorizedActions(state, direct.ParentEffectId, BudgetPolicy).Single();
            Assert.That(preview.Enabled, Is.EqualTo(allowed)); Run(engine);
            var invocation = state.EffectRuntime.AbilityActionInvocations.SingleOrDefault();
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == marker).SlotId, Is.EqualTo(allowed ? "used_from_2" : "2"));
            Assert.That(state.EffectRuntime.EffectNodes.SelectMany(n => n.PositionChanges).Count(c => c.ItemId == marker && c.From.SlotId == "2" && c.To.SlotId == "used_from_2"), Is.EqualTo(allowed ? 1 : 0));
            if (waive) Assert.That(state.EffectRuntime.EffectNodes.SelectMany(n => n.PositionChanges).Count(c => c.ItemId == marker && c.From.SlotId == "used_from_2" && c.To.SlotId == "1"), Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(allowed ? 14 : 20));
            Assert.That(state.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(allowed && !waive ? 1 : 0));
            Assert.That(invocation != null, Is.EqualTo(allowed));
            if (allowed)
            {
                Assert.That(invocation.MainActionCommitted, Is.EqualTo(!waive));
                Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(available + 2 - (waive ? 0 : 1)));
            }
            else
            {
                Assert.That(engine.GetNode(direct.EffectId).FailureReason, Is.EqualTo(preview.DisabledReason));
                Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == CityStyleSpecialActionEffectTypeIds.GrantMainActions), Is.False);
                Assert.That(state.FindPlayer(1).CharacterCardLockedThisTurn, Is.False);
            }
            Restore(state, "R2授权预算-" + available + "-" + restricted + "-" + waive);
        }
        [TestCase(false)][TestCase(true)]
        public void R2BudgetLostBeforeActivationKeepsPaymentBoundaryAndDoesNotActivate(bool afterPayment)
        {
            var state = BudgetCall(1, 0, false, out var engine, out string marker, out _);
            Until(engine, () => state.EffectRuntime.AbilityActionInvocations.Count == 1);
            if (afterPayment) Until(engine, () => state.EffectRuntime.EffectNodes.Any(n => n.Payment?.IsCommitted == true));
            new MainActionBudgetService().SpendCompletedMainAction(state, 1, true); RuleCommit.Apply(state, "r2.other-budget-use");
            state = Restore(state, "R2激活前额度变化-" + afterPayment); engine = new EffectTreeExecutor(state, registry);
            var report = engine.RunUntilQuiescent(); Assert.That(report.Faulted, Is.EqualTo(afterPayment));
            var invocation = state.EffectRuntime.AbilityActionInvocations.Single();
            Assert.That(invocation.ActivationCommitted, Is.False); Assert.That(invocation.MainActionCommitted, Is.False);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(afterPayment ? 14 : 20));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == marker).SlotId, Is.EqualTo("2"));
            Assert.That(state.FindPlayer(1).CharacterCardLockedThisTurn, Is.False);
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == CityStyleSpecialActionEffectTypeIds.GrantMainActions), Is.False);
            Restore(state, "R2激活前额度变化结果-" + afterPayment);
        }
        [Test] public void R2CompletionCannotClaimACommitWhenTheRequiredBudgetIsGone()
        {
            var state = BudgetCall(1, 0, false, out var engine, out string marker, out _);
            Until(engine, () => state.FindPlayer(1).RemainingMainActionsThisTurn == 3);
            for (int n = 0; n < 3; n++) new MainActionBudgetService().SpendCompletedMainAction(state, 1, true);
            RuleCommit.Apply(state, "r2.completion-budget-use"); state = Restore(state, "R2正文后额度变化");
            engine = new EffectTreeExecutor(state, registry); Assert.That(engine.RunUntilQuiescent().Faulted, Is.True);
            var invocation = state.EffectRuntime.AbilityActionInvocations.Single();
            Assert.That(invocation.MainActionCommitted, Is.False); Assert.That(invocation.ActivationCommitted, Is.True);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(14)); Assert.That(state.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(3));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == marker).SlotId, Is.EqualTo("used_from_2"));
            Restore(state, "R2正文后额度故障保留结果");
        }
        [Test] public void R2OrdinarySpecialEntryAlsoRechecksBudgetAfterItsRealPayment()
        {
            var state = State(); string marker = DeclareStyleFixture(state, 1);
            var option = registry.QueryActions(state, 1).Single(o => o.ActionId == SpecialActionDatabase.SourceStoneIndustrialHub);
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(AbilityActionExecutor.Create(option, "r2.ordinary.special", Args));
            Until(engine, () => state.EffectRuntime.EffectNodes.Any(n => n.Payment?.IsCommitted == true));
            new MainActionBudgetService().SpendCompletedMainAction(state, 1, true); RuleCommit.Apply(state, "r2.ordinary-budget-change");
            state = Restore(state, "R2普通特殊入口付款后额度变化"); engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.True); var invocation = state.EffectRuntime.AbilityActionInvocations.Single();
            Assert.That(invocation.PaymentCommitted, Is.True); Assert.That(invocation.ActivationCommitted, Is.False); Assert.That(invocation.MainActionCommitted, Is.False);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(14)); Assert.That(state.FindPlayer(1).CharacterCardLockedThisTurn, Is.False);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == marker).SlotId, Is.EqualTo("2"));
            Restore(state, "R2普通特殊入口故障保留付款");
        }
        private AbilityActionOption CoveredRole(GameState state)
        {
            state.StartPlayerId = 2; var engine = new EffectTreeExecutor(state, registry);
            engine.CreateRoot(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Run(engine);
            var request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
            string item = state.ItemZones.Items.Single(i => i.DefinitionId == "texas" && i.OwnerSubject.PlayerId == 1).ItemId;
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateString(item), out string error), Is.True, error); Run(engine);
            return registry.QueryActions(state, 1).Single(o => o.SourceRef.ItemId == item);
        }
        [TestCase("mode", "invalid", null)][TestCase("mode", "both", null)][TestCase("mode", "both", "invalid")]
        [TestCase("mode", "strategy", "strategy-first")][TestCase("mode", "", "tactic-first")]
        [TestCase("offerSecondEffect", "yes", null)][TestCase("offerSecondEffect", "True", null)]
        [TestCase("offerSecondEffect", "1", null)][TestCase("offerSecondEffect", "", null)]
        public void R2InvalidRoleArgumentsAreRejectedBySessionBeforeAnyAcceptance(string field, string value, string order)
        {
            var state = State(); var option = CoveredRole(state); var before = GameStateCloneService.DeepClone(state);
            var command = AbilityActionCommand.Create(1, option); command.CommandId = "r2.bad.role"; command.Parameters[field] = value;
            if (order != null) command.Parameters["order"] = order;
            var session = new GameSession(state); session.RegisterHandler(new InvokeSettlementOptionCommandHandler(registry));
            var result = session.Submit(command); Assert.That(result.Succeeded, Is.False, result.Validation.Reason);
            Assert.That(GameStateCloneService.AreEquivalent(before, session.State), Is.True);
            Assert.That(session.State.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active));
            Assert.That(session.State.EffectRuntime.AbilityActionInvocations, Is.Empty); Assert.That(session.State.CharacterUses, Is.Empty);
            Restore(session.State, "R2非法角色参数-" + field + "-" + value + "-" + order);
        }
        [TestCase("mode", "")][TestCase("mode", "strategy")][TestCase("order", "")][TestCase("offerSecondEffect", "false")]
        public void R2NonRoleSourceCannotCarryRoleArgumentFields(string field, string value)
        {
            var state = State(); var before = GameStateCloneService.DeepClone(state);
            var command = AbilityActionCommand.Create(1, Option(state)); command.CommandId = "r2.non-role"; command.Parameters[field] = value;
            var session = new GameSession(state); session.RegisterHandler(new InvokeSettlementOptionCommandHandler(registry));
            Assert.That(session.Submit(command).Succeeded, Is.False);
            Assert.That(GameStateCloneService.AreEquivalent(before, session.State), Is.True); Restore(session.State, "R2不适用角色参数-" + field + "-" + value);
        }
        [TestCase(false)][TestCase(true)]
        public void R2EmptyRoleDefaultsStillOpenTheOriginalFormalChoice(bool explicitDefaults)
        {
            var state = State(); var command = AbilityActionCommand.Create(1, CoveredRole(state)); command.CommandId = "r2.role.defaults";
            if (explicitDefaults) { command.Parameters["mode"] = ""; command.Parameters["order"] = ""; command.Parameters["offerSecondEffect"] = "false"; }
            var session = new GameSession(state); session.RegisterHandler(new InvokeSettlementOptionCommandHandler(registry));
            Assert.That(session.Submit(command).Succeeded, Is.True); Assert.That(session.State.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active));
            Assert.That(session.State.EffectRuntime.InteractionRequests.Count(r => r.Status == "open" && r.InteractionTypeId == CharacterLifecycleService.ModeInteractionTypeId), Is.EqualTo(1));
            Restore(session.State, "R2角色默认选择-" + explicitDefaults);
        }
        [TestCase("strategy", "", "false")][TestCase("tactic", "", "true")]
        [TestCase("both", "strategy-first", "false")][TestCase("both", "tactic-first", "true")]
        public void R2ValidRoleArgumentsReachOriginalLifecycleThroughSession(string mode, string order, string offer)
        {
            var state = State(); var option = CoveredRole(state);
            if (mode == "both") { state.StartPlayerId = 1; option = registry.QueryActions(state, 1).Single(o => o.SourceRef.ItemId == option.SourceRef.ItemId); }
            var command = AbilityActionCommand.Create(1, option); command.CommandId = "r2.role.valid";
            command.Parameters["mode"] = mode; command.Parameters["order"] = order; command.Parameters["offerSecondEffect"] = offer;
            var session = new GameSession(state); session.RegisterHandler(new InvokeSettlementOptionCommandHandler(registry));
            var result = session.Submit(command); Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(session.State.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active));
            Assert.That(session.State.EffectRuntime.AbilityActionInvocations.Count, Is.EqualTo(1));
            var use = session.State.CharacterUses.Single();
            Assert.That(use.ModeOrder.First(), Is.EqualTo(mode == "both" ? order == "strategy-first" ? "strategy" : "tactic" : mode));
            Assert.That(use.ModeOrder.Count, Is.EqualTo(mode == "both" ? 2 : 1));
            Restore(session.State, "R2合法角色参数-" + mode + "-" + order + "-" + offer);
        }
        [Test] public void R2RecoveryRejectsArgumentsOutsideTheFrozenHandlerProtocol()
        {
            var state = State(); var option = CoveredRole(state); var engine = new EffectTreeExecutor(state, registry);
            string ownerId = engine.CreateRoot(AbilityActionExecutor.Create(option, "r2.corrupt.arguments", Args));
            Until(engine, () => state.EffectRuntime.AbilityActionInvocations.Count == 1);
            var owner = engine.GetNode(ownerId); var arguments = Value(owner.NormalizedArguments, "arguments");
            arguments.Properties.Add(new NormalizedValueEntry { Name = "mode", Value = NormalizedValue.CreateString("invalid") });
            state.EffectRuntime.AbilityActionInvocations.Single().CommandDigest = AbilityActionExecutor.Digest(owner.NormalizedArguments);
            var archive = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state));
            var result = HostRecoveryService.RecoverJson(archive, registry); Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Diagnostic, Does.Contain("arguments_invalid"));
        }
        [Test] public void R2EarlierActionExecutorVersionIsRejectedInsteadOfSilentlyResuming()
        {
            var state = State(); var engine = Begin(state, "r2.old.version", out _); Until(engine, () => state.EffectRuntime.AbilityActionInvocations.Count == 1);
            var archive = HostRecoveryService.CreateArchive(state); archive.Snapshot.State.EffectRuntime.AbilityActionInvocations.Single().VersionRef.ExecutorVersion = "1.0.0";
            var result = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), registry);
            Assert.That(result.Succeeded, Is.False); Assert.That(result.Diagnostic, Does.Contain("ability_action.v1"));
        }
        [Test] public void JsonDeclaredTokenOffersBothKindsWithoutAwardHistory()
        {
            var state = State(); var options = registry.QueryActions(state, 1);
            Assert.That(options.Select(o => o.EntryKind), Is.EquivalentTo(new[] { "quick", "special", "quick", "quick" }));
            Assert.That(options.All(o => o.Enabled), Is.True); Assert.That(state.EffectRuntime.EffectNodes, Is.Empty);
            Assert.That(registry.QuerySources(state, 2).Single(s => s.AbilityId == Ability).SourceKey, Is.Not.EqualTo(options[0].SourceRef.SourceKey));
        }
        [Test] public void NoDeclarationMeansNoEntryAndCostInsufficientRemainsVisible()
        {
            var state = State(2); Assert.That(Option(state).Enabled, Is.False);
            // 正式注册报价器返回统一PaymentPlan诊断；公开理由表已明确两种历史拼写同义。
            Assert.That(Option(state).DisabledReason, Is.EqualTo("insufficient_resource"));
            Assert.That(AbilityActionReasons.Display(Option(state).DisabledReason), Is.EqualTo(AbilityActionReasons.Display("insufficient_resources")));
            Assert.That(registry.QueryActions(state, 1).Any(o => o.SourceRef.DefinitionId == "texas"), Is.False);
        }
        [TestCase("accepted")][TestCase("entry_payment")][TestCase("paid")][TestCase("activated")][TestCase("body")]
        public void RecoverEveryBoundaryWithoutChargingOrConstructingBodyAgain(string boundary)
        {
            var state = State(); var engine = Begin(state, "recover." + boundary, out string owner);
            Until(engine, () => state.EffectRuntime.AbilityActionInvocations.Count == 1);
            if (boundary != "accepted") Until(engine, () => state.EffectRuntime.AbilityActionInvocations.Single().Phase == "entry_payment");
            if (boundary == "paid" || boundary == "activated" || boundary == "body") Until(engine, () => state.EffectRuntime.EffectNodes.Any(n => n.Payment?.IsCommitted == true));
            if (boundary == "activated" || boundary == "body") Until(engine, () => state.EffectRuntime.AbilityActionInvocations.Single().ActivationCommitted);
            if (boundary == "body") Until(engine, () => state.EffectRuntime.AbilityActionInvocations.Single().Phase == "body");
            state = Restore(state, boundary); engine = new EffectTreeExecutor(state, registry); Run(engine);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(22)); Assert.That(state.EffectRuntime.AbilityActionInvocations.Single().Phase, Is.EqualTo("completed"));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == ResourceEffectTypeIds.Pay), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == ResourceEffectTypeIds.Gain), Is.EqualTo(1));
        }
        [Test] public void SameCommandReplaysOriginalReceiptAndChangedPayloadIsRejected()
        {
            var state = State(); var option = Option(state); var engine = Begin(state, "repeat", out string owner); Run(engine);
            Assert.That(engine.TryInvokeAbilityAction(1, option.OptionId, option.RequestRef.InteractionId, option.RequestRef.FactsDigest, option.VersionRef.ContentPackHash, "repeat", Args, out string replay, out string error), Is.True, error);
            Assert.That(replay, Is.EqualTo(owner)); Run(engine); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(22));
            var changed = Object("mode", NormalizedValue.CreateString("tactic"));
            Assert.That(engine.TryInvokeAbilityAction(1, option.OptionId, option.RequestRef.InteractionId, option.RequestRef.FactsDigest, option.VersionRef.ContentPackHash, "repeat", changed, out _, out error), Is.False);
            Assert.That(error, Is.EqualTo("action_command_payload_conflict"));
        }
        [Test] public void OtherPlayersUnrelatedCommitDoesNotExpireActionFacts()
        {
            var state = State(); var option = Option(state); state.FindPlayer(2).Score++; RuleCommit.Apply(state, "unrelated");
            var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TryInvokeAbilityAction(1, option.OptionId, option.RequestRef.InteractionId, option.RequestRef.FactsDigest, option.VersionRef.ContentPackHash, "facts", Args, out _, out string error), Is.True, error); Run(engine);
        }
        [Test] public void SourceLossRejectsOldCommandWithoutPaying()
        {
            var state = State(); var option = Option(state); state.ItemZones.Items.Single(i => i.ItemId == option.SourceRef.ItemId).ZoneId = FindZone(state, "retired").ZoneId;
            var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TryInvokeAbilityAction(1, option.OptionId, option.RequestRef.InteractionId, option.RequestRef.FactsDigest, option.VersionRef.ContentPackHash, "lost", Args, out _, out _), Is.False);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(20)); Assert.That(state.EffectRuntime.AbilityActionInvocations, Is.Empty);
        }
        [Test] public void SourceLossAfterPaymentFaultsAndPreservesFirstPayment()
        {
            var state = State(); var engine = Begin(state, "paid-lost", out _); Until(engine, () => state.EffectRuntime.EffectNodes.Any(n => n.Payment?.IsCommitted == true));
            state.ItemZones.Items.Single(i => i.ItemId == state.EffectRuntime.AbilityActionInvocations.Single().SourceRef.ItemId).ZoneId = FindZone(state, "retired").ZoneId;
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.True); Assert.That(state.EffectRuntime.AbilityActionInvocations.Single().Phase, Is.EqualTo("faulted"));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(17)); Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.Payment?.IsCommitted == true), Is.EqualTo(1));
            Restore(state, "首次付款后来源失效故障");
        }
        [Test] public void SessionPublishesPaidFaultInsteadOfRollingBackTheAcceptedInvocation()
        {
            var state = State(); var option = Option(state); var engine = Begin(state, "session.paid-fault", out _);
            Until(engine, () => state.EffectRuntime.EffectNodes.Any(n => n.Payment?.IsCommitted == true));
            state.ItemZones.Items.Single(i => i.ItemId == option.SourceRef.ItemId).ZoneId = FindZone(state, "retired").ZoneId;
            var command = AbilityActionCommand.Create(1, option); command.CommandId = "session.paid-fault";
            var session = new GameSession(state); session.RegisterHandler(new InvokeSettlementOptionCommandHandler(registry));
            var result = session.Submit(command); Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(session.State.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.PausedFault));
            Assert.That(session.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(17)); Assert.That(session.State.EffectRuntime.AbilityActionInvocations.Single().Phase, Is.EqualTo("faulted"));
        }
        [Test] public void NativeLuaDirectCallOmitsOnlyDeclaredEntryWrappers()
        {
            var state = State(); var engine = Begin(state, "direct", out _, Caller); Run(engine);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(25)); Assert.That(state.EffectRuntime.AbilityActionInvocations.Count, Is.EqualTo(2));
            Assert.That(state.EffectRuntime.AbilityActionInvocations.Last().CallPolicyRef, Is.EqualTo("qy002.fixture.direct"));
        }
        [Test] public void DirectRootAndClientSkipCostAreRejected()
        {
            var state = State(); var source = Option(state).SourceRef; var engine = new EffectTreeExecutor(state, registry);
            string root = engine.CreateRoot(AbilityActionExecutor.CreateAuthorized(1, source.ItemId, Quick, "qy002.fixture.direct")); Run(engine);
            Assert.That(engine.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(20));
            var command = AbilityActionCommand.Create(1, Option(state)); command.Parameters["skipCost"] = "true";
            Assert.That(AbilityActionCommand.Handle(state, registry, command).Succeeded, Is.False);
        }
        [Test] public void SpecialBudgetIsSpentAfterBodyAndEntryAfterEffect()
        {
            var state = State(); var engine = Begin(state, "special", out _, Special); Until(engine, () => state.EffectRuntime.AbilityActionInvocations.Any(i => i.ActivationCommitted));
            Assert.That(state.FindPlayer(1).CompletedMainActionsThisTurn, Is.Zero); Run(engine);
            Assert.That(state.FindPlayer(1).CompletedMainActionsThisTurn, Is.EqualTo(1)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
        }
        [Test] public void RawArchiveMissingPaymentOrSourceFieldsIsRejected()
        {
            var state = State(); var engine = Begin(state, "raw", out _); Until(engine, () => state.EffectRuntime.AbilityActionInvocations.Count == 1);
            string json = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)); var raw = JObject.Parse(json);
            foreach (var property in raw.Descendants().OfType<JProperty>().Where(p => p.Name == "HandlerHash").ToArray()) property.Remove();
            Assert.That(HostRecoveryService.RecoverJson(raw.ToString(), registry).Succeeded, Is.False);
        }
        [Test] public void AcceptedRequestCannotBeReusedAfterBalanceReturnsToOriginalValue()
        {
            var state = State(); var option = Option(state); var engine = Begin(state, "generation.first", out _); Run(engine);
            engine.CreateRoot(ResourceEffectSpecFactory.Pay(1, ResourceType.GoldVoucher, 2)); Run(engine); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(20));
            Assert.That(Option(state).RequestRef.RequestGeneration, Is.EqualTo(2));
            Assert.That(engine.TryInvokeAbilityAction(1, option.OptionId, option.RequestRef.InteractionId, option.RequestRef.FactsDigest, option.VersionRef.ContentPackHash, "generation.second", Args, out _, out _), Is.False);
        }
        [Test] public void AnotherPlayerCannotInvokeOrReplayOwnersAction()
        {
            var state = State(); var option = Option(state); var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TryInvokeAbilityAction(2, option.OptionId, option.RequestRef.InteractionId, option.RequestRef.FactsDigest, option.VersionRef.ContentPackHash, "authority", Args, out _, out _), Is.False);
            engine = Begin(state, "authority", out _); Run(engine);
            Assert.That(engine.TryInvokeAbilityAction(2, option.OptionId, option.RequestRef.InteractionId, option.RequestRef.FactsDigest, option.VersionRef.ContentPackHash, "authority", Args, out _, out _), Is.False);
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.Zero);
        }
        [Test] public void ActualRoleUsesOriginalLuaSubtreeAndLosesEntryWhenItLeavesCoveredArea()
        {
            var state = State(); var engine = new EffectTreeExecutor(state, registry);
            state.StartPlayerId = 2; // 本例只执行单模式，双发协议由原有流程回归覆盖。
            engine.CreateRoot(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Run(engine);
            var request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
            string item = state.ItemZones.Items.Single(i => i.DefinitionId == "texas" && i.OwnerSubject.PlayerId == 1).ItemId;
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateString(item), out string reason), Is.True, reason); Run(engine);
            var option = registry.QueryActions(state, 1).Single(o => o.SourceRef.ItemId == item);
            var args = Object("mode", NormalizedValue.CreateString("strategy"));
            engine.CreateRoot(AbilityActionExecutor.Create(option, "real.role", args)); Run(engine);
            var second = state.EffectRuntime.InteractionRequests.LastOrDefault(r => r.Status == "open" && r.InteractionTypeId == CharacterLifecycleService.SecondModeInteractionTypeId);
            if (second != null)
            { Assert.That(engine.TrySubmitInteraction(second.GetStableInteractionId(), 1, second.StateRevision, NormalizedValue.CreateString(CharacterLifecycleService.FinishChoiceId), out reason), Is.True, reason); Run(engine); }
            Assert.That(state.CharacterUses.Single().Finished, Is.True); Assert.That(state.CharacterUses.Single().CompletedModes, Is.EqualTo(new[] { "strategy" }));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == item).ZoneId, Is.EqualTo(FindZone(state, "discard", 1).ZoneId));
            Assert.That(registry.QueryActions(state, 1).Any(o => o.SourceRef.ItemId == item), Is.False);
            Restore(state, "真实角色正文及离场");
        }
        // QY-003迁移：沿真实宣告执行器创建轨道、标记和初始化收据，不再只注入位置。
        private string DeclareStyleFixture(GameState state, int player)
        {
            var engine = new EffectTreeExecutor(state, registry);
            engine.CreateRoot(CityStyleDeclarationEffectExecutor.Create(player, "city_style_source_stone_industrial_hub", new[] { 0, 3, 4, 6, 7, 8 })); Run(engine);
            Assert.That(state.FindPlayer(player).DeclaredCityStyles.Count, Is.EqualTo(1));
            return state.FindPlayer(player).DeclaredCityStyles.Single().InfluenceMarkerId;
        }
        [Test] public void PublicStyleKeepsIndependentRealPlayerAuthorizationAndActivatesItsTrackOnce()
        {
            var state = State(); string first = DeclareStyleFixture(state, 1), second = DeclareStyleFixture(state, 2);
            var one = registry.QueryActions(state, 1).Single(o => o.ActionId == SpecialActionDatabase.SourceStoneIndustrialHub);
            var two = registry.QueryActions(state, 2).Single(o => o.ActionId == one.ActionId);
            Assert.That(one.SourceRef.ItemId, Is.EqualTo(two.SourceRef.ItemId)); Assert.That(one.SourceRef.SourceKey, Is.Not.EqualTo(two.SourceRef.SourceKey));
            Assert.That(state.EffectRuntime.UsageTrackBindings.Single(b => b.BindingId == one.TrackBindingId).MarkerItemId, Is.EqualTo(first));
            Assert.That(state.EffectRuntime.UsageTrackBindings.Single(b => b.BindingId == two.TrackBindingId).MarkerItemId, Is.EqualTo(second));
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(AbilityActionExecutor.Create(one, "real.style", Args));
            Until(engine, () => state.EffectRuntime.AbilityActionInvocations.Any(i => i.PaymentCommitted));
            state = Restore(state, "真实样式费用后"); engine = new EffectTreeExecutor(state, registry); Run(engine);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(14)); Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).CharacterCardLockedThisTurn, Is.True); Assert.That(state.ItemZones.Items.Single(i => i.ItemId == first).SlotId, Is.EqualTo("used_from_2"));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == second).SlotId, Is.EqualTo("2"));
            Assert.That(registry.QueryActions(state, 1).Single(o => o.ActionId == one.ActionId).Enabled, Is.False);
        }
        [TestCase(0, 0)][TestCase(1, 0)][TestCase(2, 1)][TestCase(3, 1)][TestCase(4, 2)]
        public void ActualContractLuaCountsEachRegionAndEachHolderWithoutGeneratingActions(int count, int expected)
        {
            var state = State(realMap: true, companyCount: count); var map = StaticMapDefinitions.Resolve(state.MapId);
            var region = map.Regions.First(r => map.Locations.Where(l => l.RegionId == r.RegionId).Sum(l => l.InfluenceSlotCount) >= 4);
            var slots = map.Locations.Where(l => l.RegionId == region.RegionId).SelectMany(l => Enumerable.Range(0, l.InfluenceSlotCount).Select(i => InfluenceService.GetLocationSlotId(l.LocationId, i))).ToArray();
            // 隔离计数场景仅经原生容器转移维护Order和位置收据，不计公司安置资格或正常游戏验收。
            const string setup = "qy007.i01.contract.count.fixture";
            registry.Register(setup, c => {
                var contract = c.State.ItemZones.Items.Single(i => i.DefinitionId == CompanyInfluenceRules.ContractDefinitionId && i.OwnerSubject.PlayerId == c.Node.PlayerId);
                new ItemTransferService().RelocateItems(c, new ItemTransferRequest { ExecutingPlayerId = c.Node.PlayerId,
                    ItemIds = new List<string> { contract.ItemId }, ExpectedSource = new ItemPositionRef { ZoneId = contract.ZoneId },
                    Destination = new ZoneRef { ZoneId = FindZone(c.State, "rewards", c.Node.PlayerId).ZoneId },
                    InsertionKind = "unordered", RequiredCount = 1, OperationProfileId = "core.reward_card_transfer" });
                int index = 0;
                if (c.Node.PlayerId == 1) foreach (var company in c.State.ItemZones.Items.Where(i => i.TypeId == "infected.company_marker").ToList())
                {
                    string slot = slots[index++];
                    new ItemTransferService().RelocateItems(c, new ItemTransferRequest { ExecutingPlayerId = c.Node.PlayerId,
                        ItemIds = new List<string> { company.ItemId }, ExpectedSource = new ItemPositionRef { ZoneId = company.ZoneId },
                        Destination = new ZoneRef { ZoneId = c.State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.influence_slots" && z.LegalSlotIds.Contains(slot)).ZoneId },
                        DestinationSlotId = slot, InsertionKind = "slot", RequiredCount = 1, OperationProfileId = "core.influence_cube" });
                }
                return EffectStepResult.Completed();
            });
            var engine = new EffectTreeExecutor(state, registry);
            foreach (int player in new[] { 1, 2 }) { engine.CreateRoot(EffectSpec.Create(setup, playerId: player)); Run(engine); }
            artifacts.WriteLabeledText("地区计数原生安置后状态", JsonConvert.SerializeObject(state));
            registry.RegisterAbilityQueryContract("scoringModifiers.v1", (q, s, values) => values.Items.All(i => Text(i, "kind") == "influence_delta" && Number(i, "playerId") == s.AuthorizedPlayerId && Number(i, "delta") >= 0) ? "" : "invalid_delta");
            var source = registry.QuerySources(state, queryKind: "scoringModifiers.v1").Where(s => s.DefinitionId == CompanyInfluenceRules.ContractDefinitionId).ToList(); Assert.That(source.Count, Is.EqualTo(2));
            foreach (var holder in source)
            {
                var result = registry.QueryRules(state, new AbilityQueryContext { QueryId = "contract." + count, QueryKind = "scoringModifiers.v1", FactsDigest = "contract.facts", ContentVersion = registry.ContentPackHash, MapId = state.MapId, SourceRef = holder }).Single();
                Assert.That(Number(result.Items.Items.Single(i => Text(i, "regionId") == region.RegionId), "delta"), Is.EqualTo(expected));
                Assert.That(result.Items.Items.Where(i => Text(i, "regionId") != region.RegionId).All(i => Number(i, "delta") == 0), Is.True);
                Assert.That(registry.QueryActions(state, holder.AuthorizedPlayerId).Any(o => o.SourceRef.ItemId == holder.ItemId), Is.False);
            }
            Assert.That(state.ItemZones.Items.Where(i => i.TypeId == "infected.company_marker").All(i => i.OwnerSubject.Kind == ItemSubjectKind.Company), Is.True);
        }
        [Test] public void ExistingAuthorizedStylePrimitiveRunsTheSameNativeFrameAndKeepsSharedTrack()
        {
            var state = State(); string marker = DeclareStyleFixture(state, 1);
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(EffectSpec.Create(CharacterContentEffectExecutor.Prefix + "immediate_special", playerId: 1)); Run(engine);
            var request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateString(marker), out string reason), Is.True, reason);
            Until(engine, () => state.EffectRuntime.EffectNodes.Any(n => n.Payment?.IsCommitted == true));
            state = Restore(state, "旧授予样式入口费用后"); engine = new EffectTreeExecutor(state, registry); Run(engine);
            Assert.That(state.EffectRuntime.AbilityActionInvocations.Single().CallPolicyRef, Is.EqualTo(AbilityLegacyStyleBridge.PolicyRef));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(14)); Assert.That(state.FindPlayer(1).CompletedMainActionsThisTurn, Is.Zero);
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(3)); Assert.That(state.ItemZones.Items.Single(i => i.ItemId == marker).SlotId, Is.EqualTo("used_from_2"));
            Assert.That(registry.QueryActions(state, 1).Single(o => o.ActionId == SpecialActionDatabase.SourceStoneIndustrialHub).Enabled, Is.False);
        }
        [Test] public void ForgingInternalStyleGrantWithoutActualParentIsRejected()
        {
            var state = State(); string marker = DeclareStyleFixture(state, 1); var engine = new EffectTreeExecutor(state, registry);
            string root = engine.CreateRoot(AbilityLegacyStyleBridge.Create(1, SpecialActionDatabase.SourceStoneIndustrialHub, marker)); Run(engine);
            Assert.That(engine.GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(state.EffectRuntime.AbilityActionInvocations, Is.Empty); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(20));
        }
        private void GiftSupport(GameState state)
        {
            var item = state.ItemZones.Items.Single(i => i.DefinitionId == "infected.award.permanent_support" && i.OwnerSubject.PlayerId == 1);
            item.ZoneId = FindZone(state, "rewards", 1).ZoneId; Assert.That(ContentLifecycleEffectExecutor.Acquisition(state, item.ItemId), Is.Null);
            state.Phase = GamePhase.Cleanup;
        }
        private void RegisterCleanupSignal() => registry.Register("qy002.cleanup.signal", c => EffectStepResult.Completed().AddEvent(new EffectEventRequest { EventType = "PlayerCleanupStarted", PlayerId = 1 }));
        [Test] public void ActualQuickAwardWithoutAcquisitionRunsItsLuaAndReturnsTheRealItem()
        {
            var state = State();
            EnterpriseEffectExecutor.Register(registry, LuaContentCatalog.CreateEnterpriseDefinitions(registry, pack.ActiveDefinitions, pack.GetScript, pack));
            var award = state.ItemZones.Items.Single(i => i.DefinitionId == "infected.award.enterprise_special" && i.OwnerSubject.PlayerId == 1);
            award.ZoneId = FindZone(state, "rewards", 1).ZoneId;
            Assert.That(ContentLifecycleEffectExecutor.Acquisition(state, award.ItemId), Is.Null);
            Assert.That(registry.GetSettlementOptions(state, 1).Any(o => o.SourceItemId == award.ItemId), Is.False);
            var option = registry.QueryActions(state, 1).Single(o => o.SourceRef.ItemId == award.ItemId);
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(AbilityActionExecutor.Create(option, "real.quick.award", Args)); Run(engine);
            // 此隔离局无解锁企业，原Lua仍依其正式空候选合同结束并执行回盒；不伪造企业收益。
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == LuaDomainEffectTypeIds.ActivateEnterpriseSpecial && Text(n.NormalizedResult, "reasonCode") == "no_candidates"), Is.True);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == award.ItemId).ZoneId, Is.EqualTo(FindZone(state, "retired").ZoneId));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == ItemTransferEffectExecutor.TypeId), Is.EqualTo(1));
            Assert.That(registry.QueryActions(state, 1).Any(o => o.SourceRef.ItemId == award.ItemId), Is.False);
            Restore(state, "真实快速授勋正文及回盒");
        }
        [Test] public void ActualPersistentAwardWithoutAcquisitionKeepsOnceReceiptAcrossRecovery()
        {
            var state = State(realMap: true); GiftSupport(state); RegisterCleanupSignal(); var engine = new EffectTreeExecutor(state, registry);
            engine.CreateRoot(EffectSpec.Create("qy002.cleanup.signal", playerId: 1)); Run(engine);
            var entry = registry.GetSettlementOptions(state, 1).Single();
            Assert.That(engine.TryInvokeCharacterSettlementOption(1, entry.OptionId, "", state.EffectRuntime.StateRevision, out _, out string startReason), Is.True, startReason); Run(engine);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(23));
            state = Restore(state, "无获得历史的真实授勋收尾中"); engine = new EffectTreeExecutor(state, registry);
            var request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateString("Iron=1"), out string reason), Is.True, reason); Run(engine);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(1)); Assert.That(state.EffectRuntime.AbilitySourceUses.Single().Outcome, Is.EqualTo("completed"));
            engine.CreateRoot(EffectSpec.Create("qy002.cleanup.signal", playerId: 1)); Run(engine); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(23));
            Assert.That(state.EffectRuntime.AbilitySourceUses.Count, Is.EqualTo(1));
        }
        [Test] public void WholeCleanupDeclineRecordsSourceUseAndCannotReinjectSameRound()
        {
            var state = State(realMap: true); GiftSupport(state); RegisterCleanupSignal();
            var chain = new RoundMainChainBuilder().Build(state.GameId, "", 1, new[] { 1, 2 }, 1);
            var main = chain.Nodes.Single(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow && n.PlayerId == 1);
            state.EffectRuntime.MainNodes.AddRange(chain.Nodes); foreach (var previous in chain.Nodes.Where(n => n.MainlineIndex < main.MainlineIndex)) previous.Status = EffectNodeStatus.Completed;
            state.EffectRuntime.ActiveMainNodeId = main.NodeId; state.EffectRuntime.FirstMainNodeId = chain.FirstNodeId; state.EffectRuntime.CurrentRoundExecutionId = chain.RoundExecutionId;
            state.EffectRuntime.CurrentRoundNumber = 1; state.EffectRuntime.CurrentRoundStartPlayerId = 1; state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            var rounds = new RoundExecutionService(registry); var engine = rounds.CreateCommandExecutor(state);
            string root = engine.CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId, Object("mainNodeId", NormalizedValue.CreateString(main.NodeId)), 1));
            main.ExecutionEffectId = root; main.Status = EffectNodeStatus.Running; Run(engine);
            var window = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).SettlementWindow; Assert.That(window.Entries.Count, Is.EqualTo(1));
            Assert.That(engine.TryEndSettlementAction(1, window.Context.Id, "", "", state.EffectRuntime.StateRevision, "whole-decline", out string reason), Is.True, reason); Run(engine);
            Assert.That(state.EffectRuntime.AbilitySourceUses.Single().Outcome, Is.EqualTo("declined")); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(20));
            int sources = state.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == AbilitySourceEffectExecutor.TypeId);
            engine.CreateRoot(EffectSpec.Create("qy002.cleanup.signal", playerId: 1)); Run(engine);
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == AbilitySourceEffectExecutor.TypeId), Is.EqualTo(sources));
        }
        [Test] public void TokenEventUsesSharedLuaBodyButKeepsItsActualSourceIdentityAndRestoresOnce()
        {
            var state = State(); registry.Register("qy002.collection.signal", c => EffectStepResult.Completed().AddEvent(new EffectEventRequest { EventType = "CollectionStarted", PlayerId = 1 }));
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(EffectSpec.Create("qy002.collection.signal", playerId: 1));
            Until(engine, () => state.EffectRuntime.AbilitySourceUses.Count > 0);
            state = Restore(state, "真实token事件来源受理后"); engine = new EffectTreeExecutor(state, registry); Run(engine);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(25)); Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(5));
            Assert.That(state.EffectRuntime.AbilitySourceUses.Count, Is.EqualTo(2));
            Assert.That(state.EffectRuntime.AbilitySourceUses.All(r => r.AbilityId == "qy002.fixture.event"), Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == ResourceEffectTypeIds.Gain).All(n => n.ContinuationAbilityId == "qy002.fixture.event"), Is.True);
        }
        [Test] public void TransferringAPrivateSourceCannotResetItsConsumedOnceScope()
        {
            var state = State(); var engine = Begin(state, "once", out _, "qy002.fixture.once"); Run(engine);
            var source = Option(state, "qy002.fixture.once").SourceRef;
            var item = state.ItemZones.Items.Single(i => i.ItemId == source.ItemId);
            item.OwnerSubject = ItemSubjectRef.Player(2); item.ControllerSubject = ItemSubjectRef.Player(2); state.CurrentPlayerId = 2;
            var transferred = registry.QueryActions(state, 2).Single(o => o.SourceRef.ItemId == item.ItemId && o.ActionId == "qy002.fixture.once");
            Assert.That(transferred.Enabled, Is.False); Assert.That(transferred.DisabledReason, Is.EqualTo("action_group_occupied"));
        }
        [Test] public void SelectedFaceRequiresAnAnsweredHostCandidateAndDraftCannotEnableIt()
        {
            var state = State(); var item = state.ItemZones.Items.Single(i => i.DefinitionId == CompanyInfluenceRules.ContractDefinitionId && i.OwnerSubject.PlayerId == 1);
            item.ZoneId = FindZone(state, "rewards", 1).ZoneId;
            var declaration = new AbilitySourceDeclaration { DefinitionId = item.DefinitionId, AbilityId = "qy002.selection", Kind = "setup", HandlerRef = "qy002.selection", DeclarationVersion = "1.0.0", ActiveFaceId = "A",
                EffectiveZones = new List<string> { "core.zone.player_rewards" }, AuthorizationPolicy = "active_selection", PresentationRef = item.DefinitionId };
            registry = new EffectRegistry { ContentPackHash = pack.ContentHash }; registry.RegisterAbilityHandler("qy002.selection", "fixture.selection.v1", c => new List<EffectSpec>());
            registry.FreezeAbilityCatalog(new[] { declaration });
            state.ContentDefinitions.AbilitySources = registry.AbilityDeclarations.ToList();
            item.LocalState = Object("activeFaceId", NormalizedValue.CreateString("A"), "activationAuthorizationRef", NormalizedValue.CreateString("draft"));
            Assert.That(registry.QuerySources(state, 1), Is.Empty);
            Assert.That(AbilitySourceSelection.ValidateLocalState(state, item, out _), Is.False);
            item.LocalState = Object("activeFaceId", NormalizedValue.CreateString("A"));
            registry.Register("qy002.select", context =>
            {
                if (context.Node.FlowStage.Length == 0) return EffectStepResult.Continue("answer").AddInteraction(new EffectInteractionSpec { InteractionTypeId = "qy002.selection", AnsweringPlayerId = 1,
                    AnswerSchema = "candidate_id", CandidateIds = { "pick.A" }, MinSelections = 1, MaxSelections = 1,
                    PromptParameters = Object("abilitySourceCandidates", NormalizedValue.CreateArray(new List<NormalizedValue> { Object("candidateId", NormalizedValue.CreateString("pick.A"), "itemId", NormalizedValue.CreateString(item.ItemId), "definitionId", NormalizedValue.CreateString(item.DefinitionId), "faceId", NormalizedValue.CreateString("A")) })) });
                var answered = context.State.EffectRuntime.InteractionRequests.Single(r => r.OwnerEffectId == context.Node.EffectId);
                AbilitySourceSelection.CommitSelectedFace(context, item.ItemId, "A", answered.GetStableInteractionId(), "pick.A"); return EffectStepResult.Completed();
            }, EffectExecutorKind.IntrinsicFlow);
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(EffectSpec.Create("qy002.select", playerId: 1)); Run(engine);
            var request = state.EffectRuntime.InteractionRequests.Single(); Assert.That(registry.QuerySources(state, 1), Is.Empty);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateString("pick.A"), out string reason), Is.True, reason); Run(engine);
            Assert.That(registry.QuerySources(state, 1).Single().ActiveFaceId, Is.EqualTo("A"));
            state = Restore(state, "正式选面来源收据");
            Assert.That(registry.QuerySources(state, 1).Single().ActiveFaceId, Is.EqualTo("A"));
            state.ItemZones.Items.Single(i => i.ItemId == item.ItemId).LocalState = Object("activeFaceId", NormalizedValue.CreateString("B"));
            Assert.That(registry.QuerySources(state, 1), Is.Empty);
        }
        [Test] public void DetailProjectionUsesTheSameActualSourcesAndDropsLostHoldings()
        {
            var state = State(); var contract = state.ItemZones.Items.Single(i => i.DefinitionId == CompanyInfluenceRules.ContractDefinitionId && i.OwnerSubject.PlayerId == 1);
            contract.ZoneId = FindZone(state, "rewards", 1).ZoneId;
            var metadata = PlayerDetailDisplayCatalog.Read(pack.ActiveDefinitions);
            Assert.That(PlayerDetailEntryProjector.Project(GameStateViewProjector.ProjectForPlayer(state, 1), metadata).Count(e => e.SourceItemId == contract.ItemId), Is.EqualTo(1));
            contract.ZoneId = FindZone(state, "retired").ZoneId;
            Assert.That(PlayerDetailEntryProjector.Project(GameStateViewProjector.ProjectForPlayer(state, 1), metadata).Any(e => e.SourceItemId == contract.ItemId), Is.False);
        }
        [TestCase("unknown")][TestCase("missing_protocol")][TestCase("duplicate_case")]
        public void MalformedDeclarativeFieldsFailClosedBeforeDeserialization(string mode)
        {
            var json = JObject.Parse(File.ReadAllText(Path.Combine(fixtureRoot, "component/" + Token + ".json")));
            var source = (JObject)json["abilitySources"][0];
            if (mode == "unknown") source["runAnything"] = true;
            if (mode == "missing_protocol") source.Property("ProtocolVersion").Remove();
            if (mode == "duplicate_case") source["protocolVersion"] = 1;
            Assert.Throws<InvalidDataException>(() => AbilitySourceLuaCatalog.ValidateRaw(json));
        }
        [TestCase("presentation")] [TestCase("module_version")]
        public void Qy007B2PresentationAndFrozenModuleMetadataChangeActiveIdentityAndRejectAnOldHash(string variant)
        {
            var configuration = new GameRuleConfiguration { MapId = StaticMapDefinitions.ThreePlayerMapId, PlayerCount = 3 };
            string firstRoot = fixtureRoot, secondRoot;
            if (variant == "presentation")
            {
                const string relative = "presentation/texts.zh-CN.json";
                var texts = JObject.Parse(File.ReadAllText(Path.Combine(fixtureRoot, relative)));
                texts["messages"]["core:interaction.map.influence.place"]["template"] =
                    (string)texts["messages"]["core:interaction.map.influence.place"]["template"] + " [b]边界校验[/b]";
                secondRoot = artifacts.CopyContent(fixtureRoot, "文案字节影响活动身份私有变体", new Dictionary<string, string> { [relative] = texts.ToString() });
            }
            else
            {
                const string module = "lua/facilities/resource_reward.lua", main = "lua/city_styles/city_style_source_stone_industrial_hub.entry_cost.lua";
                string hash = LuaContentHasher.ComputeSha256(File.ReadAllText(Path.Combine(fixtureRoot, module)));
                string WithModule(string version)
                {
                    var manifest = JObject.Parse(File.ReadAllText(Path.Combine(fixtureRoot, "pack.json")));
                    manifest["luaModules"] = new JArray(new JObject { ["moduleId"] = "qy007.b2p.resource", ["version"] = version, ["script"] = module, ["sha256"] = hash });
                    manifest["scriptDependencies"] = new JArray(new JObject { ["protocolVersion"] = 1, ["script"] = main,
                        ["modules"] = new JArray(new JObject { ["moduleId"] = "qy007.b2p.resource", ["version"] = version, ["sha256"] = hash, ["alias"] = "b2resource" }) });
                    return artifacts.CopyContent(fixtureRoot, "冻结模块版本活动身份" + version, new Dictionary<string, string> { ["pack.json"] = manifest.ToString() });
                }
                firstRoot = WithModule("1.0.0"); secondRoot = WithModule("1.0.1");
            }
            var firstPack = ExternalContentPack.Load(firstRoot); var secondPack = ExternalContentPack.Load(secondRoot);
            artifacts.RecordContent("活动身份第一份实际输入", firstRoot, firstPack.ContentHash);
            artifacts.RecordContent("活动身份第二份实际输入", secondRoot, secondPack.ContentHash);
            var first = ActiveContentCatalogBuilder.Build(firstPack, configuration);
            var second = ActiveContentCatalogBuilder.Build(secondPack, configuration);
            Assert.That(second.ContentPackHash, Is.Not.EqualTo(first.ContentPackHash));
            if (variant == "module_version")
                Assert.That(first.Scripts["lua/city_styles/city_style_source_stone_industrial_hub.entry_cost.lua"], Is.Not.EqualTo(second.Scripts["lua/city_styles/city_style_source_stone_industrial_hub.entry_cost.lua"]), "冻结版本必须进入实际装配处理器身份");
            configuration.ContentPackHash = first.ContentPackHash;
            var rejected = Assert.Throws<InvalidOperationException>(() => ActiveContentCatalogBuilder.Build(secondPack, configuration));
            Assert.That(rejected.Message, Does.Contain("content_version_mismatch"));
            artifacts.WriteLabeledText("活动身份不能冒用旧hash" + variant, JsonConvert.SerializeObject(new { first = first.ContentPackHash, second = second.ContentPackHash, rejected.Message }));
        }
        private static GameplayTextContext Qy007B2TextContext(IGameplayTextCatalog catalog)
        {
            var names = new Dictionary<string, string> { ["qy007.b2p.visible"] = "可见对象" };
            return new GameplayTextContext { Catalog = catalog, ContentHash = catalog.ContentHash, Locale = catalog.Locale,
                ViewerPlayerId = 1, StateRevision = 0, VisibilityScopeId = "qy007.b2p.templates",
                PlayerNames = names, ItemNames = names, DefinitionNames = names, ResourceNames = names, LocationNames = names,
                ResourceIconKeys = new Dictionary<string, string> { ["qy007.b2p.visible"] = "core:resource.iron" } };
        }
        [Test] public void Qy007B2CurrentAuthoredTemplatesResolveTypedParametersWithoutLockingTheirWordsOrStyles()
        {
            var catalog = pack.Presentation; var resolver = new GameplayTextResolver(catalog);
            var diagnostics = new List<GameplayTextFormatException>(); resolver.Diagnostic += diagnostics.Add;
            var context = Qy007B2TextContext(catalog);
            var messages = (JObject)JObject.Parse(File.ReadAllText(Path.Combine(fixtureRoot, "presentation/texts.zh-CN.json")))["messages"];
            int resolved = 0;
            foreach (var message in messages.Properties())
            {
                diagnostics.Clear();
                Assert.That(catalog.TryGetTemplate(message.Name, out var template), Is.True, message.Name);
                var arguments = template.Parameters.Select((p, i) => new GameplayTextArgument { Name = p.Name, Kind = p.Kind,
                    Value = p.Kind == "integer" ? NormalizedValue.CreateInteger(37 + i) : p.Kind == "text" ? NormalizedValue.CreateString("可见内容") :
                        NormalizedValue.CreateStableReference(p.Kind, "qy007.b2p.visible") }).ToArray();
                var reference = new GameplayTextRef(message.Name, arguments);
                GameplayTextDocument document = null;
                Assert.DoesNotThrow(() => document = resolver.Resolve(reference, context), message.Name);
                Assert.That(diagnostics, Is.Empty, message.Name + "合法参数不能被兜底文案掩盖");
                Assert.That(document, Is.Not.Null); Assert.That(document.ToPlainText(), Does.Not.Contain("[b]").And.Not.Contain("[/b]"), message.Name);
                foreach (var argument in arguments.Where(a => a.Kind == "integer" && template.Template.Contains("{" + a.Name + "}")))
                    Assert.That(document.Runs.OfType<NumberRun>().Any(n => n.Value == argument.Value.IntegerValue) ||
                        document.Runs.OfType<IconRun>().Any(n => n.NumberValue == argument.Value.IntegerValue), Is.True, message.Name + ":" + argument.Name);
                if (arguments.Length > 0)
                {
                    void AssertRejected(GameplayTextRef invalid)
                    {
                        var errors = new List<GameplayTextFormatException>(); var rejecting = new GameplayTextResolver(catalog);
                        rejecting.Diagnostic += errors.Add;
                        var fallback = rejecting.Resolve(invalid, context);
                        Assert.That(errors.Any(e => e.TextKey == message.Name), Is.True, message.Name + "缺参/错类型必须产生诊断");
                        var expected = new GameplayTextResolver(catalog).Resolve(new GameplayTextRef("core:presentation.unavailable"), context);
                        Assert.That(fallback.ToPlainText(), Is.EqualTo(expected.ToPlainText()), message.Name + "按实际公共协议显示当前兜底文案");
                    }
                    AssertRejected(new GameplayTextRef(message.Name, arguments.Take(arguments.Length - 1).ToArray()));
                    var wrong = reference.Clone(); wrong.Arguments[0].Value = wrong.Arguments[0].Kind == "integer" ?
                        NormalizedValue.CreateString("37") : NormalizedValue.CreateInteger(37);
                    AssertRejected(wrong);
                }
                resolved++;
            }
            Assert.That(resolved, Is.EqualTo(messages.Count));
            artifacts.WriteLabeledText("现行文案模板参数完整解析", JsonConvert.SerializeObject(new { catalog.ContentHash, resolved,
                scope = "私有输入上的解析/动态参数检查，不固定人工措辞、坐标、颜色或样式，不计页面视觉验收" }));
        }
        [Test] public void Qy007B2BoldTagsPreserveTypedNumbersAndCloseTheirStyleScopeInPrivateInput()
        {
            const string relative = "presentation/texts.zh-CN.json", key = "core:interaction.map.influence.place";
            var texts = JObject.Parse(File.ReadAllText(Path.Combine(fixtureRoot, relative)));
            texts["messages"][key] = new JObject { ["parameters"] = new JObject { ["boldAmount"] = "integer", ["plainAmount"] = "integer" },
                ["template"] = "[b]{boldAmount}[/b] / {plainAmount}" };
            string input = artifacts.CopyContent(fixtureRoot, "加粗解析私有模板", new Dictionary<string, string> { [relative] = texts.ToString() });
            var actual = ExternalContentPack.Load(input); artifacts.RecordContent("加粗解析实际私有输入", input, actual.ContentHash);
            var document = new GameplayTextResolver(actual.Presentation).Resolve(new GameplayTextRef(key,
                new GameplayTextArgument { Name = "boldAmount", Kind = "integer", Value = NormalizedValue.CreateInteger(37) },
                new GameplayTextArgument { Name = "plainAmount", Kind = "integer", Value = NormalizedValue.CreateInteger(41) }), Qy007B2TextContext(actual.Presentation));
            var numbers = document.Runs.OfType<NumberRun>().ToArray();
            Assert.That(numbers.Select(n => n.Value), Is.EqualTo(new[] { 37, 41 }));
            Assert.That(numbers[0].Style.WeightMode, Is.EqualTo("Bold")); Assert.That(numbers[1].Style.WeightMode, Is.EqualTo("Inherit"));
            Assert.That(document.ToPlainText(), Does.Not.Contain("[b]").And.Not.Contain("[/b]"));
        }
        [Test] public void ActiveCatalogIncludesNewHandlerBytesInItsFrozenFingerprintAndKeepsG0()
        {
            var config = new GameRuleConfiguration { MapId = StaticMapDefinitions.ThreePlayerMapId, PlayerCount = 3 };
            string scriptPath = Path.Combine(fixtureRoot, "lua/city_styles/city_style_source_stone_industrial_hub.entry_cost.lua");
            string original = File.ReadAllText(scriptPath);
            var first = ActiveContentCatalogBuilder.Build(ExternalContentPack.Load(fixtureRoot), config);
            string installedRoot = artifacts.CopyContent(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"), "正式活动内容独占输入");
            var installed = ExternalContentPack.Load(installedRoot); artifacts.RecordContent("正式活动内容独占装载", installedRoot, installed.ContentHash);
            var frozenInstalled = ActiveContentCatalogBuilder.Build(installed, config);
            artifacts.WriteLabeledText("活动内容指纹", new JObject { ["scope"] = "正式安装目录的隔离装配，无正常开局或GameId",
                ["installedPackHash"] = installed.ContentHash, ["coreActiveCatalogHash"] = frozenInstalled.ContentPackHash,
                ["mapId"] = config.MapId, ["playerCount"] = config.PlayerCount, ["contractVersion"] = "QY-COMMON/1.0.1", ["abilityExecutorVersion"] = AbilityActionExecutor.Version }.ToString());
            Assert.That(first.Scripts.ContainsKey("lua/city_styles/city_style_source_stone_industrial_hub.entry_cost.lua"), Is.True);
            string variant = artifacts.CopyContent(fixtureRoot, "活动指纹变体", new Dictionary<string, string> { ["lua/city_styles/city_style_source_stone_industrial_hub.entry_cost.lua"] = original + "\n-- QY-002隔离指纹检查\n" });
            var variantPack = ExternalContentPack.Load(variant); artifacts.RecordContent("指纹变体装载内容", variant, variantPack.ContentHash);
            var second = ActiveContentCatalogBuilder.Build(variantPack, config);
            Assert.That(second.ContentPackHash, Is.Not.EqualTo(first.ContentPackHash));
            config.EnabledExpansionIds.Add("entrepreneur");
            Assert.That(config.TryValidate(out _), Is.False);
        }
        [Test] public void RuleQueryIsReadOnlySourceBoundAndRejectsUnconnectedOrInvalidReturn()
        {
            var state = State(); var query = new AbilityQueryContext { QueryId = "pure", QueryKind = "scoringModifiers.v1", MapId = state.MapId, ContentVersion = registry.ContentPackHash, FactsDigest = "facts", SubjectPlayerId = 1 };
            Assert.Throws<InvalidOperationException>(() => registry.QueryRules(state, query));
            registry.RegisterAbilityQueryContract(query.QueryKind, (q, s, items) => items.Items.All(i => Text(i, "kind") == "fixture_fact" && Number(i, "playerId") == s.AuthorizedPlayerId) ? "" : "invalid_fixture_fact");
            string before = RuleStateDigestService.Compute(state); var results = registry.QueryRules(state, query);
            Assert.That(results.Count, Is.EqualTo(2)); Assert.That(results.Select(r => r.SourceRef.AuthorizedPlayerId), Is.EquivalentTo(new[] { 1, 2 }));
            Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(before));
            query.Facts = Object("bad", NormalizedValue.CreateBoolean(true)); Assert.Throws<InvalidOperationException>(() => registry.QueryRules(state, query));
            Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(before));
        }
    }
}
