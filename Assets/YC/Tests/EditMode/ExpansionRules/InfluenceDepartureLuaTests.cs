using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Application.Sessions;
using YC.Application.Interactions;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>私有内容中的实际Lua组合，绝不替代012—015正式正文或正常对局证据。</summary>
    public sealed partial class InfluenceDepartureLuaTests
    {
        private IsolatedTestArtifacts artifacts;
        private GameState state;
        private EffectRegistry registry;
        private ExternalContentPack pack;
        private GameMapDefinition map;
        private RoundExecutionService rounds;
        private string targetCard, commandCard, dorothyCard;
        private int sequence;
        private const string StoreScript = @"return function(ctx)
            if tonumber(string.sub(ctx.playerId,2)) ~= ctx.payload.ownerPlayerId then return {} end
            local item = Global.Content.GetInstance(ctx.payload.influenceId)
            if item == nil then return {} end
            return {Effect.Choice({player=ctx.playerId,interactionType='effect.choice.execute',branches={
              {id='store',effects={Effect.StoreRemovedInfluence({apiVersion=2,player=ctx.playerId,cardInstanceId=ctx.payload.sourceItemId,localZoneId='markers',influenceItemId=ctx.payload.influenceId})}},
              {id='decline',effects={}}}})} end";
        private const string BatchScript = @"return function(ctx)
            local function gain(k,card)
              local supply=Global.Content.FindZones({templateId='core.zone.player_supply',owner=ctx.playerId})[1]
              local target=Global.Content.FindZones({parentId=card,localZoneId='markers'})[1]
              local stock={};for _,item in ipairs(Global.Content.FindInstances({zoneId=supply.id,typeId='core.player_marker'})) do stock[#stock+1]=item end;table.sort(stock,function(a,b)return a.id<b.id end)
              local moves={};for i=1,k do local ids={};if stock[i] then ids[1]=stock[i].id end
                moves[#moves+1]=Effect.MoveItem({apiVersion=3,player=ctx.playerId,itemIds=ids,expectedSource={zoneId=supply.id},destination={zoneId=target.id},insertion={kind='unordered'},operationProfileId='core.player_marker_storage',requiredCount=1}) end
              return Effect.Condition({decisionPlayer=ctx.playerId,leftEffects={Effect.PayResource({player=ctx.playerId,cost={GoldVoucher=2*k}})},rightEffects=moves}) end
            if ctx.eventType=='EffectCompleted' then
                if ctx.payload.effectTypeId~='effect.flow.choice' or ctx.payload.outcome~='completed' then return {} end
                return {gain(tonumber(ctx.payload.normalizedResult),ctx.payload.continuationContext.card)} end
            if tonumber(string.sub(ctx.playerId,2)) ~= ctx.payload.ownerPlayerId then return {} end
            local n=ctx.payload.departureCount or 1
            if n==1 then return {gain(1,ctx.payload.sourceItemId)} end
            local options={}; for k=1,n do table.insert(options,tostring(k)) end
            return {Effect.WithContext(Effect.Choice({player=ctx.playerId,interactionType='effect.choice.execute',options=options}),{card=ctx.payload.sourceItemId})} end";
        private const string HookScript = @"return function(ctx)
            local a=ctx.payload.facts.arguments
            if a.ownerSubject.subjectType~='player' or a.ownerSubject.playerId~=tonumber(string.sub(ctx.playerId,2)) or a.itemTypeId~=nil then return {operation='keep'} end
            local function place(t) return Effect.PlaceInfluence({executingPlayer=a.executingPlayer,influenceSource=a.influenceSource,ownerSubject=a.ownerSubject,
                targetSlotId=a.targetSlotId,targetLocationId=a.targetLocationId,candidateScope=a.candidateScope,causeKind=a.causeKind,replaceEffectId=a.replaceEffectId,itemTypeId=t}) end
            return {operation='replace',resultContract='place_result',permissionContract='inherit_original',effect=Effect.Choice({player=ctx.playerId,interactionType='effect.choice.execute',branches={
                {id='ordinary',effects={place('core.player_marker')}},{id='gang',effects={place('entrepreneur.gang_marker')}}}})} end";
        private const string ReplenishScript = @"return function(ctx)
            if tonumber(string.sub(ctx.playerId,2))~=ctx.payload.ownerPlayerId or ctx.payload.itemTypeId~='entrepreneur.gang_marker' then return {} end
            local city=PlayerData.GetCityLocation(ctx.playerId); if city==nil or city=='' then return {} end
            return {Effect.PlaceInfluence({executingPlayer=ctx.playerId,ownerSubject=ctx.playerId,influenceSource='qy006.replenish',itemTypeId='core.player_marker',targetLocationId=city,causeKind='other_intrinsic_flow'})} end";
        [SetUp] public void Begin() { artifacts = IsolatedTestArtifacts.BeginCase(); state = null; sequence = 0; }
        [TearDown] public void End() { if (state != null) artifacts.WriteLabeledText("最终真实状态", JsonConvert.SerializeObject(state)); artifacts.Finish(); }
        private void Setup(bool stores = false, bool batch = false, bool hook = false, int actor = 1, bool concurrent = false, string overrideHook = "", bool secondHook = false, bool replenish = false, int gangStock = 2, string quickScript = "", string secondHookScript = "", string storeScript = "", int markerStock = 8, int initialGold = 20, string batchScript = "", int commandCapacity = -1)
        {
            string installed = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(installed, "pack.json"))); manifest["enabledExpansionIds"] = new JArray("core", "enterprise", "infected");
            var replacements = new Dictionary<string,string> { ["pack.json"] = manifest.ToString() };
            var template = JObject.Parse(File.ReadAllText(Path.Combine(installed, "character/operator.dorothy.json")));
            void Definition(string id, string kind, string code, string[] operations, string[] queries = null)
            {
                var j = (JObject)template.DeepClone(); j["definitionId"] = id; j["contentType"] = "award"; j["itemTypeId"] = "core.award_card"; j["displayName"] = "006隔离来源"; j["version"] = "1.0.0"; j["abilities"] = new JArray(); j["abilitySources"] = new JArray();
                if (id == "qy006.commands" && commandCapacity >= 0) { j["cardZones"][0]["capacityPolicy"] = "bounded"; j["cardZones"][0]["capacity"] = commandCapacity; }
                j["data"] = new JObject { ["lifecycle"] = "persistent", ["randomAwardPool"] = false, ["templateId"] = id, ["cardId"] = id, ["name"] = "006隔离来源", ["acquisitionRole"] = "operator_deck", ["instanceTypeId"] = "core.operator_card", ["strategyAbilityId"] = "", ["tacticAbilityId"] = "" };
                j["abilityHandlers"] = new JArray(new JObject { ["handlerId"] = id + ".handler", ["script"] = "lua/characters/" + id + ".lua", ["version"] = "1.0.0", ["completionHandlerId"] = id == "qy006.commands" || id == "qy006.quick" ? id + ".complete" : "" });
                j["abilitySources"] = JArray.FromObject(new[] { new AbilitySourceDeclaration { AbilityId = id + ".source", Kind = kind, HandlerRef = id + ".handler", DeclarationVersion = "1.0.0", AuthorizationPolicy = "controller",
                    EffectiveZones = new List<string> { "core.zone.player_rewards" }, ObservedEvents = kind == "event" ? new List<string> { "InfluenceRemoved", "InfluenceReplaced" } : new List<string>(), QueryKinds = queries?.ToList() ?? new List<string>(),
                    AllowedOperations = operations.ToList(), PresentationRef = id, Actions = kind == "quick" ? new List<AbilityActionDeclaration> { new AbilityActionDeclaration { ActionId=id+".action", EntryKind="quick", BodyHandlerRef=id+".handler", PresentationRef=id } } : new List<AbilityActionDeclaration>() } });
                ((JArray)manifest["definitions"]).Add("character/" + id + ".json");
                replacements["character/" + id + ".json"] = j.ToString(); replacements["lua/characters/" + id + ".lua"] = code;
            }
            if (stores) Definition("qy006.storage", "event", storeScript.Length>0?storeScript:StoreScript, new[] { "response.destination", "response.self", "carrier.store:qy006.storage:markers:core.cube" });
            if (batch) Definition("qy006.commands", "event", batchScript.Length > 0 ? batchScript : BatchScript, new[] { "response.optional_effect", "response.self", "response.batch", "carrier.load:qy006.commands:markers:core.player_marker" });
            if (hook) Definition("qy006.hook", "ruleQuery", overrideHook.Length > 0 ? overrideHook : HookScript, Array.Empty<string>(), new[] { "influence_place_attaching.v1" });
            if (secondHook) Definition("qy006.hook2", "ruleQuery", secondHookScript.Length>0 ? secondHookScript : HookScript.Replace("place('entrepreneur.gang_marker')", "place('core.player_marker')"), Array.Empty<string>(), new[] { "influence_place_attaching.v1" });
            if (replenish) Definition("qy006.replenish", "event", ReplenishScript, new[] { "response.required_effect" });
            if (quickScript.Length>0) Definition("qy006.quick", "quick", quickScript, Array.Empty<string>());
            replacements["pack.json"] = manifest.ToString();
            string input = artifacts.CopyContent(installed, "006实际Lua私有内容", replacements); pack = ExternalContentPack.Load(input); artifacts.RecordContent("006实际装载内容", input, pack.ContentHash);
            map = StaticMapDefinitions.CreateThreePlayerMap(); var query = new MapQueryService(map);
            registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry); CharacterCoverEffectExecutor.Register(registry);
            CharacterCardLuaCatalog.Register(registry, pack); CityStyleLuaCatalog.Register(registry, pack); ContentLifecycleLuaCatalog.Register(registry, pack); AbilitySourceLuaCatalog.Register(registry, pack);
            InfluenceEffectExecutor.Register(registry, new InfluenceService(query)); ContentPrimitiveEffectExecutor.Register(registry, query, registry.InfluenceService);
            rounds = new RoundExecutionService(registry);
            var config = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = pack.ContentHash, EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" }, PlayerStageSchedulingMode = concurrent ? PlayerStageSchedulingMode.Concurrent : PlayerStageSchedulingMode.Sequential };
            var source = new GameState { GameId = "qy006.lua.isolated", MapId = map.MapId, ContentPackHash = pack.ContentHash, LaunchPlayerOrder = new List<int> { 1, 2 }, RuleConfiguration = config,
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Name = "隔离甲", Resources = new ResourceSet { GoldVoucher = initialGold } }, new PlayerState { PlayerId = 2, Name = "隔离乙" } },
                ContentDefinitions = new GameContentDefinitions { Characters = pack.CreateCharacters().ToList(), Facilities = pack.CreateFacilities().ToList(), AbilitySources = registry.AbilityDeclarations.ToList(), UsageTracks = pack.ActiveDefinitions.SelectMany(d => d.UsageTracks).ToList(), CityStyles = pack.CreateCityStyles().ToList(), SpecialActions = pack.CreateSpecialActions().ToList(), Awards = ContentLifecycleLuaCatalog.CreateAwards(pack),
                    Components = pack.Definitions.Where(d => d.ContentType == "component").Select(d => new ComponentContentDefinition { DefinitionId = d.DefinitionId, TypeId = d.ItemTypeId, QuantityPolicy = (string)d.Data["quantityPolicy"], CubeAppearance = d.CubeAppearance }).ToList() } };
            var recipe = new List<ItemRecipeEntry> { new ItemRecipeEntry { EntryId = "cities", DefinitionId = "core.component.city", TypeId = "core.mobile_city", Quantity = 1, PerPlayer = true, InitialZoneRole = "city_reserve" }, new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = markerStock, PerPlayer = true, InitialZoneRole = "supply" },
                new ItemRecipeEntry { EntryId = "gang", DefinitionId = "entrepreneur.component.gang_marker", TypeId = "entrepreneur.gang_marker", Quantity = gangStock, PerPlayer = true, InitialZoneRole = "supply" },
                new ItemRecipeEntry { EntryId = "dorothy", DefinitionId = "operator.dorothy", TypeId = "core.operator_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" } };
            foreach (var id in new[] { "qy006.storage", "qy006.commands", "qy006.hook", "qy006.hook2", "qy006.replenish", "qy006.quick" }.Where(id => pack.Definitions.Any(d => d.DefinitionId == id))) recipe.Add(new ItemRecipeEntry { EntryId = id, DefinitionId = id, TypeId = "core.award_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "rewards" });
            foreach (var location in map.Locations.Where(l => !l.IsRedZone)) recipe.Add(new ItemRecipeEntry { EntryId = "resource_" + location.LocationId, DefinitionId = "core.resource_point", TypeId = "core.resource_point_token", Quantity = 1, InitialZoneRole = "location/" + location.LocationId });
            state = new ItemZoneStateFactory().CreateForSetup(source, map, recipe, pack.Definitions.Where(d => d.CardZones.Count > 0).Select(d => new ItemCardZoneDeclaration { DefinitionId = d.DefinitionId, CardZones = d.CardZones }).ToList());
            state.Round = 1; state.ActionRound = 1; state.Phase = GamePhase.ActionRound1; state.CurrentPlayerId = actor; state.StartPlayerId = actor; state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            foreach (var location in map.Locations.Where(l => !l.IsRedZone)) state.Map.OpenLocationIds.Add(location.LocationId);
            targetCard = state.ItemZones.Items.SingleOrDefault(i => i.DefinitionId == "qy006.storage" && i.OwnerSubject.PlayerId == 1)?.ItemId;
            commandCard = state.ItemZones.Items.SingleOrDefault(i => i.DefinitionId == "qy006.commands" && i.OwnerSubject.PlayerId == 1)?.ItemId;
            dorothyCard = state.ItemZones.Items.Single(i => i.DefinitionId == "operator.dorothy" && i.OwnerSubject.PlayerId == 1).ItemId;
            if (replenish)
            {
                string cityLocation=map.Locations.Where(l=>!l.IsRedZone && l.CanDockCity && l.InfluenceSlotCount>0).Skip(1).First().LocationId;
                var city=state.ItemZones.Items.Single(i=>i.TypeId=="core.mobile_city" && i.OwnerSubject.PlayerId==1);
                var location=state.ItemZones.Zones.Single(z=>z.TemplateId=="core.zone.map_location" && z.LocalZoneId==cityLocation);
                string dock=state.ItemZones.Zones.Single(z=>z.TemplateId=="core.zone.city_dock" && z.ParentId==location.ZoneId).ZoneId;
                foreach (var order in state.ItemZones.Orders) order.ItemIds.Remove(city.ItemId);
                city.ZoneId=dock; city.SlotId=""; state.ItemZones.Orders.SingleOrDefault(o=>o.ZoneId==dock)?.ItemIds.Add(city.ItemId);
                Assert.That(new ItemZoneQuery().TryValidate(state,out string diagnostic),Is.True,diagnostic);
                artifacts.WriteLabeledText("隔离城市初始化说明","人工初始化真实城市实例位置，仅供Lua机制定位，不计正常菜单、合法行动或页面验收。");
            }
            Assert.That(registry.QuerySources(state,1).Count(s=>s.AbilityId.StartsWith("qy006.",StringComparison.Ordinal)), Is.EqualTo((stores?1:0)+(batch?1:0)+(hook?1:0)+(secondHook?1:0)+(replenish?1:0)+(quickScript.Length>0?1:0)));
            if (concurrent) Assert.That(rounds.PrepareActionWindowForSmoke(state, actor).IsValid, Is.True);
        }
        private EffectTreeExecutor Engine() => rounds.CreateCommandExecutor(state);
        private void Pump() { var r = Engine().RunUntilQuiescent(); if (r.Faulted) artifacts.WriteLabeledText("首次真实故障状态", JsonConvert.SerializeObject(state)); Assert.That(r.Faulted, Is.False, state.EffectRuntime.LastFaultMessage); RoundExecutionService.RefreshConcurrentInteractionRevisions(state); }
        private string Run(EffectSpec spec) { string id = Engine().CreateRoot(spec); Pump(); return id; }
        private string Slot(int n = 0) => InfluenceService.GetLocationSlotId(map.Locations.First(l => !l.IsRedZone && l.InfluenceSlotCount > n).LocationId,n);
        private string Place(int n = 0, string type = "core.player_marker") { string id = Run(InfluenceEffectSpecFactory.PlaceInfluence(1, Slot(n), itemTypeId:type)); return Value(state.EffectRuntime.EffectNodes.Single(v => v.EffectId == id).NormalizedResult,"influenceId").ReferenceId; }
        private void Answer(string value, string type = "") { var r=state.EffectRuntime.InteractionRequests.Last(q=>q.Status=="open" && (type.Length==0 || q.InteractionTypeId==type)); Assert.That(Engine().TrySubmitInteraction(r.GetStableInteractionId(),r.AnsweringPlayerId,r.StateRevision,NormalizedValue.CreateString(value),out string error),Is.True,error); Pump(); }
        private void Restore(string label) { artifacts.WriteLabeledText(label+"原始状态",JsonConvert.SerializeObject(state)); var r=artifacts.Recover(state,label,registry); Assert.That(r.Succeeded,Is.True,r.Diagnostic); state=r.State; }
        private string Depart(string item, int actor=1, bool replace=false, bool concurrent=false)
        {
            var spec=replace?InfluenceEffectSpecFactory.ReplaceInfluence(actor,item):InfluenceEffectSpecFactory.RemoveInfluence(actor,item);
            if (!concurrent) return Run(spec);
            var command=new GameCommand { CommandId="qy006.cause."+(++sequence),PlayerId=actor,Kind=GameCommandKind.EndAction };
            Assert.That(OperationWindowService.TryResolveCommandContext(state,command,out var cause,out var validation),Is.True,validation?.Reason); state.EffectRuntime.CurrentOperationContext=cause;
            try { Assert.That(Engine().TryCreatePlayerActionEffect(actor,spec,"1","qy006.actual-cause",out string id),Is.True); Pump(); return id; }
            finally { state.EffectRuntime.CurrentOperationContext=null; }
        }
        private int Stored(string card) => state.ItemZones.Items.Count(i=>state.ItemZones.Zones.Any(z=>z.ZoneId==i.ZoneId && z.ParentId==card && z.LocalZoneId=="markers"));
        private void ActivateDorothy() { Run(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId,playerId:1)); Answer(dorothyCard); Run(CharacterLifecycleService.CreateUseSpec(1,dorothyCard,"tactic")); Answer("1"); }
        private void AnswerResponse(string kind,string answer)
        {
            var response=state.EffectRuntime.DeferredResponses.Last(r=>r.ProtocolVersion==1 && r.Kind==kind && r.Status==DeferredResponseStatus.Pending);
            var request=state.EffectRuntime.InteractionRequests.Last(q=>q.Status=="open" && InfluenceDepartureProtocol.IsWithin(state,q.OwnerEffectId,response.ResponseRootEffectId));
            Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(),request.AnsweringPlayerId,request.StateRevision,NormalizedValue.CreateString(answer),out string reason),Is.True,reason); Pump();
        }

        [TestCase(0)][TestCase(1)] public void ActualPioneerLuaPlacesAtTheRealCityWithoutMapPlacementClick(int declared)
        {
            Setup(replenish:true); string location=state.FindPlayer(1).CityLocationId;
            artifacts.WriteLabeledText("据点隔离入口说明","仅注入样式激活测试事件并调用实际安装Lua；城市为人工初始化的真实实例，不计宣言支付、正常开局或页面验收。");
            registry.Register("qy006.pioneer.signal",c=>EffectStepResult.Completed().AddEvent(new EffectEventRequest { EventType="CityStyleSpecialActionActivated",PlayerId=1,
                RouteKey="special_action.city_style.pioneer_outpost",Payload=Object("declarationMarkerCount",NormalizedValue.CreateInteger(declared)) }));
            Run(EffectSpec.Create("qy006.pioneer.signal",playerId:1)); int expected=Math.Min(2,map.Locations.Single(l=>l.LocationId==location).InfluenceSlotCount);
            Assert.That(expected,Is.GreaterThan(0)); Assert.That(state.Map.Influences.Count,Is.EqualTo(expected)); Assert.That(state.Map.Influences.All(i=>i.LocationId==location),Is.True);
            var open=state.EffectRuntime.InteractionRequests.Where(q=>q.Status=="open").ToList();
            Assert.That(open.Any(q=>state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==q.OwnerEffectId).EffectTypeId==InfluenceEffectTypeIds.PlaceInfluence),Is.False);
            if(declared==0) Assert.That(open,Is.Empty);
            else { Assert.That(open.Count,Is.EqualTo(1)); Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==open[0].OwnerEffectId).EffectTypeId,Is.EqualTo(ContentPrimitiveEffectExecutor.SelectInfluence)); }
            Restore("真实据点Lua定点放置后");
        }
        [TestCase(false)][TestCase(true)] public void ActualLuaStrictBatchControlsTheConditionWithRealCounts(bool invalid)
        {
            Setup(); string one=Place(0),two=Place(1);
            const string code=@"return function(ctx) return {Effect.Condition({leftEffects={Effect.RemoveInfluence({apiVersion=2,executingPlayer=ctx.playerId,targets=ctx.payload.targets,requiredCount=2,reasonId='qy006.strict.lua'})},rightEffects={Effect.GainScore({recipient=ctx.playerId,amount=1})}})} end";
            var definition=new LuaScriptDefinition("qy006.strict.lua","qy006.strict.lua","qy006.strict.lua","1.0.0",code,LuaContentHasher.ComputeSha256(code));
            new LuaEffectEventDispatcher(registry).Register(new LuaHandlerDefinition("qy006.strict.lua","QY006StrictActivated","",definition,EffectHandlerRole.Primary));
            registry.Register("qy006.strict.signal",c=>EffectStepResult.Completed().AddEvent(new EffectEventRequest{EventType="QY006StrictActivated",PlayerId=1,Payload=Object("targets",Strings(new[]{one,invalid?"missing":two}))}));
            Run(EffectSpec.Create("qy006.strict.signal",playerId:1)); Restore("严格Lua条件执行待答"); Answer("continue");
            var strict=state.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==InfluenceEffectTypeIds.RemoveInfluence);
            Assert.That(strict.Status,Is.EqualTo(invalid?EffectNodeStatus.Failed:EffectNodeStatus.Completed)); Assert.That(Number(strict.NormalizedResult,"actualRemovedCount"),Is.EqualTo(invalid?0:2));
            Assert.That(state.EffectRuntime.InteractionRequests.Where(q=>q.Status=="open"),Is.Empty);
            Assert.That(state.FindPlayer(1).Score,Is.EqualTo(invalid?0:1)); Assert.That(state.Map.Influences.Count,Is.EqualTo(invalid?2:0));
            var removed=state.EffectRuntime.RuleEvents.Where(e=>e.EventType=="InfluenceRemoved").ToList(); Assert.That(removed.Count,Is.EqualTo(invalid?0:2));
            Assert.That(removed.All(e=>state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==e.SourceEffectId).EffectTypeId==InfluenceEffectTypeIds.RemoveInfluence),Is.True);
            Restore("真实Lua严格左式"+(invalid?"失败零变更":"足额才执行右式"));
        }

        [TestCase(0)] [TestCase(1)] public void ShortRealTargetArraysReachTheActualConditionLeftAndFailAtomically(int count)
        {
            Setup(); var targets = Enumerable.Range(0, count).Select(i => Place(i)).ToList();
            const string code = @"return function(ctx) return {Effect.Condition({leftEffects={Effect.RemoveInfluence({apiVersion=2,executingPlayer=ctx.playerId,targets=ctx.payload.targets,requiredCount=2,reasonId='qy007.strict.short'})},rightEffects={Effect.GainScore({recipient=ctx.playerId,amount=1})}})} end";
            var definition = new LuaScriptDefinition("qy007.strict.short", "qy007.strict.short", "qy007.strict.short", "1.0.0", code, LuaContentHasher.ComputeSha256(code));
            new LuaEffectEventDispatcher(registry).Register(new LuaHandlerDefinition("qy007.strict.short", "QY007ShortTargets", "", definition, EffectHandlerRole.Primary));
            registry.Register("qy007.strict.signal", c => EffectStepResult.Completed().AddEvent(new EffectEventRequest { EventType = "QY007ShortTargets", PlayerId = 1, Payload = Object("targets", Strings(targets)) }));
            Run(EffectSpec.Create("qy007.strict.signal", playerId: 1)); Answer("continue");
            var strict = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == InfluenceEffectTypeIds.RemoveInfluence);
            Assert.That(strict.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(Number(strict.NormalizedResult, "actualRemovedCount"), Is.Zero);
            Assert.That(state.FindPlayer(1).Score, Is.Zero); Assert.That(state.Map.Influences.Count, Is.EqualTo(count));
            Assert.That(state.EffectRuntime.InfluenceDepartures, Is.Empty); Restore("真实短目标左式失败" + count);
        }

        [Test] public void FullBatchAboveNinetyNineKeepsEveryDepartureAndLuaSelectsOnlyActuallyObtainableK()
            => RunFullBatchWithLegalK(100);
        [Test] public void LargeBatchAuthorQueryUsesOnlyCurrentFiltersAndPayloadFields()
            => RunFullBatchWithLegalK(3);
        private void RunFullBatchWithLegalK(int fullCount)
        {
            int capacity=fullCount>99?40:11,gold=fullCount>99?100:14;
            int k=Math.Min(fullCount,Math.Min(29,Math.Min(capacity,gold/2)));
            string slot = InfluenceService.GetLocationSlotId(StaticMapDefinitions.CreateThreePlayerMap().Locations.First(l => !l.IsRedZone && l.InfluenceSlotCount > 0).LocationId, 0);
            string code = @"return function(ctx) if ctx.eventType=='EffectCompleted' then return {} end
              return {Effect.DepartureBatch({effects={Effect.Repeat({apiVersion=1,count="+fullCount+@",effects={
                Effect.PlaceInfluence({executingPlayer=ctx.playerId,ownerSubject=ctx.playerId,influenceSource='qy007.full-n',targetSlotId='" + slot + @"'}),
                Effect.SelectInfluence({player=ctx.playerId,operation='remove',ownerFilter='self',count=1,requiredCount=1,requireCompleteSelection=true,allowDecline=false})}})}})} end";
            string bounded = BatchScript.Replace("local options={}; for k=1,n", @"local supplies=Global.Content.FindZones({owner=ctx.playerId,localZoneId='supply'})
local stock=0; for _,item in ipairs(Global.Content.FindInstances({zoneId=supplies[1].id,typeId='core.player_marker',owner=ctx.playerId})) do if item.ownerSubject==ctx.playerId then stock=stock+1 end end
local target=Global.Content.FindZones({parentId=ctx.payload.sourceItemId,localZoneId='markers'})[1]
local capacity=target.capacity<0 and stock or target.capacity-target.itemCount
if capacity<0 then capacity=0 end
local limit=n; if stock<limit then limit=stock end; if capacity<limit then limit=capacity end
local affordable=0;local funds=PlayerData.GetResources(ctx.playerId).goldVoucher
for k=1,limit do if 2*k<=funds then affordable=k else break end end;limit=affordable
if limit==0 then return {} end
local options={}; for k=1,limit")
                .Replace("{card=ctx.payload.sourceItemId}", "{card=ctx.payload.sourceItemId,fullDepartureCount=n,availableStock=stock,remainingCapacity=capacity,legalMaximum=limit}");
            Setup(batch:true,quickScript:code,markerStock:29,initialGold:gold,batchScript:bounded,commandCapacity:capacity);
            var option=registry.QueryActions(state,1).Single(o=>o.ActionId=="qy006.quick.action");
            Assert.That(Engine().TryInvokeAbilityAction(1,option.OptionId,option.RequestRef.InteractionId,option.RequestRef.FactsDigest,option.VersionRef.ContentPackHash,"qy007.full-100",Object(),out _,out string reason),Is.True,reason); Pump();
            for(int i=0;i<fullCount;i++) {
                var r=state.EffectRuntime.InteractionRequests.Single(q=>q.Status=="open" && state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==q.OwnerEffectId).EffectTypeId==ContentPrimitiveEffectExecutor.SelectInfluence);
                Assert.That(r.CandidateIds.Count,Is.EqualTo(1));
                Assert.That(Engine().TrySubmitInteraction(r.GetStableInteractionId(),1,r.StateRevision,Strings(r.CandidateIds),out reason),Is.True,reason); Pump();
            }
            var batch=state.EffectRuntime.InfluenceDepartureBatches.Single();
            Assert.That(batch.Sealed,Is.True); Assert.That(batch.DepartureIds.Count,Is.EqualTo(fullCount));
            Assert.That(state.EffectRuntime.InfluenceDepartures.Select(d=>d.ItemId).Distinct().Count(),Is.EqualTo(1));
            var choice=state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");
            Assert.That(choice.CandidateIds,Is.EqualTo(Enumerable.Range(1,k).Select(i=>i.ToString()).ToArray()));
            var choiceNode=Engine().GetNode(choice.OwnerEffectId);
            Assert.That(Number(choiceNode.ContinuationContext,"fullDepartureCount"),Is.EqualTo(fullCount));
            Assert.That(Number(choiceNode.ContinuationContext,"availableStock"),Is.EqualTo(29));
            Assert.That(Number(choiceNode.ContinuationContext,"remainingCapacity"),Is.EqualTo(capacity));
            Restore("完整n"+fullCount+"与真实合法k"+k+"待选恢复"); AnswerResponse("optional_effect",k.ToString());
            Restore("k"+k+"整组交付未付恢复"); AnswerResponse("optional_effect","continue");
            Assert.That(Stored(commandCard),Is.EqualTo(k)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold-2*k));
            Assert.That(state.EffectRuntime.InfluenceDepartureBatches.Single().DepartureIds.Count,Is.EqualTo(fullCount));
            string destination=state.ItemZones.Zones.Single(z=>z.ParentId==commandCard && z.LocalZoneId=="markers").ZoneId;
            var deliveries=state.EffectRuntime.EffectNodes.Where(n=>n.EffectTypeId==ItemTransferEffectExecutor.TypeId && Text(Value(n.NormalizedArguments,"destination"),"zoneId")==destination).ToList();
            Assert.That(deliveries.Count,Is.EqualTo(k)); Assert.That(deliveries.All(n=>Number(n.NormalizedArguments,"requiredCount")==1 && Number(n.NormalizedResult,"movedCount")==1),Is.True);
            Restore("n不截断而k"+k+"已付已交付恢复"); Pump(); Assert.That(Stored(commandCard),Is.EqualTo(k));
        }

        [Test] public void PureLuaCanReplaceTheRootWithOneExplicitTypedPlace()
        {
            string script=HookScript.Substring(0,HookScript.IndexOf("return {operation='replace'",StringComparison.Ordinal))+"return {operation='replace',resultContract='place_result',permissionContract='inherit_original',effect=place('entrepreneur.gang_marker')} end";
            Setup(hook:true,overrideHook:script); string id=Run(InfluenceEffectSpecFactory.PlaceInfluence(1,Slot()));
            var m=state.EffectRuntime.InfluencePlaceMappings.Single(); Assert.That(m.SelectedLeafEffectId,Is.EqualTo(id)); Assert.That(state.EffectRuntime.InteractionRequests.Where(q=>q.Status=="open"),Is.Empty);
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==state.Map.Influences.Single().InfluenceId).TypeId,Is.EqualTo("entrepreneur.gang_marker")); Restore("明确类型根不创建地图选择");
        }

        [TestCase("originalTarget")][TestCase("actualLeaf")][TestCase("sourceDuplicate")][TestCase("sourceVersion")][TestCase("sourceIdentity")]
        public void CorruptedLogicalMappingCannotChangePlacePermissions(string field)
        {
            Setup(hook:true); Run(InfluenceEffectSpecFactory.PlaceInfluence(1,Slot()));
            var raw=JObject.Parse(YC.Application.Sessions.HostRecoveryService.SerializeArchive(YC.Application.Sessions.HostRecoveryService.CreateArchive(state)));
            var runtime=raw["Snapshot"]["State"]["EffectRuntime"]; var mapping=runtime["InfluencePlaceMappings"][0];
            if(field=="originalTarget") { var p=(JArray)mapping["OriginalSpec"]["NormalizedArguments"]["Properties"]; p.Single(v=>(string)v["Name"]=="targetSlotId")["Value"]["ReferenceId"]="location:missing:0"; }
            if(field=="actualLeaf") { var leaf=runtime["EffectNodes"].First(n=>(string)n["EffectTypeId"]==LuaDomainEffectTypeIds.Choice)["NestedEffects"][0]; var p=(JArray)leaf["NormalizedArguments"]["Properties"]; p.Single(v=>(string)v["Name"]=="executingPlayer")["Value"]["ReferenceId"]="2"; }
            if(field=="sourceDuplicate") { ((JArray)mapping["Sources"]).Add(mapping["Sources"][0].DeepClone()); ((JArray)mapping["HandlerHashes"]).Add(mapping["HandlerHashes"][0].DeepClone()); }
            if(field=="sourceVersion") mapping["Sources"][0]["DeclarationVersion"]="forged-version";
            if(field=="sourceIdentity") mapping["Sources"][0]["SourceKey"]="forged-identity";
            artifacts.WriteLabeledText("损坏挂接映射"+field,raw.ToString()); var result=YC.Application.Sessions.HostRecoveryService.RecoverJson(raw.ToString(),registry); Assert.That(result.Succeeded,Is.False,result.Diagnostic);
        }
        [Test] public void ClaimedGangLeavesNoSupplyForReplacementAndDoesNotRepeatDeparture()
        {
            Setup(stores:true,hook:true,gangStock:1); ActivateDorothy(); string item=Place(type:"entrepreneur.gang_marker");
            string id=Depart(item,replace:true); AnswerResponse("destination","store"); Restore("帮派已收容替入待答"); Answer("gang");
            Assert.That(Text(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==id).NormalizedResult,"outcome"),Is.EqualTo("removed_only"));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e=>e.EventType=="InfluenceRemoved"),Is.EqualTo(1)); Assert.That(state.EffectRuntime.RuleEvents.Count(e=>e.EventType=="InfluenceReplaced"),Is.Zero); Assert.That(Stored(dorothyCard),Is.EqualTo(2)); Restore("无库存替入失败仅一离场");
        }
        [Test] public void ExistingCarrierDependencyMatchesActualZoneTypeAndOwner()
        {
            Setup(batch:true); string item=Place(); Depart(item); var zone=state.ItemZones.Zones.Single(z=>z.ParentId==commandCard && z.LocalZoneId=="markers");
            var query=new DeferredDependencyQuery{DependencyKind=DeferredDependencyKind.CardMarkerConsumption,SourceCardInstanceId=commandCard,SourceZoneId=zone.ZoneId,RequiredCount=1,RequiredTypeId="entrepreneur.gang_marker"};
            Assert.That(EffectTreeExecutor.GetDeferredResponseDependencies(state,query),Is.Empty); query.RequiredTypeId="core.player_marker"; Assert.That(EffectTreeExecutor.GetDeferredResponseDependencies(state,query).Count,Is.EqualTo(1));
            query.RequiredOwnerPlayerId=2; Assert.That(EffectTreeExecutor.GetDeferredResponseDependencies(state,query),Is.Empty); query.RequiredOwnerPlayerId=1; query.SourceZoneId="another.zone"; Assert.That(EffectTreeExecutor.GetDeferredResponseDependencies(state,query),Is.Empty); Restore("非收容依赖按真实子区类型和所有者匹配");
        }

        [Test] public void FailedStoreIsPreservedAsFailureAndNeverReturnedAsDecline()
        {
            Setup(stores:true,storeScript:StoreScript.Replace("localZoneId='markers'","localZoneId='missing'")); string item=Place(); Depart(item);
            var r=state.EffectRuntime.InteractionRequests.Single(q=>q.Status=="open");
            Assert.That(Engine().TrySubmitInteraction(r.GetStableInteractionId(),1,r.StateRevision,NormalizedValue.CreateString("store"),out string reason),Is.True,reason);
            var run=Engine().RunUntilQuiescent(); artifacts.WriteLabeledText("预期Store失败首次现场",JsonConvert.SerializeObject(state));
            Assert.That(run.Faulted,Is.True); Assert.That(state.EffectRuntime.LastFaultMessage,Does.Contain("departure_destination_store_failed"));
            var d=state.EffectRuntime.InfluenceDepartures.Single(); Assert.That(d.Status,Is.EqualTo("open")); Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==item).ZoneId,Is.EqualTo(d.CustodyZoneId));
            Assert.That(state.EffectRuntime.DeferredResponses.Single().Status,Is.EqualTo(DeferredResponseStatus.Pending));
        }
        [Test] public void RequiredProducerWaitKeepsIndependentOwnerAnswerRoute()
        {
            Setup(stores:true,concurrent:true); string item=Place();
            var spec=EffectSpec.Condition(new[]{InfluenceEffectSpecFactory.RemoveInfluence(1,item)},new[]{ResourceEffectSpecFactory.GainScore(1,1)});
            var command=new GameCommand{CommandId="qy006.required-cause",PlayerId=1,Kind=GameCommandKind.EndAction};
            Assert.That(OperationWindowService.TryResolveCommandContext(state,command,out var cause,out _),Is.True); state.EffectRuntime.CurrentOperationContext=cause;
            string id; try { Assert.That(Engine().TryCreatePlayerActionEffect(1,spec,"1","qy006.required",out id),Is.True); Pump(); } finally {state.EffectRuntime.CurrentOperationContext=null;}
            var response=state.EffectRuntime.DeferredResponses.Single(); Assert.That(response.IsBypass,Is.True); Assert.That(response.RequiresProducerCompletion,Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==id).Status,Is.EqualTo(EffectNodeStatus.Blocked)); Restore("必要生产等待与本人回答路由分离");
            AnswerResponse("destination","store"); Assert.That(Stored(targetCard),Is.EqualTo(1)); Restore("必要生产完成");
        }
        [Test] public void StrictTwoItemsEachUseIndependentClaimsAndOneOptionalBatch()
        {
            Setup(stores:true,batch:true); ActivateDorothy(); string one=Place(0),two=Place(1);
            Run(InfluenceEffectExecutor.StrictRemove(1,new[]{one,two},2)); Assert.That(state.EffectRuntime.InfluenceDepartures.Count,Is.EqualTo(2));
            Assert.That(state.EffectRuntime.DeferredResponses.Count(r=>r.Kind=="optional_effect"),Is.Zero); Restore("严格两枚各自首来源待答");
            // 每枚依来源独立选择，拒绝甲不吞掉乙的机会。
            for(int i=0;i<2;i++) { AnswerResponse("destination","decline"); Restore("严格两枚交接"+i); AnswerResponse("destination","store"); }
            Assert.That(state.EffectRuntime.DeferredResponses.Count(r=>r.Kind=="optional_effect"),Is.EqualTo(1));
            AnswerResponse("optional_effect","2"); AnswerResponse("optional_effect","continue");
            Assert.That(Stored(targetCard),Is.EqualTo(2)); Assert.That(Stored(commandCard),Is.EqualTo(2)); Assert.That(Stored(dorothyCard),Is.EqualTo(1)); Restore("严格两枚各归唯一保管且批次交付一次");
        }
        [Test] public void NestedProductionScopesSealSeparatelyWithoutWaitingForOwnOptionalBody()
        {
            Setup(batch:true); string one=Place(0),two=Place(1);
            Run(InfluenceDepartureBatchExecutor.Create(1,new[]{InfluenceEffectSpecFactory.RemoveInfluence(1,one),InfluenceDepartureBatchExecutor.Create(1,new[]{InfluenceEffectSpecFactory.RemoveInfluence(1,two)})}));
            Assert.That(state.EffectRuntime.InfluenceDepartureBatches.Count,Is.EqualTo(2)); Assert.That(state.EffectRuntime.InfluenceDepartureBatches.All(b=>b.Sealed && b.DepartureIds.Count==1),Is.True);
            Assert.That(state.EffectRuntime.DeferredResponses.Count,Is.EqualTo(2)); Restore("嵌套真实生产均已封口");
            AnswerResponse("optional_effect","continue"); AnswerResponse("optional_effect","continue"); Assert.That(Stored(commandCard),Is.EqualTo(2)); Restore("嵌套每批一次交付");
        }

        [Test] public void RealLuaRootRewritePreservesOneLogicalPlaceAndRestoresChoice()
        {
            Setup(hook:true); string id=Run(InfluenceEffectSpecFactory.PlaceInfluence(1,Slot()));
            Assert.That(state.EffectRuntime.InfluencePlaceMappings.Count,Is.EqualTo(1)); Assert.That(state.Map.Influences,Is.Empty); Restore("根类型待答");
            Answer("gang"); var m=state.EffectRuntime.InfluencePlaceMappings.Single(); Assert.That(m.ActualRootEffectId,Is.EqualTo(id)); Assert.That(m.SelectedLeafEffectId,Is.Not.Empty);
            Assert.That(state.Map.Influences.Count,Is.EqualTo(1)); Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==state.Map.Influences.Single().InfluenceId).TypeId,Is.EqualTo("entrepreneur.gang_marker")); Restore("真实叶完成");
        }
        [Test] public void ConflictingRealLuaRewritersRejectBeforeAnyMapChange()
        { Setup(hook:true,secondHook:true); Assert.That(Engine().TryCreateRoot(InfluenceEffectSpecFactory.PlaceInfluence(1,Slot()),out _),Is.False); Assert.That(state.Map.Influences,Is.Empty); Assert.That(state.EffectRuntime.LastFaultMessage,Does.Contain("influence_place_rewrite_conflict")); }
        [Test] public void RealLuaSingleStorageMovesExactlyTheDepartedInstance()
        { Setup(stores:true); string item=Place(); Depart(item); Restore("单来源收容待答"); Answer("store"); Assert.That(Stored(targetCard),Is.EqualTo(1)); Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==item).ZoneId,Is.EqualTo(state.ItemZones.Zones.Single(z=>z.ParentId==targetCard && z.LocalZoneId=="markers").ZoneId)); Restore("单来源收容完成"); }
        [Test] public void RealLuaBatchWaitsForSealAndDoesNotHoldRemovedItems()
        {
            Setup(batch:true); string one=Place(0),two=Place(1); Run(InfluenceDepartureBatchExecutor.Create(1,new[]{InfluenceEffectSpecFactory.RemoveInfluence(1,one),InfluenceEffectSpecFactory.RemoveInfluence(1,two)}));
            var r=state.EffectRuntime.DeferredResponses.Single(); Assert.That(r.Kind,Is.EqualTo("optional_effect")); Assert.That(r.ReservedItemId,Is.Empty); Assert.That(r.SourceDepartureEffectId,Is.Empty); Assert.That(r.SourceEventIds.Count,Is.EqualTo(2)); Restore("批次数量待答"); Answer("2"); Restore("批次付款待答"); Answer("continue");
            Assert.That(Stored(commandCard),Is.EqualTo(2)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(16)); Assert.That(r.ReservedItemId,Is.Empty); Restore("批次付款交付完成");
        }
        [TestCase("decline","store")][TestCase("store","")][TestCase("decline","decline")]
        public void ActualDorothyAndIndependentLuaStorageKeepOneCustody(string first,string second)
        {
            Setup(stores:true,batch:true,replenish:true,gangStock:1); ActivateDorothy(); string item=Place(type:"entrepreneur.gang_marker");
            Depart(item); var d=state.EffectRuntime.InfluenceDepartures.Single();
            Assert.That(d.ResponseIds.Select(id=>state.EffectRuntime.DeferredResponses.Single(r=>r.DeferredResponseId==id).SourceCardInstanceId),Is.EqualTo(new[]{dorothyCard,targetCard}));
            Assert.That(state.EffectRuntime.InteractionRequests.Count(q=>q.Status=="open" && q.InteractionTypeId=="effect.choice.execute"),Is.EqualTo(1)); Restore("独立第一来源待答");
            AnswerResponse("destination",first); Restore("第一来源结束交接");
            if (second.Length>0) { AnswerResponse("destination",second); Restore("第二来源结束"); }
            else Assert.That(state.EffectRuntime.DeferredResponses.Single(r=>r.SourceCardInstanceId==targetCard).Status,Is.EqualTo(DeferredResponseStatus.Ineligible));
            AnswerResponse("optional_effect","continue");
            Assert.That(Stored(commandCard),Is.EqualTo(1)); Assert.That(Stored(dorothyCard),Is.EqualTo(first=="store"?2:1)); Assert.That(Stored(targetCard),Is.EqualTo(second=="store"?1:0));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e=>e.EventType=="InfluenceRemoved"),Is.EqualTo(1)); Assert.That(state.Map.Influences.Count,Is.EqualTo(1));
            Assert.That(state.ItemZones.Items.Count(i=>i.ItemId==item),Is.EqualTo(1)); Restore("三类响应独立完成");
        }
        [TestCase(1)][TestCase(2)] public void OwnAndOtherActorUseTheOwnerBypass(int actor)
        {
            Setup(stores:true,actor:actor,concurrent:true); string item=Place(); Depart(item,actor,concurrent:true);
            var r=state.EffectRuntime.DeferredResponses.Single(); Assert.That(r.IsBypass,Is.True); Assert.That(r.AnsweringPlayerId,Is.EqualTo(1)); Assert.That(r.MainWindowPlayerIdAtCreation,Is.EqualTo(actor)); Restore("本人或他人真实窗口旁路");
            AnswerResponse("destination","store"); Assert.That(Stored(targetCard),Is.EqualTo(1)); Restore("旁路独立回答完成");
        }
        [Test] public void ReplacementQueriesReturnedGangSupplyAndGetsTheRealSelectedPlaceResult()
        {
            Setup(stores:true,hook:true,replenish:true,gangStock:1); ActivateDorothy(); string item=Place(type:"entrepreneur.gang_marker");
            string replace=Depart(item,replace:true); AnswerResponse("destination","decline"); AnswerResponse("destination","decline"); Restore("返供后替入类型待答");
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==item).ZoneId,Is.EqualTo(state.ItemZones.Items.Single(i=>i.ItemId==item).OriginalSupplyZoneId));
            Answer("gang"); var n=state.EffectRuntime.EffectNodes.Single(v=>v.EffectId==replace); Assert.That(Text(n.NormalizedResult,"outcome"),Is.EqualTo("replaced"));
            var removed=state.EffectRuntime.RuleEvents.Single(e=>e.EventType=="InfluenceRemoved"); var replaced=state.EffectRuntime.RuleEvents.Single(e=>e.EventType=="InfluenceReplaced");
            Assert.That(Text(removed.Payload,"departureId"),Is.EqualTo(Text(replaced.Payload,"departureId"))); Assert.That(Value(replaced.Payload,"placeEffectId").ReferenceId,Is.EqualTo(state.EffectRuntime.InfluencePlaceMappings.Single().SelectedLeafEffectId));
            Assert.That(state.EffectRuntime.DeferredResponses.Count(r=>r.Kind=="destination"),Is.EqualTo(2)); Restore("替换真实叶结果");
        }
        [TestCase("dynamic")][TestCase("condition")] public void RealLuaRewriteAlsoCoversActuallyAttachedDynamicNodes(string mode)
        {
            Setup(hook:true); var place=InfluenceEffectSpecFactory.PlaceInfluence(1,Slot());
            Run(mode=="dynamic"?InfluenceDepartureBatchExecutor.Create(1,new[]{place}):EffectSpec.Condition(new[]{ResourceEffectSpecFactory.GainScore(1,1)},new[]{place}));
            Assert.That(state.EffectRuntime.InfluencePlaceMappings.Count,Is.EqualTo(1)); Restore("动态挂接类型待答"); Answer("ordinary"); Assert.That(state.Map.Influences.Count,Is.EqualTo(1)); Restore("动态挂接完成");
        }
        [Test] public void UnselectedConditionDoesNotRewriteOrExecuteItsPlaceTemplate()
        {
            Setup(hook:true); Run(EffectSpec.Condition(new[]{InfluenceEffectExecutor.StrictRemove(1,new[]{"missing1","missing2"},2)},new[]{InfluenceEffectSpecFactory.PlaceInfluence(1,Slot())}));
            Assert.That(state.EffectRuntime.InfluencePlaceMappings,Is.Empty); Assert.That(state.Map.Influences,Is.Empty); Restore("未选条件无改写");
        }
        [Test] public void IdenticalRealLuaRewritesMergeSourceReceiptsAndExecuteOnePlace()
        {
            Setup(hook:true,secondHook:true,secondHookScript:HookScript); Run(InfluenceEffectSpecFactory.PlaceInfluence(1,Slot()));
            Assert.That(state.EffectRuntime.InfluencePlaceMappings.Single().Sources.Count,Is.EqualTo(2)); Answer("ordinary"); Assert.That(state.Map.Influences.Count,Is.EqualTo(1)); Restore("同义改写归并");
        }
        [Test] public void ChosenEmptyGangDoesNotFallBackToOrdinarySupply()
        {
            Setup(hook:true,gangStock:0); Run(InfluenceEffectSpecFactory.PlaceInfluence(1,Slot()));
            var choice=state.EffectRuntime.InteractionRequests.Single(q=>q.Status=="open"); Assert.That(choice.CandidateIds,Is.EquivalentTo(new[]{"ordinary","gang"})); Answer("gang");
            Assert.That(state.Map.Influences,Is.Empty); Assert.That(state.EffectRuntime.InfluencePlaceMappings.Count,Is.EqualTo(1)); Restore("空帮派不回退");
        }
        [Test] public void MainActionCancellationPermissionSurvivesTypeChoice()
        {
            Setup(hook:true); MainActionEffectExecutor.Register(registry); string root=Run(MainActionEffectExecutor.Create(1,InfluenceEffectSpecFactory.PlaceInfluence(1,"")));
            Assert.That(state.EffectRuntime.InteractionRequests.Single(q=>q.Status=="open").AllowDecline,Is.False); Answer("ordinary");
            var request=state.EffectRuntime.InteractionRequests.Single(q=>q.Status=="open"); Assert.That(request.AllowDecline,Is.True); Restore("保留原地图取消权"); request=state.EffectRuntime.InteractionRequests.Single(q=>q.Status=="open");
            Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(),1,request.StateRevision,NormalizedValue.CreateBoolean(false),out string reason),Is.True,reason); Pump();
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root).Status,Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn,Is.EqualTo(1)); Assert.That(state.Map.Influences,Is.Empty); Restore("取消结果只续接一次");
        }
        [Test] public void PendingOwnerBypassAllowsUnrelatedRealLuaActionThroughGameSession()
        {
            Setup(stores:true,actor:2,concurrent:true,quickScript:"return function(ctx) if ctx.eventType=='EffectCompleted' then return {} end return {Effect.GainScore({recipient=ctx.playerId,amount=1})} end");
            string item=Place(); Depart(item,2,concurrent:true); var option=registry.QueryActions(state,2).Single(o=>o.ActionId=="qy006.quick.action"); Assert.That(option.Enabled,Is.True,option.DisabledReason);
            var session=new GameSession(state); session.RegisterHandler(new InvokeSettlementOptionCommandHandler(registry)); var command=AbilityActionCommand.Create(2,option); command.CommandId="qy006.unrelated.actual-command";
            var result=session.Submit(command); artifacts.WriteLabeledText("无关正式命令结果",JsonConvert.SerializeObject(result)); Assert.That(result.Succeeded,Is.True,result.Validation?.Reason); state=session.State;
            Assert.That(state.FindPlayer(2).Score,Is.EqualTo(1)); Assert.That(state.EffectRuntime.DeferredResponses.Single().Status,Is.EqualTo(DeferredResponseStatus.Pending)); Restore("无关行动未冻结"); AnswerResponse("destination","decline"); Restore("无关行动后旁路仍可答");
        }
        [Test] public void OriginalLuaContinuationReadsLogicalPlaceOnceAfterReplacement()
        {
            const string code=@"return function(ctx)
              if ctx.eventType=='EffectCompleted' then
                if ctx.payload.logicalOperation==nil then return {} end
                if ctx.payload.logicalOperation.effectTypeId~='effect.influence.place' or ctx.payload.logicalOperation.playerId~=tonumber(string.sub(ctx.playerId,2)) or not ctx.payload.normalizedResult.changed then error('logical_place_contract_lost') end
                return {Effect.GainScore({recipient=ctx.playerId,amount=1})} end
              return {Effect.WithContext(Effect.PlaceInfluence({executingPlayer=ctx.playerId,ownerSubject=ctx.playerId,influenceSource='qy006.quick',targetLocationId=PlayerData.GetCityLocation(ctx.playerId)}),{stage='place'})} end";
            Setup(hook:true,replenish:true,quickScript:code); var option=registry.QueryActions(state,1).Single(o=>o.ActionId=="qy006.quick.action");
            Assert.That(Engine().TryInvokeAbilityAction(1,option.OptionId,option.RequestRef.InteractionId,option.RequestRef.FactsDigest,option.VersionRef.ContentPackHash,"qy006.continuation",Object(),out _,out string reason),Is.True,reason); Pump(); Restore("原Lua续接类型待答"); Answer("gang");
            Assert.That(state.FindPlayer(1).Score,Is.EqualTo(1)); Assert.That(state.Map.Influences.Count,Is.EqualTo(1)); Restore("原Lua续接已完成"); Pump(); Assert.That(state.FindPlayer(1).Score,Is.EqualTo(1));
        }
        [TestCase("payment")][TestCase("random")][TestCase("target")]
        public void PureLuaHookRejectsNonPlaceOrChangedTargetsWithoutSideEffects(string kind)
        {
            string leaf=kind=="payment"?"Effect.PayResource({player=ctx.playerId,cost={GoldVoucher=1}})":"Effect.RollDice({apiVersion=1,player=ctx.playerId,count=1,sides=6})";
            string code=kind=="target"?HookScript.Replace("targetSlotId=a.targetSlotId","targetSlotId='location:A-01:1'"):
                "return function(ctx) return {operation='replace',resultContract='place_result',permissionContract='inherit_original',effect="+leaf+"} end";
            Setup(hook:true,overrideHook:code); string before=JsonConvert.SerializeObject(state.RandomRuntime); Assert.That(Engine().TryCreateRoot(InfluenceEffectSpecFactory.PlaceInfluence(1,Slot()),out _),Is.False);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(20)); Assert.That(state.Map.Influences,Is.Empty); Assert.That(JsonConvert.SerializeObject(state.RandomRuntime),Is.EqualTo(before)); artifacts.WriteLabeledText("纯钩子拒绝现场",JsonConvert.SerializeObject(state));
        }
        [TestCase("ProtocolVersion")][TestCase("CustodyZoneId")][TestCase("OperationIndex")]
        public void CorruptedDepartureArchiveIsRejectedWithoutInventingCustody(string field)
        {
            Setup(stores:true); string item=Place(); Depart(item); string json=HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)); artifacts.WriteLabeledText("损坏前原归档",json);
            var archive=JObject.Parse(json); var d=(JObject)archive["Snapshot"]["State"]["EffectRuntime"]["InfluenceDepartures"][0];
            if (field=="ProtocolVersion") d.Remove(field); else d[field]=field=="OperationIndex"?new JValue(99):new JValue("unrelated-zone");
            string corrupted=archive.ToString(); artifacts.WriteLabeledText("明确损坏归档",corrupted); var result=HostRecoveryService.RecoverJson(corrupted,registry); artifacts.WriteLabeledText("损坏恢复拒绝结果",JsonConvert.SerializeObject(new{result.Succeeded,result.Diagnostic,result.FaultCode})); Assert.That(result.Succeeded,Is.False);
        }
    }
}
