using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Exploration;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;

namespace YC.Domain.Effects
{
    public static class RouteResourceSelectionEffectExecutor
    {
        public const string TypeId = "effect.route_resource.select";
        public const string DefinitionVersion = "1";
        public static void Register(EffectRegistry registry)
        {
            Register(registry, null);
        }

        public static void Register(EffectRegistry registry, IMapQueryService mapQuery)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            // 保留首个注册环境；注入地图须早于公共/回合默认注册。
            if (!registry.TryGet(TypeId, out _)) registry.Register(new EffectRegistration(TypeId,
                context => Execute(context, mapQuery), EffectExecutorKind.IntrinsicFlow, DefinitionVersion));
        }

        public static EffectSpec CreateExploration(int playerId, string sourceId = "", bool consumeMainAction = false, string targetLocationId = "")
        {
            var spec = Create(playerId, RouteSelectionPurpose.Exploration, sourceId, "", consumeMainAction);
            if (!string.IsNullOrEmpty(targetLocationId)) RouteSelectionSessionAuthority.Set(spec.NormalizedArguments, "targetLocationUpperBound", NormalizedValue.CreateString(targetLocationId));
            return spec;
        }
        public static EffectSpec CreateHarvest(int playerId, bool extra, string sourceId = "", string budgetOwnerEffectId = "")
        {
            var spec = Create(playerId, extra ? RouteSelectionPurpose.ExtraHarvest : RouteSelectionPurpose.NormalHarvest, sourceId, budgetOwnerEffectId, false);
            RouteSelectionSessionAuthority.Set(spec.NormalizedArguments, "collectionPlanProtocol", NormalizedValue.CreateInteger(CollectionPlanService.ProtocolVersion));
            return spec;
        }
        private static EffectSpec Create(int player, RouteSelectionPurpose purpose, string source, string budgetOwner, bool consume)
        {
            var args = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
            RouteSelectionSessionAuthority.Set(args, "purpose", NormalizedValue.CreateInteger((int)purpose));
            RouteSelectionSessionAuthority.Set(args, "budgetOwnerEffectId", NormalizedValue.CreateString(budgetOwner));
            RouteSelectionSessionAuthority.Set(args, "consumeMainAction", NormalizedValue.CreateBoolean(consume));
            return new EffectSpec(TypeId, args) { PlayerId = player, SourceId = source, DefinitionVersion = DefinitionVersion, Visibility = "owner" };
        }

        public static IReadOnlyList<string> GetSelectableLocations(GameState state, RouteSelectionSession session, IMapQueryService map)
        {
            if (session.Purpose == RouteSelectionPurpose.Exploration)
                return new ExplorationService(map).GetSelectableLocationIds(state, session.PlayerId, session.ActionContextId != "main_action");
            return new ResourceCollectionService(map).GetSelectableLocationIds(state, session.PlayerId, session.Purpose == RouteSelectionPurpose.ExtraHarvest);
        }

        private static EffectStepResult Execute(EffectExecutionContext context, IMapQueryService configuredMap)
        {
            var map = configuredMap;
            if (map == null)
            {
                try { map = new MapQueryService(StaticMapDefinitions.Resolve(context.State.MapId)); }
                catch (ArgumentException) { return EffectStepResult.Failed("route_selection_map_unknown"); }
            }
            if (map.Map == null || string.IsNullOrEmpty(context.State.MapId) ||
                !string.Equals(map.Map.MapId, context.State.MapId, StringComparison.Ordinal))
                return EffectStepResult.Failed("route_selection_map_mismatch");
            var node = context.Node;
            var marker = RouteSelectionSessionAuthority.Value(node.NormalizedArguments, "collectionPlanProtocol");
            if (marker != null && (marker.Kind != NormalizedValueKind.Integer ||
                RouteSelectionSessionAuthority.Integer(node.NormalizedArguments,"collectionPlanProtocol",-1) != CollectionPlanService.ProtocolVersion ||
                RouteSelectionSessionAuthority.Integer(node.NormalizedArguments,"purpose",-1) == (int)RouteSelectionPurpose.Exploration))
                return EffectStepResult.Failed("collection_plan_protocol_invalid");
            if (node.FlowStage == "execution")
            {
                foreach (var child in context.ChildNodes)
                {
                    if (child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted) return EffectStepResult.Failed(child.FailureReason);
                    if (child.Status != EffectNodeStatus.Completed) return EffectStepResult.NoProgress("等待选择后的操作完成。");
                }
                return EffectStepResult.Completed();
            }
            var authority = new RouteSelectionSessionAuthority(map, context.Registry);
            RouteSelectionSession session;
            RouteSelectionRequestBinding binding;
            if (node.FlowStage == "")
            {
                var player = context.State.FindPlayer(node.PlayerId);
                int purposeValue = RouteSelectionSessionAuthority.Integer(node.NormalizedArguments, "purpose", -1);
                if (player == null || purposeValue < 0 || purposeValue > 2) return EffectStepResult.Failed("route_selection_arguments_invalid");
                var purpose = (RouteSelectionPurpose)purposeValue;
                bool consume = RouteSelectionSessionAuthority.Value(node.NormalizedArguments, "consumeMainAction")?.BooleanValue == true;
                var seed = new RouteSelectionSession { PlayerId = node.PlayerId, Purpose = purpose, ActionContextId = consume ? "main_action" : "granted" };
                var eligible = GetSelectableLocations(context.State, seed, map);
                string targetUpperBound = RouteSelectionSessionAuthority.Text(node.NormalizedArguments, "targetLocationUpperBound");
                if (targetUpperBound.Length > 0) eligible = Array.AsReadOnly(eligible.Where(id => id == targetUpperBound).ToArray());
                if (purpose == RouteSelectionPurpose.Exploration && eligible.Count == 0) return EffectStepResult.Completed(NormalizedValue.CreateBoolean(false));
                var options = new RouteSelectionOptions { SchemaVersion = purpose != RouteSelectionPurpose.Exploration && RouteSelectionSessionAuthority.Integer(node.NormalizedArguments, "collectionPlanProtocol", 1) == CollectionPlanService.ProtocolVersion ? CollectionPlanService.ProtocolVersion : 1, PlayerId = node.PlayerId, Purpose = purpose, StartLocationId = player.CityLocationId, ActionContextId = seed.ActionContextId,
                    SelectableLocationUpperBound = eligible, MinimumLocations = purpose == RouteSelectionPurpose.Exploration ? 1 : 0,
                    MaximumLocations = purpose == RouteSelectionPurpose.Exploration ? 1 : eligible.Count, AllowCancelBeforeCommit = purpose == RouteSelectionPurpose.Exploration,
                    ExistingStartBudgetBindingId = RouteSelectionSessionAuthority.Text(node.NormalizedArguments, "budgetOwnerEffectId") };
                if (options.SchemaVersion == CollectionPlanService.ProtocolVersion)
                {
                    // 本次来源边界为地图；C仍逐次按当前资格查。影响力移点后可刷新新合法点，而非锁死开页时C。
                    options.SelectableLocationUpperBound = targetUpperBound.Length > 0
                        ? Array.AsReadOnly(new[] { targetUpperBound })
                        : Array.AsReadOnly(map.Map.Locations.Select(l => l.LocationId).ToArray());
                    options.MaximumLocations = options.SelectableLocationUpperBound.Count;
                }
                var opened = authority.TryOpen(context.State, node, options, out session);
                if (!opened.IsValid) return EffectStepResult.Failed(opened.Reason);
                return Wait(context, authority, session, eligible, "", map);
            }
            var restored = authority.TryRestore(context.State, node, out session, out binding);
            if (!restored.IsValid) return EffectStepResult.Failed(restored.Reason);
            var selectedRequest = context.State.EffectRuntime.InteractionRequests.Where(r => r.OwnerEffectId == node.EffectId && r.InteractionTypeId == "route_resource.selection" && r.Status == "answered").OrderByDescending(r => r.AnsweredAtRevision).FirstOrDefault();
            if (selectedRequest == null) return EffectStepResult.NoProgress("等待最终选路答案。");
            var answerValue = selectedRequest.NormalizedAnswer;
            var ids = answerValue.Kind == NormalizedValueKind.Array ? answerValue.Items.Select(v => v.Kind == NormalizedValueKind.StableReference ? v.ReferenceId : v.StringValue).ToArray()
                : new[] { answerValue.Kind == NormalizedValueKind.StableReference ? answerValue.ReferenceId : answerValue.StringValue };
            var parsed = new RouteSelectionAnswerCodec().TryParse(selectedRequest, ids, out var answer);
            var currentEligible = GetSelectableLocations(context.State, session, map).Where(id => session.SelectableLocationUpperBound.Contains(id)).ToArray();
            if (!parsed.IsValid || selectedRequest.CandidateSetVersion != binding.CandidateSetVersion) return Wait(context, authority, session, currentEligible, parsed.Reason, map);
            if (answer.IsCancellation)
            {
                var cancelled = authority.Cancel(context.State, node, session);
                return cancelled.IsValid ? EffectStepResult.Completed(NormalizedValue.CreateBoolean(false)) : EffectStepResult.Failed(cancelled.Reason);
            }
            var finalized = new RouteResourceSelectionService(map, context.Registry).TryFinalize(context.State, session, binding, answer.Draft, currentEligible, out var commitment);
            if (!finalized.IsValid) return Wait(context, authority, session, currentEligible, finalized.Reason, map);
            var sealedResult = authority.Seal(context.State, node, session, commitment);
            if (!sealedResult.IsValid) return EffectStepResult.Failed(sealedResult.Reason);
            if (session.Purpose == RouteSelectionPurpose.Exploration)
            {
                // 新消费者只传封存选择引用，托管执行器读取authority的可信计划。
                return EffectStepResult.Continue("execution").AddChild(ExplorationEffectSpecFactory.ExploreFinalized(node.PlayerId,
                    node.EffectId, session.SessionId, commitment.CommitmentId, commitment.AcceptedAtStateRevision, commitment.AcceptedPreview.SelectedLocationIds[0],
                    "", session.ActionContextId != "main_action", session.ActionContextId == "main_action", node.SourceId));
            }
            var collection = new ResourceCollectionService(map) { RuleRegistry = context.Registry };
            var result = collection.CollectPlan(context.State, session, commitment);
            if (!result.Succeeded) return EffectStepResult.Failed(result.Validation.Reason);
            var normalized = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
            RouteSelectionSessionAuthority.Set(normalized, "changed", NormalizedValue.CreateBoolean(result.LocationIds.Count > 0));
            RouteSelectionSessionAuthority.Set(normalized, "collectedLocationIds", NormalizedValue.CreateArray(result.LocationIds.Select(NormalizedValue.CreateString).ToList()));
            RouteSelectionSessionAuthority.Set(normalized, "paidRouteIds", NormalizedValue.CreateArray(result.Payments.Select(p => NormalizedValue.CreateString(p.RouteId)).ToList()));
            return EffectStepResult.Completed(normalized);
        }

        private static EffectStepResult Wait(EffectExecutionContext context, RouteSelectionSessionAuthority authority, RouteSelectionSession session, IReadOnlyList<string> eligible, string diagnostic, IMapQueryService map)
        {
            var prepared = authority.TryPrepareRequest(context.State, context.Node, session, eligible, out var binding, out var candidates);
            if (!prepared.IsValid) return EffectStepResult.Failed(prepared.Reason);
            var parameters = RouteSelectionSessionAuthority.SessionRecord(session);
            if (diagnostic.Length > 0) RouteSelectionSessionAuthority.Set(parameters, "selectionDiagnostic", NormalizedValue.CreateString(diagnostic));
            var request = new EffectInteractionSpec { InteractionTypeId = "route_resource.selection", AnsweringPlayerId = session.PlayerId, Visibility = "owner", PromptKey = "route_resource.selection", PromptParameters = parameters,
                CandidateSetId = binding.CandidateSetId, CandidateSetVersion = binding.CandidateSetVersion, AnswerSchema = "candidate_ids", MinSelections = 1,
                MaxSelections = Math.Min(127, 1 + new RouteTollService(map).ReadRouteFacts(context.State, session.PlayerId).Count(r => r.FreeReason == RouteFreeReason.None) + session.MaximumLocations) };
            request.CandidateIds.AddRange(candidates);
            return EffectStepResult.Continue("selecting").AddInteraction(request);
        }
    }
}
