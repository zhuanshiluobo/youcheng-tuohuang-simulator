using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Cards;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>005隔离合同测试；不代表企业家正式正文或正常独立程序验收。</summary>
    public sealed partial class CardMarkerContractTests
    {
        private IsolatedTestArtifacts artifacts;
        private ExternalContentPack pack;
        private string fixtureRoot;
        private EffectRegistry registry;
        private GameState state;
        private Func<IList<EffectSpec>> body;
        private string card, local, definition;
        [SetUp] public void Begin()
        {
            state = null; registry = null; body = null; card = local = definition = "";
            artifacts = IsolatedTestArtifacts.BeginCase();
            string source = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(source, "pack.json"))); manifest["enabledExpansionIds"] = new JArray("core", "enterprise", "infected");
            string root = artifacts.CopyContent(source, "005私有内容", new Dictionary<string, string> { ["pack.json"] = manifest.ToString() });
            fixtureRoot = root;
            pack = ExternalContentPack.Load(root); artifacts.RecordContent("005内容装载", root, pack.ContentHash);
        }
        [TearDown] public void Finish()
        {
            if (state != null) artifacts.WriteLabeledText("最终真实状态", JsonConvert.SerializeObject(state));
            artifacts.Finish();
        }
        private void State(int supply = 6, bool mixed = false, bool allow = true, bool multipleSupplies = false, bool tokenCost = false, bool twoAuthorizedSources = false, int initialGold = 20)
        {
            definition = mixed ? "operator.dorothy" : "entrepreneur.ability.A01"; local = mixed ? "markers" : "commands";
            var declarations = new List<AbilitySourceDeclaration>();
            foreach (string identity in new[] { "unprivileged", "allowed" })
                declarations.Add(new AbilitySourceDeclaration { DefinitionId = definition, AbilityId = "qy005." + identity, Kind = "quick", HandlerRef = "qy005." + identity,
                    DeclarationVersion = "1.0.0", AuthorizationPolicy = "controller", EffectiveZones = new List<string> { mixed ? "core.zone.character_persistent" : "core.zone.entrepreneur_active" }, PresentationRef = definition,
                    AllowedOperations = (identity == "allowed" || twoAuthorizedSources) && allow ? new[] { "load", "store", "transfer.original_supply", "transfer.retired" }.Select(op => "carrier." + op + ":" + definition + ":" + local + ":" + (mixed ? "core.cube" : "core.player_marker")).ToList() : new List<string>(),
                    Actions = new List<AbilityActionDeclaration> { new AbilityActionDeclaration { ActionId = "qy005." + identity, EntryKind = "quick", BodyHandlerRef = "qy005." + identity, PresentationRef = definition } } });
            if (tokenCost) declarations.Single(d => d.AbilityId == "qy005.allowed").Actions.Add(new AbilityActionDeclaration { ActionId = "qy005.paid", EntryKind = "quick", BodyHandlerRef = "qy005.allowed", EntryCostRef = "qy005.cost", PresentationRef = definition });
            registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry); ItemTransferEffectExecutor.Register(registry); AbilityActionExecutor.Register(registry);
            registry.RegisterAbilityHandler("qy005.unprivileged", "qy005.unprivileged/1", _ => body());
            registry.RegisterAbilityHandler("qy005.allowed", "qy005.allowed/1", _ => body());
            if (tokenCost) registry.RegisterAbilityHandler("qy005.cost", "qy005.cost/1", _ => new[] { TransferTokens(2) });
            registry.FreezeAbilityCatalog(declarations);
            var map = StaticMapDefinitions.CreateThreePlayerMap();
            var source = new GameState { GameId = "qy005.isolated", ContentPackHash = pack.ContentHash, MapId = map.MapId,
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = pack.ContentHash }, LaunchPlayerOrder = new List<int> { 1, 2 },
                ContentDefinitions = new GameContentDefinitions { AbilitySources = declarations, Facilities = pack.CreateFacilities().ToList(), Characters = pack.CreateCharacters().ToList(),
                    Components = pack.Definitions.Where(d => d.ContentType == "component").Select(d => new ComponentContentDefinition { DefinitionId = d.DefinitionId, TypeId = d.ItemTypeId, QuantityPolicy = (string)d.Data["quantityPolicy"], CubeAppearance = d.CubeAppearance }).ToList() },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Name = "隔离甲", Resources = new ResourceSet { GoldVoucher = initialGold } }, new PlayerState { PlayerId = 2, Name = "隔离乙" } } };
            var recipe = new List<ItemRecipeEntry> { new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = supply, PerPlayer = true, InitialZoneRole = "supply" },
                new ItemRecipeEntry { EntryId = "carrier", DefinitionId = definition, TypeId = mixed ? "core.operator_card" : "entrepreneur.ability_card", Quantity = 1, PerPlayer = true, InitialZoneRole = mixed ? "persistent" : "entrepreneur_active" } };
            if (mixed) recipe.Add(new ItemRecipeEntry { EntryId = "gang", DefinitionId = "entrepreneur.component.gang_marker", TypeId = "entrepreneur.gang_marker", Quantity = 2, PerPlayer = true, InitialZoneRole = "supply" });
            if (multipleSupplies) recipe.Add(new ItemRecipeEntry { EntryId = "reserve_marker", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = 1, PerPlayer = true, InitialZoneRole = "reserve" });
            state = new ItemZoneStateFactory().CreateForSetup(source, map, recipe, new[] { new ItemCardZoneDeclaration { DefinitionId = definition, CardZones = pack.Definitions.Single(d => d.DefinitionId == definition).CardZones } });
            state.Phase = GamePhase.ActionRound1; state.Round = 1; state.ActionRound = 1; state.CurrentPlayerId = 1; state.StartPlayerId = 1;
            state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            card = state.ItemZones.Items.Single(i => i.DefinitionId == definition && i.OwnerSubject.PlayerId == 1).ItemId;
        }
        private ZoneInstanceState Storage => state.ItemZones.Zones.Single(z => z.ParentId == card && z.LocalZoneId == local);
        private List<ItemInstanceState> Stored => state.ItemZones.Items.Where(i => i.ZoneId == Storage.ZoneId).ToList();
        private EffectTreeExecutor Engine() => new EffectTreeExecutor(state, registry);
        private void Run()
        { var result = Engine().RunUntilQuiescent(); if (result.Faulted) artifacts.WriteLabeledText("首次故障", JsonConvert.SerializeObject(state)); Assert.That(result.Faulted, Is.False, state.EffectRuntime.LastFaultMessage); }
        private string Invoke(params EffectSpec[] specs) => InvokeAs("allowed", specs);
        private string InvokeAs(string identity, params EffectSpec[] specs)
        {
            body = () => specs; var option = registry.QueryActions(state, 1).Single(o => o.ActionId == "qy005." + identity);
            string id = Engine().CreateRoot(AbilityActionExecutor.Create(option, "qy005.command." + state.EffectRuntime.AbilityActionInvocations.Count, Object())); Run(); return id;
        }
        // 只作通用转移合同的隔离库存准备，不是退役Load接口或正式卡面实现。
        private EffectSpec SeedStoredTokens(int n) => TokenSpec(1,
            state.ItemZones.Items.Where(i => i.ZoneId == FindZone(state, "supply", 1).ZoneId && i.TypeId == "core.player_marker").OrderBy(i => i.ItemId).Take(n).Select(i => i.ItemId),
            FindZone(state, "supply", 1).ZoneId, Storage.ZoneId, n);
        private string TokenTransferExpression(string count, string destination = "original_supply", string selection = "equivalent")
        {
            string target = destination == "original_supply" ? "{kind='original_supply'}" : "{zoneId='" + FindZone(state, "retired").ZoneId + "'}";
            return @"(function()
local tokens={};local cursor=0
repeat local page=SourceData.GetTransferableTokens({carrierItemId='" + card + "',localZoneId='" + local + @"',cursor=cursor})
 for _,item in ipairs(page.items) do tokens[#tokens+1]=item end;cursor=page.nextCursor until cursor==-1
local n=" + count + @"
local function move(ids) return Effect.MoveItem({apiVersion=3,player=ctx.playerId,itemIds=ids,expectedSource={zoneId='" + Storage.ZoneId + @"'},
 destination=" + target + @",insertion={kind='unordered'},operationProfileId='core.cube_storage',requiredCount=n}) end
local selection='" + selection + @"'
if selection=='types' then
 local normal={};local other={};for _,v in ipairs(tokens) do local group=v.typeId=='core.player_marker' and normal or other;group[#group+1]=v.itemId end
 local branches={}
 local lower=n-#other;if lower<0 then lower=0 end
 local upper=n;if upper>#normal then upper=#normal end
 for k=lower,upper do
  local ids={};for i=1,k do ids[#ids+1]=normal[i] end;for i=1,n-k do ids[#ids+1]=other[i] end
  branches[#branches+1]={id=tostring(k),effects={move(ids)}}
 end
 if #branches==1 then return branches[1].effects[1] end
 return Effect.Choice({player=ctx.playerId,branches=branches})
elseif selection=='original_supply' then
 local branches={};for _,v in ipairs(tokens) do branches[#branches+1]={id=v.itemId,effects={move({v.itemId})}} end
 return Effect.Choice({player=ctx.playerId,branches=branches})
end
local ids={};for _,v in ipairs(tokens) do if #ids<n then ids[#ids+1]=v.itemId end end;return move(ids)
end)()";
        }
        // 通用原语夹具只传当前Lua已选定实例；不恢复专用付款接口或内核自动组合。
        private EffectSpec TransferTokens(int n, string destination = "original_supply") => B2MarkerSpecs(TokenTransferExpression(n.ToString(), destination)).Single();
        private EffectSpec ChooseTokenTypes(int n) => B2MarkerSpecs(TokenTransferExpression(n.ToString(), selection: "types")).Single();
        private bool IsTokenTransfer(EffectNodeRuntimeState node) => node.EffectTypeId == ItemTransferEffectExecutor.TypeId && Number(node.NormalizedArguments, "apiVersion") == 3 && Text(Value(node.NormalizedArguments, "expectedSource"), "zoneId") == Storage.ZoneId;
        private void AwaitChosenTokenBeforeCommit(string choice)
        {
            var request = state.EffectRuntime.InteractionRequests.Last(q => q.Status == "open");
            Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision, NormalizedValue.CreateString(choice), out string reason), Is.True, reason);
            for (int i = 0; i < 80 && !state.EffectRuntime.EffectNodes.Any(IsTokenTransfer); i++) Assert.That(Engine().Advance(), Is.True, Engine().LastDiagnostic);
            var token = state.EffectRuntime.EffectNodes.Single(IsTokenTransfer);
            Assert.That(token.PositionChanges, Is.Empty); Assert.That(token.Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
        }
        private void Answer(string value)
        {
            var r = state.EffectRuntime.InteractionRequests.Last(q => q.Status == "open");
            Assert.That(Engine().TrySubmitInteraction(r.GetStableInteractionId(), r.AnsweringPlayerId, r.StateRevision, NormalizedValue.CreateString(value), out string reason), Is.True, reason); Run();
        }
        private void Restore(string label)
        { artifacts.WriteLabeledText(label + "原始状态", JsonConvert.SerializeObject(state)); var recovered = artifacts.Recover(state, label, registry); Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic); state = recovered.State; }
        [TestCase(17)] [TestCase(29)] [TestCase(99)] public void V3CompactResultsTransferTheFullCountAndRestoreWithoutRaisingGlobalBudgets(int n)
        {
            State(supply: n); Invoke(SeedStoredTokens(n)); Assert.That(Stored.Count, Is.EqualTo(n));
            var load = state.EffectRuntime.EffectNodes.Last(v => v.EffectTypeId == ItemTransferEffectExecutor.TypeId);
            Assert.That(Number(Value(load.ContinuationData, "transferBatchReceipt"), "protocolVersion"), Is.EqualTo(3));
            Assert.That(Value(load.NormalizedResult, "positionChanges"), Is.Null); Assert.That(load.PositionChanges.Count, Is.EqualTo(n));
            Assert.That(load.NormalizedResult.TryValidate(out string reason), Is.True, reason); Restore("v3完整装载" + n);
            var ids = Stored.Select(i => i.ItemId).ToList(); Invoke(TransferTokens(n)); Assert.That(Stored, Is.Empty);
            var token = state.EffectRuntime.EffectNodes.Single(IsTokenTransfer);
            Assert.That(token.TransferBatchRequest.ProtocolVersion, Is.EqualTo(3));
            Assert.That(Number(Value(token.ContinuationData, "transferBatchReceipt"), "protocolVersion"), Is.EqualTo(3));
            Assert.That(Value(token.NormalizedArguments, "itemIds").Items.Select(v => v.StringValue), Is.EqualTo(Value(token.NormalizedResult, "movedItemIds").Items.Select(v => v.StringValue)));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(q => q.Status == "open"), Is.False, "等价实例不产生多余选择页。");
            Assert.That(ids.All(id => state.ItemZones.Items.Single(i => i.ItemId == id).ZoneId == state.ItemZones.Items.Single(i => i.ItemId == id).OriginalSupplyZoneId), Is.True);
            Restore("v3足额支付" + n); Run(); Assert.That(Stored, Is.Empty);
        }
        [Test] public void V3MixedItemsUseOneMandatoryExactSelectionAndRestoreTheSelectedWholeGroup()
        {
            State(17, mixed: true);
            // 明确的隔离收容库存；必要组合由Lua按真实类型产生，不调用内核付款选择。
            var items = state.ItemZones.Items.Where(i => i.OwnerSubject.PlayerId == 1 && i.PhysicalKind == ItemPhysicalKind.Cube && i.ZoneId == FindZone(state, "supply", 1).ZoneId).ToList();
            foreach (var item in items)
            {
                foreach (var order in state.ItemZones.Orders.Where(o => o.ZoneId == item.ZoneId)) order.ItemIds.Remove(item.ItemId);
                item.ZoneId = Storage.ZoneId; foreach (var order in state.ItemZones.Orders.Where(o => o.ZoneId == Storage.ZoneId)) order.ItemIds.Add(item.ItemId);
            }
            Assert.That(Stored.Count, Is.EqualTo(19)); Invoke(ChooseTokenTypes(17));
            var request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
            Assert.That(request.CandidateIds, Is.EqualTo(new[] { "15", "16", "17" }));
            Assert.That(request.MinSelections, Is.EqualTo(1)); Assert.That(request.MaxSelections, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.EffectNodes.Any(IsTokenTransfer), Is.False); Restore("Lua真实混合类型组合待选");
            request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
            var selected = items.Where(i => i.TypeId == "core.player_marker").OrderBy(i => i.ItemId, StringComparer.Ordinal).Take(15)
                .Concat(items.Where(i => i.TypeId == "entrepreneur.gang_marker").OrderBy(i => i.ItemId, StringComparer.Ordinal)).Select(i => i.ItemId).ToList();
            Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(), 2, request.StateRevision, NormalizedValue.CreateString("15"), out _), Is.False);
            Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, Strings(Enumerable.Repeat(selected[0], 17)), out _), Is.False);
            AwaitChosenTokenBeforeCommit("15"); Restore("Lua组合已选token尚未提交"); Run();
            var token = state.EffectRuntime.EffectNodes.Single(IsTokenTransfer);
            Assert.That(token.TransferBatchRequest.ProtocolVersion, Is.EqualTo(3));
            Assert.That(token.TransferBatchRequest.Transfers.SelectMany(v => v.ItemIds), Is.EqualTo(selected));
            Assert.That(Stored.Count, Is.EqualTo(2)); Assert.That(selected.All(id => state.ItemZones.Items.Single(i => i.ItemId == id).ZoneId == state.ItemZones.Items.Single(i => i.ItemId == id).OriginalSupplyZoneId), Is.True);
            Restore("Lua混合17枚足额原子提交后"); Run(); Assert.That(Stored.Count, Is.EqualTo(2));
        }
        [TestCase(1)][TestCase(2)][TestCase(6)] public void GenericTransferReturnsActualInstancesAndKeepsEmptyAbilityCarrier(int n)
        {
            State(); Invoke(SeedStoredTokens(n)); Assert.That(Stored.Count, Is.EqualTo(n)); var ids = Stored.Select(i => i.ItemId).ToArray(); Restore("固定装载后");
            Invoke(TransferTokens(n)); Assert.That(Stored, Is.Empty); Assert.That(ids.All(id => state.ItemZones.Items.Single(i => i.ItemId == id).ZoneId == state.ItemZones.Items.Single(i => i.ItemId == id).OriginalSupplyZoneId), Is.True);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == card).ZoneId, Is.EqualTo(FindZone(state, "entrepreneur_active", 1).ZoneId)); Restore("支付清空能力留牌");
        }
        [Test] public void InsufficientPaymentDoesNotCreateRightSideOrEvents()
        { State(); Invoke(SeedStoredTokens(1)); int score = state.FindPlayer(1).Score; Invoke(EffectSpec.Condition(new[] { TransferTokens(2) }, new[] { ResourceEffectSpecFactory.GainScore(1, 9) })); Assert.That(Stored.Count, Is.EqualTo(1)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score)); Assert.That(state.EffectRuntime.RuleEvents.Any(e => e.EventType == "InfluenceRemoved" || e.EventType == "InfluenceReplaced"), Is.False); }
        [Test] public void ExplicitRetiredPaymentPreservesTypeAndOriginalSupply()
        { State(); Invoke(SeedStoredTokens(3)); var original = Stored.ToDictionary(i => i.ItemId, i => i.OriginalSupplyZoneId); Invoke(TransferTokens(3, "retired")); Assert.That(original.All(p => state.ItemZones.Items.Single(i => i.ItemId == p.Key).ZoneId == FindZone(state, "retired").ZoneId && state.ItemZones.Items.Single(i => i.ItemId == p.Key).OriginalSupplyZoneId == p.Value), Is.True); Restore("永久回盒后"); }
        [Test] public void UsesActualOperationSourceAndRejectsUnprivilegedSameCard()
        {
            State(); body = () => new[] { SeedStoredTokens(2) };
            var denied = registry.QueryActions(state, 1).Single(o => o.ActionId == "qy005.unprivileged");
            Engine().CreateRoot(AbilityActionExecutor.Create(denied, "qy005.denied.source", Object()));
            Assert.That(Engine().RunUntilQuiescent().Faulted, Is.True); Assert.That(Stored, Is.Empty);
            State(); Invoke(SeedStoredTokens(2)); Assert.That(Stored.Count, Is.EqualTo(2));
        }
        [Test] public void GenericTransferCannotBypassCarrierPermission()
        { State(allow: false); var ids = state.ItemZones.Items.Where(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1).Take(1).Select(i => i.ItemId); body = () => new[] { MoveSpec(1, ids, FindZone(state, "supply", 1).ZoneId, Storage.ZoneId, "unordered", 1, "core.cube_storage") }; var option = registry.QueryActions(state, 1).Single(o => o.ActionId == "qy005.allowed"); Engine().CreateRoot(AbilityActionExecutor.Create(option, "bad_transfer")); var result = Engine().RunUntilQuiescent(); Assert.That(result.Faulted, Is.True); Assert.That(Stored, Is.Empty); }
        [TestCase(0)][TestCase(1)][TestCase(2)] public void MixedTypesUseLuaChoicesAndReturnEachOriginalSupply(int normalCount)
        {
            State(2, true); var ids = state.ItemZones.Items.Where(i => i.OwnerSubject.PlayerId == 1 && i.PhysicalKind == ItemPhysicalKind.Cube && i.ZoneId == FindZone(state, "supply", 1).ZoneId).Select(i => i.ItemId).ToArray();
            foreach (string id in ids) { var item = state.ItemZones.Items.Single(i => i.ItemId == id); foreach (var order in state.ItemZones.Orders.Where(o => o.ZoneId == item.ZoneId)) order.ItemIds.Remove(id); item.ZoneId = Storage.ZoneId; foreach (var order in state.ItemZones.Orders.Where(o => o.ZoneId == Storage.ZoneId)) order.ItemIds.Add(id); }
            Invoke(ChooseTokenTypes(2)); var request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
            Assert.That(request.AllowDecline, Is.False); Assert.That(request.CandidateIds, Is.EqualTo(new[] { "0", "1", "2" }));
            Restore("Lua三种有实际类型差异的组合待选");
            if (normalCount == 1) { AwaitChosenTokenBeforeCommit("1"); Restore("Lua混合组合已选未提交"); Run(); }
            else Answer(normalCount.ToString());
            var token = state.EffectRuntime.EffectNodes.Single(IsTokenTransfer);
            var moved = token.TransferBatchRequest.Transfers.SelectMany(v => v.ItemIds).ToArray();
            Assert.That(moved.Length, Is.EqualTo(2)); Assert.That(moved.Count(id => state.ItemZones.Items.Single(i => i.ItemId == id).TypeId == "core.player_marker"), Is.EqualTo(normalCount));
            Assert.That(Stored.Count, Is.EqualTo(2)); Assert.That(state.ItemZones.Items.Where(i => ids.Contains(i.ItemId) && i.ZoneId != Storage.ZoneId).All(i => i.ZoneId == i.OriginalSupplyZoneId), Is.True); Restore("Lua混合组合足额返原供应");
        }
        [Test] public void ActualDorothyLuaKeepsInitialChoiceAndUsesAuthorizedTokenCleanup()
            => ActualDorothy();
        private void ActualDorothy()
        {
            registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry); CharacterCoverEffectExecutor.Register(registry);
            CharacterCardLuaCatalog.Register(registry, pack); CityStyleLuaCatalog.Register(registry, pack); ContentLifecycleLuaCatalog.Register(registry, pack); AbilitySourceLuaCatalog.Register(registry, pack);
            var map = StaticMapDefinitions.CreateThreePlayerMap(); definition = "operator.dorothy"; local = "markers";
            InfluenceEffectExecutor.Register(registry, new InfluenceService(new MapQueryService(map)));
            state = new ItemZoneStateFactory().CreateForSetup(new GameState { GameId = "qy005.actual_dorothy.isolated", MapId = map.MapId, ContentPackHash = pack.ContentHash,
                LaunchPlayerOrder = new List<int> { 1, 2 }, RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = pack.ContentHash },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Name = "隔离甲", Resources = new ResourceSet { GoldVoucher = 20 } }, new PlayerState { PlayerId = 2, Name = "隔离乙" } },
                ContentDefinitions = new GameContentDefinitions { Characters = pack.CreateCharacters().ToList(), Facilities = pack.CreateFacilities().ToList(), AbilitySources = registry.AbilityDeclarations.ToList(),
                    UsageTracks = pack.ActiveDefinitions.SelectMany(d => d.UsageTracks).ToList(), CityStyles = pack.CreateCityStyles().ToList(), SpecialActions = pack.CreateSpecialActions().ToList(), Awards = ContentLifecycleLuaCatalog.CreateAwards(pack) } }, map,
                new[] { new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = 6, PerPlayer = true, InitialZoneRole = "supply" },
                    new ItemRecipeEntry { EntryId = "card", DefinitionId = definition, TypeId = "core.operator_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" } },
                new[] { new ItemCardZoneDeclaration { DefinitionId = definition, CardZones = pack.Definitions.Single(d => d.DefinitionId == definition).CardZones } });
            state.Round = 1; state.ActionRound = 1; state.Phase = GamePhase.ActionRound1; state.CurrentPlayerId = 1; state.StartPlayerId = 1;
            card = state.ItemZones.Items.Single(i => i.DefinitionId == definition && i.OwnerSubject.PlayerId == 1).ItemId;
            Engine().CreateRoot(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Run(); Answer(card);
            Engine().CreateRoot(CharacterLifecycleService.CreateUseSpec(1, card, "tactic")); Run(); Answer("2"); Assert.That(Stored.Count, Is.EqualTo(2)); Restore("真实多萝西可选装载");
            var chain = new RoundMainChainBuilder().Build(state.GameId, "", 1, new[] { 1, 2 }, 1);
            var main = chain.Nodes.Single(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow && n.PlayerId == 1);
            state.EffectRuntime.MainNodes.AddRange(chain.Nodes); foreach (var previous in chain.Nodes.Where(n => n.MainlineIndex < main.MainlineIndex)) previous.Status = EffectNodeStatus.Completed;
            state.EffectRuntime.ActiveMainNodeId = main.NodeId; state.EffectRuntime.FirstMainNodeId = chain.FirstNodeId; state.EffectRuntime.CurrentRoundExecutionId = chain.RoundExecutionId;
            state.EffectRuntime.CurrentRoundNumber = 1; state.EffectRuntime.CurrentRoundStartPlayerId = 1; state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            state.Phase = GamePhase.Cleanup;
            var rounds = new RoundExecutionService(registry); string root = rounds.CreateCommandExecutor(state).CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId, Object("mainNodeId", NormalizedValue.CreateString(main.NodeId)), 1));
            main = state.EffectRuntime.MainNodes.Single(n => n.NodeId == main.NodeId); main.ExecutionEffectId = root; main.Status = EffectNodeStatus.Running;
            Run();
            var option = registry.GetSettlementOptions(state, 1).Single(o => o.SourceItemId == card);
            Assert.That(Engine().TryInvokeCharacterSettlementOption(1, option.OptionId, "", option.StateRevision, out _, out var error), Is.True, error); Run();
            Answer("continue"); Assert.That(Stored.Count, Is.EqualTo(0));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => IsTokenTransfer(n) && Number(n.NormalizedResult, "movedCount") == 2 && n.TransferBatchRequest.ProtocolVersion == 3), Is.True);
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == card).ZoneId, Is.EqualTo(FindZone(state, "persistent", 1).ZoneId)); Restore("真实多萝西支付后放置待答");
        }
        [Test] public void TokenTransferPreviewChecksPermissionAndRealInventory()
        {
            State(); var source = registry.QuerySources(state, 1).Single(s => s.AbilityId == "qy005.allowed");
            var service = new ItemTransferService();
            Assert.That(service.TryPlanTokens(state, source, TransferTokens(2).NormalizedArguments, registry.AbilityDeclarations, "qy006.preview.empty", out _, out string reason), Is.False);
            Assert.That(reason, Is.EqualTo("insufficient_items"));
            Invoke(SeedStoredTokens(2)); var args = TransferTokens(2).NormalizedArguments;
            Assert.That(service.TryPlanTokens(state, source, args, registry.AbilityDeclarations, "qy006.preview.full", out var plan, out reason), Is.True, reason);
            Assert.That(plan.Transfers.Sum(v => v.ItemIds.Count), Is.EqualTo(2));
            var denied = registry.QuerySources(state, 1).Single(s => s.AbilityId == "qy005.unprivileged");
            Assert.That(service.TryPlanTokens(state, denied, args, registry.AbilityDeclarations, "qy006.preview.denied", out _, out reason), Is.False);
            Assert.That(reason, Is.Not.Empty);
        }
        [TestCase(2, false)][TestCase(3, false)][TestCase(2, true)][TestCase(3, true)]
        public void TokenEntryCostAndDuplicateCommandUseOneAtomicTransferReceipt(int apiVersion, bool fullPreview)
        {
            State(tokenCost: true); body = () => new[] { ResourceEffectSpecFactory.GainScore(1, 1) };
            if (fullPreview) registry.RegisterActionCostPreview((s, source, action, costs) => ResourcePaymentCostPreview.PreviewActionCosts(s, registry, source, action, costs));
            Assert.That(registry.QueryActions(state, 1).Single(o => o.ActionId == "qy005.paid").Enabled, Is.False);
            Invoke(SeedStoredTokens(2)); body = () => new[] { ResourceEffectSpecFactory.GainScore(1, 1) };
            var option = registry.QueryActions(state, 1).Single(o => o.ActionId == "qy005.paid"); Assert.That(option.Enabled, Is.True, option.DisabledReason);
            Assert.That(Engine().TryInvokeAbilityAction(1, option.OptionId, option.RequestRef.InteractionId, option.RequestRef.FactsDigest, option.VersionRef.ContentPackHash, "qy005.cost-repeat", Object(), out string owner, out string error), Is.True, error); Run();
            Assert.That(Stored, Is.Empty); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1)); Restore("v" + apiVersion + "入口费已支付");
            Assert.That(Engine().TryInvokeAbilityAction(1, option.OptionId, option.RequestRef.InteractionId, option.RequestRef.FactsDigest, option.VersionRef.ContentPackHash, "qy005.cost-repeat", Object(), out string replay, out error), Is.True, error);
            Assert.That(replay, Is.EqualTo(owner)); Run(); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1)); Assert.That(Stored, Is.Empty);
            Assert.That(Engine().TryInvokeAbilityAction(1, option.OptionId, option.RequestRef.InteractionId, option.RequestRef.FactsDigest, option.VersionRef.ContentPackHash, "qy005.cost-repeat", Object("count", NormalizedValue.CreateInteger(1)), out _, out _), Is.False);
        }
        [Test] public void SameTypeDifferentOriginalSuppliesRemainSeparateLuaChoices()
        {
            State(1, true, multipleSupplies: true);
            var cubes = state.ItemZones.Items.Where(i => i.OwnerSubject.PlayerId == 1 && i.TypeId == "core.player_marker").ToList();
            foreach (var item in cubes) { foreach (var order in state.ItemZones.Orders.Where(o => o.ZoneId == item.ZoneId)) order.ItemIds.Remove(item.ItemId); item.ZoneId = Storage.ZoneId; }
            var selected = cubes.Single(i => i.OriginalSupplyZoneId == FindZone(state, "reserve").ZoneId);
            artifacts.WriteLabeledText("不同原供应的隔离载体现场", JsonConvert.SerializeObject(state));
            Invoke(B2MarkerSpecs(TokenTransferExpression("1", selection: "original_supply")));
            var request = state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open"); Assert.That(request.CandidateIds.Count, Is.EqualTo(2));
            Assert.That(request.CandidateIds, Does.Contain(selected.ItemId)); Answer(selected.ItemId);
            Assert.That(state.EffectRuntime.EffectNodes.Single(IsTokenTransfer).TransferBatchRequest.Transfers.Single().ItemIds, Is.EqualTo(new[] { selected.ItemId }));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == selected.ItemId).ZoneId, Is.EqualTo(FindZone(state, "reserve").ZoneId)); Restore("Lua按原供应差异选择真实实例后恢复");
        }
        [Test] public void ExplicitDependencyZoneDoesNotWaitForAnotherLocalStorage()
        {
            State(); var declaration = GameStateCloneService.DeepClone(Storage.Configuration); declaration.LocalZoneId = "other_storage";
            Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, card, declaration, out string otherZone, out string reason), Is.True, reason);
            string pending = state.ItemZones.Items.First(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1).ItemId;
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "qy005.query.response", PlayerId = 1,
                NestedEffects = new List<EffectSpecRuntimeState> { new EffectSpecRuntimeState { EffectTypeId = ContentPrimitiveEffectExecutor.StoreRemoved,
                    NormalizedArguments = Object("cardInstanceId", NormalizedValue.CreateString(card), "localZoneId", NormalizedValue.CreateString(local), "influenceItemId", NormalizedValue.CreateString(pending)) } } });
            state.EffectRuntime.DeferredResponses.Add(new DeferredResponseState { DeferredResponseId = "qy005.query.pending", ResponseRootEffectId = "qy005.query.response", SourceCardInstanceId = card, ReservedItemId = pending });
            var query = new DeferredDependencyQuery { DependencyKind = DeferredDependencyKind.CardMarkerConsumption, SourceCardInstanceId = card, SourceZoneId = otherZone, RequiredCount = 1, RequiredTypeIds = new List<string> { "core.player_marker" } };
            Assert.That(EffectTreeExecutor.GetDeferredResponseDependencies(state, query), Is.Empty);
            query.SourceZoneId = Storage.ZoneId; Assert.That(EffectTreeExecutor.GetDeferredResponseDependencies(state, query).Count, Is.EqualTo(1));
            query.RequiredOwnerPlayerId = 2; Assert.That(EffectTreeExecutor.GetDeferredResponseDependencies(state, query), Is.Empty);
            artifacts.WriteLabeledText("真实子区依赖纯查询模型", "仅验证正式查询的子区和类型匹配，不计完整离场调度或正常游戏验收。");
        }
        [TestCase(1)] [TestCase(2)] [TestCase(3)]
        public void RetiredMarkerPaymentApiIsRejectedWithoutTransferringOrInventingChoices(int api)
        {
            State(); Invoke(SeedStoredTokens(2)); string before = RuleStateDigestService.Compute(state);
            string old = "Effect.PayCardMarkers({apiVersion=" + api + ",player=ctx.playerId,cardInstanceId='" + card + "',localZoneId='" + local + "',count=2})";
            Assert.That(B2CompileMarkerLua(old, out _, out _), Is.False);
            Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(before));
            var arguments = Object("apiVersion", NormalizedValue.CreateInteger(api), "player", NormalizedValue.CreateString("1"), "cardInstanceId", NormalizedValue.CreateString(card), "localZoneId", NormalizedValue.CreateString(local), "count", NormalizedValue.CreateInteger(2));
            Assert.That(ContentPrimitiveEffectExecutor.ValidateContentFlow(ContentPrimitiveEffectExecutor.PayMarkers, arguments), Is.EqualTo(ContentPrimitiveEffectExecutor.RetiredMarkerPaymentReason));
            Assert.That(CardMarkerCarrier.ValidPermission("carrier.pay.original_supply:" + definition + ":" + local + ":core.player_marker"), Is.False);
            var raw = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            var node = raw["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"].Single(n => (string)n["EffectTypeId"] == ItemTransferEffectExecutor.TypeId);
            node["EffectTypeId"] = ContentPrimitiveEffectExecutor.PayMarkers; node["NormalizedArguments"] = JObject.FromObject(arguments);
            artifacts.WriteLabeledText("明确退役旧标记付款存档探针", raw.ToString());
            var result = HostRecoveryService.RecoverJson(raw.ToString(), registry);
            Assert.That(result.Succeeded, Is.False); Assert.That(result.Diagnostic, Is.EqualTo("ability_action.v1:retired_marker_payment_state_requires_migration"));
            Assert.That(Stored.Count, Is.EqualTo(2)); Assert.That(state.EffectRuntime.InteractionRequests.Any(q => q.Status == "open"), Is.False);
        }
        [TestCase(0)][TestCase(1)] public void ActualTSetupShortageFaultsWithZeroAllocationAndRestores(int available)
        {
            var official = EntrepreneurContentCatalog.Read(pack.Definitions); registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
            ResourceEffectExecutor.Register(registry); ExpansionSetupFlow.Register(registry); ItemTransferEffectExecutor.Register(registry);
            registry.RegisterAbilityHandler("qy005.setup.identity", "qy005.setup.identity/1", _ => new List<EffectSpec>());
            var declarations = official.Select(d => new AbilitySourceDeclaration { DefinitionId = d.DefinitionId, AbilityId = d.DefinitionId + ".identity", Kind = "setup", HandlerRef = "qy005.setup.identity",
                DeclarationVersion = d.Version, AuthorizationPolicy = "controller", EffectiveZones = new List<string> { "core.zone.entrepreneur_active" }, PresentationRef = d.DefinitionId }).ToList();
            registry.FreezeAbilityCatalog(declarations);
            var enterprises = pack.Definitions.Where(d => d.ExpansionId == "enterprise" && (d.ContentType == "enterprise" || d.ContentType == "department")).ToList();
            foreach (var d in enterprises) d.RuntimeId = d.DefinitionId;
            registry.EnterpriseDefinitions = LuaContentCatalog.CreateEnterpriseDefinitions(registry, enterprises, pack.GetScript, pack);
            var recipe = GameSetupRecipe.CreateRegisteredStandard(StaticMapDefinitions.ThreePlayerMapId); recipe.EnterpriseEntrance = true;
            foreach (var e in recipe.Entries.Where(e => e.TypeId == "core.player_marker")) e.Quantity = available;
            recipe.Entries.AddRange(GameStateCloneService.DeepClone(pack.SetupRecipes.Single(r => r.RecipeId == "entrepreneur").Entries));
            foreach (var e in enterprises.Where(e => e.ContentType == "enterprise")) recipe.Add(e.DefinitionId, e.DefinitionId, "core.board_card", "reserve");
            recipe.CardZoneDeclarations = pack.Definitions.Where(d => d.ContentType == "entrepreneur_ability").Select(d => new ItemCardZoneDeclaration { DefinitionId = d.DefinitionId, CardZones = d.CardZones }).ToList();
            var definitions = GameContentDefinitions.CreateRegisteredStandard(); definitions.Entrepreneurs = official; definitions.AbilitySources = declarations;
            definitions.Components = pack.Definitions.Where(d => d.ContentType == "component").Select(d => new ComponentContentDefinition { DefinitionId = d.DefinitionId, TypeId = d.ItemTypeId, CubeAppearance = d.CubeAppearance, QuantityPolicy = (string)d.Data["quantityPolicy"] }).ToList();
            Func<GameState> initial = () => GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1, new[] { new PlayerSeat { PlayerId = 1, PlayerName = "隔离甲" }, new PlayerSeat { PlayerId = 2, PlayerName = "隔离乙" } },
                new GameRuleConfiguration { MapId = StaticMapDefinitions.ThreePlayerMapId, PlayerCount = 2, ContentPackHash = pack.ContentHash }, 47, recipe, definitions, new[] { 1, 2 }, "qy005.setup.shortage." + available, null);
            if (available == 0)
            {
                var invalidRecipe = Assert.Throws<InvalidOperationException>(() => initial()); Assert.That(invalidRecipe.Message, Does.Contain("setup_recipe_invalid"));
                artifacts.WriteLabeledText("零数量配方在建局前正式拒绝", invalidRecipe.Message); return;
            }
            state = initial();
            // 仅构造本例设置不足的隔离机制，不回退当前已整合模式状态，不计正常建房开局。
            state.RuleConfiguration.RuleModeId = "entrepreneur"; state.RuleConfiguration.EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" };
            state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 }; registry.ContentDefinitions = GameStateCloneService.DeepClone(definitions);
            Assert.That(enterprises.Any(d => d.ContentType == "enterprise") && enterprises.Where(d => d.ContentType == "enterprise").All(d => registry.EnterpriseDefinitions.FindEnterprise(d.DefinitionId) != null), Is.True, "本例须消费当前真实企业定义，不能以历史G0替代设置验证。");
            Engine().CreateRoot(ExpansionSetupFlow.PreparationSpec(1, false)); Run();
            foreach (var request in state.EffectRuntime.InteractionRequests.Where(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.PreviewInteraction).ToList())
            { RoundExecutionService.RefreshConcurrentInteractionRevisions(state); Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, Strings(new[] { "continue" }), out string reason), Is.True, reason); Run(); }
            RoundExecutionService.RefreshConcurrentInteractionRevisions(state);
            var choice = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.ChooseInteraction);
            var original = state.ItemZones.Items.ToDictionary(i => i.ItemId, i => i.ZoneId);
            Assert.That(Engine().TrySubmitInteraction(choice.GetStableInteractionId(), choice.AnsweringPlayerId, Strings(new[] { "entrepreneur.ability.T09" }), out string error), Is.True, error);
            var result = Engine().RunUntilQuiescent(); artifacts.WriteLabeledText("正式T设置不足首次现场", JsonConvert.SerializeObject(state));
            Assert.That(result.Faulted, Is.True); Assert.That(state.EffectRuntime.LastFaultMessage, Does.Contain("entrepreneur_setup_insufficient_components"));
            Assert.That(state.ItemZones.Items.All(i => original.TryGetValue(i.ItemId, out string zone) && zone == i.ZoneId), Is.True); Assert.That(state.EntrepreneurSetup.Players, Is.Empty);
            OperationWindowService.Synchronize(state);
            Restore("正式T设置不足暂停恢复");
        }
    }
}
