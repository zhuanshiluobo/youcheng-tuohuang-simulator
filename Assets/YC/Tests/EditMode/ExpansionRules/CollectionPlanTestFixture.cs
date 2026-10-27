using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 隔离机制夹具：私有安装内容副本、真实实体、真实Lua与正式Effect回答，不计正常UI/对局。
    internal sealed class CollectionPlanTestFixture : IDisposable
    {
        public const string Token = "infected.component.gavial_token";
        public readonly IsolatedTestArtifacts Artifacts;
        public readonly ExternalContentPack Pack;
        public readonly IMapQueryService Map;
        public readonly EffectRegistry Registry;
        public GameState State;

        public const string FreeScript = @"return function(ctx)
            local seen, items = {}, {}
            for _, item in ipairs(Global.Content.FindInstances({typeId='entrepreneur.gang_marker',owner=ctx.playerId})) do
                if item.ownerSubject == ctx.playerId then
                    for _, inf in ipairs(Global.Map.FindInfluences({influenceId=item.id})) do
                        if inf.locationId ~= '' and not seen[inf.locationId] then
                            seen[inf.locationId] = true
                            table.insert(items,{kind='free_location',locationId=inf.locationId})
                        end
                    end
                end
            end
            return {queryId=ctx.payload.queryId,basisDigest=ctx.payload.basisDigest,items=items}
        end";
        public const string CityScript = @"return function(ctx)
            local gang, excluded = {}, {}
            for _, item in ipairs(Global.Content.FindInstances({typeId='entrepreneur.gang_marker',owner=ctx.playerId})) do
                if item.ownerSubject == ctx.playerId then
                    for _, inf in ipairs(Global.Map.FindInfluences({influenceId=item.id})) do
                        if inf.locationId ~= '' then gang[inf.locationId] = true end
                    end
                end
            end
            for _, row in ipairs(ctx.payload.context.destinations) do
                if gang[row.locationId] then
                    for _, inf in ipairs(Global.Map.FindInfluences({ownerPlayerId=ctx.playerId,locationId=row.locationId})) do
                        local items = Global.Content.FindInstances({id=inf.influenceId})
                        if #items == 1 and items[1].ownerSubject == ctx.playerId and items[1].typeId ~= 'entrepreneur.gang_marker' then
                            table.insert(excluded,row.candidateId)
                            break
                        end
                    end
                end
            end
            if #excluded==0 then return {} end
            return {Candidate.Remove({ids=excluded,reasonCode='qy009.fixture.mixed'})}
        end";

        public CollectionPlanTestFixture(string freeScript = FreeScript, string cityScript = CityScript, string mapId = null,
            Action<JObject,IDictionary<string,string>,JObject> configure = null, IEnumerable<ItemRecipeEntry> extraRecipe = null,
            Func<MapLocationDefinition,bool> resourceLocationFilter = null)
        {
            Artifacts = IsolatedTestArtifacts.BeginCase();
            string installed = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            var token = JObject.Parse(File.ReadAllText(Path.Combine(installed, "component", Token + ".json")));
            var declarations = new[] {
                new AbilitySourceDeclaration { DefinitionId=Token, AbilityId="qy009.fixture.free", Kind="ruleQuery", HandlerRef="qy009.fixture.free",
                    DeclarationVersion="1.0.0", AuthorizationPolicy="owner", EffectiveZones=new List<string>{"core.zone.box_reserve"},
                    QueryKinds=new List<string>{CollectionFreeLocationQuery.QueryKind}, PresentationRef=Token },
                new AbilitySourceDeclaration { DefinitionId=Token, AbilityId="qy009.fixture.city", Kind="ruleQuery", HandlerRef="qy009.fixture.city",
                    DeclarationVersion="1.0.0", AuthorizationPolicy="owner", EffectiveZones=new List<string>{"core.zone.box_reserve"},
                    QueryKinds=new List<string>{CityDestinationQueryService.QueryKind}, PresentationRef=Token } };
            token["abilitySources"] = JArray.FromObject(declarations);
            token["abilityHandlers"] = JArray.FromObject(new[] {
                new ExternalAbilityHandler {HandlerId="qy009.fixture.free",Script="lua/qy009_free.lua",Version="1.0.0"},
                new ExternalAbilityHandler {HandlerId="qy009.fixture.city",Script="lua/qy009_city.lua",Version="1.0.0"} });
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(installed, "pack.json")));
            manifest["enabledExpansionIds"] = new JArray("core","enterprise","infected");
            if (!((JArray)manifest["definitions"]).Values<string>().Contains("component/" + Token + ".json"))
                ((JArray)manifest["definitions"]).Add("component/" + Token + ".json");
            var replacements=new Dictionary<string,string> {
                ["component/"+Token+".json"]=token.ToString(), ["lua/qy009_free.lua"]=freeScript,
                ["lua/qy009_city.lua"]=cityScript, ["pack.json"]=manifest.ToString() };
            configure?.Invoke(token,replacements,manifest);replacements["component/"+Token+".json"]=token.ToString();replacements["pack.json"]=manifest.ToString();
            var input = Artifacts.CopyContent(installed, "采集城市纯查询输入", replacements);
            Pack=ExternalContentPack.Load(input); Artifacts.RecordContent("采集城市装载",input,Pack.ContentHash);
            Registry=new EffectRegistry {ContentPackHash=Pack.ContentHash};
            ResourceEffectExecutor.Register(Registry); GenericLuaEffectExecutor.Register(Registry);
            CharacterCardLuaCatalog.Register(Registry,Pack); CityStyleLuaCatalog.Register(Registry,Pack);
            ContentLifecycleLuaCatalog.Register(Registry,Pack); AbilitySourceLuaCatalog.Register(Registry,Pack);
            CityDestinationLuaCatalog.Register(Registry,Pack);
            Map=new MapQueryService(StaticMapDefinitions.Resolve(mapId ?? StaticMapDefinitions.FourPlayerMapId));
            var initial=new GameState {GameId="qy009.isolated",ContentPackHash=Pack.ContentHash,MapId=Map.Map.MapId,
                RuleConfiguration=new GameRuleConfiguration {MapId=Map.Map.MapId,PlayerCount=Map.Map.MinPlayers,ContentPackHash=Pack.ContentHash},
                ContentDefinitions=new GameContentDefinitions {
                    AbilitySources=Registry.AbilityDeclarations.ToList(), Facilities=Pack.CreateFacilities().ToList(),
                    Characters=Pack.CreateCharacters().ToList(), CityStyles=Pack.CreateCityStyles().ToList(),
                    SpecialActions=Pack.CreateSpecialActions().ToList(), Awards=ContentLifecycleLuaCatalog.CreateAwards(Pack),
                    Components=Pack.Definitions.Where(d=>d.ContentType=="component").Select(d=>new ComponentContentDefinition {
                        DefinitionId=d.DefinitionId,TypeId=d.ItemTypeId,QuantityPolicy=(string)d.Data["quantityPolicy"],CubeAppearance=d.CubeAppearance}).ToList() },
                Players=Enumerable.Range(1,Map.Map.MinPlayers).Select(id=>new PlayerState {PlayerId=id,Name="玩家"+id,
                    Resources=new ResourceSet {GoldVoucher=30,OriginiumShard=4}}).ToList() };
            var recipe=new List<ItemRecipeEntry> {
                new ItemRecipeEntry {EntryId="rules",DefinitionId=Token,TypeId="infected.gavial_token",Quantity=1,PerPlayer=true,InitialZoneRole="reserve"},
                new ItemRecipeEntry {EntryId="city",DefinitionId="core.component.city",TypeId="core.mobile_city",Quantity=1,PerPlayer=true,InitialZoneRole="city_reserve"},
                new ItemRecipeEntry {EntryId="normal",DefinitionId="core.player_marker",TypeId="core.player_marker",Quantity=12,PerPlayer=true,InitialZoneRole="supply"},
                new ItemRecipeEntry {EntryId="gang",DefinitionId="entrepreneur.component.gang_marker",TypeId="entrepreneur.gang_marker",Quantity=4,PerPlayer=true,InitialZoneRole="supply"},
                new ItemRecipeEntry {EntryId="company",DefinitionId="infected.component.company_marker",TypeId="infected.company_marker",Quantity=3,InitialZoneRole="company_markers"} };
            if(extraRecipe!=null) recipe.AddRange(extraRecipe);
            foreach(var location in Map.Map.Locations.Where(x=>!x.IsRedZone&&(resourceLocationFilter==null||resourceLocationFilter(x))))
                recipe.Add(new ItemRecipeEntry {EntryId="resource_"+location.LocationId,DefinitionId="core.resource_point",TypeId="core.resource_point_token",Quantity=1,InitialZoneRole="location/"+location.LocationId});
            State=new ItemZoneStateFactory().CreateForSetup(initial,Map.Map,recipe);
            foreach(var location in Map.Map.Locations.Where(x=>!x.IsRedZone)) State.Map.OpenLocationIds.Add(location.LocationId);
            SetCity(1,"A-03"); SetCity(2,"C-02");
            foreach(var player in State.Players.Where(p=>p.PlayerId>2))
                SetCity(player.PlayerId,Map.Map.Locations.First(l=>!l.IsRedZone&&State.Players.All(p=>p.CityLocationId!=l.LocationId)).LocationId);
            State.Round=4;State.ActionRound=1;State.Phase=GamePhase.ResourceCollection;State.CurrentPlayerId=1;State.StartPlayerId=1;
            State.EffectRuntime.PlayerOrderSnapshot=State.Players.Select(p=>p.PlayerId).ToList();
            foreach(var player in State.Players) player.ResourceCollectionStartGoldVoucher=player.Resources.GoldVoucher;
            InfluenceEffectExecutor.Register(Registry,new InfluenceService(Map));
            RouteResourceSelectionEffectExecutor.Register(Registry,Map);
            ResourceCollectionEffectExecutor.Register(Registry);
            var costs=new TravelCostService(Map);var influence=new InfluenceService(Map);
            CityMoveEffectExecutor.Register(Registry,Map,influence,new CityMovementService(Map,influence,costs),costs,new ResourceTokenService());
        }

        private void Position(ItemInstanceState item,string zone,string slot="")
        {
            typeof(ItemZoneStateMigrator).GetMethod("SetPosition",BindingFlags.NonPublic|BindingFlags.Static)
                .Invoke(null,new object[]{State,item,zone});
            item.SlotId=slot;
        }
        public void SetCity(int player,string location)
        {
            var parent=State.ItemZones.Zones.Single(z=>z.TemplateId=="core.zone.map_location"&&z.LocalZoneId==location);
            var dock=State.ItemZones.Zones.Single(z=>z.ParentId==parent.ZoneId&&z.TemplateId=="core.zone.city_dock");
            Position(State.ItemZones.Items.Single(i=>i.PhysicalKind==ItemPhysicalKind.MobileCity&&i.OwnerSubject.PlayerId==player),dock.ZoneId);
        }
        public string Put(int player,string location,bool gang)
        {
            var parent=State.ItemZones.Zones.Single(z=>z.TemplateId=="core.zone.map_location"&&z.LocalZoneId==location);
            var zone=State.ItemZones.Zones.Single(z=>z.ParentId==parent.ZoneId&&z.TemplateId=="core.zone.influence_slots");
            string slot=zone.LegalSlotIds.First(s=>!State.ItemZones.Items.Any(i=>i.ZoneId==zone.ZoneId&&i.SlotId==s));
            var item=State.ItemZones.Items.First(i=>i.OwnerSubject.PlayerId==player&&i.TypeId==(gang?"entrepreneur.gang_marker":"core.player_marker")&&
                State.ItemZones.Zones.Single(z=>z.ZoneId==i.ZoneId).TemplateId=="core.zone.player_supply");
            Position(item,zone.ZoneId,slot);return item.ItemId;
        }
        public string RemotePoint(int slots=1)
        {
            var all=Map.Map.Locations.Select(l=>l.LocationId).ToArray();
            var facts=new RouteTollService(Map).ReadRouteFacts(State,1);
            var graph=RouteSelectionGraphQuery.Query(all,facts.Select(f=>new RouteGraphRoute(f.RouteId,
                f.CoveredLocationIds,f.FreeReason!=RouteFreeReason.None,f.TollGoldVoucherAmount)).ToArray(),
                State.FindPlayer(1).CityLocationId,Array.Empty<string>(),all,Array.Empty<string>());
            Assert.That(graph.IsValid,Is.True,graph.Diagnostic);
            return Map.Map.Locations.First(l=>!l.IsRedZone&&l.InfluenceSlotCount>=slots&&
                State.Players.All(p=>p.CityLocationId!=l.LocationId)&&!graph.ReachableLocationIds.Contains(l.LocationId)).LocationId;
        }
        public string AdjacentPoint(int slots=1) => Map.GetAdjacentLocations(State.FindPlayer(1).CityLocationId)
            .First(l=>!l.IsRedZone&&l.InfluenceSlotCount>=slots&&State.Players.All(p=>p.CityLocationId!=l.LocationId)).LocationId;
        public void PutCompany(string location)
        {
            var parent=State.ItemZones.Zones.Single(z=>z.TemplateId=="core.zone.map_location"&&z.LocalZoneId==location);
            var zone=State.ItemZones.Zones.Single(z=>z.ParentId==parent.ZoneId&&z.TemplateId=="core.zone.influence_slots");
            string slot=zone.LegalSlotIds.First(s=>!State.ItemZones.Items.Any(i=>i.ZoneId==zone.ZoneId&&i.SlotId==s));
            var item=State.ItemZones.Items.First(i=>i.TypeId=="infected.company_marker"&&
                State.ItemZones.Zones.Single(z=>z.ZoneId==i.ZoneId).TemplateId!="core.zone.influence_slots");
            Position(item,zone.ZoneId,slot);
        }
        public void RetireSource(int player)
        {
            var item=State.ItemZones.Items.Single(i=>i.DefinitionId==Token&&i.OwnerSubject.PlayerId==player);
            Position(item,State.ItemZones.Zones.First(z=>z.TemplateId=="core.zone.box_retired").ZoneId);
        }
        public string Snapshot() => JsonConvert.SerializeObject(State);
        public CollectionFreeLocationResult Free(int player=1,string purpose="normal")
        {
            var service=new ResourceCollectionService(Map){RuleRegistry=Registry};
            return new CollectionFreeLocationQuery(Registry,Map).Query(State,new CollectionQueryContext(player,purpose,
                "fixture.selection","fixture.session",State.FindPlayer(player).CityLocationId,State.ContentPackHash,
                service.GetSelectableLocationIds(State,player,purpose=="extra")));
        }
        public EffectTreeExecutor Harvest(bool extra,out string owner)
        {
            var engine=new EffectTreeExecutor(State,Registry);
            if(extra) owner=engine.CreateRoot(EffectSpec.Create(ResourceCollectionEffectExecutor.TypeId,
                normalizedArguments:YC.Domain.Effects.ItemTransferEffectExecutor.Object("apiVersion",NormalizedValue.CreateInteger(1),
                    "player",NormalizedValue.CreateString("1"),"scope",NormalizedValue.CreateString("extra")),playerId:1));
            else owner=engine.CreateRoot(RouteResourceSelectionEffectExecutor.CreateHarvest(1,false,"qy009.fixture"));
            var run=engine.RunUntilQuiescent();Assert.That(run.Faulted,Is.False,State.EffectRuntime.LastFaultMessage);return engine;
        }
        public InteractionRequest Selection()=>State.EffectRuntime.InteractionRequests.Last(r=>r.Status=="open"&&r.InteractionTypeId=="route_resource.selection");
        public void Submit(EffectTreeExecutor engine,params string[] locations) => SubmitPlan(engine,locations,Array.Empty<string>());
        public void SubmitPlan(EffectTreeExecutor engine,IReadOnlyList<string> locations,IReadOnlyList<string> routes,IReadOnlyList<RouteRecipientChoice> recipients=null)
        {
            var request=Selection();string scope=request.PromptParameters.Properties.Single(p=>p.Name=="scopeKey").Value.StringValue;
            var ids=locations.Select(id=>RouteSelectionAnswerCodec.Encode(scope,RouteSelectionCandidateKind.Location,id)).ToList();
            ids.AddRange(routes.Select(id=>RouteSelectionAnswerCodec.Encode(scope,RouteSelectionCandidateKind.RouteAndRecipient,id,recipients?.Single(r=>r.RouteId==id).ReceiverSubjectKey??"bank")));
            ids.Add(RouteSelectionAnswerCodec.Encode(scope,RouteSelectionCandidateKind.Commit));
            Assert.That(engine.TrySubmitInteraction(request.InteractionId,1,State.EffectRuntime.StateRevision,
                NormalizedValue.CreateArray(ids.Select(id=>NormalizedValue.CreateStableReference("candidate",id)).ToList()),out var diagnostic),Is.True,diagnostic);
            var run=engine.RunUntilQuiescent();Assert.That(run.Faulted,Is.False,State.EffectRuntime.LastFaultMessage);
        }
        public void Restore(string label)
        {
            var restored=Artifacts.Recover(State,label,Registry);Assert.That(restored.Succeeded,Is.True,restored.Diagnostic);State=restored.State;
        }
        public void Dispose(){if(State!=null)Artifacts.WriteLabeledText("009实际最终现场",Snapshot());Artifacts.Finish();}
    }
}
