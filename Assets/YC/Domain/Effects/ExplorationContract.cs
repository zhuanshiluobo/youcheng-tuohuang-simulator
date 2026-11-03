using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Domain.Travel;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    // API-14 / 4.6：公开输入只描述选择范围或路线意图；封存和资金收据始终由Host生成。
    public static class ExplorationContract
    {
        public const string Version = "exploration.v3.1";
        public static readonly string[] Fields = { "kind", "effectTypeId", "apiVersion", "targetMode", "player", "consumeMainAction", "targetLocationId", "routeIds", "recipients" };
        public static bool IsV3(NormalizedValue args) => Value(args, "apiVersion") != null || Value(args, "targetMode") != null;
        public static string ValidateArguments(NormalizedValue args)
        {
            if (args?.Kind != NormalizedValueKind.Object || !args.IsValid() || args.Properties.Any(p => !Fields.Contains(p.Name))) return "explore_contract_fields_invalid";
            if (Value(args, "kind") != null && Text(args, "kind") != "effect_spec" || Value(args, "effectTypeId") != null && Text(args, "effectTypeId") != ExplorationEffectTypeIds.Explore) return "explore_contract_fields_invalid";
            if (Value(args, "apiVersion")?.Kind != NormalizedValueKind.Integer || Value(args, "apiVersion").IntegerValue != 3) return "explore_contract_version_invalid";
            string mode = Text(args, "targetMode");
            if (Value(args, "targetMode")?.Kind != NormalizedValueKind.String || mode != "select" && mode != "explicit") return "explore_contract_mode_invalid";
            string player = Text(args, "player");
            if (Value(args, "player")?.Kind != NormalizedValueKind.String || !int.TryParse(player, NumberStyles.None, CultureInfo.InvariantCulture, out int id) ||
                id <= 0 || id.ToString(CultureInfo.InvariantCulture) != player) return "explore_contract_player_invalid";
            if (Value(args, "consumeMainAction")?.Kind != NormalizedValueKind.Boolean) return "explore_contract_action_policy_required";
            var target = Value(args, "targetLocationId");
            if (target != null && (target.Kind != NormalizedValueKind.String || target.StringValue.Length == 0)) return "explore_contract_target_invalid";
            if (mode == "select") return Value(args, "routeIds") == null && Value(args, "recipients") == null ? "" : "explore_contract_modes_mixed";
            if (target == null) return "explore_contract_plan_required";
            var routes = Value(args, "routeIds"); var recipients = Value(args, "recipients");
            if (routes?.Kind != NormalizedValueKind.Array || routes.Items.Any(r => r.Kind != NormalizedValueKind.String || r.StringValue.Length == 0) ||
                routes.Items.Select(r => r.StringValue).Distinct(StringComparer.Ordinal).Count() != routes.Items.Count || recipients?.Kind != NormalizedValueKind.Array)
                return "explore_contract_plan_invalid";
            if (recipients.Items.Any(r => r.Kind != NormalizedValueKind.Object || r.Properties.Count != 2 || r.Properties.Any(p => p.Name != "routeId" && p.Name != "subject") ||
                Value(r, "routeId")?.Kind != NormalizedValueKind.String || Value(r, "subject")?.Kind != NormalizedValueKind.String ||
                Text(r, "routeId").Length == 0 || Text(r, "subject").Length == 0 || !routes.Items.Any(v => v.StringValue == Text(r, "routeId"))) ||
                recipients.Items.Select(r => Text(r, "routeId")).Distinct(StringComparer.Ordinal).Count() != recipients.Items.Count) return "explore_contract_recipients_invalid";
            return "";
        }

        public static string ValidateSpec(EffectSpec spec)
        {
            string error = ValidateArguments(spec?.NormalizedArguments);
            if (error.Length > 0) return error;
            return spec.PlayerId.ToString(CultureInfo.InvariantCulture) == Text(spec.NormalizedArguments, "player") && !spec.AllowDecline &&
                (spec.DecisionPlayerId < 0 || spec.DecisionPlayerId == spec.PlayerId) && spec.Children.Count == 0 && spec.NestedEffectGroups.Count == 0 ? "" : "explore_contract_controls_invalid";
        }
        public static string ValidateSavedSpec(EffectSpecRuntimeState spec)
        {
            if (spec.NestedEffects.Count != 0 || spec.NestedGroupSizes.Count != 0) return "explore_contract_controls_invalid";
            return ValidateSpec(new EffectSpec(spec.EffectTypeId, spec.NormalizedArguments) {
                PlayerId = spec.PlayerId, AllowDecline = spec.AllowDecline, DecisionPlayerId = spec.DecisionPlayerId });
        }
        public static EffectSpec Select(int player, bool consumeMainAction, string targetLocationId = "", string sourceId = "")
        {
            var args = Object("apiVersion", NormalizedValue.CreateInteger(3), "targetMode", NormalizedValue.CreateString("select"),
                "player", NormalizedValue.CreateString(player.ToString(CultureInfo.InvariantCulture)), "consumeMainAction", NormalizedValue.CreateBoolean(consumeMainAction));
            if (targetLocationId.Length > 0) RouteSelectionSessionAuthority.Set(args, "targetLocationId", NormalizedValue.CreateString(targetLocationId));
            return new EffectSpec(ExplorationEffectTypeIds.Explore, args) { PlayerId = player, SourceId = sourceId };
        }
        public static EffectSpec Explicit(int player, string targetLocationId, IReadOnlyList<string> routes, IReadOnlyList<RouteRecipientChoice> recipients,
            bool consumeMainAction, string sourceId = "")
        {
            if (routes == null) throw new ArgumentNullException(nameof(routes));
            if (recipients == null) throw new ArgumentNullException(nameof(recipients));
            var spec = Select(player, consumeMainAction, targetLocationId, sourceId);
            RouteSelectionSessionAuthority.Set(spec.NormalizedArguments, "targetMode", NormalizedValue.CreateString("explicit"));
            RouteSelectionSessionAuthority.Set(spec.NormalizedArguments, "routeIds", NormalizedValue.CreateArray(routes.Select(NormalizedValue.CreateString).ToList()));
            RouteSelectionSessionAuthority.Set(spec.NormalizedArguments, "recipients", NormalizedValue.CreateArray(recipients.Select(r => Object(
                "routeId", NormalizedValue.CreateString(r.RouteId), "subject", NormalizedValue.CreateString(r.ReceiverSubjectKey))).ToList()));
            return spec;
        }
        private static string Digest(EffectNodeRuntimeState node)
        {
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(Version + "|" + node.EffectId + "|" + node.PlayerId + "|" + node.SourceId + "|" + node.NormalizedArguments.ToDeterministicString())).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }
        internal static EffectStepResult Execute(EffectExecutionContext context, IMapQueryService map)
        {
            var node = context.Node;
            if (Value(node.ContinuationData, "exploreContractProtocolVersion") == null)
            {
                if (!TryValidateNode(context.State, node, out string initialError, map)) throw new KernelException("explore_contract_state_invalid", initialError);
                RouteSelectionSessionAuthority.Set(node.ContinuationData, "exploreContractProtocolVersion", NormalizedValue.CreateInteger(1));
                RouteSelectionSessionAuthority.Set(node.ContinuationData, "exploreContractArgumentsDigest", NormalizedValue.CreateString(Digest(node)));
            }
            if (!TryValidateNode(context.State, node, out string error, map)) throw new KernelException("explore_contract_state_invalid", error);
            bool select = Text(node.NormalizedArguments, "targetMode") == "select";
            if (node.FlowStage.Length == 0)
            {
                if (select) return EffectStepResult.Continue("selection").AddChild(RouteResourceSelectionEffectExecutor.CreateExploration(node.PlayerId, node.SourceId,
                    Flag(node.NormalizedArguments, "consumeMainAction"), Text(node.NormalizedArguments, "targetLocationId")));
                var preselected = ExplorationActionEffectExecutor.ExecutePreselected(context, map, Text(node.NormalizedArguments, "targetLocationId"),
                    Value(node.NormalizedArguments, "routeIds").Items.Select(v => v.StringValue).ToArray(),
                    Value(node.NormalizedArguments, "recipients").Items.Select(r => new RouteRecipientChoice(Text(r, "routeId"), Text(r, "subject"))).ToArray(), Flag(node.NormalizedArguments, "consumeMainAction"));
                return preselected.PendingOutcome == EffectPendingOutcome.Failed
                    ? EffectStepResult.Failed(preselected.FailureReason, Result("failed", "", "", "", preselected.FailureReason)) : preselected;
            }
            var children = OwnChildren(context.State, node);
            if (children.Count != 1) throw new KernelException("explore_contract_child_invalid", "探索流程归属无效。");
            var child = children[0];
            if (child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted)
                return EffectStepResult.Failed(child.FailureReason, Result("failed", "", "", "", child.FailureReason));
            if (child.Status != EffectNodeStatus.Completed) return EffectStepResult.NoProgress("等待探索、事件响应与路费交付完成。");
            if (!TryResult(context.State, node, map, out var result)) throw new KernelException("explore_contract_result_invalid", "探索结算结果缺少权威依据。");
            return EffectStepResult.Completed(result);
        }

        private static List<EffectNodeRuntimeState> OwnChildren(GameState state, EffectNodeRuntimeState node) => node.ChildEffectIds.Select(id => state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == id))
            .Where(n => n != null && !state.EffectRuntime.DispatchReceipts.Any(r => r.Succeeded && r.AttachedEffectIds.Contains(n.EffectId))).ToList();

        public static bool TryValidateNode(GameState state, EffectNodeRuntimeState node, out string error, IMapQueryService map = null)
        {
            error = "";
            if (node.EffectTypeId != ExplorationEffectTypeIds.Explore) return true;
            var protocol = Value(node.ContinuationData, "exploreContractProtocolVersion");
            if (!IsV3(node.NormalizedArguments))
            {
                if (protocol != null || node.FlowStage == "selection" || node.FlowStage == "execution") { error = "explore_contract_mode_missing"; return false; }
                return true;
            }
            error = ValidateArguments(node.NormalizedArguments); if (error.Length > 0) return false;
            error = "explore_contract_state_invalid";
            if (Text(node.NormalizedArguments, "player") != node.PlayerId.ToString(CultureInfo.InvariantCulture) || node.AllowDecline || node.DecisionPlayerId != node.PlayerId ||
                node.NestedEffects.Count != 0 || node.NestedGroupSizes.Count != 0 || state.EffectRuntime.InteractionRequests.Any(r => r.OwnerEffectId == node.EffectId)) return false;
            if (protocol == null)
            {
                bool initial = node.FlowStage == "" && new[] { EffectNodeStatus.Created, EffectNodeStatus.Ready, EffectNodeStatus.Running }.Contains(node.Status) &&
                    node.PendingOutcome == EffectPendingOutcome.None && node.NormalizedResult?.Kind == NormalizedValueKind.Null && node.ContinuationData?.Kind == NormalizedValueKind.Object &&
                    node.ContinuationData.Properties.Count == 0 && node.ChildEffectIds.Count == 0;
                if (initial) error = ""; return initial;
            }
            if (protocol.Kind != NormalizedValueKind.Integer || protocol.IntegerValue != 1 || Text(node.ContinuationData, "exploreContractArgumentsDigest") != Digest(node) ||
                !new[] { "", "selection", "execution" }.Contains(node.FlowStage) || node.ChildEffectIds.Distinct().Count() != node.ChildEffectIds.Count ||
                node.ChildEffectIds.Any(id => !state.EffectRuntime.EffectNodes.Any(n => n.EffectId == id && n.ParentEffectId == node.EffectId))) return false;
            var children = OwnChildren(state, node); bool select = Text(node.NormalizedArguments, "targetMode") == "select";
            if (node.FlowStage == "")
            {
                if (children.Count != 0) return false;
            }
            else
            {
                if (children.Count != 1 || children[0].PlayerId != node.PlayerId || children[0].DecisionPlayerId != node.PlayerId || children[0].AllowDecline ||
                    children[0].NestedEffects.Count != 0 || children[0].NestedGroupSizes.Count != 0 || children[0].SourceId != node.SourceId || (node.FlowStage == "selection") != select) return false;
                if (select)
                {
                    var expected = RouteResourceSelectionEffectExecutor.CreateExploration(node.PlayerId, node.SourceId, Flag(node.NormalizedArguments, "consumeMainAction"), Text(node.NormalizedArguments, "targetLocationId"));
                    if (children[0].EffectTypeId != expected.EffectTypeId || children[0].DefinitionVersion != RouteResourceSelectionEffectExecutor.DefinitionVersion ||
                        children[0].NormalizedArguments.ToDeterministicString() != expected.NormalizedArguments.ToDeterministicString()) return false;
                    var selector = children[0];
                    if (Value(selector.ContinuationData, "routeSelection") != null)
                    {
                        if (!new RouteSelectionSessionAuthority(map).TryRestore(state, selector, out var session, out _).IsValid ||
                            session.SchemaVersion != 1 || session.Purpose != RouteSelectionPurpose.Exploration || !session.AllowCancelBeforeCommit ||
                            session.MinimumLocations != 1 || session.MaximumLocations != 1 || session.ActionContextId != (Flag(node.NormalizedArguments, "consumeMainAction") ? "main_action" : "granted") ||
                            Text(node.NormalizedArguments, "targetLocationId").Length > 0 && session.SelectableLocationUpperBound.Any(id => id != Text(node.NormalizedArguments, "targetLocationId"))) return false;
                    }
                    var settlements = OwnChildren(state, selector);
                    if (settlements.Count > 1 || selector.FlowStage == "execution" && settlements.Count != 1 || settlements.Any(actual => !ValidateSettlement(state, node, selector, actual, map))) return false;
                }
                else if (!ValidateSettlement(state, node, node, children[0], map)) return false;
            }
            if (node.Status == EffectNodeStatus.Completed || node.PendingOutcome == EffectPendingOutcome.Completed)
            {
                if (!TryResult(state, node, map, out var result) || result.ToDeterministicString() != node.NormalizedResult?.ToDeterministicString()) { error = "explore_contract_result_invalid"; return false; }
            }
            error = ""; return true;
        }

        private static bool ValidateSettlement(GameState state, EffectNodeRuntimeState root, EffectNodeRuntimeState owner, EffectNodeRuntimeState actual, IMapQueryService map)
        {
            if (actual.EffectTypeId != ExplorationEffectTypeIds.Explore || actual.DefinitionVersion != ExplorationEffectExecutor.DefinitionVersion || IsV3(actual.NormalizedArguments) ||
                actual.ParentEffectId != owner.EffectId || actual.PlayerId != root.PlayerId || actual.DecisionPlayerId != root.PlayerId || actual.AllowDecline || actual.SourceId != root.SourceId) return false;
            var authority = new RouteSelectionSessionAuthority(map);
            if (!authority.TryResolveFinalized(state, owner.EffectId, out var session, out var commitment).IsValid || session.Purpose != RouteSelectionPurpose.Exploration ||
                session.ActionContextId != (Flag(root.NormalizedArguments, "consumeMainAction") ? "main_action" : "granted") || commitment.AcceptedPreview.SelectedLocationIds.Count != 1) return false;
            string target = commitment.AcceptedPreview.SelectedLocationIds[0], limit = Text(root.NormalizedArguments, "targetLocationId");
            if (limit.Length > 0 && target != limit) return false;
            var expected = ExplorationEffectSpecFactory.ExploreFinalized(root.PlayerId, owner.EffectId, session.SessionId, commitment.CommitmentId,
                commitment.AcceptedAtStateRevision, target, "", !Flag(root.NormalizedArguments, "consumeMainAction"), Flag(root.NormalizedArguments, "consumeMainAction"), root.SourceId);
            return expected.NormalizedArguments.ToDeterministicString() == actual.NormalizedArguments.ToDeterministicString();
        }
        private static bool TryResult(GameState state, EffectNodeRuntimeState node, IMapQueryService map, out NormalizedValue result)
        {
            result = null; var children = OwnChildren(state, node); if (children.Count != 1 || children[0].Status != EffectNodeStatus.Completed) return false;
            var owner = node; var actual = children[0];
            if (Text(node.NormalizedArguments, "targetMode") == "select")
            {
                owner = children[0];
                var actuals = OwnChildren(state, owner);
                if (actuals.Count == 0)
                {
                    if (owner.NormalizedResult?.Kind != NormalizedValueKind.Boolean || owner.NormalizedResult.BooleanValue) return false;
                    bool cancelled = Value(owner.ContinuationData, "routeSelection") != null;
                    if (cancelled && (!new RouteSelectionSessionAuthority(map).TryRestore(state, owner, out var session, out _).IsValid || session.Stage != RouteSelectionStage.Cancelled)) return false;
                    result = Result(cancelled ? "cancelled" : "no_legal_target", "", "", "", ""); return true;
                }
                if (actuals.Count != 1) return false;
                actual = actuals[0];
            }
            if (actual.Status != EffectNodeStatus.Completed || !ValidateSettlement(state, node, owner, actual, map) ||
                !RouteTollEscrowService.TryReadReceipt(actual, out var receipt, out _) || receipt.State != "settled" || Text(actual.NormalizedResult, "outcome") != "completed" ||
                Text(actual.NormalizedResult, "targetLocationId") != Value(actual.NormalizedArguments, "targetLocation")?.ReferenceId ||
                Text(actual.NormalizedResult, "tollSettlementReceiptId") != receipt.SettlementReceiptId) return false;
            result = Result("completed", Text(actual.NormalizedResult, "targetLocationId"), actual.EffectId, receipt.SettlementReceiptId, ""); return true;
        }
        private static NormalizedValue Result(string outcome, string target, string actual, string receipt, string failure) => Object(
            "outcome", NormalizedValue.CreateString(outcome), "targetLocationId", NormalizedValue.CreateString(target),
            "explorationEffectId", NormalizedValue.CreateString(actual), "tollSettlementReceiptId", NormalizedValue.CreateString(receipt), "failureCode", NormalizedValue.CreateString(failure));
    }
}
