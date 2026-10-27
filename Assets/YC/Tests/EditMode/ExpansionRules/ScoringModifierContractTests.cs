using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Enterprises;
using YC.Domain.SpecialActions;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class ScoringModifierContractTests
    {
        private FinalScoringTestFixture fixture;
        [TearDown] public void FinishArtifacts() => fixture?.Finish();
        [TestCase(0, 0)] [TestCase(1, 0)] [TestCase(2, 1)] [TestCase(3, 1)] [TestCase(4, 2)] [TestCase(5, 2)]
        public void InstalledContractLuaCountsEachHolderOnce(int count, int expected)
        {
            fixture = new FinalScoringTestFixture(companyCount: count, holders: 2);
            string before = JsonConvert.SerializeObject(fixture.State);
            var result = fixture.Snapshot().Regions.Single(r => r.RegionId == fixture.RegionId);
            Assert.That(result.InfluenceCounts[1], Is.EqualTo(expected)); Assert.That(result.InfluenceCounts[2], Is.EqualTo(expected));
            Assert.That(result.InfluenceModifiers.Count, Is.EqualTo(2));
            Assert.That(new CompanyInfluenceRules(fixture.Map).GetControlContribution(fixture.State, fixture.RegionId, 1), Is.EqualTo(expected), "现有显示/采集关联类仍保留。");
            Assert.That(JsonConvert.SerializeObject(fixture.State), Is.EqualTo(before));
            Assert.That(fixture.Registry.QueryActions(fixture.State, 1).Any(a => a.SourceRef.DefinitionId == CompanyInfluenceRules.ContractDefinitionId), Is.False);
            Assert.That(new RegionControlService(fixture.Map, fixture.Queries).Evaluate(fixture.State).Single(r => r.RegionId == fixture.RegionId).InfluenceCounts[1], Is.EqualTo(expected));
            Assert.Throws<InvalidOperationException>(() => new RegionControlService(fixture.Map).Evaluate(fixture.State));
        }
        [Test] public void ContractRemaindersDoNotCrossRegionsAndLostHolderStopsContributing()
        {
            fixture = new FinalScoringTestFixture(companyCount: 2, holders: 1);
            var company = fixture.State.ItemZones.Items.Where(i => i.TypeId == "infected.company_marker").ToArray();
            var other = fixture.Definition.Regions.First(r => r.RegionId != fixture.RegionId && fixture.Slots(r.RegionId).Any());
            fixture.Place(company[1], fixture.Slots(other.RegionId).First());
            Assert.That(fixture.Snapshot().Regions.All(r => r.InfluenceCounts[1] == 0), Is.True);
            fixture.Place(company[1], fixture.Slots(fixture.RegionId).Skip(1).First());
            Assert.That(fixture.Snapshot().Regions.Single(r => r.RegionId == fixture.RegionId).InfluenceCounts[1], Is.EqualTo(1));
            fixture.State.ItemZones.Items.Single(i => i.DefinitionId == CompanyInfluenceRules.ContractDefinitionId && i.OwnerSubject.PlayerId == 1).ZoneId = fixture.Zone("reserve").ZoneId;
            Assert.That(fixture.Snapshot().Regions.Single(r => r.RegionId == fixture.RegionId).InfluenceCounts[1], Is.Zero);
        }
        [Test] public void FullCityAndContractTotalsResolvePositiveTieWithoutPhysicalMarker()
        {
            string script = @"return function(ctx) local f=ctx.payload.facts; local items={}; local me=ctx.payload.authorizedPlayerId
              if me==1 and f.pass=='influence_delta' then local city=0; for _,v in ipairs(f.cityCounts) do if v.playerId==me then city=v.count end end; if city>0 then items={{kind='influence_delta',regionId=f.regionId,playerId=me,delta=2}} end
              elseif me==1 and f.pass=='tie_claim' then items={{kind='tie_claim',regionId=f.regionId,playerId=me,ruleId='fixture.priority'}}
              elseif me==1 and f.pass=='region_value_delta' then for _,c in ipairs(f.controlClaims) do if c.ruleId=='fixture.priority' then items={{kind='region_value_delta',regionId=f.regionId,delta=-2,reasonRef='claim:fixture.priority'}} end end end
              return {queryId=ctx.payload.queryId,basisDigest=ctx.payload.basisDigest,items=items} end";
            fixture = new FinalScoringTestFixture(new[] { (script, new[] { "influence_delta", "tie_claim", "region_value_delta" }) }, companyCount: 2, holders: 2, includeCities: true);
            var location = fixture.Definition.Locations.First(l => l.RegionId == fixture.RegionId && l.CanDockCity);
            var parent = fixture.State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == location.LocationId);
            var dock = fixture.State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.city_dock" && z.ParentId == parent.ZoneId);
            var city = fixture.State.ItemZones.Items.Single(i => i.PhysicalKind == ItemPhysicalKind.MobileCity && i.OwnerSubject.PlayerId == 1);
            typeof(ItemZoneStateMigrator).GetMethod("SetPosition", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Invoke(null, new object[] { fixture.State, city, dock.ZoneId });
            Assert.That(fixture.State.FindPlayer(1).CityLocationId, Is.EqualTo(location.LocationId));
            fixture.PlacePlayer(2, 4, skip: 2);
            var r = fixture.Snapshot().Regions.Single(v => v.RegionId == fixture.RegionId);
            Assert.That(r.PhysicalCounts[1], Is.Zero); Assert.That(r.InfluenceCounts[1], Is.EqualTo(5)); Assert.That(r.InfluenceCounts[2], Is.EqualTo(5));
            Assert.That(r.ControllerPlayerId, Is.EqualTo(1)); Assert.That(r.ScoreValue, Is.EqualTo(r.BaseScoreValue - 2));
            Assert.That(r.ControlReason, Is.EqualTo("tie_claim"));
        }
        [Test] public void ZeroTieNeverRunsAClaimAndUniqueWinnerGetsNoTieReason()
        {
            fixture = new FinalScoringTestFixture(new[] { (FinalScoringTestFixture.ReturnItem("{kind='tie_claim',regionId=f.regionId,playerId=me,ruleId='priority'}"), new[] { "tie_claim" }) });
            Assert.That(fixture.Snapshot().Regions.All(r => !r.ControllerPlayerId.HasValue), Is.True);
            fixture.PlacePlayer(1, 1);
            var r = fixture.Snapshot().Regions.Single(v => v.RegionId == fixture.RegionId);
            Assert.That(r.ControllerPlayerId, Is.EqualTo(1)); Assert.That(r.ControlClaims, Is.Empty); Assert.That(r.ScoreValue, Is.EqualTo(r.BaseScoreValue));
        }
        [Test] public void DifferentPositiveTieClaimantsConflictInsteadOfChoosingRegistrationOrder()
        {
            fixture = new FinalScoringTestFixture(new[] { (FinalScoringTestFixture.ReturnItem("{kind='tie_claim',regionId=f.regionId,playerId=me,ruleId='priority'}"), new[] { "tie_claim" }) });
            fixture.PlacePlayer(1, 1); fixture.PlacePlayer(2, 1, skip: 1);
            string before = JsonConvert.SerializeObject(fixture.State);
            Assert.Throws<InvalidOperationException>(() => fixture.Snapshot()); Assert.That(JsonConvert.SerializeObject(fixture.State), Is.EqualTo(before));
        }
        [TestCase("{kind='influence_delta',regionId=f.regionId,playerId=99,delta=1}")]
        [TestCase("{kind='influence_delta',regionId='unknown',playerId=me,delta=1}")]
        [TestCase("{kind='influence_delta',regionId=f.regionId,playerId=me,delta=1.5}")]
        [TestCase("{kind='influence_delta',regionId=f.regionId,playerId=me,delta=1,unexpected=1}")]
        [TestCase("{kind='unknown',regionId=f.regionId,playerId=me,delta=1}")]
        public void InvalidPureReturnRejectsWithoutStateMutation(string item)
        {
            fixture = new FinalScoringTestFixture(new[] { (FinalScoringTestFixture.ReturnItem(item), new[] { "influence_delta" }) });
            string before = JsonConvert.SerializeObject(fixture.State);
            Assert.Throws<InvalidOperationException>(() => fixture.Snapshot()); Assert.That(JsonConvert.SerializeObject(fixture.State), Is.EqualTo(before));
        }
        [TestCase("return {Effect.GainScore({recipient=ctx.playerId,amount=1})}")]
        [TestCase("local n=math.random(); return {}")]
        public void PureQueryCannotCreateEffectsOrRandom(string body)
        {
            fixture = new FinalScoringTestFixture(new[] { ("return function(ctx) " + body + " end", new[] { "influence_delta" }) });
            string before = JsonConvert.SerializeObject(fixture.State); Assert.Throws<InvalidOperationException>(() => fixture.Snapshot());
            Assert.That(JsonConvert.SerializeObject(fixture.State), Is.EqualTo(before));
        }
        [Test] public void MultipleResourceProfilesForSamePlayerReject()
        {
            var script = FinalScoringTestFixture.ReturnItem("{kind='resource_profile',playerId=me,profileId='replace',entries={{resourceType='Iron',units=4,points=1}}}");
            fixture = new FinalScoringTestFixture(new[] { (script, new[] { "resource_profile" }), (script, new[] { "resource_profile" }) });
            Assert.Throws<InvalidOperationException>(() => fixture.Queries.QueryResourceProfile(fixture.State, "test", 1));
        }
    }

    // 仅隔离规则/恢复夹具。文件副作用只在BeginCase/CopyContent/Record/Recover/Finish的独占输出内。
    internal sealed class FinalScoringTestFixture
    {
        public const string Token = "infected.component.gavial_token", RootType = "qy011.final.session";
        public readonly IsolatedTestArtifacts Artifacts;
        public readonly EffectRegistry Registry;
        public readonly GameMapDefinition Definition;
        public readonly IMapQueryService Map;
        public readonly ScoringModifierQueryService Queries;
        public readonly FinalScoringSessionService Service;
        public GameState State;
        public string OwnerId = "";
        public readonly string RegionId;
        public FinalScoringTestFixture(IEnumerable<(string Script, string[] Kinds)> modifiers = null, string eventScript = null, int companyCount = 0, int holders = 0, bool completionHandler = false, bool departureResponse = false, bool includeCities = false)
        {
            Artifacts = IsolatedTestArtifacts.BeginCase();
            string installed = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            var token = JObject.Parse(File.ReadAllText(Path.Combine(installed, "component/" + Token + ".json")));
            var sources = new List<AbilitySourceDeclaration>(); var handlers = new List<ExternalAbilityHandler>(); var replacements = new Dictionary<string, string>(); int index = 0;
            foreach (var modifier in modifiers ?? Array.Empty<(string Script, string[] Kinds)>())
            {
                string id = "qy011.query." + index++, path = "lua/qy011/" + id + ".lua";
                sources.Add(new AbilitySourceDeclaration { AbilityId = id, Kind = "ruleQuery", HandlerRef = id, DeclarationVersion = "1.0.0", AuthorizationPolicy = "owner", EffectiveZones = new List<string> { "core.zone.box_reserve" }, PresentationRef = Token,
                    QueryKinds = new List<string> { ScoringModifierProtocol.QueryKind }, AllowedOperations = modifier.Kinds.Select(ScoringModifierProtocol.Permission).ToList() });
                handlers.Add(new ExternalAbilityHandler { HandlerId = id, Script = path, Version = "1.0.0" }); replacements[path] = modifier.Script;
            }
            if (eventScript != null)
            {
                sources.Add(new AbilitySourceDeclaration { AbilityId = "qy011.special", Kind = "event", HandlerRef = "qy011.special", DeclarationVersion = "1.0.0", AuthorizationPolicy = "owner", EffectiveZones = new List<string> { "core.zone.box_reserve" }, PresentationRef = Token, ObservedEvents = new List<string> { "FinalScoringStarted" } });
                handlers.Add(new ExternalAbilityHandler { HandlerId = "qy011.special", Script = "lua/qy011/special.lua", Version = "1.0.0", CompletionHandlerId = completionHandler ? "qy011.special.completed" : "" }); replacements["lua/qy011/special.lua"] = eventScript;
            }
            if(departureResponse) {
                sources.Add(new AbilitySourceDeclaration {AbilityId="qy011.actual-departure",Kind="event",HandlerRef="qy011.actual-departure",DeclarationVersion="1.0.0",AuthorizationPolicy="owner",
                    EffectiveZones=new List<string>{"core.zone.box_reserve"},PresentationRef=Token,ObservedEvents=new List<string>{"InfluenceRemoved"},
                    AllowedOperations=new List<string>{"response.optional_effect","response.self","response.batch"}});
                handlers.Add(new ExternalAbilityHandler {HandlerId="qy011.actual-departure",Script="lua/qy011/actual-departure.lua",Version="1.0.0"});
                replacements["lua/qy011/actual-departure.lua"]="return function(ctx) if ctx.payload.ownerPlayerId~=tonumber(string.sub(ctx.playerId,2)) then return {} end; return {Effect.Choice({player=ctx.playerId,interactionType='effect.choice.execute',branches={{id='iron',effects={Effect.GainResource({player=ctx.playerId,resourceType='iron',amount=4})}},{id='leave',effects={}}}})} end";
            }
            token["abilitySources"] = JArray.FromObject(sources); token["abilityHandlers"] = JArray.FromObject(handlers); token["actionCallPolicies"] = new JArray(); replacements["component/" + Token + ".json"] = token.ToString();
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(installed, "pack.json"))); manifest["enabledExpansionIds"] = new JArray("core", "enterprise", "infected"); replacements["pack.json"] = manifest.ToString();
            string input = Artifacts.CopyContent(installed, "终局私有规则输入", replacements); var pack = ExternalContentPack.Load(input); Artifacts.RecordContent("终局实际装载内容", input, pack.ContentHash);
            Registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
            ResourceEffectExecutor.Register(Registry); GenericLuaEffectExecutor.Register(Registry); CharacterCoverEffectExecutor.Register(Registry);
            CharacterCardLuaCatalog.Register(Registry, pack); CityStyleLuaCatalog.Register(Registry, pack); ContentLifecycleLuaCatalog.Register(Registry, pack); AbilitySourceLuaCatalog.Register(Registry, pack);
            Definition = StaticMapDefinitions.Resolve(StaticMapDefinitions.FourPlayerMapId); Map = new MapQueryService(Definition);
            InfluenceEffectExecutor.Register(Registry,new InfluenceService(Map));
            var source = new GameState { GameId = "qy011.rule.isolated", ContentPackHash = pack.ContentHash, MapId = Definition.MapId,
                RuleConfiguration = new GameRuleConfiguration { MapId = Definition.MapId, PlayerCount = 2, ContentPackHash = pack.ContentHash },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Name = "甲" }, new PlayerState { PlayerId = 2, Name = "乙" } },
                ContentDefinitions = new GameContentDefinitions { UsageTracks = pack.ActiveDefinitions.SelectMany(d => d.UsageTracks).ToList(), Facilities = pack.CreateFacilities().ToList(), Characters = pack.CreateCharacters().ToList(),
                    Components = pack.ActiveDefinitions.Where(d => d.ContentType == "component").Select(d => new ComponentContentDefinition { DefinitionId = d.DefinitionId, TypeId = d.ItemTypeId, QuantityPolicy = (string)d.Data["quantityPolicy"], CubeAppearance = d.CubeAppearance }).ToList(),
                    AbilitySources = Registry.AbilityDeclarations.ToList(), Awards = ContentLifecycleLuaCatalog.CreateAwards(pack), CityStyles = pack.CreateCityStyles().ToList(), SpecialActions = pack.CreateSpecialActions().ToList() } };
            State = new ItemZoneStateFactory().CreateForSetup(source, Definition, new[] {
                new ItemRecipeEntry { EntryId = "token", DefinitionId = Token, TypeId = "infected.gavial_token", Quantity = 1, PerPlayer = true, InitialZoneRole = "reserve" },
                new ItemRecipeEntry { EntryId = "markers", DefinitionId = "component.marker", TypeId = "core.player_marker", Quantity = 8, PerPlayer = true, InitialZoneRole = "supply" },
                new ItemRecipeEntry { EntryId = "contracts", DefinitionId = CompanyInfluenceRules.ContractDefinitionId, TypeId = ItemTypeRegistry.ContractTokenTypeId, Quantity = 1, PerPlayer = true, InitialZoneRole = "contracts" },
                new ItemRecipeEntry { EntryId = "city", DefinitionId = "core.component.city", TypeId = "core.mobile_city", Quantity = includeCities ? 1 : 0, PerPlayer = true, InitialZoneRole = "city_reserve" },
                new ItemRecipeEntry { EntryId = "company", DefinitionId = "infected.component.company_marker", TypeId = "infected.company_marker", Quantity = companyCount, InitialZoneRole = "company_markers" } });
            State.Phase = GamePhase.FinalScoring; State.Round = State.MaxRounds = 8; State.StartPlayerId = 1; State.CurrentPlayerId = -1;
            State.LaunchPlayerOrder = new List<int> { 1, 2 };
            foreach (var contract in State.ItemZones.Items.Where(i => i.DefinitionId == CompanyInfluenceRules.ContractDefinitionId && i.OwnerSubject.PlayerId <= holders)) contract.ZoneId = Zone("rewards", contract.OwnerSubject.PlayerId).ZoneId;
            RegionId = Definition.Regions.OrderByDescending(r => Slots(r.RegionId).Count()).First().RegionId;
            int slot = 0; foreach (var item in State.ItemZones.Items.Where(i => i.TypeId == "infected.company_marker")) Place(item, Slots(RegionId).ElementAt(slot++));
            Queries = new ScoringModifierQueryService(Map, Registry); Service = new FinalScoringSessionService(Queries);
            Registry.Register(new EffectRegistration(RootType, ExecuteSession, EffectExecutorKind.IntrinsicFlow, ScoringModifierProtocol.Version));
        }
        public static string ReturnItem(string item) => "return function(ctx) local f=ctx.payload.facts; local me=ctx.payload.authorizedPlayerId; return {queryId=ctx.payload.queryId,basisDigest=ctx.payload.basisDigest,items={" + item + "}} end";
        public IEnumerable<string> Slots(string region) => Definition.Locations.Where(l => l.RegionId == region).SelectMany(l => Enumerable.Range(0, l.InfluenceSlotCount).Select(n => InfluenceService.GetLocationSlotId(l.LocationId, n)));
        public ZoneInstanceState Zone(string role, int owner = -1) => State.ItemZones.Zones.Single(z => z.LocalZoneId == role && (owner < 0 || z.OwnerPlayerId == owner));
        public void Place(ItemInstanceState item, string slot)
        {
            // 仅隔离夹具初始位置；沿生产初始化助手同步固定槽Orders，先保证合法归档再测终局坏档。
            string zone = State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.influence_slots" && z.LegalSlotIds.Contains(slot)).ZoneId;
            typeof(ItemZoneStateMigrator).GetMethod("SetPosition", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Invoke(null, new object[] { State, item, zone });
            item.SlotId = slot;
        }
        public void PlacePlayer(int player, int count, int skip = 0)
        {
            var items = State.ItemZones.Items.Where(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == player && State.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.LocalZoneId == "supply")).Take(count).ToArray();
            Assert.That(items.Length, Is.EqualTo(count)); for (int n = 0; n < count; n++) Place(items[n], Slots(RegionId).ElementAt(skip + n));
        }
        public ScoringControlSnapshot Snapshot() => Queries.BuildControlSnapshot(State, "qy011.snapshot");
        private EffectStepResult ExecuteSession(EffectExecutionContext context)
        {
            if (context.Node.FlowStage.Length == 0)
            {
                string id = "qy011.main." + context.Node.EffectId;
                var main = new MainlineNodeRuntimeState { NodeId = id, NodeTypeId = RoundMainlineNodeTypeIds.RoundEnded, ExecutionEffectId = context.Node.EffectId, RoundNumber = 8, CompletionRequested = true };
                main.PublishedEventKeys.Add("RoundEnded"); context.State.EffectRuntime.MainNodes.Add(main); context.State.EffectRuntime.ActiveMainNodeId = id;
                return EffectStepResult.Continue(FinalScoringSessionService.Prepare).AddEvent(new EffectEventRequest { EventType = "RoundEnded", SourceEffectId = context.Node.EffectId, OwnerNodeId = context.Node.EffectId });
            }
            return Service.Advance(context, context.State.EffectRuntime.MainNodes.Single(m => m.ExecutionEffectId == context.Node.EffectId), () => new EffectEventRequest { EventType = "FinalScoringStarted" });
        }
        public EffectTreeExecutor Engine() => new EffectTreeExecutor(State, Registry);
        public void Start() { OwnerId = Engine().CreateRoot(EffectSpec.Create(RootType, playerId: 1)); }
        public RoundExecutionService StartOriginalLastRound()
        {
            var rounds=new RoundExecutionService(Registry);
            var chain=new RoundMainChainBuilder().Build(State.GameId,"",8,State.LaunchPlayerOrder,1);
            var main=chain.Nodes.First(n=>n.NodeTypeId==RoundMainlineNodeTypeIds.PlayerCleanupWindow);
            foreach(var prior in chain.Nodes.Where(n=>n.MainlineIndex<main.MainlineIndex)) prior.Status=EffectNodeStatus.Completed;
            var rt=State.EffectRuntime;rt.MainNodes.AddRange(chain.Nodes);rt.ActiveMainNodeId=main.NodeId;rt.FirstMainNodeId=chain.FirstNodeId;
            rt.CurrentRoundExecutionId=chain.RoundExecutionId;rt.CurrentRoundNumber=8;rt.CurrentRoundStartPlayerId=1;rt.PlayerOrderSnapshot=State.LaunchPlayerOrder.ToList();
            OwnerId=rounds.CreateCommandExecutor(State).CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId,
                YC.Domain.Effects.ItemTransferEffectExecutor.Object("mainNodeId",NormalizedValue.CreateString(main.NodeId)),main.PlayerId));
            main.ExecutionEffectId=OwnerId;main.Status=EffectNodeStatus.Running;State.Phase=GamePhase.Cleanup;State.CurrentPlayerId=1;
            for(int guard=0;guard<10;guard++) {
                Pump();var final=State.EffectRuntime.MainNodes.Single(n=>n.NodeTypeId==RoundMainlineNodeTypeIds.RoundEnded);
                if(final.ExecutionEffectId.Length>0) break;
                var cleanup=State.EffectRuntime.EffectNodes.SingleOrDefault(n=>n.SettlementWindow?.Context?.Kind==SettlementEntryService.PlayerCleanup && !n.SettlementWindow.IsClosed);
                if(cleanup!=null) {
                    var window=cleanup.SettlementWindow;int player=window.Context.PlayerId;
                    Assert.That(Engine().TryEndSettlementAction(player,window.Context.Id,"","",State.EffectRuntime.StateRevision,"qy007.actual-last-cleanup-"+player,out var reason),Is.True,reason);
                }
                var advance=rounds.Advance(State);Assert.That(advance.IsValid,Is.True,advance.Reason);
            }
            Assert.That(State.EffectRuntime.MainNodes.Where(n=>n.NodeTypeId==RoundMainlineNodeTypeIds.PlayerCleanupWindow).All(n=>n.Status==EffectNodeStatus.Completed),Is.True);
            OwnerId=State.EffectRuntime.MainNodes.Single(n=>n.NodeTypeId==RoundMainlineNodeTypeIds.RoundEnded).ExecutionEffectId;
            Assert.That(OwnerId,Is.Not.Empty);return rounds;
        }
        public EffectNodeRuntimeState Owner => State.EffectRuntime.EffectNodes.Single(n => n.EffectId == OwnerId);
        public void Until(Func<bool> condition)
        { var e = Engine(); for (int i = 0; i < 200 && !condition(); i++) Assert.That(e.Advance(), Is.True, e.LastDiagnostic); Assert.That(condition(), Is.True); Assert.That(State.EffectRuntime.Status, Is.Not.EqualTo(EffectRuntimeStatus.PausedFault), State.EffectRuntime.LastFaultMessage); }
        public void Pump() { var report = Engine().RunUntilQuiescent(); Assert.That(report.Faulted, Is.False, State.EffectRuntime.LastFaultMessage); }
        public void Restore(string label) { var result = Artifacts.Recover(State, label, Registry); Assert.That(result.Succeeded, Is.True, result.Diagnostic); State = result.State; }
        public void Finish() { Artifacts.WriteLabeledText("011当前隔离实际状态",JsonConvert.SerializeObject(State)); Artifacts.Finish(); }
    }
}
