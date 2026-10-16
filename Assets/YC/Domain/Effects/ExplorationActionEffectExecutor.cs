using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Domain.Travel;

namespace YC.Domain.Effects
{
    /// <summary>主要行动已确认的本地选择在权威工作副本内封存；最小探索仍负责原结算及一次行动消费。</summary>
    public static class ExplorationActionEffectExecutor
    {
        public const string TypeId = "action.exploration.selected";
        public const string DefinitionVersion = "1";

        public static void Register(EffectRegistry registry, IMapQueryService map)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (!registry.TryGet(TypeId, out _))
                registry.Register(new EffectRegistration(TypeId, context => Execute(context, map),
                    EffectExecutorKind.IntrinsicFlow, DefinitionVersion) { Validator = Validate });
        }

        public static EffectSpec Create(int playerId, string targetLocationId, IReadOnlyList<string> routeIds,
            IReadOnlyList<RouteRecipientChoice> recipients, string sourceId = "")
        {
            if (routeIds == null) throw new ArgumentNullException(nameof(routeIds));
            if (recipients == null) throw new ArgumentNullException(nameof(recipients));
            return new EffectSpec(TypeId, NormalizedValue.CreateObject(new List<NormalizedValueEntry> {
                Entry("targetLocation", NormalizedValue.CreateString(targetLocationId)),
                Entry("routeIds", NormalizedValue.CreateArray(routeIds.Select(NormalizedValue.CreateString).ToList())),
                Entry("recipients", NormalizedValue.CreateArray(recipients.Select(r => NormalizedValue.CreateObject(new List<NormalizedValueEntry> {
                    Entry("routeId", NormalizedValue.CreateString(r.RouteId)), Entry("subject", NormalizedValue.CreateString(r.ReceiverSubjectKey))
                })).ToList()))
            })) { PlayerId = playerId, SourceId = sourceId ?? "", DefinitionVersion = DefinitionVersion };
        }

        private static string Validate(EffectSpec spec)
        {
            var args = spec.NormalizedArguments;
            if (spec.PlayerId <= 0 || args?.Kind != NormalizedValueKind.Object ||
                args.Properties.Count != 3 || args.Properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 3 ||
                args.Properties.Any(p => p.Name != "targetLocation" && p.Name != "routeIds" && p.Name != "recipients") ||
                RouteSelectionSessionAuthority.Value(args, "targetLocation")?.Kind != NormalizedValueKind.String ||
                string.IsNullOrEmpty(RouteSelectionSessionAuthority.Text(args, "targetLocation")) ||
                RouteSelectionSessionAuthority.Value(args, "routeIds")?.Kind != NormalizedValueKind.Array ||
                RouteSelectionSessionAuthority.Value(args, "recipients")?.Kind != NormalizedValueKind.Array)
                return "exploration_preselection_arguments_invalid";
            var routes = RouteSelectionSessionAuthority.Value(args, "routeIds").Items;
            if (routes.Any(r => r.Kind != NormalizedValueKind.String || string.IsNullOrEmpty(r.StringValue)) ||
                routes.Select(r => r.StringValue).Distinct(StringComparer.Ordinal).Count() != routes.Count)
                return "exploration_preselection_routes_invalid";
            var recipients = RouteSelectionSessionAuthority.Value(args, "recipients").Items;
            if (recipients.Any(r => r?.Kind != NormalizedValueKind.Object || r.Properties.Count != 2 ||
                r.Properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 2 ||
                r.Properties.Any(p => p.Name != "routeId" && p.Name != "subject") ||
                RouteSelectionSessionAuthority.Value(r, "routeId")?.Kind != NormalizedValueKind.String ||
                RouteSelectionSessionAuthority.Value(r, "subject")?.Kind != NormalizedValueKind.String ||
                string.IsNullOrEmpty(RouteSelectionSessionAuthority.Text(r, "routeId")) ||
                string.IsNullOrEmpty(RouteSelectionSessionAuthority.Text(r, "subject"))) ||
                recipients.Select(r => RouteSelectionSessionAuthority.Text(r, "routeId")).Distinct(StringComparer.Ordinal).Count() != recipients.Count)
                return "exploration_preselection_recipients_invalid";
            return null;
        }

        private static EffectStepResult Execute(EffectExecutionContext context, IMapQueryService configuredMap)
        {
            var node = context.Node;
            if (node.FlowStage == "execution")
            {
                if (context.ChildNodes.Count != 1) return EffectStepResult.Failed("exploration_preselection_child_invalid");
                var child = context.ChildNodes[0];
                if (child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted) return EffectStepResult.Failed(child.FailureReason);
                if (child.Status != EffectNodeStatus.Completed) return EffectStepResult.NoProgress("等待探索结算完成。");
                return EffectStepResult.Completed(child.NormalizedResult.Clone());
            }
            if (node.FlowStage != "") return EffectStepResult.Failed("exploration_preselection_stage_invalid");
            var map = configuredMap ?? new MapQueryService(StaticMapDefinitions.Resolve(context.State.MapId));
            var player = context.State.FindPlayer(node.PlayerId);
            var seed = new RouteSelectionSession { PlayerId = node.PlayerId, Purpose = RouteSelectionPurpose.Exploration, ActionContextId = "main_action" };
            var eligible = RouteResourceSelectionEffectExecutor.GetSelectableLocations(context.State, seed, map);
            string target = RouteSelectionSessionAuthority.Text(node.NormalizedArguments, "targetLocation");
            if (player == null || !eligible.Contains(target)) return EffectStepResult.Failed("route_selection_target_unavailable");
            var authority = new RouteSelectionSessionAuthority(map);
            var options = new RouteSelectionOptions(RouteSelectionPurpose.Exploration, node.PlayerId, player.CityLocationId,
                eligible, 1, 1, false, "main_action");
            var opened = authority.TryOpen(context.State, node, options, out var session);
            if (!opened.IsValid) return EffectStepResult.Failed(opened.Reason);
            var prepared = authority.TryPrepareRequest(context.State, node, session, eligible, out var binding, out _);
            if (!prepared.IsValid) return EffectStepResult.Failed(prepared.Reason);
            var routes = RouteSelectionSessionAuthority.Value(node.NormalizedArguments, "routeIds").Items.Select(v => v.StringValue).ToArray();
            var recipients = RouteSelectionSessionAuthority.Value(node.NormalizedArguments, "recipients").Items.Select(v =>
                new RouteRecipientChoice(RouteSelectionSessionAuthority.Text(v, "routeId"), RouteSelectionSessionAuthority.Text(v, "subject"))).ToArray();
            var draft = new RouteSelectionDraft(session.SessionId, 0, routes, new[] { target }, recipients);
            var finalized = new RouteResourceSelectionService(map).TryFinalize(context.State, session, binding, draft, eligible, out var commitment);
            if (!finalized.IsValid) return EffectStepResult.Failed(finalized.Reason);
            var sealedResult = authority.Seal(context.State, node, session, commitment);
            if (!sealedResult.IsValid) return EffectStepResult.Failed(sealedResult.Reason);
            return EffectStepResult.Continue("execution").AddChild(ExplorationEffectSpecFactory.ExploreFinalized(node.PlayerId,
                node.EffectId, session.SessionId, commitment.CommitmentId, commitment.AcceptedAtStateRevision, target,
                "", false, true, node.SourceId));
        }

        private static NormalizedValueEntry Entry(string key, NormalizedValue value) => new NormalizedValueEntry { Name = key, Value = value };
    }
}
