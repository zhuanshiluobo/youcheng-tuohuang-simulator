using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>纯规则隔离：真实Lua完成回调、编译器、来源行动、独立移动与结果传播；不代表正常局或UI验收。</summary>
    public sealed partial class QY005LoadRetirementR17Tests
    {
        public string LastEvidence = "";
        private GameState state;
        private EffectRegistry registry;
        private string card, storage, supply;
        private const string Ability = "r17.transfer.source", Handler = "r17.transfer.body", Completed = "r17.transfer.completed";
        private EffectTreeExecutor Engine => new EffectTreeExecutor(state, registry);
        private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }

        private void Setup(string mode)
        {
            var source = new AbilitySourceDeclaration { DefinitionId = "r17.carrier", AbilityId = Ability, Kind = "quick", HandlerRef = Handler,
                DeclarationVersion = "1.0.0", EffectiveZones = new List<string> { "core.zone.entrepreneur_active" }, PresentationRef = "r17.carrier",
                AllowedOperations = mode == "permission" ? new List<string>() : new List<string> { "carrier.load:r17.carrier:tokens:core.player_marker" },
                Actions = new List<AbilityActionDeclaration> { new AbilityActionDeclaration { ActionId = Handler, EntryKind = "quick", BodyHandlerRef = Handler, PresentationRef = "r17.carrier" } } };
            var initial = new GameState { GameId = "r17.isolated", MapId = StaticMapDefinitions.ThreePlayerMapId, ContentPackHash = "r17.input.v1",
                RuleConfiguration = new GameRuleConfiguration { PlayerCount = 2, ContentPackHash = "r17.input.v1" },
                ContentDefinitions = new GameContentDefinitions { AbilitySources = new List<AbilitySourceDeclaration> { source },
                    Components = new List<ComponentContentDefinition> { new ComponentContentDefinition { DefinitionId = "core.player_marker", TypeId = "core.player_marker", QuantityPolicy = "finite" } } },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Resources = new ResourceSet { GoldVoucher = 20 } }, new PlayerState { PlayerId = 2 } } };
            state = new ItemZoneStateFactory().CreateForSetup(initial, StaticMapDefinitions.CreateThreePlayerMap(), new[] {
                new ItemRecipeEntry { EntryId = "cubes", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = mode == "empty" ? 2 : 3, PerPlayer = true, InitialZoneRole = "supply" },
                new ItemRecipeEntry { EntryId = "carrier", DefinitionId = "r17.carrier", TypeId = "entrepreneur.ability_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "entrepreneur_active" } }, new[] {
                new ItemCardZoneDeclaration { DefinitionId = "r17.carrier", CardZones = new List<CardZoneDefinition> { new CardZoneDefinition {
                    LocalZoneId = "tokens", ZoneTemplateId = "core.zone.card_cube_storage", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube },
                    AcceptedTypeIds = new List<string> { "core.player_marker" }, EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer",
                    CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Unordered, VisibilityPolicyId = ItemZoneVisibility.Public, CarrierExitPolicyId = "core.preserve_contents" } } } });
            state.Phase = GamePhase.ActionRound1; state.Round = 1; state.ActionRound = 1; state.CurrentPlayerId = state.StartPlayerId = 1;
            state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            card = state.ItemZones.Items.Single(i => i.DefinitionId == "r17.carrier" && i.ControllerSubject.PlayerId == 1).ItemId;
            storage = state.ItemZones.Zones.Single(z => z.ParentId == card && z.LocalZoneId == "tokens").ZoneId;
            supply = FindZone(state, "supply", 1).ZoneId;
            registry = new EffectRegistry { ContentPackHash = state.ContentPackHash };
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry); AbilityActionExecutor.Register(registry);
            string code = @"return function(ctx)
local stock={};for _,id in ipairs(ctx.payload.continuationContext.itemIds) do stock[#stock+1]={id=id} end
table.sort(stock,function(a,b)return a.id<b.id end)
local effects={}
local function move(ids,n)return Effect.MoveItem({apiVersion=3,player=ctx.playerId,itemIds=ids,expectedSource={zoneId='" + supply + @"'},
 destination={zoneId='" + storage + @"'},insertion={kind='unordered'},operationProfileId='core.player_marker_storage',requiredCount=n})end
" + (mode == "empty" || mode == "batch" ? "" : "effects[1]=Effect.MoveItem({apiVersion=3,player=ctx.playerId,itemIds={stock[3].id},expectedSource={zoneId='" + supply + "'},destination={zoneId='" + FindZone(state, "retired").ZoneId + "'},insertion={kind='unordered'},operationProfileId='core.player_marker_storage',requiredCount=1})\n") +
                (mode == "batch" ? "effects={move({stock[1].id},2)}" : @"for i=1,3 do
 local ids={};if stock[i] then ids[1]=stock[i].id end
 local leaf=move(ids,1)
 " + (mode == "wrapped" ? "leaf=Effect.Condition({ruleCondition=false,leftEffects={leaf},rightEffects={}})" : "") + @"
 effects[#effects+1]=leaf
end") + @"
effects[#effects+1]=Effect.GainResource({recipient=ctx.playerId,resourceType='Iron',amount=1})
return effects end";
            string hash = LuaContentHasher.ComputeSha256(code);
            var dispatcher = new LuaEffectEventDispatcher(registry);
            dispatcher.RegisterContinuation(new LuaHandlerDefinition(Completed, "EffectCompleted", "", new LuaScriptDefinition("r17.fixture", Ability, Completed, "1.0.0", code, hash),
                EffectHandlerRole.Continuation, contentInstanceId: "r17.carrier", abilityId: Ability));
            registry.RegisterAbilityHandler(Handler, hash, call => {
                var choice = EffectSpec.Create(LuaDomainEffectTypeIds.Choice, Object("player", NormalizedValue.CreateString("1"), "options", Strings(new[] { "3" }),
                    "minSelections", NormalizedValue.CreateInteger(1), "maxSelections", NormalizedValue.CreateInteger(1)), 1);
                choice.CompletionHandlerId = Completed; choice.ContinuationContentId = "r17.fixture"; choice.ContinuationContentInstanceId = "r17.carrier";
                choice.ContinuationAbilityId = Ability; choice.ContinuationDefinitionVersion = "1.0.0"; choice.ContinuationContentHash = hash;
                choice.ContinuationContext = Object("stage", NormalizedValue.CreateString("move"), "itemIds", Strings(state.ItemZones.Items.Where(i => i.ZoneId == supply).Select(i => i.ItemId).OrderBy(id => id)));
                return new[] { choice };
            }, "1.0.0");
            registry.FreezeAbilityCatalog(new[] { source });
        }
        private string Exercise(string mode)
        {
            Setup(mode);
            var option = registry.QueryActions(state, 1).Single(o => o.ActionId == Handler);
            string root = Engine.CreateRoot(AbilityActionExecutor.Create(option, "r17.command", Object()));
            Require(!Engine.RunUntilQuiescent().Faulted, state.EffectRuntime.LastFaultMessage);
            // 完成回调首个合法独立移动使后续一枚来源过期；不强改状态或交互revision。
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Require(Engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateString("3"), out string reason), reason);
            var report = Engine.RunUntilQuiescent();
            LastEvidence = JsonConvert.SerializeObject(new { mode, report.Faulted, state.EffectRuntime.LastFaultMessage,
                nodes = state.EffectRuntime.EffectNodes.Select(n => new { n.EffectId, n.ParentEffectId, n.EffectTypeId, n.Status, n.FailureReason, n.NormalizedResult }),
                invocation = state.EffectRuntime.AbilityActionInvocations, stored = state.ItemZones.Items.Count(i => i.ZoneId == storage), state.FindPlayer(1).Resources.Iron });
            return root;
        }
        [Test] public void IndependentSingleItemStockChangeKeepsSuccessfulSiblingsAndActionContinues()
        {
            string root = Exercise("shortage");
            Require(state.ItemZones.Items.Count(i => i.ZoneId == storage) == 2, LastEvidence);
            Require(state.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == TypeId && n.Status == EffectNodeStatus.Failed && n.FailureReason == "source_zone_changed") == 1, LastEvidence);
            Require(state.FindPlayer(1).Resources.Iron == 1, LastEvidence);
            Require(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status == EffectNodeStatus.Completed, LastEvidence);
        }
        [Test] public void EmptySingleItemListCompilesAndOnlyThatEffectFails()
        {
            string root = Exercise("empty");
            Require(state.ItemZones.Items.Count(i => i.ZoneId == storage) == 2, LastEvidence);
            Require(state.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == TypeId && n.Status == EffectNodeStatus.Failed && n.FailureReason == "insufficient_items") == 1, LastEvidence);
            Require(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status == EffectNodeStatus.Completed, LastEvidence);
        }
        [Test] public void ConditionConsumedFailureRemainsVisibleAndDoesNotFaultAction()
        { string root = Exercise("wrapped"); Require(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status == EffectNodeStatus.Completed, LastEvidence); }
        [Test] public void PermissionFailureStillFaultsActivatedAction()
        { Exercise("permission"); Require(state.EffectRuntime.LastFaultMessage.Contains("action_required_transfer_failed"), LastEvidence); }
        [Test] public void MultiItemRequiredTransferDoesNotBecomeAnExpectedSingleItemFailure()
        { Exercise("batch"); Require(state.EffectRuntime.LastFaultMessage.Contains("action_required_transfer_failed"), LastEvidence); }
    }
}
