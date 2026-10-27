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
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>规则隔离夹具：私有内容、真实ItemZones/Effect/Lua；不是正常对局或页面验收。</summary>
    public abstract class FacilityLayerTestFixture
    {
        protected const string Token = "infected.component.gavial_token", QueryAbility = "qy010.fixture.query";
        protected virtual bool UseInMemoryContent => false;
        protected virtual bool UseCardQueryCarrier => false;
        protected EffectRegistry Registry;
        protected ExternalContentPack Pack;
        protected GameMapDefinition Map;
        internal IsolatedTestArtifacts artifacts;
        [SetUp] public void PreparePrivateContent()
        {
            artifacts = IsolatedTestArtifacts.BeginCase();
            string installed = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            if (!UseInMemoryContent) installed = artifacts.CopyContent(installed, "设施旧合同完整未修改基线");
            var token = JObject.Parse(File.ReadAllText(Path.Combine(installed, "component/" + Token + ".json")));
            if (UseCardQueryCarrier) { token["itemTypeId"] = "core.board_card"; token.Remove("cubeAppearance"); }
            token["abilitySources"] = JArray.FromObject(new[] { new AbilitySourceDeclaration {
                DefinitionId = Token, AbilityId = QueryAbility, Kind = "ruleQuery", HandlerRef = QueryAbility,
                DeclarationVersion = "1.0.0", EffectiveZones = new List<string> { "core.zone.box_reserve" },
                AuthorizationPolicy = "owner", PresentationRef = Token,
                QueryKinds = new List<string> { FacilityBuildQueryService.BuiltKind, FacilityBuildQueryService.SlotKind, FacilityBuildQueryService.SourceKind },
                AllowedOperations = new List<string> { "execution.bind" } } });
            const string lua = @"return function(ctx)
 local p=ctx.payload; local f=p.facts; local items={}
 if p.queryKind=='built_facilities.v1' then items=f.allCityItemIds
 elseif p.queryKind=='facility.build.slot.v1' then
  for _,c in ipairs(f.cells) do
   if c.topItemId~='' and f.newScore>c.topScore then
    table.insert(items,{slotIndex=c.slotIndex,expectedTopItemId=c.topItemId,orderDigest=c.orderDigest,
     requiredResponseAbilityId='qy010.fixture.query',removalPrecondition={operation='remove',ownerFilter='self',requiredCount=1}})
   end
  end
 elseif p.queryKind=='facility.build.sources.v1' and f.requestedSource=='authorized' then items=f.candidatePoolIds end
 return {queryId=p.queryId,basisDigest=p.basisDigest,items=items}
end";
            token["abilityHandlers"] = JArray.FromObject(new[] { new ExternalAbilityHandler {
                HandlerId = QueryAbility, Script = "lua/qy010_fixture_query.lua", Version = "1.0.0" } });
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(installed, "pack.json")));
            string relative = "component/" + Token + ".json";
            if (!((JArray)manifest["definitions"]).Values<string>().Contains(relative)) ((JArray)manifest["definitions"]).Add(relative);
            manifest["enabledExpansionIds"] = new JArray("core", "enterprise", "infected");
            var replacements = new Dictionary<string, string> {
                [relative] = token.ToString(), ["pack.json"] = manifest.ToString(), ["lua/qy010_fixture_query.lua"] = lua };
            if (UseInMemoryContent)
            {
                Pack = ExternalContentPack.Current.CreateWithOverrides(replacements);
                artifacts.WriteLabeledText("设施小型覆盖输入", JsonConvert.SerializeObject(new { basisHash = ExternalContentPack.Current.ContentHash, overrides = replacements, Pack.ContentHash }));
            }
            else
            {
                string privateRoot = artifacts.CopyContent(installed, "设施分层协议私有输入", replacements);
                Pack = ExternalContentPack.Load(privateRoot);
                artifacts.RecordContent("设施分层协议装载", privateRoot, Pack.ContentHash);
            }
        }
        [TearDown] public void RecordCase() => artifacts?.Finish();
        protected GameState State(bool response = false, int copies = 4, IEnumerable<ItemRecipeEntry> extraRecipe = null)
        {
            Registry = new EffectRegistry { ContentPackHash = Pack.ContentHash };
            GenericLuaEffectExecutor.Register(Registry); ResourceEffectExecutor.Register(Registry);
            CharacterCoverEffectExecutor.Register(Registry); CharacterCardLuaCatalog.Register(Registry, Pack);
            CityStyleLuaCatalog.Register(Registry, Pack); ContentLifecycleLuaCatalog.Register(Registry, Pack);
            FacilityLuaCatalog.Register(Registry,Pack);
            AbilitySourceLuaCatalog.Register(Registry, Pack); ItemTransferEffectExecutor.Register(Registry);
            DeckShuffleEffectExecutor.Register(Registry); RepeatEffectExecutor.Register(Registry);
            FacilityEntryEffectExecutor.Register(Registry); FacilitySelectionEffectExecutor.Register(Registry);
            FacilityLayerLuaCatalog.Register(Registry);
            Map = StaticMapDefinitions.CreateThreePlayerMap();
            var source = new GameState { GameId = "qy010.isolated." + Guid.NewGuid().ToString("N"), MapId = Map.MapId,
                ContentPackHash = Pack.ContentHash, Phase = GamePhase.ActionRound1, Round = 1, ActionRound = 1,
                StartPlayerId = 1, CurrentPlayerId = 1,
                RuleConfiguration = new GameRuleConfiguration { MapId = Map.MapId, PlayerCount = 2, ContentPackHash = Pack.ContentHash },
                ContentDefinitions = new GameContentDefinitions { Facilities = Pack.CreateFacilities().ToList(),
                    Characters = Pack.CreateCharacters().ToList(), Events = Pack.CreateEvents().ToList(),
                    CityStyles = Pack.CreateCityStyles().ToList(), Awards = ContentLifecycleLuaCatalog.CreateAwards(Pack),
                    UsageTracks = Pack.ActiveDefinitions.SelectMany(d => d.UsageTracks).ToList(),
                    AbilitySources = Registry.AbilityDeclarations.ToList(),
                    Components = Pack.ActiveDefinitions.Where(d => d.ContentType == "component").Select(d =>
                        new ComponentContentDefinition { DefinitionId = d.DefinitionId, TypeId = d.ItemTypeId,
                            QuantityPolicy = (string)d.Data["quantityPolicy"], CubeAppearance = d.CubeAppearance }).ToList() },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Name = "甲", Resources = new ResourceSet { GoldVoucher = 200, Originium = 100, Iron = 100, OriginiumShard = 100 } },
                    new PlayerState { PlayerId = 2, Name = "乙", Resources = new ResourceSet { GoldVoucher = 200 } } } };
            source.RandomRuntime = GameplayRandomRuntime.Create(source.GameId);
            var entries = new List<ItemRecipeEntry> {
                new ItemRecipeEntry { EntryId = "query", DefinitionId = Token, TypeId = Pack.ActiveDefinitions.Single(d=>d.DefinitionId==Token).ItemTypeId, Quantity = 1, PerPlayer = true, InitialZoneRole = !UseCardQueryCarrier && Pack.ActiveDefinitions.Single(d=>d.DefinitionId==Token).ItemTypeId=="core.board_card" ? "retired" : "reserve" },
                new ItemRecipeEntry { EntryId = "closure", DefinitionId = "operator.closure", TypeId = "core.operator_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" },
                new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = 29, PerPlayer = true, InitialZoneRole = "supply" } };
            if (extraRecipe != null) entries.AddRange(extraRecipe);
            foreach (var definition in source.ContentDefinitions.Facilities)
                entries.Add(new ItemRecipeEntry { EntryId = definition.FacilityId, DefinitionId = definition.FacilityId,
                    TypeId = "core.facility_card", Quantity = copies, InitialZoneRole = "facility_deck" });
            var state = new ItemZoneStateFactory().CreateForSetup(source, Map, entries,
                Pack.ActiveDefinitions.Select(d => new ItemCardZoneDeclaration { DefinitionId = d.DefinitionId, CardZones = d.CardZones }).ToList());
            state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            InfluenceEffectExecutor.Register(Registry, state, new MapQueryService(Map));
            // 通用服务的隔离入口，仅在此测试程序集注册；不冒充内容卡效果或正常操作授权。
            Registry.Register(new EffectRegistration("qy010.fixture.transfer", c => {
                var moved = new ItemTransferService().RelocateBatch(c, BatchRequest(c.Node.NormalizedArguments));
                return EffectStepResult.Completed(Object("movedCount", NormalizedValue.CreateInteger(moved.MovedCount)));
            }));
            if (response) RegisterRequiredResponse(state);
            return state;
        }
        protected void RegisterRequiredResponse(GameState state)
        {
            var carrier = state.ItemZones.Items.Single(i => i.DefinitionId == Token && i.OwnerSubject.PlayerId == 1);
            const string script = @"return function(ctx)
 local f=ctx.payload.buildFacts
 if not f or not f.overlay then return {} end
 return {Effect.WithContext(Effect.SelectInfluence({player=ctx.playerId,operation='remove',ownerFilter='self',
  count=1,requiredCount=1,requireCompleteSelection=true,allowDecline=false}),
  {requiredOutcome={protocolVersion=1,resultField='completedCount',expectedValue=1}})}
end";
            var definition = new LuaScriptDefinition(Token, QueryAbility, "qy010.fixture.required", "1.0.0", script, LuaContentHasher.ComputeSha256(script));
            new LuaEffectEventDispatcher(Registry).Register(new LuaHandlerDefinition("qy010.fixture.required",
                FacilityBuildJournal.EventType, "", definition, contentInstanceId: carrier.ItemId, abilityId: QueryAbility));
        }
        protected ItemInstanceState Card(GameState state, string definition, int number = 0) => state.ItemZones.Items
            .Where(i => i.DefinitionId == definition && i.ZoneId == FindZone(state, "facility_deck").ZoneId).OrderBy(i => i.ItemId, StringComparer.Ordinal).Skip(number).First();
        protected string Definition(GameState state, Func<FacilityCardDefinition, bool> predicate) =>
            state.ContentDefinitions.Facilities.Where(predicate).OrderBy(d => d.Score).ThenBy(d => d.FacilityId, StringComparer.Ordinal).First().FacilityId;
        protected FacilityCellLayerSnapshot Cell(GameState state, int slot, int player = 1) => FacilityLayerQuery.ResolveCell(state, player, slot);
        protected ItemTransferRequest Leg(GameState state, string itemId, string destination, string insertion = "unordered", int player = 1) =>
            new ItemTransferRequest { ExecutingPlayerId = player, ItemIds = new List<string> { itemId }, RequiredCount = 1,
                ExpectedSource = new ItemPositionRef { ZoneId = state.ItemZones.Items.Single(i => i.ItemId == itemId).ZoneId },
                Destination = new ZoneRef { ZoneId = destination }, InsertionKind = insertion, OperationProfileId = "core.facility_card_transfer" };
        protected EffectNodeRuntimeState Transfer(GameState state, params ItemTransferRequest[] legs)
        {
            var request = ItemTransferService.CreateBatch(state, legs[0].ExecutingPlayerId, "fixture:" + Guid.NewGuid().ToString("N"), legs);
            return Run(state, EffectSpec.Create("qy010.fixture.transfer", ItemTransferService.BatchArguments(request), request.ExecutingPlayerId));
        }
        protected void Put(GameState state, string id, int slot, int player = 1) => Transfer(state, Leg(state, id, Cell(state, slot, player).CellZoneId, player: player));
        protected void Overlay(GameState state, string id, int slot)
        {
            var cell = Cell(state, slot);
            Transfer(state, Leg(state, cell.TopItemId, cell.CoveredZoneId, "top"), Leg(state, id, cell.CellZoneId));
        }
        protected EffectNodeRuntimeState Run(GameState state, EffectSpec spec)
        {
            var engine = new EffectTreeExecutor(state, Registry); string id = engine.CreateRoot(spec);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            return engine.GetNode(id);
        }
        protected InteractionRequest Open(GameState state) => state.EffectRuntime.InteractionRequests.Last(i => i.Status == "open");
        protected void Answer(GameState state, string answer)
        {
            var request = Open(state); var engine = new EffectTreeExecutor(state, Registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision,
                NormalizedValue.CreateString(answer), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
        }
        protected GameState Recover(GameState state, string label)
        {
            var result = artifacts.Recover(state, label, Registry);
            Assert.That(result.Succeeded, Is.True, result.Diagnostic); return result.State;
        }
        protected void Evidence(GameState state, string label) => artifacts.WriteLabeledText(label, JsonConvert.SerializeObject(state, Formatting.Indented));
        protected void OwnInfluence(GameState state)
        {
            Run(state, InfluenceEffectSpecFactory.PlaceInfluence(1, ""));
            if (state.EffectRuntime.InteractionRequests.Any(i => i.Status == "open")) Answer(state, Open(state).CandidateIds[0]);
        }
        protected EffectSpec Build(GameState state, string itemId, int slot, string payment = "free")
        {
            var spec = FacilityBuildEffectSpecFactory.Build(1, itemId, slot, payment, false, "qy010.isolated", false,
                state.ItemZones.Items.Single(i => i.ItemId == itemId).ZoneId, "deck");
            if (slot >= 0) spec.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "slotObservation",
                Value = FacilityBuildJournal.EncodeSlot(Registry.FacilityBuildQueries.ResolveSlots(state, 1, itemId).Single(c => c.SlotIndex == slot)) });
            spec.NormalizedArguments.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            return spec;
        }
    }
}

