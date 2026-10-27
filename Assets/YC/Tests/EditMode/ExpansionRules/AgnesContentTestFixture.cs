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
using YC.Domain.Influence;
using YC.Domain.Enterprises;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 隔离模块/Host夹具。不会打开正常模式、伪造正常GameId或修改真实存档。
    // Probe只改变私有Lua调用入口；费用、数量、选择及收益仍执行正式agnes.lua原文。
    internal sealed class AgnesContentTestFixture : IDisposable
    {
        internal const string Definition = "entrepreneur.ability.A01", Probe = "qy013.module.probe";
        internal readonly IsolatedTestArtifacts Artifacts = IsolatedTestArtifacts.BeginCase();
        internal ExternalContentPack Pack;
        internal EffectRegistry Registry;
        internal GameState State;
        internal string Card;
        private string module;
        internal static string Installed => Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
        internal ZoneInstanceState Storage => State.ItemZones.Zones.Single(z => z.ParentId == Card && z.LocalZoneId == "commands");
        internal int Stored => State.ItemZones.Items.Count(i => i.ZoneId == Storage.ZoneId);
        internal EffectTreeExecutor Engine => new EffectTreeExecutor(State, Registry);
        internal InteractionRequest Open => State.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
        internal AgnesContentTestFixture(bool multiObligationProbe = false, bool independentH1 = false)
        {
            // 待共享登记后才由唯一运行者执行；不跳过缺依赖、不替换为假模块。
            string root = Artifacts.CopyContent(Installed, "艾格尼丝正式内容私有输入");
            if (multiObligationProbe)
            {
                // 仅多义务协议定位：两个私有无轨道入口复用正式A08正文，不改正式三入口和共享manifest。
                // 实际card effect仍是正式agnes模块；不能计为官方同战术一回合可用两次。
                const string path = "entrepreneur/entrepreneur.round.A.1.json";
                var json = JObject.Parse(File.ReadAllText(Path.Combine(root, path)));
                var source = json["abilitySources"].Single(s => (string)s["abilityId"] == "entrepreneur.A08.tactic");
                foreach (string id in new[] { "qy013.obligation.first", "qy013.obligation.second" })
                    ((JArray)source["actions"]).Add(JObject.FromObject(new AbilityActionDeclaration { ActionId = id, EntryKind = "quick",
                        BodyHandlerRef = "entrepreneur.A08.tactic", PresentationRef = (string)source["presentationRef"] }));
                root = Artifacts.CopyContent(root, "多义务独立Invocation私有入口", new Dictionary<string, string> { [path] = json.ToString() });
            }
            if (independentH1)
            {
                // 私有元数据消费者：来源H0与实际行动正文H1字节不同；不改正文、不增manifest登记。
                const string path = "entrepreneur/entrepreneur.round.A.5.json";
                var json = JObject.Parse(File.ReadAllText(Path.Combine(root, path)));
                var source = json["abilitySources"].Single(s => (string)s["abilityId"] == "entrepreneur.A05.cleanup");
                source["handlerRef"] = "qy013.A05.declaration"; source["kind"] = "quick"; source["observedEvents"] = new JArray(); source["cleanupInjectionPolicy"] = "always";
                source["actions"] = JArray.FromObject(new[] { new AbilityActionDeclaration { ActionId = "qy013.A05.H1.action", EntryKind = "quick",
                    BodyHandlerRef = "entrepreneur.A05.cleanup", PresentationRef = (string)source["presentationRef"] } });
                ((JArray)json["abilityHandlers"]).Add(JObject.FromObject(new ExternalAbilityHandler { HandlerId = "qy013.A05.declaration",
                    Script = "lua/entrepreneurs/agnes.after.lua", Version = "1.1.0" }));
                root = Artifacts.CopyContent(root, "H0声明H1真实正文政策隔离", new Dictionary<string, string> { [path] = json.ToString() });
            }
            Pack = ExternalContentPack.Load(root);
            Artifacts.RecordContent("艾格尼丝安装内容", root, Pack.ContentHash);
            module = File.ReadAllText(Path.Combine(root, "lua/entrepreneurs/agnes.lua"));
        }
        internal LuaInvocationResult Lua(string payloadJson, string call = "return A.invoke(ctx,{})", string eventType = "AbilityInvoked", LuaFacadeSnapshot snapshot = null,
            string responseKind = "effects", NormalizedValue settlementFacts = null, LuaSnapshotValue sourceFacts = null)
        {
            string script = "local A=(function()\n" + module + "\nend)()\nreturn function(ctx) " + call + " end";
            snapshot ??= LuaFacadeSnapshot.FromInvocation("p1", 0, 100, 2, "1.0.0");
            if (snapshot.Round == 0) snapshot.Round = 1;
            snapshot.EventPayload = Snapshot(JToken.Parse(payloadJson));
            var player = snapshot.Players.Single(p => p.PlayerId == "p1");
            var context = new LuaInvocationContext("qy013.module.input", eventType, "p1", snapshot.StateRevision ?? 0, "1.0.0", 2, 0, player.GoldVoucherCount,
                player.AvailableGoldVoucherCount, player.ReservedGoldVoucherCount, snapshot) {
                ResponseKind = responseKind, SettlementFacts = settlementFacts ?? Object(), SourceFacts = sourceFacts ?? LuaSnapshotValue.Null };
            if (responseKind == "settlementAvailability")
                context.SettlementContext = new SettlementWindowContext { Id = "qy013.pure.window", Kind = "player_cleanup",
                    OwnerEffectId = "qy013.module.input", PlayerId = 1, StateRevision = context.StateRevision };
            var result = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition(Definition, Probe, Probe, "1.0.0", script, LuaContentHasher.ComputeSha256(script)), context);
            Artifacts.WriteLabeledText("实际Lua输入结果", JsonConvert.SerializeObject(new { payloadJson, call, result.Status, result.Diagnostic }));
            return result;
        }
        internal void Host(string expression, int supply = 6, int gold = 100, string continuationEffects = "")
        {
            // continuationEffects仅为规则隔离安排真实Effect前置兄弟；正文仍调用正式A.invoke，不直接改Host状态。
            string code = "return function(ctx) local A=Shared.agnes; if ctx.eventType=='EffectCompleted' then local effects=A.invoke(ctx,{}); " + continuationEffects + " return effects end; " + expression + " end";
            string completed = Probe + ".completed";
            var source = new AbilitySourceDeclaration { DefinitionId = Definition, AbilityId = Probe, Kind = "quick", HandlerRef = Probe,
                DeclarationVersion = "1.0.0", AuthorizationPolicy = "controller", EffectiveZones = new List<string> { "core.zone.entrepreneur_active" }, PresentationRef = Definition,
                AllowedOperations = Pack.Definitions.Single(d => d.DefinitionId == Definition).AbilitySources.First().AllowedOperations.ToList(),
                Actions = new List<AbilityActionDeclaration> { new AbilityActionDeclaration { ActionId = Probe, EntryKind = "quick", BodyHandlerRef = Probe, PresentationRef = Definition } } };
            // 模块探针也走生产声明/编译器；只在私有输入替换已有薄入口，模块原文和依赖登记不补写。
            const string definitionPath = "entrepreneur/entrepreneur.ability.A01.json";
            const string scriptPath = "lua/entrepreneurs/agnes.after.lua";
            var probeDefinition = JObject.Parse(File.ReadAllText(Path.Combine(Pack.RootPath, definitionPath)));
            probeDefinition["applicableModes"] = new JArray("standard", "entrepreneur");
            probeDefinition["abilitySources"] = new JArray(JObject.FromObject(source));
            probeDefinition["abilityHandlers"] = new JArray(JObject.FromObject(new ExternalAbilityHandler {
                HandlerId = Probe, Script = scriptPath, Version = "1.0.0", CompletionHandlerId = completed }));
            // 私有来源已替换为探针，详情也须引用同一来源；继续接受正式详情校验。
            var probeDetail = (JObject)probeDefinition["data"]["playerDetails"].Single(value => (string)value["abilityId"] == "entrepreneur.A01.cover").DeepClone();
            probeDetail["abilityId"] = Probe;
            probeDetail["requiredRuleRef"] = "";
            probeDefinition["data"]["playerDetails"] = new JArray(probeDetail);
            var probeManifest = JObject.Parse(File.ReadAllText(Path.Combine(Pack.RootPath, "pack.json")));
            probeManifest["enabledExpansionIds"] = new JArray("core", "enterprise", "infected");
            string probeRoot = Artifacts.CopyContent(Pack.RootPath, "模块探针正式声明私有输入", new Dictionary<string, string> {
                [definitionPath] = probeDefinition.ToString(), [scriptPath] = code, ["pack.json"] = probeManifest.ToString() });
            Pack = ExternalContentPack.Load(probeRoot);
            Artifacts.RecordContent("模块探针实际装配身份", probeRoot, Pack.ContentHash);
            Registry = new EffectRegistry { ContentPackHash = Pack.ContentHash };
            ResourceEffectExecutor.Register(Registry); GenericLuaEffectExecutor.Register(Registry);
            ContentPrimitiveEffectExecutor.Register(Registry); ItemTransferEffectExecutor.Register(Registry);
            AbilitySourceLuaCatalog.Register(Registry, Pack);
            var map = StaticMapDefinitions.CreateThreePlayerMap();
            var seed = new GameState { GameId = "qy013.module.isolated", MapId = map.MapId, ContentPackHash = Pack.ContentHash,
                LaunchPlayerOrder = new List<int> { 1, 2 }, RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = Pack.ContentHash },
                ContentDefinitions = new GameContentDefinitions { AbilitySources = new List<AbilitySourceDeclaration> { source }, Facilities = Pack.CreateFacilities().ToList(),
                    Characters = Pack.CreateCharacters().ToList(), Components = Pack.Definitions.Where(d => d.ContentType == "component").Select(d => new ComponentContentDefinition {
                        DefinitionId = d.DefinitionId, TypeId = d.ItemTypeId, QuantityPolicy = (string)d.Data["quantityPolicy"], CubeAppearance = d.CubeAppearance }).ToList() },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Name = "隔离甲", Resources = new ResourceSet { GoldVoucher = gold } }, new PlayerState { PlayerId = 2, Name = "隔离乙" } } };
            State = new ItemZoneStateFactory().CreateForSetup(seed, map, new[] {
                new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = supply, PerPlayer = true, InitialZoneRole = "supply" },
                new ItemRecipeEntry { EntryId = "carrier", DefinitionId = Definition, TypeId = "entrepreneur.ability_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "entrepreneur_active" } },
                new[] { new ItemCardZoneDeclaration { DefinitionId = Definition, CardZones = Pack.Definitions.Single(d => d.DefinitionId == Definition).CardZones } });
            State.Phase = GamePhase.ActionRound1; State.Round = 1; State.ActionRound = 1; State.CurrentPlayerId = State.StartPlayerId = 1;
            State.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            Card = State.ItemZones.Items.Single(i => i.DefinitionId == Definition && i.OwnerSubject.PlayerId == 1).ItemId;
            Artifacts.WriteLabeledText("隔离调用入口说明", "只验证正式模块函数在真实Host的库存/付款/选择/恢复，不代表完整选人选面、事件生产、战术轨道或正常页面。");
        }
        internal void Start()
        {
            var option = Registry.QueryActions(State, 1).Single(o => o.ActionId == Probe);
            Assert.That(option.Enabled, Is.True, option.DisabledReason);
            Engine.CreateRoot(AbilityActionExecutor.Create(option, "qy013.probe.command", Object())); Pump();
        }
        internal void Pump()
        {
            var result = Engine.RunUntilQuiescent();
            if (result.Faulted) Artifacts.WriteLabeledText("首次故障原状态", JsonConvert.SerializeObject(State));
            Assert.That(result.Faulted, Is.False, State.EffectRuntime.LastFaultMessage);
            AssertNoTokenPaymentSelection();
        }
        internal void Answer(string value)
        {
            RoundExecutionService.RefreshConcurrentInteractionRevisions(State);
            var request = Open;
            NormalizedValue answer = NormalizedValue.CreateString(value);
            if (request.AnswerSchema == "candidate_ids" || request.AnswerSchema == "candidate_id_array") answer = NormalizedValue.CreateArray(new[] { answer });
            Assert.That(Engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision,
                answer, out string reason), Is.True, reason); Pump();
        }
        internal void Decline()
        {
            RoundExecutionService.RefreshConcurrentInteractionRevisions(State);
            var request = Open;
            Assert.That(Engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision,
                NormalizedValue.CreateBoolean(false), out string reason), Is.True, reason); Pump();
        }
        internal void Restore(string label)
        {
            var recovered = Artifacts.Recover(State, label, Registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic); State = recovered.State;
        }
        internal void Formal()
        {
            // 规则隔离装配：调用生产内部活动快照，保G0；不进入正常启动，不补manifest或缺失依赖。
            // 哈希明确标为隔离活动身份，原始安装输入及生产装载ContentHash另由Artifacts封存。
            var select = typeof(ExternalContentPack).GetMethod("CreateActiveSnapshot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, new[] { typeof(IEnumerable<string>), typeof(string), typeof(string) }, null);
            Assert.That(select, Is.Not.Null);
            string isolatedHash = LuaContentHasher.ComputeSha256(Pack.ContentHash + "|qy013.formal.isolated|entrepreneur");
            Pack = (ExternalContentPack)select.Invoke(Pack, new object[] { new[] { "core", "enterprise", "infected" }, isolatedHash, "entrepreneur" });
            var defs = Pack.ActiveDefinitions;
            var recipes = Pack.SetupRecipes;
            var recipe = recipes.Single(r => r.RecipeId == "standard");
            var enterprise = recipes.Single(r => r.RecipeId == "enterprise");
            var infected = recipes.Single(r => r.RecipeId == "infected");
            recipe.Entries.AddRange(enterprise.Entries); recipe.Entries.AddRange(infected.Entries);
            recipe.Entries.AddRange(recipes.Single(r => r.RecipeId == "entrepreneur").Entries);
            recipe.RecipeId = "infected"; recipe.EnterpriseEntrance = true;
            recipe.InitialBasicCharacterCount = infected.InitialBasicCharacterCount;
            recipe.BasicCharacterKeepCount = infected.BasicCharacterKeepCount; recipe.MaxRetainedBasicCharacterCount = infected.MaxRetainedBasicCharacterCount;
            recipe.CityStyleLevelOneDrawCount = enterprise.CityStyleLevelOneDrawCount; recipe.CityStyleLevelTwoDrawCount = enterprise.CityStyleLevelTwoDrawCount;
            recipe.OperatorDrawCount = 3;
            foreach (var entry in recipe.Entries)
            {
                if (entry.InitialZoneRole == "operator_deck") entry.Condition = new SetupRecipeCondition();
                var definition = defs.SingleOrDefault(d => d.RuntimeId == entry.DefinitionId || d.DefinitionId == entry.DefinitionId);
                if (definition == null) { Assert.That(new[] { "core.player_marker", "core.mobile_city" }, Does.Contain(entry.DefinitionId)); entry.TypeId = entry.DefinitionId; }
                else { entry.DefinitionId = definition.DefinitionId; entry.TypeId = string.IsNullOrEmpty(definition.ItemTypeId) ?
                    definition.ContentType == "character" ? "core.basic_character_card" : definition.ContentType == "facility" ? "core.facility_card" :
                    definition.ContentType == "event" ? "core.event_card" : "core.board_card" : definition.ItemTypeId; }
            }
            recipe.Entries.RemoveAll(e => defs.Any(d => d.DefinitionId == e.DefinitionId && d.ContentType == "event" && YC.Domain.Cards.EventCardDatabase.IsFourPlayerOnly(d.RuntimeId)));
            recipe.CardZoneDeclarations = defs.Where(d => d.CardZones.Count > 0).Select(d => new ItemCardZoneDeclaration { DefinitionId = d.DefinitionId, CardZones = d.CardZones }).ToList();
            var content = new GameContentDefinitions { Entrepreneurs = EntrepreneurContentCatalog.Read(defs), AbilitySources = AbilitySourceLuaCatalog.CreateDeclarations(Pack),
                UsageTracks = defs.SelectMany(d => d.UsageTracks).ToList(), Awards = ContentLifecycleLuaCatalog.CreateAwards(Pack),
                Characters = Pack.CreateCharacters().ToList(), Facilities = Pack.CreateFacilities().ToList(), Events = Pack.CreateEvents().ToList(),
                CityStyles = Pack.CreateCityStyles().ToList(), SpecialActions = Pack.CreateSpecialActions().ToList(),
                EnterpriseDefinitionIds = defs.Where(d => d.ContentType == "enterprise").Select(d => d.DefinitionId).ToList(),
                Components = defs.Where(d => d.ContentType == "component").Select(d => new ComponentContentDefinition { DefinitionId = d.DefinitionId,
                    TypeId = d.ItemTypeId, QuantityPolicy = (string)d.Data["quantityPolicy"], CubeAppearance = d.CubeAppearance }).ToList() };
            Registry = new EffectRegistry { ContentPackHash = Pack.ContentHash, ContentDefinitions = content, SetupRecipe = recipe };
            var map = StaticMapDefinitions.CreateThreePlayerMap(); var query = new MapQueryService(map); var influence = new InfluenceService(query);
            ResourceEffectExecutor.Register(Registry); GenericLuaEffectExecutor.Register(Registry); ItemTransferEffectExecutor.Register(Registry);
            ContentPrimitiveEffectExecutor.Register(Registry, query, influence); InfluenceEffectExecutor.Register(Registry, influence);
            PlayerEntranceLuaCatalog.Register(Registry); EventCardLuaCatalog.Register(Registry, Pack); CityStyleLuaCatalog.Register(Registry, Pack);
            ContentLifecycleLuaCatalog.Register(Registry, Pack);
            CityDestinationLuaCatalog.Register(Registry, Pack); FacilityLayerLuaCatalog.Register(Registry);
            YC.Domain.Scoring.ScoringModifierQueryService.Register(Registry);
            EnterpriseEffectExecutor.Register(Registry, LuaContentCatalog.CreateEnterpriseDefinitions(Registry, defs, Pack.GetScript, Pack));
            ExpansionSetupFlow.Register(Registry); FacilityLuaCatalog.Register(Registry, Pack); CharacterCardLuaCatalog.Register(Registry, Pack);
            AbilitySourceLuaCatalog.Register(Registry, Pack);
            var config = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, RuleModeId = "standard",
                EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" }, ContentPackHash = Pack.ContentHash };
            State = GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1, new[] { new PlayerSeat { PlayerId = 1, PlayerName = "隔离甲" },
                new PlayerSeat { PlayerId = 2, PlayerName = "隔离乙" } }, config, 47, recipe, content, new[] { 1, 2 }, "qy013.formal.isolated", null);
            State.RuleConfiguration.RuleModeId = "entrepreneur"; State.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            Run(ExpansionSetupFlow.PreparationSpec(1, false));
            for (int guard = 0; guard < 200 && State.EntrepreneurSetup?.Stage != "complete"; guard++)
            {
                var request = Open;
                if (request.InteractionTypeId == EntrepreneurFlow.ChooseInteraction)
                    Answer(request.AnsweringPlayerId == 1 ? Definition : "entrepreneur.ability.T09");
                else if (request.MinSelections > 1) AnswerValue(NormalizedValue.CreateArray(request.CandidateIds.Take(request.MinSelections).Select(NormalizedValue.CreateString).ToList()));
                else Answer(request.CandidateIds.First());
            }
            Assert.That(State.EntrepreneurSetup.Stage, Is.EqualTo("complete"));
            // 完成同一真实准备树的企业选择/部门等请求；业务结果仍由生产Lua与Host执行。
            for (int guard = 0; guard < 200 && State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"); guard++)
            {
                var q = Open;
                if (q.MinSelections > 1) AnswerValue(NormalizedValue.CreateArray(q.CandidateIds.Take(q.MinSelections).Select(NormalizedValue.CreateString).ToList()));
                else Answer(q.CandidateIds.First());
            }
            Card = State.EntrepreneurSetup.Players.Single(p => p.PlayerId == 1).AbilityItemId;
            State.Round = 1; State.ActionRound = 1; State.Phase = GamePhase.ActionRound1; State.CurrentPlayerId = State.StartPlayerId = 1;
            Artifacts.WriteLabeledText("正式声明与Host隔离边界", "真实准备/选面/来源收据/Lua/共轨道/收尾。为规则定位隔离设置动作阶段和后续轮号；不是菜单开局、正常GameId或页面验收。未补manifest。");
        }
        internal void Run(EffectSpec spec) { Engine.CreateRoot(spec); Pump(); }
        internal void AnswerValue(NormalizedValue value)
        {
            RoundExecutionService.RefreshConcurrentInteractionRevisions(State);
            var q = Open; Assert.That(Engine.TrySubmitInteraction(q.GetStableInteractionId(), q.AnsweringPlayerId, q.StateRevision, value, out string reason), Is.True, reason); Pump();
        }
        internal void Choose(int round, string face, string gain = null, bool declineGain = false)
        {
            State.Round = round; State.ActionRound = 1; State.Phase = GamePhase.ActionRound1; State.CurrentPlayerId = 1;
            Run(ExpansionSetupFlow.RoundPreparationSpec(1));
            while (Open.InteractionTypeId == ExpansionSetupFlow.SupplyRetirementInteraction) Answer(Open.CandidateIds.First());
            Assert.That(Open.InteractionTypeId, Is.EqualTo(EntrepreneurFlow.FaceInteraction));
            Assert.That(Open.AnsweringPlayerId, Is.EqualTo(1));
            Answer("entrepreneur.candidate." + face);
            while (State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"))
            {
                var q = Open;
                if (q.AllowDecline && declineGain && q.CandidateIds.All(id => int.TryParse(id, out int number) && number > 0)) Decline();
                else if (q.CandidateIds.Contains(gain ?? "")) Answer(gain);
                else if (q.CandidateIds.Contains("continue")) Answer("continue");
                else Answer(q.CandidateIds.First());
            }
        }
        internal AbilityActionOption Action(string action, int player = 1) => Registry.QueryActions(State, player).Single(a => a.ActionId == action);
        internal void Invoke(string action, int player = 1, string commandId = null) { var a = Action(action, player); Assert.That(a.Enabled, Is.True, a.DisabledReason); Run(AbilityActionExecutor.Create(a, commandId ?? "qy013." + action, Object())); }
        // 只核指定Invocation的外层费用门；由用例显式Answer继续或放弃，不自动接受正文Condition。
        internal string AssertQuickEntryPending(string action)
        {
            var invocation = State.EffectRuntime.AbilityActionInvocations.Last(i => i.ActionId == action);
            var request = Open;
            Assert.That(request.InteractionTypeId, Is.EqualTo("action.decline_effect"));
            Assert.That(request.AnsweringPlayerId, Is.EqualTo(1));
            Assert.That(request.Presentation.Prompt.Key, Is.EqualTo("core:entrepreneur.A.quick.condition"));
            Assert.That(request.CandidateIds, Is.EquivalentTo(new[] { "continue", "decline" }));
            var gate = State.EffectRuntime.EffectNodes.Single(n => n.EffectId == request.OwnerEffectId);
            Assert.That(gate.EffectTypeId, Is.EqualTo(EffectTypeIds.Condition));
            Assert.That(gate.ParentEffectId, Is.EqualTo(invocation.OwnerEffectId));
            Assert.That(invocation.ProtocolVersion, Is.EqualTo(2));
            Assert.That(invocation.EntryConditionVersion, Is.EqualTo(AbilityActionEntryCondition.Version));
            Assert.That(invocation.Phase, Is.EqualTo("entry_payment"));
            Assert.That(invocation.PaymentCommitted, Is.False);
            Assert.That(invocation.ActivationCommitted, Is.False);
            Assert.That(invocation.MainActionCommitted, Is.False);
            Assert.That(invocation.BodyEffectIds, Is.Empty);
            return invocation.InvocationId;
        }
        internal void Signal(string eventType, int player, NormalizedValue payload = null)
        {
            string type = "qy013.fixture.signal." + eventType + "." + player;
            if (!Registry.TryGet(type, out _)) Registry.Register(type, c => EffectStepResult.Completed().AddEvent(new EffectEventRequest {
                EventType = eventType, PlayerId = player, OwnerNodeId = c.Node.EffectId, SourceEffectId = c.Node.EffectId, Payload = c.Node.NormalizedArguments.Clone() }));
            Run(EffectSpec.Create(type, payload ?? Object(), player > 0 ? player : 1));
        }
        internal void Cleanup() => Signal("PlayerCleanupStarted", 1);
        internal void StartCleanupObligation(string obligationId = null)
        {
            var due = State.EffectRuntime.CleanupObligations.Where(r => r.Status == "pending" && (obligationId == null || r.ObligationId == obligationId)).First();
            var option = Registry.GetSettlementOptions(State, due.PlayerId).Single(o => o.LocalId == CleanupObligation.SettlementLocalId &&
                State.EffectRuntime.EffectNodes.Any(n => n.EffectId == o.TargetEffectId && Text(n.NormalizedArguments, "obligationId") == due.ObligationId));
            Assert.That(option.IsEnabled, Is.True, option.DisabledReasonCode);
            Assert.That(option.CanDeclineWholeEffect, Is.False);
            var command = new YC.Domain.Commands.GameCommand { Kind = YC.Domain.Rules.GameCommandKind.InvokeSettlementOption, PlayerId = due.PlayerId };
            command.Parameters["optionId"] = option.OptionId; command.Parameters["useId"] = option.TargetUseId;
            command.Parameters["expectedRevision"] = option.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var session = new GameSession(State); session.RegisterHandler(new YC.Application.Interactions.InvokeSettlementOptionCommandHandler(Registry));
            var result = session.Submit(command); Assert.That(result.Succeeded, Is.True, result.Validation?.Reason); Pump();
        }
        internal void StartCleanupOption()
        {
            var option = Registry.GetSettlementOptions(State, 1).Single(o => o.LocalId == "entrepreneur.A05.cleanup.cleanup_order");
            Assert.That(Engine.TryInvokeCharacterSettlementOption(1, option.OptionId, "", State.EffectRuntime.StateRevision, out _, out string reason), Is.True, reason); Pump();
        }
        internal void AssertNoTokenPaymentSelection()
        {
            foreach (var request in State.EffectRuntime.InteractionRequests)
            {
                Assert.That(request.PromptKey, Does.Not.Contain("payment_items"), "等价指令应由Lua一次确定并原子转移，不能补答多余付款页");
                Assert.That(request.InteractionTypeId, Does.Not.Contain("payment_items"));
            }
        }
        internal void ResolveMapRequests()
        {
            for (int guard = 0; guard < 20 && State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"); guard++)
            {
                var q = Open; var node = State.EffectRuntime.EffectNodes.Single(n => n.EffectId == q.OwnerEffectId);
                if (!new[] { EffectTypeIds.PlaceInfluence, EffectTypeIds.MoveInfluence, EffectTypeIds.RemoveInfluence, ContentPrimitiveEffectExecutor.SelectInfluence }.Contains(node.EffectTypeId)) return;
                Assert.That(q.AllowDecline, Is.False); Answer(q.CandidateIds.First());
            }
        }
        internal void ExitSelectedSource(string face)
        {
            string id = Registry.QuerySources(State, 1).First(s => s.ActiveFaceId == face).ItemId;
            string retired = State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.box_retired").ZoneId;
            // 明确隔离资格退出控制，使用生产转移服务维护Order和位置收据；不声称正常行动或卡面效果。
            Registry.Register("qy013.fixture.exit", c => {
                var item = c.State.ItemZones.Items.Single(i => i.ItemId == id);
                new ItemTransferService().RelocateItems(c, new ItemTransferRequest { ExecutingPlayerId = c.Node.PlayerId,
                    ItemIds = new List<string> { id }, ExpectedSource = new ItemPositionRef { ZoneId = item.ZoneId, SlotId = item.SlotId },
                    Destination = new ZoneRef { ZoneId = retired }, InsertionKind = "unordered", RequiredCount = 1,
                    OperationProfileId = "core.entrepreneur_card_transfer" });
                return EffectStepResult.Completed(); });
            Run(EffectSpec.Create("qy013.fixture.exit", Object(), 1));
            Assert.That(Registry.QuerySources(State, 1).Any(s => s.ItemId == id), Is.False);
            Artifacts.WriteLabeledText("来源退出隔离控制", "已通过生产ItemTransferService移动真实已选实体至retired；未取消已登记义务，不计正常行动证据。");
        }
        internal int OwnMapCount => new InfluenceSubjectQuery().GetCandidateItemIds(State, 1, "self").Count;
        internal void LeaveOwnMapCount(int count)
        {
            foreach (string id in new InfluenceSubjectQuery().GetCandidateItemIds(State, 1, "self").Skip(count).ToArray())
                Run(InfluenceEffectSpecFactory.RemoveInfluence(1, id));
            Assert.That(OwnMapCount, Is.EqualTo(count));
            Artifacts.WriteLabeledText("收尾零少目标规则定位", "通过真实RemoveInfluence原语安排剩余目标，仅作隔离规则输入，不计正常菜单行动覆盖。");
        }
        internal static LuaSnapshotValue Snapshot(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return LuaSnapshotValue.Null;
            if (value is JObject obj) return LuaSnapshotValue.Object(obj.Properties().Select(p => new LuaSnapshotEntry(p.Name, Snapshot(p.Value))).ToArray());
            if (value is JArray array) return LuaSnapshotValue.Array(array.Select(Snapshot).ToArray());
            if (value.Type == JTokenType.Boolean) return LuaSnapshotValue.Boolean((bool)value);
            if (value.Type == JTokenType.Integer) return LuaSnapshotValue.Integer((long)value);
            return LuaSnapshotValue.String((string)value);
        }
        public void Dispose()
        {
            if (State != null) Artifacts.WriteLabeledText("最终隔离真实状态", JsonConvert.SerializeObject(State));
            Artifacts.Finish();
        }
    }
}
