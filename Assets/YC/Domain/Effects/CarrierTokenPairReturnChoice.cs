using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.Presentation;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    [Serializable] public sealed class CarrierPairReturnGroup
    {
        public string Key = "", First = "", Second = "";
        public int Count;
    }
    [Serializable] public sealed class CarrierPairReturnSnapshot
    {
        public int ProtocolVersion = 1, TotalCount;
        public string ZoneId = "";
        public List<CarrierPairReturnGroup> Groups = new List<CarrierPairReturnGroup>();
    }

    // 4.16.2/3：选择只属于此独立编排，最终叶子仍是一笔确定的足量MoveItem。
    public static class CarrierTokenPairReturnChoice
    {
        public static string ValidateRaw(Newtonsoft.Json.Linq.JObject node, bool runtimeNode) => node["CarrierPairReturn"] is Newtonsoft.Json.Linq.JToken value && value.Type != Newtonsoft.Json.Linq.JTokenType.Null &&
            (!runtimeNode || !ItemMovementV4.RawShape(value, typeof(CarrierPairReturnSnapshot))) ? "carrier_pair_raw_shape_invalid" : "";
        public const string TypeId = "flow.carrier.choose_pair_return", SecondTypeId = "flow.carrier.pair_return_second", Version = "1.0.0";
        public static string ValidateArguments(string type, NormalizedValue a)
        {
            if (a?.Kind != NormalizedValueKind.Object || !a.IsValid()) return "carrier_pair_arguments_invalid";
            if (type == SecondTypeId) return a.Properties.Count == 2 && Text(a,"rootEffectId").Length > 0 && Value(a,"firstGroupIndex")?.Kind == NormalizedValueKind.Integer &&
                Value(a,"firstGroupIndex").IntegerValue >= 0 && Value(a,"firstGroupIndex").IntegerValue <= int.MaxValue &&
                a.Properties.All(p => new[] { "rootEffectId", "firstGroupIndex" }.Contains(p.Name)) ? "" : "carrier_pair_second_arguments_invalid";
            return type == TypeId && Value(a,"apiVersion")?.Kind == NormalizedValueKind.Integer && Value(a,"apiVersion").IntegerValue == 1 &&
                int.TryParse(Text(a,"player"), NumberStyles.None, CultureInfo.InvariantCulture, out int player) && player > 0 &&
                Text(a,"carrierItemId").Length > 0 && Text(a,"localZoneId").Length > 0 && ItemTransferService.IsTokenProfile(Text(a,"operationProfileId")) &&
                a.Properties.All(p => new[] { "apiVersion", "player", "carrierItemId", "localZoneId", "operationProfileId", "kind", "effectTypeId" }.Contains(p.Name))
                ? "" : "carrier_pair_arguments_invalid";
        }
        public static void Register(EffectRegistry registry)
        {
            foreach (string type in new[] { TypeId, SecondTypeId })
                if (!registry.TryGet(type, out _)) registry.Register(new EffectRegistration(type, Execute, EffectExecutorKind.IntrinsicFlow, Version) {
                    Validator = s => ValidateArguments(type, s.NormalizedArguments) });
        }
        private static List<EffectNodeRuntimeState> Own(GameState s, EffectNodeRuntimeState n) => n.ChildEffectIds
            .Where(id => !s.EffectRuntime.DispatchReceipts.Any(r => r.Succeeded && r.AttachedEffectIds.Contains(id)))
            .Select(id => s.EffectRuntime.EffectNodes.Single(c => c.EffectId == id)).ToList();
        private static long PairCount(CarrierPairReturnSnapshot d) => (long)d.Groups.Count * (d.Groups.Count - 1) / 2 + d.Groups.Count(g => g.Count > 1);
        private static GameplayTextRef Prompt(EffectNodeRuntimeState root) => root.Presentation?.Interaction?.Prompt?.Clone() ?? new GameplayTextRef("core:interaction.carrier_pair.prompt");
        private static EffectSpec Move(EffectNodeRuntimeState root, IEnumerable<string> ids)
        {
            var spec = TokenSpec(root.PlayerId, ids, root.CarrierPairReturn.ZoneId, null, 2, Text(root.NormalizedArguments,"operationProfileId"));
            if (!ItemMoveInputNormalization.TryNormalize(spec, out string error)) throw new InvalidOperationException(error);
            return spec;
        }
        private sealed class Branch { public string Id; public GameplayTextRef Label; public EffectSpec Body; }
        private static EffectSpec Choice(EffectNodeRuntimeState root, List<Branch> branches)
        {
            var options = new List<string>(); var groups = new List<List<EffectSpec>>(); var labels = new List<GameplayOptionPresentation>();
            if (branches.Count > 16)
            {
                int size = 16; while ((branches.Count + size - 1) / size > 16) size = checked(size * 16);
                for (int first = 0; first < branches.Count; first += size)
                {
                    string id = "page:" + branches[first].Id; options.Add(id);
                    groups.Add(new List<EffectSpec> { Choice(root, branches.Skip(first).Take(size).ToList()) });
                    labels.Add(new GameplayOptionPresentation { CandidateId = id, Label = new GameplayTextRef("core:interaction.choice.item_group",
                        new GameplayTextArgument { Name = "minimum", Kind = "integer", Value = NormalizedValue.CreateInteger(first + 1) },
                        new GameplayTextArgument { Name = "maximum", Kind = "integer", Value = NormalizedValue.CreateInteger(Math.Min(first + size, branches.Count)) }) });
                }
            }
            else foreach (var b in branches) { options.Add(b.Id); groups.Add(new List<EffectSpec> { b.Body }); labels.Add(new GameplayOptionPresentation { CandidateId = b.Id, Label = b.Label }); }
            var choice = EffectSpec.Create(LuaDomainEffectTypeIds.Choice, Object("player", NormalizedValue.CreateString(root.PlayerId.ToString(CultureInfo.InvariantCulture)),
                "options", Strings(options), "minSelections", NormalizedValue.CreateInteger(1), "maxSelections", NormalizedValue.CreateInteger(1),
                "interactionType", NormalizedValue.CreateString("effect.choice.execute")), root.PlayerId);
            choice.DecisionPlayerId = root.PlayerId; choice.NestedEffectGroups = groups;
            choice.Presentation = new EffectPresentationDefinition { Interaction = new GameplayInteractionPresentation { Prompt = Prompt(root), Options = labels } };
            return choice;
        }
        private static Branch Pair(EffectNodeRuntimeState root, string first, string second, string id) => new Branch {
            Id = id, Body = Move(root, new[] { first, second }), Label = new GameplayTextRef("core:interaction.carrier_pair.pair",
                new GameplayTextArgument { Name = "first", Kind = "item", Value = NormalizedValue.CreateStableReference("item", first) },
                new GameplayTextArgument { Name = "second", Kind = "item", Value = NormalizedValue.CreateStableReference("item", second) }) };
        private static EffectSpec First(EffectNodeRuntimeState root)
        {
            var d = root.CarrierPairReturn;
            if (d.TotalCount <= 2 || d.Groups.Count <= 1)
                return Move(root, d.Groups.SelectMany(g => g.Count > 1 ? new[] { g.First, g.Second } : new[] { g.First }).OrderBy(id => id, StringComparer.Ordinal).Take(2));
            var branches = new List<Branch>();
            if (PairCount(d) <= 16)
            {
                for (int i = 0; i < d.Groups.Count; i++)
                {
                    var g = d.Groups[i];
                    if (g.Count > 1) branches.Add(Pair(root, g.First, g.Second, "pair:" + branches.Count));
                    for (int j = i + 1; j < d.Groups.Count; j++) branches.Add(Pair(root, g.First, d.Groups[j].First, "pair:" + branches.Count));
                }
            }
            else for (int i = 0; i < d.Groups.Count; i++)
            {
                var g = d.Groups[i]; if (i == d.Groups.Count - 1 && g.Count == 1) continue;
                int alternatives = d.Groups.Count - i - 1 + (g.Count > 1 ? 1 : 0);
                if (alternatives == 1)
                {
                    branches.Add(Pair(root, g.First, g.Count > 1 ? g.Second : d.Groups[i + 1].First, "first:" + i));
                    continue;
                }
                branches.Add(new Branch { Id = "first:" + i, Label = new GameplayTextRef("core:interaction.carrier_pair.first",
                    new GameplayTextArgument { Name = "first", Kind = "item", Value = NormalizedValue.CreateStableReference("item", g.First) }),
                    Body = EffectSpec.Create(SecondTypeId, Object("rootEffectId", NormalizedValue.CreateString(root.EffectId), "firstGroupIndex", NormalizedValue.CreateInteger(i)), root.PlayerId) });
            }
            return Choice(root, branches);
        }
        private static EffectSpec Second(EffectNodeRuntimeState root, int first)
        {
            var d = root.CarrierPairReturn; var g = d.Groups[first]; var branches = new List<Branch>();
            if (g.Count > 1) branches.Add(Pair(root, g.First, g.Second, "pair:" + first));
            for (int j = first + 1; j < d.Groups.Count; j++) branches.Add(Pair(root, g.First, d.Groups[j].First, "pair:" + j));
            if (branches.Count == 0) throw new InvalidOperationException("carrier_pair_second_empty");
            return branches.Count == 1 ? branches[0].Body : Choice(root, branches);
        }
        private static CarrierPairReturnSnapshot Snapshot(EffectExecutionContext c, out string error)
        {
            var a = c.Node.NormalizedArguments;
            var zone = CardMarkerCarrier.Resolve(c.State, c.Node, Text(a,"carrierItemId"), Text(a,"localZoneId"), "transfer.original_supply", out var types, out error,
                c.Registry.AbilityDeclarations, registry: c.Registry);
            if (zone == null) return null;
            var data = new CarrierPairReturnSnapshot { ZoneId = zone.ZoneId }; var keys = new Dictionary<string, CarrierPairReturnGroup>(StringComparer.Ordinal);
            foreach (var item in c.State.ItemZones.Items.Where(i => i.ZoneId == zone.ZoneId && CardMarkerCarrier.AcceptsTransfer(c.State, zone, i, c.Node.PlayerId, types)).OrderBy(i => i.ItemId, StringComparer.Ordinal))
            {
                // 保留类型、归属、控制、原供应和本地规则状态；开放离场实例不互相折叠。
                string key = ResourcePaymentSourceService.ItemSelectionGroup(c.State, item);
                if (!keys.TryGetValue(key, out var group)) { group = new CarrierPairReturnGroup { Key = key, First = item.ItemId }; keys.Add(key, group); data.Groups.Add(group); }
                group.Count = checked(group.Count + 1); if (group.Count == 2) group.Second = item.ItemId;
                data.TotalCount = checked(data.TotalCount + 1);
            }
            return data;
        }
        private static IEnumerable<EffectNodeRuntimeState> Moves(GameState s, EffectNodeRuntimeState n)
        {
            foreach (var child in Own(s, n))
                if (child.EffectTypeId == ItemTransferEffectExecutor.TypeId) yield return child;
                else if (child.EffectTypeId == LuaDomainEffectTypeIds.Choice || child.EffectTypeId == SecondTypeId)
                    foreach (var move in Moves(s, child)) yield return move;
        }
        private static NormalizedValue Result(GameState s, EffectNodeRuntimeState n)
        {
            var moves = Moves(s, n).ToList();
            if (moves.Count > 1) throw new InvalidOperationException("carrier_pair_multiple_moves");
            return moves.Count == 1 ? moves[0].NormalizedResult.Clone() : Object("changed", NormalizedValue.CreateBoolean(false), "movedCount", NormalizedValue.CreateInteger(0));
        }
        private static EffectStepResult Execute(EffectExecutionContext c)
        {
            var n = c.Node; string error = ValidateArguments(n.EffectTypeId, n.NormalizedArguments); if (error.Length > 0) return EffectStepResult.Failed(error);
            var root = n.EffectTypeId == TypeId ? n : c.State.EffectRuntime.EffectNodes.SingleOrDefault(r => r.EffectId == Text(n.NormalizedArguments,"rootEffectId"));
            if (root?.EffectTypeId != TypeId || root.PlayerId != n.PlayerId || Text(root.NormalizedArguments,"player") != n.PlayerId.ToString(CultureInfo.InvariantCulture)) return EffectStepResult.Failed("carrier_pair_owner_invalid");
            if (n.FlowStage == "")
            {
                if (n == root) { n.CarrierPairReturn = Snapshot(c, out error); if (n.CarrierPairReturn == null) return EffectStepResult.Failed(error); }
                else if (!Bound(c.State, root, n)) return EffectStepResult.Failed("carrier_pair_second_unbound");
                var spec = n == root ? First(root) : Second(root, Number(n.NormalizedArguments,"firstGroupIndex")); spec.StableKey = "pair-step";
                return EffectStepResult.Continue("choosing").AddChild(spec);
            }
            if (!ValidateSaved(c.State, n)) throw new InvalidOperationException("carrier_pair_state_invalid");
            if (n.FlowStage == "finished") return n.Status == EffectNodeStatus.Failed || n.PendingOutcome == EffectPendingOutcome.Failed
                ? EffectStepResult.Failed(n.FailureReason, n.NormalizedResult).WithFlowStage("finished") : EffectStepResult.Completed(n.NormalizedResult).WithFlowStage("finished");
            var child = Own(c.State, n).Single();
            if (child.Status != EffectNodeStatus.Completed && child.Status != EffectNodeStatus.Failed) return EffectStepResult.NoProgress("等待两枚标记的实际返还及响应完成。");
            var moves = Moves(c.State,n).ToList(); var result = Result(c.State,n);
            bool success = child.Status == EffectNodeStatus.Completed && moves.Count == 1 && moves[0].Status == EffectNodeStatus.Completed && Flag(result,"changed");
            return (success ? EffectStepResult.Completed(result) : EffectStepResult.Failed(child.Status == EffectNodeStatus.Failed ? child.FailureReason : "carrier_pair_no_committed_transfer", result)).WithFlowStage("finished");
        }
        private static object Shape(EffectSpecRuntimeState s) => new { s.EffectTypeId, s.PlayerId, s.NormalizedArguments, s.ItemMoveInput, s.Presentation,
            Decision = s.DecisionPlayerId < 0 ? s.PlayerId : s.DecisionPlayerId, s.AllowDecline, s.NestedGroupSizes, Children = s.NestedEffects.Select(Shape).ToList() };
        private static EffectSpecRuntimeState Spec(EffectNodeRuntimeState n) => new EffectSpecRuntimeState { EffectTypeId = n.EffectTypeId, PlayerId = n.PlayerId,
            NormalizedArguments = n.NormalizedArguments, ItemMoveInput = n.ItemMoveInput, Presentation = n.Presentation, DecisionPlayerId = n.DecisionPlayerId, AllowDecline = n.AllowDecline,
            NestedGroupSizes = n.NestedGroupSizes, NestedEffects = n.NestedEffects };
        private static bool Bound(GameState s, EffectNodeRuntimeState root, EffectNodeRuntimeState n)
        {
            var child = n; var seen = new HashSet<string>();
            while (seen.Add(child.EffectId))
            {
                var p = s.EffectRuntime.EffectNodes.SingleOrDefault(c => c.EffectId == child.ParentEffectId && c.PlayerId == root.PlayerId);
                if (p == root) return child.EffectTypeId == LuaDomainEffectTypeIds.Choice && Own(s,root).Contains(child);
                if (p?.EffectTypeId != LuaDomainEffectTypeIds.Choice || !p.ChildEffectIds.Contains(child.EffectId) || p.FlowStage != "branch_completed" || p.NormalizedResult?.Kind != NormalizedValueKind.String) return false;
                int index = Value(p.NormalizedArguments,"options")?.Items.FindIndex(v => v.StringValue == p.NormalizedResult.StringValue) ?? -1;
                if (index < 0 || index >= p.NestedGroupSizes.Count || p.NestedGroupSizes[index] != 1) return false;
                if (ItemTransferService.MovementV4Digest(Shape(p.NestedEffects[p.NestedGroupSizes.Take(index).Sum()])) != ItemTransferService.MovementV4Digest(Shape(Spec(child)))) return false;
                child = p;
            }
            return false;
        }
        public static bool ValidateSaved(GameState s, EffectNodeRuntimeState n)
        {
            if (n.EffectTypeId != TypeId && n.EffectTypeId != SecondTypeId) return n.CarrierPairReturn == null;
            try
            {
                if (n.DefinitionVersion != Version || ValidateArguments(n.EffectTypeId,n.NormalizedArguments).Length > 0 || n.PositionChanges.Count != 0 || n.NestedEffects.Count != 0 ||
                    s.EffectRuntime.InteractionRequests.Any(r => r.OwnerEffectId == n.EffectId)) return false;
                var root = n.EffectTypeId == TypeId ? n : s.EffectRuntime.EffectNodes.SingleOrDefault(c => c.EffectId == Text(n.NormalizedArguments,"rootEffectId"));
                if (root?.EffectTypeId != TypeId || root.DefinitionVersion != Version || root.PlayerId != n.PlayerId || s.FindPlayer(n.PlayerId) == null ||
                    Text(root.NormalizedArguments,"player") != n.PlayerId.ToString(CultureInfo.InvariantCulture)) return false;
                var own = Own(s,n);
                if (n == root && n.FlowStage == "") return own.Count == 0 && n.CarrierPairReturn == null;
                var d = root.CarrierPairReturn; var zone = s.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == d?.ZoneId);
                if (d?.ProtocolVersion != 1 || d.Groups == null || d.TotalCount < 0 || zone?.ParentKind != ZoneParentKind.Item ||
                    zone.ParentId != Text(root.NormalizedArguments,"carrierItemId") || zone.LocalZoneId != Text(root.NormalizedArguments,"localZoneId") ||
                    d.Groups.Any(g => g == null || g.Count < 1 || string.IsNullOrEmpty(g.Key) || string.IsNullOrEmpty(g.First) ||
                        (g.Count > 1 ? string.IsNullOrEmpty(g.Second) || StringComparer.Ordinal.Compare(g.First,g.Second) >= 0 : g.Second != "")) ||
                    d.Groups.Sum(g => (long)g.Count) != d.TotalCount || d.Groups.Select(g => g.Key).Distinct(StringComparer.Ordinal).Count() != d.Groups.Count ||
                    !d.Groups.Select(g => g.First).SequenceEqual(d.Groups.Select(g => g.First).OrderBy(id => id,StringComparer.Ordinal))) return false;
                var ids = d.Groups.SelectMany(g => g.Count > 1 ? new[] { g.First,g.Second } : new[] { g.First }).ToList(); if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) return false;
                if (n != root && (n.CarrierPairReturn != null || !Bound(s,root,n) || PairCount(d) <= 16 ||
                    Number(n.NormalizedArguments,"firstGroupIndex") >= d.Groups.Count)) return false;
                if (n.FlowStage == "") return own.Count == 0;
                if (!new[] { "choosing", "finished" }.Contains(n.FlowStage) || own.Count != 1) return false;
                var expected = n == root ? First(root) : Second(root,Number(n.NormalizedArguments,"firstGroupIndex"));
                if (ItemTransferService.MovementV4Digest(Shape(expected.ToPersistentDefinition())) != ItemTransferService.MovementV4Digest(Shape(Spec(own[0])))) return false;
                if (n.FlowStage != "finished") return true;
                var child = own[0]; if (child.Status != EffectNodeStatus.Completed && child.Status != EffectNodeStatus.Failed) return false;
                var moves = Moves(s,n).ToList(); var result = Result(s,n);
                bool success = child.Status == EffectNodeStatus.Completed && moves.Count == 1 && moves[0].Status == EffectNodeStatus.Completed && Flag(result,"changed");
                bool completionFailure = n.ContinuationContext != null && n.FailureReason == "continuation_child_failed" && n.ChildEffectIds.Any(id =>
                    s.EffectRuntime.EffectNodes.Any(c => c.EffectId == id && c.Status == EffectNodeStatus.Failed && !ItemTransferEffectExecutor.IsExpectedIndependentFailure(c)));
                return ItemTransferService.MovementV4Digest(n.NormalizedResult) == ItemTransferService.MovementV4Digest(result) &&
                    (success ? n.Status == EffectNodeStatus.Completed || n.PendingOutcome == EffectPendingOutcome.Completed ||
                        completionFailure && (n.Status == EffectNodeStatus.Failed || n.PendingOutcome == EffectPendingOutcome.Failed)
                        : n.Status == EffectNodeStatus.Failed || n.PendingOutcome == EffectPendingOutcome.Failed);
            }
            catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is OverflowException) { return false; }
        }
    }
}
