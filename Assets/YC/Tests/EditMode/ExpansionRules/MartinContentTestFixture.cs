using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 仅规则隔离：读取本人真实定义/Lua，私有standard激活不改正式G0；不是正常流程或页面验收。
    public abstract class MartinContentTestFixture
    {
        protected ExternalContentPack Pack;
        protected EffectRegistry Registry;
        protected GameState LastState;
        internal IsolatedTestArtifacts Artifacts;
        protected static string Installed => Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
        protected const string Rounds = "lua/entrepreneurs/martin_rounds.lua";
        protected const string Urban = "lua/facilities/martin_urbanization.lua";
        protected const string Ability = "lua/entrepreneurs/martin_ability.lua";
        protected const string Award = "lua/entrepreneurs/martin_award_grant.lua";
        [SetUp] public virtual void StartMartin()
        {
            Artifacts = IsolatedTestArtifacts.BeginCase();
            // 统一运行者须在所有作者停止后执行；先封存整份正式内容，再从同一基线读取所有输入。
            string baseline = Artifacts.CopyContent(Installed, "马丁正式内容私有基线");
            var baselinePack = ExternalContentPack.Load(baseline);
            Artifacts.RecordContent("马丁正式基线装载", baseline, baselinePack.ContentHash);
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(baseline, "pack.json")));
            RequireFormalModuleBindings(manifest);
            Artifacts.WriteLabeledText("马丁正式基线依赖登记", JsonConvert.SerializeObject(new {
                inputRoot = baseline, contentPackHash = baselinePack.ContentHash,
                luaModules = manifest["luaModules"], scriptDependencies = manifest["scriptDependencies"]
            }, Formatting.Indented));
            manifest["enabledExpansionIds"] = new JArray("core", "enterprise", "infected");
            var patches = new Dictionary<string, string>();
            // 只激活本人正式内容及007既有M01，不替换任何卡面处理器。
            var definitions = new[] { "entrepreneur/entrepreneur.ability.M13.json",
                "entrepreneur/entrepreneur.round.M.1.json", "entrepreneur/entrepreneur.round.M.3.json",
                "entrepreneur/entrepreneur.round.M.5.json", "entrepreneur/entrepreneur.round.M.7.json",
                "facility/entrepreneur.facility.M02.json", "facility/entrepreneur.facility.M03.json",
                "facility/entrepreneur.facility.M04.json", "facility/entrepreneur.facility.additional_core_tower.json",
                "award/entrepreneur.award.M01.json" };
            foreach (string relative in definitions)
            {
                var doc = JObject.Parse(File.ReadAllText(Path.Combine(baseline, relative)));
                doc["applicableModes"] = new JArray("standard", "entrepreneur"); patches[relative] = doc.ToString();
            }
            patches["pack.json"] = manifest.ToString();
            string input = Artifacts.CopyContent(baseline, "马丁正式正文私有机制输入", patches);
            Pack = ExternalContentPack.Load(input); Artifacts.RecordContent("马丁正文与冻结模块", input, Pack.ContentHash);
        }
        private static void RequireFormalModuleBindings(JObject manifest)
        {
            // 仅检查正式依赖存在；模块身份、版本、SHA和完整引用形状由生产装载器核验。
            // 不从历史提案补登记，不锁定旧模块版本，也不在缺依赖时跳过测试。
            if (manifest["luaModules"] is not JArray catalog || catalog.Count == 0 ||
                manifest["scriptDependencies"] is not JArray dependencies)
                throw new InvalidDataException("马丁测试缺少正式冻结模块登记：luaModules/scriptDependencies");
            foreach (string script in new[] { Rounds, Ability })
            {
                var bindings = dependencies.OfType<JObject>().Where(d => (string)d["script"] == script).ToList();
                if (bindings.Count != 1 || bindings[0]["modules"] is not JArray modules || modules.Count == 0)
                    throw new InvalidDataException("马丁测试缺少正式脚本依赖绑定：" + script);
            }
        }
        [TearDown] public void FinishMartin()
        {
            if (LastState != null) Artifacts.WriteLabeledText("马丁最终隔离现场", JsonConvert.SerializeObject(LastState));
            Artifacts?.Finish(); LastState = null;
        }
        protected GameState State(string round = "", bool carrier = false, IEnumerable<ItemRecipeEntry> extraRecipe = null)
        {
            Registry = new EffectRegistry { ContentPackHash = Pack.ContentHash };
            GenericLuaEffectExecutor.Register(Registry); ResourceEffectExecutor.Register(Registry);
            ContentLifecycleLuaCatalog.Register(Registry, Pack); CharacterCardLuaCatalog.Register(Registry, Pack);
            AbilitySourceLuaCatalog.Register(Registry, Pack); CharacterCoverEffectExecutor.Register(Registry);
            ItemTransferEffectExecutor.Register(Registry); FacilityEntryEffectExecutor.Register(Registry);
            FacilitySelectionEffectExecutor.Register(Registry); FacilityLuaCatalog.Register(Registry, Pack);
            FacilityLayerLuaCatalog.Register(Registry);
            var map = StaticMapDefinitions.CreateThreePlayerMap();
            var initial = new GameState { GameId = "qy016.isolated." + Guid.NewGuid().ToString("N"), MapId = map.MapId,
                ContentPackHash = Pack.ContentHash, Phase = GamePhase.ActionRound1, Round = 1, ActionRound = 1,
                CurrentPlayerId = 1, StartPlayerId = 1,
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 3, ContentPackHash = Pack.ContentHash,
                    EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } },
                Players = Enumerable.Range(1, 3).Select(p => new PlayerState { PlayerId = p,
                    RemainingMainActionsThisTurn = 1, Resources = new ResourceSet { Originium = 100, OriginiumShard = 100, Iron = 100, GoldVoucher = 100, PureOriginium = 3 } }).ToList(),
                ContentDefinitions = new GameContentDefinitions { Facilities = Pack.CreateFacilities().ToList(), Characters = Pack.CreateCharacters().ToList(),
                    Entrepreneurs = EntrepreneurContentCatalog.Read(Pack.ActiveDefinitions),
                    Awards = ContentLifecycleLuaCatalog.CreateAwards(Pack), AbilitySources = Registry.AbilityDeclarations.ToList(),
                    UsageTracks = Pack.ActiveDefinitions.SelectMany(d => d.UsageTracks).ToList() } };
            initial.RandomRuntime = GameplayRandomRuntime.Create(initial.GameId);
            var recipe = new List<ItemRecipeEntry> { new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker",
                TypeId = "core.player_marker", Quantity = 29, PerPlayer = true, InitialZoneRole = "supply" } };
            foreach (var d in initial.ContentDefinitions.Facilities)
                recipe.Add(new ItemRecipeEntry { EntryId = d.FacilityId, DefinitionId = d.FacilityId,
                    TypeId = "core.facility_card", Quantity = 6, InitialZoneRole = "facility_deck" });
            if (round.Length > 0) recipe.Add(new ItemRecipeEntry { EntryId = "round", DefinitionId = round,
                TypeId = "entrepreneur.round_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "entrepreneur_active" });
            if (carrier) recipe.Add(new ItemRecipeEntry { EntryId = "carrier", DefinitionId = "entrepreneur.ability.M13",
                TypeId = "entrepreneur.ability_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "entrepreneur_active" });
            if (extraRecipe != null) recipe.AddRange(extraRecipe);
            var state = new ItemZoneStateFactory().CreateForSetup(initial, map, recipe,
                Pack.ActiveDefinitions.Select(d => new ItemCardZoneDeclaration { DefinitionId = d.DefinitionId, CardZones = d.CardZones }).ToList());
            state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2, 3 };
            InfluenceEffectExecutor.Register(Registry, state, new MapQueryService(map));
            var query = new MapQueryService(map);
            var influence = Registry.InfluenceService;
            var costs = new TravelCostService(query);
            ContentPrimitiveEffectExecutor.Register(Registry, query, influence);
            CityDestinationLuaCatalog.Register(Registry, Pack);
            CityMoveEffectExecutor.Register(Registry, query, influence,
                new CityMovementService(query, influence, costs), costs, new ResourceTokenService());
            Registry.Register("qy016.fixture.setup", c => EffectStepResult.Completed().AddEvent(new EffectEventRequest {
                EventType = "QY016SetupBody", PlayerId = c.Node.PlayerId, RouteKey = Text(c.Node.NormalizedArguments, "abilityId"), Payload = Object() }));
            Registry.RegisterEventHandler(new EffectEventHandlerRegistration("qy016.fixture.setup.source", "QY016SetupBody", "*", "qy016.fixture.setup.source", c => {
                var source = Registry.QuerySources(c.State, c.Event.PlayerId, "setup").Single(s => s.AbilityId == c.Event.RouteKey);
                var declaration = Registry.AbilityDeclarations.Single(d => d.DefinitionId == source.DefinitionId && d.AbilityId == source.AbilityId);
                return new List<EffectSpec> { AbilitySourceEffectExecutor.Create(source, c.Event, declaration.HandlerRef, false, c.State.Round) };
            }, EffectHandlerRole.Observer));
            Registry.Register(new EffectRegistration("qy016.isolated.positions", c => {
                var moved = new ItemTransferService().RelocateBatch(c, BatchRequest(c.Node.NormalizedArguments));
                return EffectStepResult.Completed(Object("movedCount", NormalizedValue.CreateInteger(moved.MovedCount)));
            }));
            // 仅通用选面收据适配；候选取本人真实定义，调用公共CommitSelectedFace，不写LocalState。
            Registry.Register(new EffectRegistration("qy016.isolated.select_face", c => {
                string definition = Text(c.Node.NormalizedArguments, "definitionId");
                var item = c.State.ItemZones.Items.Single(i => i.DefinitionId == definition && i.ControllerSubject.PlayerId == c.Node.PlayerId);
                var faces = Pack.ActiveDefinitions.Single(d => d.DefinitionId == definition).Data["faces"].ToObject<List<RoundCandidateDefinition>>();
                if (c.Node.FlowStage.Length == 0)
                {
                    var request = new EffectInteractionSpec { InteractionTypeId = "qy016.face", AnsweringPlayerId = c.Node.PlayerId,
                        AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, AllowDecline = false,
                        PromptKey = "qy016.isolated.face", Visibility = "owner",
                        PromptParameters = Object("abilitySourceCandidates", NormalizedValue.CreateArray(faces.Select(f => Object(
                            "candidateId", NormalizedValue.CreateString(f.CandidateId), "itemId", NormalizedValue.CreateString(item.ItemId),
                            "definitionId", NormalizedValue.CreateString(definition), "faceId", NormalizedValue.CreateString(f.FaceId))).ToList())) };
                    request.CandidateIds.AddRange(faces.Select(f => f.CandidateId));
                    return EffectStepResult.Continue("selected").AddInteraction(request);
                }
                var answer = c.GetLatestInteractionAnswer().StringValue;
                var selected = faces.Single(f => f.CandidateId == answer);
                var prior = c.State.EffectRuntime.InteractionRequests.Last(r => r.OwnerEffectId == c.Node.EffectId && r.Status == "answered");
                AbilitySourceSelection.CommitSelectedFace(c, item.ItemId, selected.FaceId, prior.GetStableInteractionId(), answer);
                return EffectStepResult.Completed();
            }));
            LastState = state; return state;
        }
        protected EffectNodeRuntimeState Run(GameState state, EffectSpec spec)
        {
            LastState = state; var engine = new EffectTreeExecutor(state, Registry); string id = engine.CreateRoot(spec);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage); return engine.GetNode(id);
        }
        protected InteractionRequest Open(GameState state) => state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
        protected void Answer(GameState state, NormalizedValue value)
        {
            var r = Open(state); var engine = new EffectTreeExecutor(state, Registry);
            Assert.That(engine.TrySubmitInteraction(r.GetStableInteractionId(), r.AnsweringPlayerId, r.StateRevision, value, out string why), Is.True, why);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
        }
        protected void SelectFace(GameState state, string definition, string face)
        {
            Run(state, EffectSpec.Create("qy016.isolated.select_face", Object("definitionId", NormalizedValue.CreateString(definition)), 1));
            Answer(state, NormalizedValue.CreateString("entrepreneur.candidate." + face));
        }
        protected void InitializeTrack(GameState state, string round, string track)
        {
            string id = state.ItemZones.Items.Single(i => i.DefinitionId == round && i.ControllerSubject.PlayerId == 1).ItemId;
            Run(state, UsageTracks.CreateInitialization(1, id, track));
        }
        protected void Immediate(GameState state, string face)
        {
            Run(state, EffectSpec.Create("qy016.fixture.setup", Object("abilityId", NormalizedValue.CreateString("entrepreneur." + face + ".setup")), 1));
        }
        protected void Action(GameState state, string face)
        {
            var option = Registry.QueryActions(state, 1).Single(o => o.SourceRef.AbilityId == "entrepreneur.face." + face);
            Assert.That(option.Enabled, Is.True, option.DisabledReason);
            Run(state, AbilityActionExecutor.Create(option, "qy016:" + Guid.NewGuid().ToString("N")));
        }
        protected ItemInstanceState Card(GameState state, string definition) => state.ItemZones.Items.Where(i => i.DefinitionId == definition &&
            i.ZoneId == FindZone(state, "facility_deck").ZoneId).OrderBy(i => i.ItemId, StringComparer.Ordinal).First();
        protected FacilityCellLayerSnapshot Cell(GameState state, int slot, int player = 1) => FacilityLayerQuery.ResolveCell(state, player, slot);
        protected void Position(GameState state, ItemInstanceState item, string target, string insertion = "unordered", int player = 1)
        {
            var leg = new ItemTransferRequest { ExecutingPlayerId = player, ItemIds = new List<string> { item.ItemId }, RequiredCount = 1,
                ExpectedSource = new ItemPositionRef { ZoneId = item.ZoneId }, Destination = new ZoneRef { ZoneId = target },
                InsertionKind = insertion, OperationProfileId = "core.facility_card_transfer" };
            var request = ItemTransferService.CreateBatch(state, player, "qy016:position:" + Guid.NewGuid().ToString("N"), new[] { leg });
            Run(state, EffectSpec.Create("qy016.isolated.positions", ItemTransferService.BatchArguments(request), player));
        }
        protected LuaInvocationResult Invoke(GameState state, string ability, string script, NormalizedValue payload, string eventType = "AbilityInvoked", string responseKind = "effects")
        {
            var snapshot = LuaFacadeSnapshot.FromInvocation("p1", state.FindPlayer(1).Score, state.FindPlayer(1).Resources.GoldVoucher, 3, "1.2.0");
            snapshot.GlobalFacilities = FacilityLayerLuaCatalog.CreateProjection(state); snapshot.EventPayload = Snapshot(payload);
            var context = new LuaInvocationContext("qy016.host", eventType, "p1", state.EffectRuntime.StateRevision, "1.2.0", 3,
                state.FindPlayer(1).Score, state.FindPlayer(1).Resources.GoldVoucher, snapshot) { ResponseKind = responseKind };
            string source = Pack.GetScript(script);
            return new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("qy016.isolated.host", ability, ability, "1.2.0", source, LuaContentHasher.ComputeSha256(source)), context);
        }
        private static LuaSnapshotValue Snapshot(NormalizedValue v)
        {
            if (v == null || v.Kind == NormalizedValueKind.Null) return LuaSnapshotValue.Null;
            if (v.Kind == NormalizedValueKind.String) return LuaSnapshotValue.String(v.StringValue);
            if (v.Kind == NormalizedValueKind.Integer) return LuaSnapshotValue.Integer(v.IntegerValue);
            if (v.Kind == NormalizedValueKind.Boolean) return LuaSnapshotValue.Boolean(v.BooleanValue);
            if (v.Kind == NormalizedValueKind.Array) return LuaSnapshotValue.Array(v.Items.Select(Snapshot).ToList());
            if (v.Kind == NormalizedValueKind.Object) return LuaSnapshotValue.Object(v.Properties.Select(p => new LuaSnapshotEntry(p.Name, Snapshot(p.Value))).ToList());
            throw new InvalidOperationException("隔离输入不支持该值类型");
        }
        protected void Overlay(GameState state, ItemInstanceState next, int slot)
        {
            var cell = Cell(state, slot);
            var old = state.ItemZones.Items.Single(i => i.ItemId == cell.TopItemId);
            var legs = new[] { new ItemTransferRequest { ExecutingPlayerId = 1, ItemIds = new List<string> { old.ItemId }, RequiredCount = 1,
                ExpectedSource = new ItemPositionRef { ZoneId = old.ZoneId }, Destination = new ZoneRef { ZoneId = cell.CoveredZoneId },
                InsertionKind = "top", OperationProfileId = "core.facility_card_transfer" },
                new ItemTransferRequest { ExecutingPlayerId = 1, ItemIds = new List<string> { next.ItemId }, RequiredCount = 1,
                ExpectedSource = new ItemPositionRef { ZoneId = next.ZoneId }, Destination = new ZoneRef { ZoneId = cell.CellZoneId },
                InsertionKind = "unordered", OperationProfileId = "core.facility_card_transfer" } };
            var request = ItemTransferService.CreateBatch(state, 1, "qy016:overlay:" + Guid.NewGuid().ToString("N"), legs);
            Run(state, EffectSpec.Create("qy016.isolated.positions", ItemTransferService.BatchArguments(request), 1));
        }
        protected void AcceptOptionalGates(GameState state)
        {
            for (int i=0; i<10 && state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open") && Open(state).InteractionTypeId == "action.decline_effect"; i++)
                Answer(state, NormalizedValue.CreateString("continue"));
        }
        protected EffectTreeExecutor BeginImmediate(GameState state, string face)
        {
            var engine = new EffectTreeExecutor(state, Registry);
            engine.CreateRoot(EffectSpec.Create("qy016.fixture.setup", Object("abilityId", NormalizedValue.CreateString("entrepreneur." + face + ".setup")), 1)); return engine;
        }
        protected string BlueprintZone(GameState state, int player = 1)
        {
            var carrier = state.ItemZones.Items.Single(i => i.DefinitionId == "entrepreneur.ability.M13" && i.ControllerSubject.PlayerId == player);
            return state.ItemZones.Zones.Single(z => z.ParentId == carrier.ItemId && z.LocalZoneId == "blueprints").ZoneId;
        }
        protected ItemInstanceState Blueprint(GameState state, string definition)
        {
            var card = Card(state, definition); Position(state, card, BlueprintZone(state));
            return state.ItemZones.Items.Single(i => i.ItemId == card.ItemId);
        }
        protected string SupplyZone(GameState state, int slot) => FacilityLayerQuery.Supply(state).Single(s => s.SlotIndex == slot).SlotZoneId;
        protected ItemInstanceState Supply(GameState state, string definition, int slot)
        {
            var card = Card(state, definition); Position(state, card, SupplyZone(state,slot), "top");
            return state.ItemZones.Items.Single(i => i.ItemId == card.ItemId);
        }
        protected void ActivateFace(GameState state, string round, string face)
        {
            SelectFace(state, round, face);
            InitializeTrack(state, round, "usage.entrepreneur.M." + round.Split('.').Last());
        }
        protected void UntilPrompt(GameState state, string prompt)
        {
            // 隔离测试只沿当前真实候选回答；不生成、改写或跳过业务效果。
            for (int i=0;i<60;i++)
            {
                var request=Open(state); if(request.PromptKey==prompt) return;
                string answer=request.CandidateIds.Contains("continue")?"continue":request.CandidateIds.Contains("pay")?"pay":request.CandidateIds.First();
                Answer(state,NormalizedValue.CreateString(answer));
            }
            Assert.Fail("未到达预期正式请求："+prompt);
        }
        protected void OwnInfluence(GameState state)
        {
            Run(state,InfluenceEffectSpecFactory.PlaceInfluence(1,""));
            for(int i=0;i<10 && state.EffectRuntime.InteractionRequests.Any(r=>r.Status=="open");i++)
                Answer(state,NormalizedValue.CreateString(Open(state).CandidateIds.First()));
            Assert.That(state.Map.Influences.Any(i=>i.PlayerId==1),Is.True);
        }
        protected EffectSpec BuildScenario(GameState state, ItemInstanceState card, int slot)
        {
            var spec=FacilityBuildEffectSpecFactory.Build(1,card.ItemId,slot,"free",false,"qy016.isolated",false,card.ZoneId,"deck");
            var observed=Registry.FacilityBuildQueries.ResolveSlots(state,1,card.ItemId).Single(c=>c.SlotIndex==slot);
            spec.NormalizedArguments.Properties.Add(new NormalizedValueEntry {Name="slotObservation",Value=FacilityBuildJournal.EncodeSlot(observed)});
            spec.NormalizedArguments.Properties.Sort((a,b)=>System.StringComparer.Ordinal.Compare(a.Name,b.Name)); return spec;
        }
        protected void PrepareGoldBuild(GameState state, string itemId, string zone)
        {
            var request=Open(state); Assert.That(request.InteractionTypeId,Is.EqualTo(FacilitySelectionEffectExecutor.NormalBuildInteractionType));
            var engine=new EffectTreeExecutor(state,Registry);
            Assert.That(engine.TryPrepareEffectBuildPayment(1,request.OwnerEffectId,request.GetStableInteractionId(),request.CandidateSetId,
                request.CandidateSetVersion,itemId,Object("paymentMode",NormalizedValue.CreateString("gold"),
                "expectedSourceZoneId",NormalizedValue.CreateString(zone)),state.EffectRuntime.StateRevision,Guid.NewGuid().ToString("N"),out string reason),Is.True,reason);
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
        }
        protected EffectTreeExecutor BeginAction(GameState state,string face)
        {
            var option=Registry.QueryActions(state,1).Single(o=>o.SourceRef.AbilityId=="entrepreneur.face."+face);
            Assert.That(option.Enabled,Is.True,option.DisabledReason);
            var engine=new EffectTreeExecutor(state,Registry);engine.CreateRoot(AbilityActionExecutor.Create(option,"qy016:"+Guid.NewGuid().ToString("N")));return engine;
        }
        protected void UntilNode(EffectTreeExecutor engine,Func<bool> predicate)
        {
            for(int i=0;i<1200 && !predicate();i++) Assert.That(engine.Advance(),Is.True,engine.State.EffectRuntime.LastFaultMessage);
            Assert.That(predicate(),Is.True);
        }
        protected GameState Recover(GameState state, string label)
        {
            var result = Artifacts.Recover(state, label, Registry); Assert.That(result.Succeeded, Is.True, result.Diagnostic);
            LastState = result.State; return result.State;
        }
    }
}
