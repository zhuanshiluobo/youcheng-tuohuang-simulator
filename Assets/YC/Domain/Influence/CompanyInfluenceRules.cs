using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Influence
{
    /// <summary>公司没有玩家所有者；持约资格及区控只读真实实例位置。</summary>
    public sealed class CompanyInfluenceRules
    {
        public const string CompanyId = "stonehoove_security";
        public const string ContractDefinitionId = "infected.contract.lifetime_security";
        private readonly IMapQueryService map;
        private readonly Func<GameState, string, bool> placementAllowed;
        public CompanyInfluenceRules(IMapQueryService map = null, Func<GameState, string, bool> placementAllowed = null)
        { this.map = map; this.placementAllowed = placementAllowed; }

        public IReadOnlyList<string> GetPlacementSlotIds(GameState state) => new InfluenceSubjectQuery().GetLegalResourcePointSlotIds(
            state, map ?? new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)), slot => InfluenceLockRules.PlacementAllowed(state, slot) && placementAllowed?.Invoke(state, slot) != false);

        public static bool HasContract(GameState state, int playerId) => state?.FindPlayer(playerId) != null && state.ItemZones.Items.Any(item =>
            item.DefinitionId == ContractDefinitionId && item.TypeId == "core.contract_card" &&
            state.ItemZones.Zones.Any(zone => zone.ZoneId == item.ZoneId && zone.TemplateId == "core.zone.player_awards" && zone.OwnerPlayerId == playerId));

        public int GetControlContribution(GameState state, string regionId, int playerId)
        {
            if (!HasContract(state, playerId)) return 0;
            var query = new ItemZoneQuery();
            return state.ItemZones.Items.Count(item => item.OwnerSubject.Kind == ItemSubjectKind.Company && item.OwnerSubject.CompanyId == CompanyId &&
                query.IsInfluence(state, item.ItemId) && InfluenceSubjectQuery.GetRegionId(state, item.ItemId) == regionId) / 2;
        }
    }

    /// <summary>取得真实奖励实例并发布唯一领域事件；具体授勋生命周期由内容订阅接续。</summary>
    public static class RewardAcquisitionFlow
    {
        public const string TypeId = "effect.reward.acquire";
        public const string InteractionTypeId = "reward.choose";
        public const string AcquiredEventTypeId = "reward.acquired";

        public static EffectStepResult Execute(EffectExecutionContext context)
        {
            var node = context.Node;
            string role = Text(node.NormalizedArguments, "poolRole");
            if (role != "awards" && role != "contracts") return EffectStepResult.Failed("reward_pool_rejected");
            if (Flag(node.ContinuationData, "acquisitionCompleted")) return EffectStepResult.Completed(node.NormalizedResult);
            var source = context.State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.reward_pool" && z.LocalZoneId == role);
            var destination = context.State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.player_awards" && z.OwnerPlayerId == node.PlayerId);
            if (role == "contracts" && context.State.ItemZones.Items.Any(i => i.ZoneId == destination.ZoneId && i.TypeId == "core.contract_card"))
                return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(false), "reasonCode", NormalizedValue.CreateString("limit_reached")));
            string type = role == "awards" ? "core.award_card" : "core.contract_card";
            var candidates = context.State.ItemZones.Items.Where(i => i.ZoneId == source.ZoneId && i.TypeId == type).OrderBy(i => i.ItemId, StringComparer.Ordinal).ToList();
            if (candidates.Count == 0) return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(false), "reasonCode", NormalizedValue.CreateString("no_candidates")));
            if (node.FlowStage == "" && role == "awards")
            {
                var request = new EffectInteractionSpec { InteractionTypeId = InteractionTypeId, AnsweringPlayerId = node.PlayerId,
                    Visibility = "owner", PromptKey = "reward.choose", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1 };
                request.CandidateIds.AddRange(candidates.Select(i => i.ItemId));
                return EffectStepResult.Continue("choose_reward").AddInteraction(PrepareInteraction(context, request));
            }
            if (node.FlowStage != "" && node.FlowStage != "choose_reward") return EffectStepResult.Failed("reward_stage_invalid");
            var selected = role == "contracts" ? candidates[0] : candidates.SingleOrDefault(i => i.ItemId == Answer(context));
            if (selected == null) return EffectStepResult.Failed("reward_candidate_changed");
            new ItemTransferService().RelocateItems(context, new ItemTransferRequest { ExecutingPlayerId = node.PlayerId,
                ItemIds = new List<string> { selected.ItemId }, ExpectedSource = new ItemPositionRef { ZoneId = source.ZoneId },
                Destination = new ZoneRef { ZoneId = destination.ZoneId }, InsertionKind = "unordered", RequiredCount = 1,
                OperationProfileId = "core.reward_card_transfer", CarrierPolicyId = "preserve" });
            Set(node, "selectedRewardItemId", NormalizedValue.CreateString(selected.ItemId));
            Set(node, "acquisitionCompleted", NormalizedValue.CreateBoolean(true));
            var result = Object("changed", NormalizedValue.CreateBoolean(true), "rewardItemId", NormalizedValue.CreateString(selected.ItemId),
                "definitionId", NormalizedValue.CreateString(selected.DefinitionId), "poolRole", NormalizedValue.CreateString(role), "playerId", NormalizedValue.CreateInteger(node.PlayerId));
            return EffectStepResult.Completed(result).WithFlowStage("finished").AddEvent(new EffectEventRequest {
                EventType = AcquiredEventTypeId, RouteKey = selected.DefinitionId, PlayerId = node.PlayerId, Visibility = "public", Payload = result.Clone() });
        }
    }
}
