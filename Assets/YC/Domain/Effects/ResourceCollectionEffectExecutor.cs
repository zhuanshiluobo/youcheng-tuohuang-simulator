using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Domain.Travel;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public static class ResourceCollectionEffectExecutor
    {
        public const string TypeId = "effect.collection.collect";
        public static void Register(EffectRegistry registry)
        {
            RouteResourceSelectionEffectExecutor.Register(registry);
            if (!registry.TryGet(TypeId, out _)) registry.Register(TypeId, Execute, EffectExecutorKind.IntrinsicFlow);
        }
        public static string ValidateArguments(NormalizedValue args) => args?.Kind == NormalizedValueKind.Object &&
            args.Properties.All(p => new[] { "kind", "effectTypeId", "apiVersion", "player", "scope" }.Contains(p.Name)) && Number(args, "apiVersion") == 1 &&
            Text(args, "scope") == "extra" && int.TryParse(Text(args, "player"), NumberStyles.None, CultureInfo.InvariantCulture, out int player) && player > 0 ? "" : "collection_arguments_invalid";
        public static EffectStepResult Execute(EffectExecutionContext context)
        {
            string reason = ValidateArguments(context.Node.NormalizedArguments);
            if (reason.Length > 0) return EffectStepResult.Failed(reason);
            if (context.Node.FlowStage == "")
            {
                int player = context.Node.PlayerId;
                if (Text(context.Node.NormalizedArguments, "player") != player.ToString(CultureInfo.InvariantCulture) || context.State.FindPlayer(player) == null) return EffectStepResult.Failed("invalid_player");
                Set(context.Node, "collectionStartGoldVoucherAmounts", NormalizedValue.CreateArray(context.State.Players.Select(p => Object("playerId", NormalizedValue.CreateInteger(p.PlayerId), "amount", NormalizedValue.CreateInteger(p.Resources.GoldVoucher))).ToList()));
                return EffectStepResult.Continue("common_selection").AddChild(RouteResourceSelectionEffectExecutor.CreateHarvest(player, true, context.Node.SourceId, context.Node.EffectId));
            }
            if (context.Node.FlowStage == "common_selection")
            {
                foreach (var child in context.ChildNodes)
                {
                    if (child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted) return EffectStepResult.Failed(child.FailureReason);
                    if (child.Status != EffectNodeStatus.Completed) return EffectStepResult.NoProgress("等待共用选路及额外采集完成。");
                    return EffectStepResult.Completed(child.NormalizedResult);
                }
                return EffectStepResult.NoProgress("等待额外采集选择子节点。");
            }
            // 已保存choose_locations/choose_payments/resolve阶段沿原节点恢复。
            return ExecuteLegacy(context);
        }
        private static EffectStepResult ExecuteLegacy(EffectExecutionContext context)
        {
            string reason = ValidateArguments(context.Node.NormalizedArguments); if (reason.Length > 0) return EffectStepResult.Failed(reason);
            int player = context.Node.PlayerId;
            if (Text(context.Node.NormalizedArguments, "player") != player.ToString(CultureInfo.InvariantCulture) || context.State.FindPlayer(player) == null) return EffectStepResult.Failed("invalid_player");
            var node = context.Node; var map = new MapQueryService(StaticMapDefinitions.Resolve(context.State.MapId));
            var service = new ResourceCollectionService(map) { RuleRegistry = context.Registry }; var toll = new RouteTollService(map);
            if (node.FlowStage == string.Empty)
            {
                Set(node, "collectionStartGoldVoucherAmounts", NormalizedValue.CreateArray(context.State.Players.Select(p => Object("playerId", NormalizedValue.CreateInteger(p.PlayerId), "amount", NormalizedValue.CreateInteger(p.Resources.GoldVoucher))).ToList()));
                var candidates = new InfluenceSubjectQuery().GetCollectionLocationIds(context.State, player, "extra", false).Where(id => new YC.Domain.Cards.ResourceTokenService().HasResourceToken(context.State.Map, id)).ToList();
                if (candidates.Count == 0)
                {
                    foreach (string key in new[] { "selectedLocationIds", "selectedRouteIds", "paymentRecipientSubjectKeys" }) Set(node, key, Strings(Array.Empty<string>()));
                    return Result(Array.Empty<string>(), Array.Empty<string>());
                }
                var request = new EffectInteractionSpec { InteractionTypeId = "collection.extra.locations", AnsweringPlayerId = player, Visibility = "owner", PromptKey = "collection.extra.locations",
                    MinSelections = 0, MaxSelections = candidates.Count, AnswerSchema = "candidate_ids" };
                request.CandidateIds.AddRange(candidates);
                return EffectStepResult.Continue("choose_locations").AddInteraction(PrepareInteraction(context, request));
            }
            if (node.FlowStage == "choose_locations")
            {
                var answer = context.GetLatestInteractionAnswer();
                var locations = answer?.Kind == NormalizedValueKind.Array ? answer.Items.Select(v => v.Kind == NormalizedValueKind.StableReference ? v.ReferenceId : v.StringValue).ToList() : new List<string> { Answer(context) };
                if (locations.Count != locations.Distinct().Count() || locations.Any(id => !new InfluenceSubjectQuery().GetCollectionLocationIds(context.State, player, "extra", false).Contains(id))) return EffectStepResult.Failed("collection_candidate_changed");
                Set(node, "selectedLocationIds", Strings(locations));
                Set(node, "selectedRouteIds", Strings(service.GetExtraRouteIds(context.State, player, locations).Distinct()));
                Set(node, "paymentRecipientSubjectKeys", Strings(Array.Empty<string>()));
                Set(node, "nextIndex", NormalizedValue.CreateInteger(0));
                return EffectStepResult.Continue("choose_payments");
            }
            var routes = Value(node.ContinuationData, "selectedRouteIds").Items.Select(v => v.StringValue).ToList();
            var recipients = Value(node.ContinuationData, "paymentRecipientSubjectKeys").Items.Select(v => v.StringValue).ToList();
            if (node.FlowStage == "choose_payments" || node.FlowStage == "awaiting_payment")
            {
                int index = Number(node.ContinuationData, "nextIndex");
                if (index >= routes.Count) return EffectStepResult.Continue("resolve");
                var owners = toll.GetOpponentInfluenceOwnersOnPaymentKey(context.State, routes[index], player, RouteTollPaymentKeyMode.RouteId);
                var candidates = owners.Select(id => id < 0 ? "bank" : "player:" + id.ToString(CultureInfo.InvariantCulture)).Distinct().ToList();
                if (!toll.IsPaymentRequired(context.State, routes[index], player, RouteTollPaymentKeyMode.RouteId)) candidates = new List<string> { "none" };
                if (candidates.Count == 0) candidates.Add("bank");
                string selected;
                if (candidates.Count == 1) selected = candidates[0];
                else
                {
                    if (node.FlowStage == "choose_payments")
                    {
                        var request = new EffectInteractionSpec { InteractionTypeId = "collection.extra.payment", AnsweringPlayerId = player, Visibility = "owner", PromptKey = "collection.extra.payment", MinSelections = 1, MaxSelections = 1, AnswerSchema = "candidate_id" };
                        request.CandidateIds.AddRange(candidates); return EffectStepResult.Continue("awaiting_payment").AddInteraction(PrepareInteraction(context, request));
                    }
                    selected = Answer(context); if (!candidates.Contains(selected)) return EffectStepResult.Failed("collection_payment_candidate_changed");
                }
                recipients.Add(selected); Set(node, "paymentRecipientSubjectKeys", Strings(recipients)); Set(node, "nextIndex", NormalizedValue.CreateInteger(index + 1));
                return EffectStepResult.Continue("choose_payments");
            }
            if (node.FlowStage != "resolve") throw new InvalidOperationException("collection_stage_invalid");
            var locationsToCollect = Value(node.ContinuationData, "selectedLocationIds").Items.Select(v => v.StringValue).ToList();
            var payments = new Dictionary<string, int>();
            for (int index = 0; index < routes.Count; index++) if (recipients[index].StartsWith("player:", StringComparison.Ordinal)) payments[routes[index]] = int.Parse(recipients[index].Substring(7), CultureInfo.InvariantCulture); else if (recipients[index] == "bank" && toll.GetOpponentInfluenceOwnersOnPaymentKey(context.State, routes[index], player, RouteTollPaymentKeyMode.RouteId).Contains(-1)) payments[routes[index]] = -1;
            int startGold = Number(Value(node.ContinuationData, "collectionStartGoldVoucherAmounts").Items.Single(v => Number(v, "playerId") == player), "amount");
            var result = service.CollectExtra(context.State, player, locationsToCollect, routes, payments, startGold);
            if (!result.Succeeded) return EffectStepResult.Failed("collection_unavailable", NormalizedValue.CreateString(result.Validation.Reason));
            return Result(locationsToCollect, result.Payments.Select(p => p.RouteId));
        }
        private static EffectStepResult Result(IEnumerable<string> locations, IEnumerable<string> paid) => EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(locations.Any()), "collectedLocationIds", Strings(locations), "paidRouteIds", Strings(paid))).WithFlowStage("finished");
    }
}
