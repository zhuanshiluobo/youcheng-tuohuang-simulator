using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>真实装载器、Lua、PlayerCleanupStarted、统一挂接及Host命令；隔离输入不计正常对局。</summary>
    internal sealed class CleanupSourceTestFixture
    {
        internal const string Definition = "infected.award.permanent_support", Ability = Definition + ".cleanup";
        // 隔离Lua原语夹具：完整翻页、限定真实来源卡/子区；固定数量和实例选择留在Lua。
        internal const string TokenTransferPrelude = @"
local function tokenTransfer(card,n,skip)
 local ids={};local cursor=0;local zone=nil;local seen=0;skip=skip or 0
 repeat
  local page=SourceData.GetTransferableTokens({carrierItemId=card,localZoneId='markers',cursor=cursor})
  for _,item in ipairs(page.items) do
   zone=zone or item.zoneId
   seen=seen+1;if seen>skip and #ids<n then ids[#ids+1]=item.itemId end
  end
  cursor=page.nextCursor
 until cursor==-1
 if not zone then zone=Global.Content.FindZones({parentId=card,localZoneId='markers'})[1].id end
 return Effect.MoveItem({apiVersion=3,player=ctx.playerId,itemIds=ids,expectedSource={zoneId=zone},
  destination={kind='original_supply'},insertion={kind='unordered'},operationProfileId='core.cube_storage',requiredCount=n})
end
";
        internal static string WithTokenTransfers(string body) => body.Replace("return function(ctx)", "return function(ctx)" + TokenTransferPrelude);

        internal readonly IsolatedTestArtifacts Artifacts;
        internal EffectRegistry Registry;
        internal GameState State;
        internal readonly RoundExecutionService Rounds;
        internal string CardId, RootId, ContentRoot;
        private bool faultRecorded;
        internal EffectTreeExecutor Engine => Rounds.CreateCommandExecutor(State);
        internal SettlementWindowRuntimeState Window => State.EffectRuntime.EffectNodes.Single(n => n.EffectId == RootId).SettlementWindow;
        internal IList<CharacterSettlementOption> Options => Registry.GetSettlementOptions(State, 1).ToList();
        internal CleanupSourceEntryState Source => Window.Entries.Single(e => e.CleanupSource != null && e.SourceItemId == CardId).CleanupSource;
        internal int Stored => State.ItemZones.Items.Count(i => i.ZoneId == Storage.ZoneId);
        internal ZoneInstanceState Storage => State.ItemZones.Zones.Single(z => z.ParentId == CardId && z.LocalZoneId == "markers");

        internal CleanupSourceTestFixture(IsolatedTestArtifacts artifacts, bool legacyResource = false, int initialMarkers = 6, bool mandatory = false, string bodyOverride = null, bool replenish = false, int playerCount = 3, bool storageResponse = false,
            int markerSupply = 6, int additionalCarriers = 0, bool mixedMarkers = false, bool concurrent = false, bool inMemoryInput = false)
        {
            Artifacts = artifacts;
            string installed = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            var award = JObject.Parse(File.ReadAllText(Path.Combine(installed, "award/" + Definition + ".json")));
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(installed, "pack.json"))); manifest["enabledExpansionIds"] = new JArray("core", "enterprise", "infected");
            var t = JObject.Parse(File.ReadAllText(Path.Combine(installed, "entrepreneur/entrepreneur.ability.T09.json")));
            award["cardZones"] = t["cardZones"].DeepClone();
            var source = (JObject)award["abilitySources"][0]; source["repeatPolicy"] = legacyResource ? "once" : "unbounded";
            source["allowedOperations"] = new JArray("carrier.transfer.original_supply:" + Definition + ":markers:core.player_marker");
            if (mixedMarkers)
            {
                // 私有通用混合载体声明；不扩大正式T09卡面的接纳类型。
                ((JArray)award["cardZones"][0]["acceptedTypeIds"]).Add("entrepreneur.gang_marker");
                ((JArray)source["allowedOperations"]).Add("carrier.transfer.original_supply:" + Definition + ":markers:entrepreneur.gang_marker");
            }
            string availability = File.ReadAllText(Path.Combine(installed, "lua/settlements/" + Ability + ".lua"));
            availability = availability.Replace("local enabled = ctx.window.kind == 'player_cleanup'", legacyResource ?
                "local enabled = ctx.window.kind == 'player_cleanup' and ctx.facts.payment.available.originium >= 2" :
                "local enabled = ctx.window.kind == 'player_cleanup' and SourceData.GetTransferableTokens({carrierItemId=ctx.facts.source.itemId,localZoneId='markers'}).totalCount >= 3");
            string body = bodyOverride ?? (legacyResource ? @"return function(ctx)
 return { Effect.Condition({ leftEffects={Effect.PayResource({player=ctx.playerId,resourceType='originium',amount=2})},
 rightEffects={Effect.GainScore({player=ctx.playerId,amount=1})} }) }
end" : @"return function(ctx)
 return { Effect.Condition({ ruleCondition=true, decisionPlayer=ctx.playerId,
 leftEffects={tokenTransfer(ctx.payload.sourceItemId,3)},
 rightEffects={Effect.Choice({interactionType='effect.choice.execute',player=ctx.playerId,branches={
 {id='place',effects={Effect.PlaceInfluence({executingPlayer=ctx.playerId,ownerSubject=ctx.playerId,influenceSource='player_supply'})}},
 {id='resource',effects={Effect.GainResource({player=ctx.playerId,resourceType='pure_originium',amount=1})}}
 }})} }) }
end"); // 普通条件缺省v1；v2仅用于005 fixedDeliveryRequirement。
            body = WithTokenTransfers(body);
            var replacements = new Dictionary<string, string> {
                ["award/" + Definition + ".json"] = award.ToString(),
                ["lua/awards/" + Ability + ".lua"] = body, ["lua/settlements/" + Ability + ".lua"] = availability };
            const string requiredDefinition = "infected.award.score";
            if (mandatory || replenish)
            {
                var required = JObject.Parse(File.ReadAllText(Path.Combine(installed, "award/" + requiredDefinition + ".json")));
                string requiredAbility = requiredDefinition + ".cleanup";
                // 私有合成能力只改业务标识；保留原已登记的显示引用，不生成不存在的core文案键。
                var ability = (JObject)award["abilities"][0].DeepClone();
                ability["abilityId"] = requiredAbility; ability["subscriptionId"] = "content." + requiredAbility;
                ability["script"] = "lua/awards/" + requiredAbility + ".lua";
                ability["settlement"]["localId"] = requiredAbility + ".cleanup_order";
                ability["settlement"]["availabilityHandlerId"] = requiredAbility + ".settlement.availability";
                ability["settlement"]["wholeEffectPolicy"] = mandatory ? "required" : "optional";
                var requiredSource = (JObject)source.DeepClone();
                requiredSource["abilityId"] = requiredAbility; requiredSource["handlerRef"] = "content." + requiredAbility;
                requiredSource["presentationRef"] = requiredAbility;
                requiredSource["repeatPolicy"] = "once"; requiredSource["allowedOperations"] = replenish ?
                    new JArray("carrier.load:" + Definition + ":markers:core.player_marker") : new JArray();
                required["abilities"] = new JArray(ability); required["abilitySources"] = new JArray(requiredSource);
                replacements["award/" + requiredDefinition + ".json"] = required.ToString();
                // 独立必选请求或真实补充正文，均经Lua；不存在测试专属C#卡面回调。
                replacements["lua/awards/" + requiredDefinition + ".cleanup.lua"] = mandatory ?
                    "return function(ctx) return {Effect.Choice({player=ctx.playerId,options={'required'},minSelections=1,maxSelections=1})} end" :
                    "return function(ctx) local cards=Global.Content.FindInstances({definitionId='" + Definition + "',controller=ctx.playerId}) " +
                    "return {Effect.LoadCardMarkers({apiVersion=2,player=ctx.playerId,cardInstanceId=cards[1].id,localZoneId='markers',amountMode='fixed',count=3})} end";
                replacements["lua/settlements/" + requiredDefinition + ".cleanup.lua"] = File.ReadAllText(Path.Combine(installed, "lua/settlements/" + Ability + ".lua"))
                    .Replace("localId = '" + Ability + ".cleanup_order'", "localId = '" + requiredAbility + ".cleanup_order'")
                    .Replace("availabilityHandlerId = '" + Ability + ".settlement.availability'", "availabilityHandlerId = '" + requiredAbility + ".settlement.availability'")
                    .Replace("wholeEffectPolicy = 'optional'", mandatory ? "wholeEffectPolicy = 'required'" : "wholeEffectPolicy = 'optional'");
                var handler = manifest["settlementHandlers"].Single(h => (string)h["handlerId"] == Ability + ".settlement.availability");
                ((JArray)manifest["settlementHandlers"]).Add(JObject.Parse(handler.ToString().Replace(Definition, requiredDefinition)));
            }
            if (storageResponse)
            {
                const string storageAbility = "qy008.storage.source", storageHandler = "qy008.storage.handler";
                ((JArray)award["abilitySources"]).Add(JObject.FromObject(new AbilitySourceDeclaration {
                    AbilityId = storageAbility, Kind = "event", HandlerRef = storageHandler, DeclarationVersion = "1.0.0", AuthorizationPolicy = "controller",
                    EffectiveZones = new List<string> { "core.zone.player_rewards" }, ObservedEvents = new List<string> { "InfluenceRemoved" }, PresentationRef = Definition,
                    AllowedOperations = new List<string> { "response.destination", "response.self", "carrier.store:" + Definition + ":markers:core.player_marker" } }));
                award["abilityHandlers"] = new JArray(new JObject { ["handlerId"] = storageHandler, ["script"] = "lua/awards/qy008.storage.lua", ["version"] = "1.0.0", ["completionHandlerId"] = "" });
                replacements["award/" + Definition + ".json"] = award.ToString();
                replacements["lua/awards/qy008.storage.lua"] = @"return function(ctx)
if tonumber(string.sub(ctx.playerId,2))~=ctx.payload.ownerPlayerId then return {} end
return {Effect.Choice({player=ctx.playerId,interactionType='effect.choice.execute',branches={
{id='store',effects={Effect.StoreRemovedInfluence({apiVersion=2,player=ctx.playerId,cardInstanceId=ctx.payload.sourceItemId,localZoneId='markers',influenceItemId=ctx.payload.influenceId})}},
{id='decline',effects={}}}})} end";
            }
            replacements["pack.json"] = manifest.ToString();
            ExternalContentPack pack;
            if (inMemoryInput)
            {
                ContentRoot = installed;
                pack = ExternalContentPack.Current.CreateWithOverrides(replacements);
                artifacts.WriteLabeledText("收尾小型覆盖输入", JsonConvert.SerializeObject(new { basisHash = ExternalContentPack.Current.ContentHash, overrides = replacements, pack.ContentHash }));
            }
            else
            {
                ContentRoot = artifacts.CopyContent(installed, "008正式收尾私有输入", replacements);
                pack = ExternalContentPack.Load(ContentRoot);
            }
            Registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
            var map = StaticMapDefinitions.ForPlayerCount(playerCount == 2 ? 3 : playerCount);
            // 两位隔离玩家仍用既有三人地图，不创建正式两人地图。
            var order = Enumerable.Range(1, playerCount).ToList();
            var query = new MapQueryService(map);
            ResourceEffectExecutor.Register(Registry); GenericLuaEffectExecutor.Register(Registry); ItemTransferEffectExecutor.Register(Registry);
            InfluenceEffectExecutor.Register(Registry, new InfluenceService(query)); ContentPrimitiveEffectExecutor.Register(Registry, query, Registry.InfluenceService);
            AbilitySourceLuaCatalog.Register(Registry, pack);
            if (!inMemoryInput) artifacts.RecordContent("008正式装载", ContentRoot, pack.ContentHash);
            var initial = new GameState { GameId = "qy008.isolated", ContentPackHash = pack.ContentHash, MapId = map.MapId,
                LaunchPlayerOrder = order.ToList(), Round = 1, MaxRounds = 8, ActionRound = 1, CurrentPlayerId = 1, StartPlayerId = 1, Phase = GamePhase.Cleanup,
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = playerCount, ContentPackHash = pack.ContentHash, EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" },
                    PlayerStageSchedulingMode = concurrent ? PlayerStageSchedulingMode.Concurrent : PlayerStageSchedulingMode.Sequential },
                ContentDefinitions = new GameContentDefinitions { AbilitySources = Registry.AbilityDeclarations.ToList(), Facilities = pack.CreateFacilities().ToList(),
                    Characters = pack.CreateCharacters().ToList(), Awards = ContentLifecycleLuaCatalog.CreateAwards(pack), CityStyles = pack.CreateCityStyles().ToList(), SpecialActions = pack.CreateSpecialActions().ToList(), UsageTracks = pack.ActiveDefinitions.SelectMany(d => d.UsageTracks).ToList(),
                    Components = pack.Definitions.Where(d => d.ContentType == "component").Select(d => new ComponentContentDefinition {
                        DefinitionId = d.DefinitionId, TypeId = d.ItemTypeId, QuantityPolicy = (string)d.Data["quantityPolicy"], CubeAppearance = d.CubeAppearance }).ToList() },
                Players = order.Select(id => new PlayerState { PlayerId = id, Name = "隔离玩家" + id, Resources = new ResourceSet { Originium = 4 } }).ToList() };
            var recipe = new List<ItemRecipeEntry> {
                new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = Math.Max(markerSupply, initialMarkers), PerPlayer = true, InitialZoneRole = "supply" },
                new ItemRecipeEntry { EntryId = "source", DefinitionId = Definition, TypeId = "core.award_card", Quantity = (legacyResource ? 2 : 1) + additionalCarriers, PerPlayer = true, InitialZoneRole = "rewards" } };
            if (mixedMarkers) recipe.Add(new ItemRecipeEntry { EntryId = "gang", DefinitionId = "entrepreneur.component.gang_marker", TypeId = "entrepreneur.gang_marker", Quantity = 4, PerPlayer = true, InitialZoneRole = "supply" });
            foreach (var location in map.Locations.Where(l => !l.IsRedZone)) recipe.Add(new ItemRecipeEntry { EntryId = "resource_" + location.LocationId, DefinitionId = "core.resource_point", TypeId = "core.resource_point_token", Quantity = 1, InitialZoneRole = "location/" + location.LocationId });
            if (mandatory || replenish) recipe.Add(new ItemRecipeEntry { EntryId = "required", DefinitionId = requiredDefinition, TypeId = "core.award_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "rewards" });
            State = new ItemZoneStateFactory().CreateForSetup(initial, map, recipe,
                new[] { new ItemCardZoneDeclaration { DefinitionId = Definition, CardZones = pack.Definitions.Single(d => d.DefinitionId == Definition).CardZones } });
            foreach (var location in map.Locations.Where(l => !l.IsRedZone)) State.Map.OpenLocationIds.Add(location.LocationId);
            CardId = State.ItemZones.Items.First(i => i.DefinitionId == Definition && i.OwnerSubject.PlayerId == 1).ItemId;
            // 清楚标记的隔离初始库存；不是正常游戏验收或对原存档的修改。
            foreach (var marker in State.ItemZones.Items.Where(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1).Take(initialMarkers)) marker.ZoneId = Storage.ZoneId;
            Rounds = new RoundExecutionService(Registry);
            var chain = new RoundMainChainBuilder().Build(State.GameId, "", 1, order, 1);
            var main = chain.Nodes.Single(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow && n.PlayerId == 1);
            State.EffectRuntime.MainNodes.AddRange(chain.Nodes);
            foreach (var previous in chain.Nodes.Where(n => n.MainlineIndex < main.MainlineIndex)) previous.Status = EffectNodeStatus.Completed;
            State.EffectRuntime.ActiveMainNodeId = main.NodeId; State.EffectRuntime.FirstMainNodeId = chain.FirstNodeId; State.EffectRuntime.CurrentRoundExecutionId = chain.RoundExecutionId;
            State.EffectRuntime.CurrentRoundNumber = 1; State.EffectRuntime.CurrentRoundStartPlayerId = 1; State.EffectRuntime.PlayerOrderSnapshot = order.ToList();
            RootId = Engine.CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId, Object("mainNodeId", NormalizedValue.CreateString(main.NodeId)), 1));
            main.ExecutionEffectId = RootId; main.Status = EffectNodeStatus.Running; Drain();
        }
        internal void Drain()
        {
            var result = Engine.RunUntilQuiescent();
            if (result.Faulted && !faultRecorded) { faultRecorded = true; Snapshot("008首次实际故障"); }
            Assert.That(result.Faulted, Is.False, State.EffectRuntime.LastFaultMessage);
            RoundExecutionService.RefreshConcurrentInteractionRevisions(State);
        }
        internal InteractionRequest Open => State.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
        internal void Answer(string value) { var r = Open; Assert.That(Engine.TrySubmitInteraction(r.GetStableInteractionId(), r.AnsweringPlayerId, r.StateRevision, NormalizedValue.CreateString(value), out string reason), Is.True, reason); Drain(); }
        internal void Start(string command = "qy008.click")
        {
            var option = Options.Single(o => o.CleanupRequest != null && o.SourceItemId == CardId); var cmd = CleanupSourceCommand.Create(1, command, option.CleanupRequest, State.EffectRuntime.StateRevision);
            if (State.RuleConfiguration.PlayerStageSchedulingMode == PlayerStageSchedulingMode.Concurrent)
            {
                OperationWindowService.Synchronize(State);
                Assert.That(OperationWindowService.TryResolveCommandContext(State, cmd, out var cause, out var validation), Is.True, validation?.Reason);
                State.EffectRuntime.CurrentOperationContext = cause;
            }
            try
            {
                var result = new InvokeSettlementOptionCommandHandler(Registry).Handle(State, cmd); Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
            }
            finally { State.EffectRuntime.CurrentOperationContext = null; }
        }
        internal void Restore(string label)
        {
            var result = Artifacts.Recover(State, label, Registry); Assert.That(result.Succeeded, Is.True, result.Diagnostic); State = result.State;
        }
        internal void Snapshot(string label) => Artifacts.WriteLabeledText(label, JsonConvert.SerializeObject(State));
    }
}
