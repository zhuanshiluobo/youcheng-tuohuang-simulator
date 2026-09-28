using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    // 草案保存在 Effect 中；确认前只查询规则，不支付、不移动、不揭示信息。
    public static class MainActionSelectionEffectExecutor
    {
        public const string Confirm = "action.main.confirm_selection";
        public const string Dispatch = "action.dispatch.choose";
        public const string Move = "action.city_move.choose";
        public const string Special = "action.special.choose";
        public static void Register(EffectRegistry registry)
        {
            MainActionEffectExecutor.Register(registry);
            FacilitySelectionEffectExecutor.Register(registry);
            ExplorationSelectionEffectExecutor.Register(registry);
            foreach (var type in new[] { Dispatch, Move, Special, Confirm })
                if (!registry.TryGet(type, out _)) registry.Register(new EffectRegistration(type, Execute, EffectExecutorKind.IntrinsicFlow, "1"));
        }
        public static EffectSpec Create(int player, string type, string action = "", string marker = "")
        {
            return MainActionEffectExecutor.Create(player, new EffectSpec(type, NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "action", Value = NormalizedValue.CreateString(action ?? "") },
                new NormalizedValueEntry { Name = "marker", Value = NormalizedValue.CreateString(marker ?? "") }
            })) { PlayerId = player, DefinitionVersion = "1" });
        }
        // 完整参数的协议入口同样只保存草案，不能直接执行支付或揭示事件。
        public static EffectSpec ConfirmSelection(int player, EffectSpec operation)
        {
            var selection = new EffectSpec(Confirm) { PlayerId = player, DefinitionVersion = "1" };
            selection.Children.Add(operation);
            return MainActionEffectExecutor.Create(player, selection);
        }
        private static string Text(NormalizedValue value, string key) => value?.Properties?.Find(p => p.Name == key)?.Value.StringValue ?? "";
        private static string Encode(string value) => Uri.EscapeDataString(value ?? "");
        private static string Answer(EffectExecutionContext c)
        {
            var v = c.GetLatestInteractionAnswer();
            if (v?.Kind == NormalizedValueKind.Array && v.Items.Count == 1) v = v.Items[0];
            return v?.Kind == NormalizedValueKind.StableReference ? v.ReferenceId : v?.StringValue ?? "";
        }
        private static EffectStepResult Wait(EffectExecutionContext c, string stage, string prompt, IEnumerable<string> ids)
        {
            var request = new EffectInteractionSpec { InteractionTypeId = "facility.entry.choice", PromptKey = prompt,
                AnsweringPlayerId = c.Node.PlayerId, Visibility = "owner", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, AllowDecline = true };
            request.CandidateIds.AddRange(ids);
            if (request.CandidateIds.Count == 0) return EffectStepResult.Failed("no_legal_target");
            return EffectStepResult.Continue(stage).AddInteraction(request);
        }
        private static EffectStepResult Execute(EffectExecutionContext c)
        {
            if (MainActionEffectExecutor.IsCancellation(c)) return EffectStepResult.Failed("player_cancelled");
            if (c.Node.FlowStage == "executing")
            {
                foreach (var child in c.ChildNodes)
                {
                    if (child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted) return EffectStepResult.Failed(child.FailureReason);
                    if (child.Status != EffectNodeStatus.Completed) return EffectStepResult.NoProgress("等待行动效果结算。");
                }
                return EffectStepResult.Completed();
            }
            if (c.Node.EffectTypeId == Confirm)
            {
                if (string.IsNullOrEmpty(c.Node.FlowStage))
                    return Wait(c, "confirm", "action.main.confirm", new[] { "action.confirm" });
                if (Answer(c) != "action.confirm" || c.Node.NestedEffects.Count != 1)
                    return EffectStepResult.Failed("invalid_confirmation");
                return EffectStepResult.Continue("executing").AddChild(EffectRegistry.FromRuntimeSpec(c.Node.NestedEffects[0]));
            }
            var map = new MapQueryService(StaticMapDefinitions.Resolve(c.State.MapId));
            if (c.Node.EffectTypeId == Dispatch) return ChooseDispatch(c, map);
            if (c.Node.EffectTypeId == Move) return ChooseMove(c, map);
            if (c.Node.EffectTypeId == Special) return ChooseSpecial(c, map);
            return EffectStepResult.Failed("invalid_action_selection");
        }
        private static List<string> Slots(MapQueryService map)
        {
            var ids = new List<string>();
            foreach (var location in map.Map.Locations)
                for (int i = 0; i < location.InfluenceSlotCount; i++) ids.Add(InfluenceService.GetLocationSlotId(location.LocationId, i));
            foreach (var route in map.Map.Routes)
                for (int i = 0; i < route.InfluenceSlotCount; i++) ids.Add(InfluenceService.GetRouteSlotId(route.RouteId, i));
            return ids;
        }
        private static List<string> Targets(EffectExecutionContext c, InfluenceService service, List<string> slots, List<InfluenceMoveRequest> moves, string source)
        {
            var result = new List<string>();
            foreach (var slot in slots)
            {
                var draft = new List<InfluenceMoveRequest>(moves) { new InfluenceMoveRequest(source, slot) };
                if (service.CanMoveAtomically(c.State, c.Node.PlayerId, draft).IsValid) result.Add(slot);
            }
            return result;
        }
        private static EffectStepResult ChooseDispatch(EffectExecutionContext c, MapQueryService map)
        {
            var service = new InfluenceService(map);
            var slots = Slots(map);
            string[] stage = (c.Node.FlowStage ?? "").Split('|');
            var moves = new List<InfluenceMoveRequest>();
            if (stage.Length >= 3 && stage[1].Length > 0 && stage[2].Length > 0)
                moves.Add(new InfluenceMoveRequest(Uri.UnescapeDataString(stage[1]), Uri.UnescapeDataString(stage[2])));
            string saved = moves.Count == 0 ? "|" : Encode(moves[0].SourceSlotId) + "|" + Encode(moves[0].TargetSlotId);
            string answer = Answer(c);
            if (stage[0] == "confirm")
            {
                if (stage.Length == 5) moves.Add(new InfluenceMoveRequest(Uri.UnescapeDataString(stage[3]), Uri.UnescapeDataString(stage[4])));
                if (answer != "action.confirm" || !service.CanMoveAtomically(c.State, c.Node.PlayerId, moves).IsValid) return EffectStepResult.Failed("invalid_dispatch_confirmation");
                var pairs = new List<KeyValuePair<string, string>>();
                foreach (var move in moves)
                {
                    var influence = service.FindInfluence(c.State, move.SourceSlotId);
                    if (influence == null) return EffectStepResult.Failed("dispatch_source_missing");
                    pairs.Add(new KeyValuePair<string, string>(InfluenceIdentity.GetStableId(c.State, influence), move.TargetSlotId));
                }
                return EffectStepResult.Continue("executing").AddChild(InfluenceEffectSpecFactory.MoveInfluences(c.Node.PlayerId, pairs, InfluenceCauseKinds.MoveCity));
            }
            if (stage[0] == "source")
            {
                return Wait(c, "target|" + saved + "|" + Encode(answer), "action.dispatch.target", Targets(c, service, slots, moves, answer));
            }
            if (stage[0] == "target" && stage.Length == 4)
            {
                string source = Uri.UnescapeDataString(stage[3]);
                if (!Targets(c, service, slots, moves, source).Contains(answer)) return EffectStepResult.Failed("stale_dispatch_target");
                if (moves.Count == 1)
                    return Wait(c, "confirm|" + saved + "|" + Encode(source) + "|" + Encode(answer), "action.main.confirm", new[] { "action.confirm" });
                saved = Encode(source) + "|" + Encode(answer);
                return Wait(c, "decision|" + saved, "action.dispatch.continue", new[] { "action.dispatch.finish", "action.dispatch.add" });
            }
            if (stage[0] == "decision" && answer == "action.dispatch.finish")
                return Wait(c, "confirm|" + saved, "action.main.confirm", new[] { "action.confirm" });
            var sources = new List<string>();
            foreach (var influence in c.State.Map.Influences)
                if (influence.PlayerId == c.Node.PlayerId && !moves.Exists(m => m.SourceSlotId == influence.SlotId) && Targets(c, service, slots, moves, influence.SlotId).Count > 0)
                    sources.Add(influence.SlotId);
            // 没有第二个合法来源时仍可以确认第一次移动，不能陷入死路。
            if (sources.Count == 0 && moves.Count > 0)
                return Wait(c, "confirm|" + saved, "action.main.confirm", new[] { "action.confirm" });
            return Wait(c, "source|" + saved, "action.dispatch.source", sources);
        }
        private static EffectStepResult ChooseMove(EffectExecutionContext c, MapQueryService map)
        {
            var influence = new InfluenceService(map);
            var travel = new TravelCostService(map);
            var movement = new CityMovementService(map, influence, travel);
            var query = new CityMoveCandidateQueryService(map, movement, travel);
            var targets = query.QueryLegal(c.State, c.Node.PlayerId).Select(t => t.TargetLocationId).ToList();
            string[] stage = (c.Node.FlowStage ?? "").Split('|');
            if (stage[0] == "") return Wait(c, "target", "action.move.target", targets);
            if (stage[0] == "target" && targets.Contains(Answer(c)))
                return Wait(c, "confirm|" + Encode(Answer(c)), "action.main.confirm", new[] { "action.confirm" });
            if (stage[0] == "confirm" && stage.Length == 2 && Answer(c) == "action.confirm" && targets.Contains(Uri.UnescapeDataString(stage[1])))
                return EffectStepResult.Continue("executing").AddChild(CityMoveEffectSpecFactory.Move(c.Node.PlayerId, Uri.UnescapeDataString(stage[1]), false, false, c.Node.SourceId));
            return EffectStepResult.Failed("invalid_move_selection");
        }
        private static EffectStepResult ChooseSpecial(EffectExecutionContext c, MapQueryService map)
        {
            string[] stage = (c.Node.FlowStage ?? "").Split('|');
            string action = stage.Length > 1 ? Uri.UnescapeDataString(stage[1]) : Text(c.Node.NormalizedArguments, "action");
            string marker = stage.Length > 2 ? Uri.UnescapeDataString(stage[2]) : Text(c.Node.NormalizedArguments, "marker");
            var influence = new InfluenceService(map);
            var query = new SpecialActionOptionQueryService(map, influence, new CityMovementService(map, influence, new TravelCostService(map)), new SpecialActionLifecycleService());
            // 执行器恢复时当前答案已关闭，查询仍以权威状态重新计算可用性。
            var options = query.Query(c.State, c.Node.PlayerId).Options;
            if (stage[0] == "action")
            {
                var parts = Answer(c).Split(':');
                if (parts.Length != 3) return EffectStepResult.Failed("invalid_special_action");
                action = Uri.UnescapeDataString(parts[1]); marker = Uri.UnescapeDataString(parts[2]);
            }
            if (string.IsNullOrEmpty(action) || string.IsNullOrEmpty(marker))
                return Wait(c, "action", "action.special.choose", options.Where(o => o.CanUse).Select(o => "special:" + Encode(o.SpecialActionId) + ":" + Encode(o.DeclarationMarkerId)));
            var option = options.Find(o => o.SpecialActionId == action && o.DeclarationMarkerId == marker);
            if (option == null || !option.CanUse) return EffectStepResult.Failed("special_action_unavailable");
            string saved = Encode(action) + "|" + Encode(marker);
            var definition = SpecialActionDatabase.Get(action);
            if (stage[0] == "confirm")
            {
                if (Answer(c) != "action.confirm") return EffectStepResult.Failed("invalid_confirmation");
                int ore = stage.Length > 3 ? int.Parse(stage[3], CultureInfo.InvariantCulture) : -1;
                int iron = stage.Length > 4 ? int.Parse(stage[4], CultureInfo.InvariantCulture) : -1;
                var operation = CityStyleSpecialActionEffectSpecFactory.Activate(c.Node.PlayerId, action, marker, ore, iron, c.Node.SourceId);
                operation.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "consumeMainAction", Value = NormalizedValue.CreateBoolean(false) });
                return EffectStepResult.Continue("executing").AddChild(operation);
            }
            if (stage[0] == "payment")
            {
                string[] payment = Answer(c).Split(':');
                if (payment.Length != 3) return EffectStepResult.Failed("invalid_payment");
                return Wait(c, "confirm|" + saved + "|" + payment[1] + "|" + payment[2], "action.main.confirm", new[] { "action.confirm" });
            }
            if (definition.FlexibleOriginiumAndIronCost > 0)
                return Wait(c, "payment|" + saved, "action.special.payment", option.PaymentOptions.Select(p => "payment:" + p.Originium + ":" + p.Iron));
            return Wait(c, "confirm|" + saved, "action.main.confirm", new[] { "action.confirm" });
        }
    }
}
