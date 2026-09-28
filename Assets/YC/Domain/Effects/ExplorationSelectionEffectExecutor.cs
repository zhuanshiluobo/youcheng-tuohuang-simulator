using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Exploration;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Domain.Travel;

namespace YC.Domain.Effects
{
    // 路线、落点、收费方逐步选择；直到确认完成才创建执行探索的子节点。
    public static class ExplorationSelectionEffectExecutor
    {
        public const string TypeId = "effect.exploration.choose";
        public static void Register(EffectRegistry registry)
        {
            if (!registry.TryGet(TypeId, out _)) registry.Register(new EffectRegistration(TypeId, Execute, EffectExecutorKind.IntrinsicFlow, "1.0.0"));
        }
        private static string Encode(IEnumerable<string> ids) => string.Join(",", ids.Select(Uri.EscapeDataString));
        private static List<string> Decode(string ids) => ids.Length == 0 ? new List<string>() : ids.Split(',').Select(Uri.UnescapeDataString).ToList();
        private static string Answer(EffectExecutionContext c)
        {
            var v = c.GetLatestInteractionAnswer();
            if (v?.Kind == NormalizedValueKind.Array && v.Items.Count == 1) v = v.Items[0];
            return v?.Kind == NormalizedValueKind.StableReference ? v.ReferenceId : v?.StringValue ?? "";
        }
        private static EffectStepResult Wait(EffectExecutionContext c, string stage, string prompt, List<string> ids)
        {
            var request = new EffectInteractionSpec { InteractionTypeId = "facility.entry.choice", PromptKey = prompt,
                AllowDecline = MainActionEffectExecutor.IsPlanning(c), AnsweringPlayerId = c.Node.PlayerId, Visibility = "owner", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1 };
            request.CandidateIds.AddRange(ids);
            return EffectStepResult.Continue(stage).AddInteraction(request);
        }
        private static EffectStepResult Execute(EffectExecutionContext c)
        {
            if (MainActionEffectExecutor.IsCancellation(c)) return EffectStepResult.Failed("player_cancelled");
            if (c.Node.FlowStage == "applied")
            {
                foreach (var child in c.ChildNodes)
                {
                    if (child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted) return EffectStepResult.Failed("exploration_child_failed");
                    if (child.Status != EffectNodeStatus.Completed) return EffectStepResult.NoProgress("等待探索结算完成。");
                }
                return EffectStepResult.Completed();
            }
            var map = new MapQueryService(StaticMapDefinitions.Resolve(c.State.MapId));
            var player = c.State.FindPlayer(c.Node.PlayerId);
            if (player == null || string.IsNullOrEmpty(player.CityLocationId)) return EffectStepResult.Failed("invalid_exploration_player");
            var service = new ExplorationService(map);
            var tolls = new RouteTollService(map);
            string[] stage = (c.Node.FlowStage ?? "").Split('|');
            var path = new MapPath();
            if (stage.Length > 2) { path.LocationIds.AddRange(Decode(stage[1])); path.RouteIds.AddRange(Decode(stage[2])); }
            else path.LocationIds.Add(player.CityLocationId);
            string answer = Answer(c), slot = stage.Length > 3 ? Uri.UnescapeDataString(stage[3]) : "";
            var recipients = new Dictionary<string, int>();
            if (stage.Length > 4 && stage[4].Length > 0)
                foreach (var pair in stage[4].Split(','))
                { var parts = pair.Split('='); recipients.Add(Uri.UnescapeDataString(parts[0]), int.Parse(parts[1], CultureInfo.InvariantCulture)); }
            int paymentIndex = stage.Length > 5 ? int.Parse(stage[5], CultureInfo.InvariantCulture) : 0;
            if (MainActionEffectExecutor.IsPlanning(c) && (stage[0] == "" || stage[0] == "target" || stage[0] == "routes"))
            {
                if (stage[0] == "")
                {
                    var targets = map.Map.Locations.Where(location => location.LocationId != player.CityLocationId &&
                        LegalPaths(service, c, location.LocationId).Count > 0)
                        .Select(location => "explore.target:" + location.LocationId).ToList();
                    if (targets.Count == 0) return EffectStepResult.Failed("no_legal_exploration_target");
                    return Wait(c, "target", "action.explore.target", targets);
                }
                string target = stage[0] == "target" && answer.StartsWith("explore.target:", StringComparison.Ordinal)
                    ? answer.Substring(15) : stage.Length == 2 ? Uri.UnescapeDataString(stage[1]) : "";
                var choices = LegalPaths(service, c, target);
                if (choices.Count == 0) return EffectStepResult.Failed("exploration_target_no_longer_legal");
                if (stage[0] == "routes")
                {
                    path = choices.Find(choice => PathCandidate(choice) == answer);
                    if (path == null) return EffectStepResult.Failed("invalid_exploration_path");
                }
                else
                {
                    if (service.RequiresPathChoice(c.State, c.Node.PlayerId, choices))
                        return Wait(c, "routes|" + Uri.EscapeDataString(target), "action.explore.path", choices.Select(PathCandidate).ToList());
                    path = choices[0];
                }
                // Original exploration chooses the first legal destination slot automatically.
                slot = service.ResolveInfluenceSlotId(c.State, c.Node.PlayerId, target, "");
            }
            if (stage[0] == "path")
            {
                if (answer == "choice.skip") return MainActionEffectExecutor.IsPlanning(c) ? EffectStepResult.Failed("player_cancelled") : EffectStepResult.Completed();
                if (answer == "explore.back" && path.RouteIds.Count > 0)
                { path.RouteIds.RemoveAt(path.RouteIds.Count - 1); path.LocationIds.RemoveAt(path.LocationIds.Count - 1); }
                else if (answer.StartsWith("explore.step:", StringComparison.Ordinal))
                {
                    var parts = answer.Substring(13).Split(':');
                    if (parts.Length != 2) return EffectStepResult.Failed("invalid_exploration_step");
                    string route = Uri.UnescapeDataString(parts[0]), next = Uri.UnescapeDataString(parts[1]);
                    if (!Steps(map, path).Contains(answer)) return EffectStepResult.Failed("stale_exploration_step");
                    path.RouteIds.Add(route); path.LocationIds.Add(next);
                }
                else if (answer.StartsWith("explore.finish:", StringComparison.Ordinal))
                    slot = answer.Substring(15);
                else if (answer != "explore.back") return EffectStepResult.Failed("invalid_exploration_answer");
            }
            if (slot.Length == 0)
            {
                var options = Steps(map, path);
                string target = path.LocationIds[path.LocationIds.Count - 1];
                if (path.RouteIds.Count > 0)
                {
                    for (int i = 0; i < map.GetLocation(target).InfluenceSlotCount; i++)
                    {
                        string candidateSlot = YC.Domain.Influence.InfluenceService.GetLocationSlotId(target, i);
                        if (service.CanExplore(c.State, c.Node.PlayerId, target, path, -1, candidateSlot, recipients, null, false, true).IsValid)
                            options.Insert(0, "explore.finish:" + candidateSlot);
                    }
                    options.Add("explore.back");
                }
                if (!MainActionEffectExecutor.IsPlanning(c)) options.Add("choice.skip");
                return Wait(c, "path|" + Encode(path.LocationIds) + "|" + Encode(path.RouteIds), "effect.explore.choose_path", options);
            }
            string destination = path.LocationIds[path.LocationIds.Count - 1];
            if (!service.CanExplore(c.State, c.Node.PlayerId, destination, path, -1, slot, recipients, null, false, true).IsValid)
                return EffectStepResult.Failed("exploration_target_no_longer_legal");
            if (!tolls.TryBuildPaymentPlan(c.State, c.Node.PlayerId, path.RouteIds, recipients, RouteTollPaymentKeyMode.SharedRegion, out var payments).IsValid)
                return EffectStepResult.Failed("exploration_payment_invalid");
            if (stage[0] == "toll")
            {
                if (paymentIndex >= payments.Count || !answer.StartsWith("explore.pay:", StringComparison.Ordinal) || !int.TryParse(answer.Substring(12), out int recipient))
                    return EffectStepResult.Failed("invalid_exploration_recipient");
                var payment = payments[paymentIndex];
                var owners = tolls.GetOpponentInfluenceOwnersOnPaymentKey(c.State, tolls.GetRoutePaymentKey(payment.RouteId, RouteTollPaymentKeyMode.SharedRegion), c.Node.PlayerId, RouteTollPaymentKeyMode.SharedRegion);
                if (!owners.Contains(recipient)) return EffectStepResult.Failed("stale_exploration_recipient");
                recipients[payment.RouteId] = recipient;
                paymentIndex++;
            }
            for (; paymentIndex < payments.Count; paymentIndex++)
            {
                var payment = payments[paymentIndex];
                var owners = tolls.GetOpponentInfluenceOwnersOnPaymentKey(c.State, tolls.GetRoutePaymentKey(payment.RouteId, RouteTollPaymentKeyMode.SharedRegion), c.Node.PlayerId, RouteTollPaymentKeyMode.SharedRegion);
                if (owners.Count <= 1) continue;
                string saved = string.Join(",", recipients.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Uri.EscapeDataString(p.Key) + "=" + p.Value.ToString(CultureInfo.InvariantCulture)));
                return Wait(c, "toll|" + Encode(path.LocationIds) + "|" + Encode(path.RouteIds) + "|" + Uri.EscapeDataString(slot) + "|" + saved + "|" + paymentIndex,
                    "effect.explore.choose_recipient", owners.Select(id => "explore.pay:" + id.ToString(CultureInfo.InvariantCulture)).ToList());
            }
            if (MainActionEffectExecutor.IsPlanning(c) && stage[0] != "confirm")
            {
                string saved = string.Join(",", recipients.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Uri.EscapeDataString(p.Key) + "=" + p.Value.ToString(CultureInfo.InvariantCulture)));
                return Wait(c, "confirm|" + Encode(path.LocationIds) + "|" + Encode(path.RouteIds) + "|" + Uri.EscapeDataString(slot) + "|" + saved + "|" + payments.Count,
                    "action.main.confirm", new List<string> { "action.confirm" });
            }
            if (stage[0] == "confirm" && answer != "action.confirm") return EffectStepResult.Failed("invalid_confirmation");
            return EffectStepResult.Continue("applied").AddChild(ExplorationEffectSpecFactory.Explore(c.Node.PlayerId, destination,
                path, slot, recipients, true, false, c.Node.SourceId));
        }
        private static List<MapPath> LegalPaths(ExplorationService service, EffectExecutionContext c, string target)
        {
            if (string.IsNullOrEmpty(target)) return new List<MapPath>();
            try
            {
                return service.FindDefaultPathChoices(c.State, c.Node.PlayerId, target)
                    .Where(path => service.CanExplore(c.State, c.Node.PlayerId, target, path, -1, "", null, null, false, true).IsValid).ToList();
            }
            catch (ArgumentException) { return new List<MapPath>(); }
        }

        public static string PathCandidate(MapPath path) => "explore.path:" + Encode(path.LocationIds) + ":" + Encode(path.RouteIds);

        public static MapPath ReadPathCandidate(string candidate)
        {
            if (candidate == null || !candidate.StartsWith("explore.path:", StringComparison.Ordinal)) return null;
            var parts = candidate.Substring(13).Split(':');
            if (parts.Length != 2) return null;
            var path = new MapPath();
            path.LocationIds.AddRange(Decode(parts[0]));
            path.RouteIds.AddRange(Decode(parts[1]));
            return path;
        }

        private static List<string> Steps(MapQueryService map, MapPath path)
        {
            var result = new List<string>();
            string current = path.LocationIds[path.LocationIds.Count - 1];
            foreach (var route in map.Map.Routes)
            {
                var locations = new List<string>(route.CoveredLocationIds ?? new List<string>());
                if (!locations.Contains(route.FromLocationId)) locations.Add(route.FromLocationId);
                if (!locations.Contains(route.ToLocationId)) locations.Add(route.ToLocationId);
                if (!locations.Contains(current)) continue;
                foreach (string next in locations)
                    if (!string.IsNullOrEmpty(next) && !path.LocationIds.Contains(next))
                        result.Add("explore.step:" + Uri.EscapeDataString(route.RouteId) + ":" + Uri.EscapeDataString(next));
            }
            return result;
        }
    }
}
