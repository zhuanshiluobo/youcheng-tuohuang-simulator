using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Facilities;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Presentation;

namespace YC.Domain.Effects
{
    /// <summary>开局固有流程：选择入场点、放置城市、发布入场事件，等待全部响应子树。</summary>
    public sealed class PlayerEntranceEffectExecutor
    {
        public const string TypeId = "flow.player.enter";
        public const string InteractionTypeId = "entrance.location";
        public const string EventType = "PlayerEntered";
        public const string RulesSubscriptionId = "world.player-entered";
        public const string Version = "1.0.0";
        private readonly IMapQueryService map;
        private PlayerEntranceEffectExecutor(IMapQueryService map) { this.map = map; }

        public static void Register(EffectRegistry registry, IMapQueryService map)
        {
            if (registry.TryGet(TypeId, out _)) return;
            registry.Register(new EffectRegistration(TypeId, new PlayerEntranceEffectExecutor(map).Execute,
                EffectExecutorKind.IntrinsicFlow, Version));
            ResourceEffectExecutor.Register(registry);
        }

        public static EffectSpec Create(int playerId) => new EffectSpec(TypeId)
        { PlayerId = playerId, DefinitionVersion = Version };

        private EffectStepResult Execute(EffectExecutionContext context)
        {
            var player = context.State.FindPlayer(context.Node.PlayerId);
            if (player == null) return EffectStepResult.Failed("entrance_player_missing");
            if (!TryValidateEntranceContext(context.State, context.Node, context.Node.FlowStage != "entered", out string diagnostic))
                return EffectStepResult.Failed(diagnostic);
            if (context.Node.FlowStage == "entered")
            {
                foreach (var child in context.ChildNodes)
                {
                    if (child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted)
                        return EffectStepResult.Failed(child.FailureReason);
                    if (child.Status != EffectNodeStatus.Completed) return EffectStepResult.NoProgress("等待入场事件结算。");
                }
                return EffectStepResult.Completed(context.Node.NormalizedResult);
            }
            var candidates = GetCandidates(context.State);
            if (context.Node.FlowStage == string.Empty)
            {
                if (!string.IsNullOrEmpty(player.CityLocationId))
                    return EffectStepResult.Failed("entrance_already_placed");
                if (candidates.Count == 0) return EffectStepResult.Failed("entrance_no_location");
                var request = new EffectInteractionSpec
                {
                    InteractionTypeId = InteractionTypeId, AnsweringPlayerId = player.PlayerId,
                    PromptKey = "entrance.choose_location", Visibility = GameStateVisibilityPolicy.Owner,
                    AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1,
                    Presentation = new GameplayInteractionPresentation { Prompt = new GameplayTextRef("core:interaction.map.entrance") }
                };
                request.CandidateIds.AddRange(candidates);
                return EffectStepResult.Continue("choosing_location").AddInteraction(request);
            }
            if (context.Node.FlowStage != "choosing_location") return EffectStepResult.Failed("entrance_stage_invalid");
            var answer = context.GetLatestInteractionAnswer();
            if (answer != null && answer.Kind == NormalizedValueKind.Array && answer.Items.Count == 1) answer = answer.Items[0];
            string location = answer == null ? string.Empty : answer.Kind == NormalizedValueKind.StableReference ? answer.ReferenceId : answer.StringValue;
            if (!candidates.Contains(location)) return EffectStepResult.Failed("entrance_location_unavailable");
            if (!context.Registry.HasEventHandler(RulesSubscriptionId))
                throw new InvalidOperationException("未注册玩家入场规则处理器。");
            var entranceLocation = map.GetLocation(location);
            var entranceReward = entranceLocation.InitialEntranceReward;
            var hasInitialEntranceReward = entranceReward != null &&
                (entranceReward.Originium != 0 || entranceReward.OriginiumShard != 0 ||
                 entranceReward.Iron != 0 || entranceReward.PureOriginium != 0 ||
                 entranceReward.GoldVoucher != 0);
            if (YC.Domain.Items.ItemZoneLegacyProjection.IsActive(context.State))
            {
                var city = context.State.ItemZones.Items.Single(i => i.PhysicalKind == YC.Domain.Items.ItemPhysicalKind.MobileCity && i.OwnerSubject.PlayerId == player.PlayerId);
                var parent = context.State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == location);
                var destination = context.State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.city_dock" && z.ParentId == parent.ZoneId);
                new YC.Domain.Items.ItemTransferService().RelocateItems(context, new YC.Domain.Items.ItemTransferRequest { ExecutingPlayerId = player.PlayerId,
                    ItemIds = new List<string> { city.ItemId }, ExpectedSource = new YC.Domain.Items.ItemPositionRef { ZoneId = city.ZoneId }, Destination = new YC.Domain.Items.ZoneRef { ZoneId = destination.ZoneId },
                    InsertionKind = "unordered", OperationProfileId = "core.mobile_city_move", CarrierPolicyId = "preserve", RequiredCount = 1 });
            }
            else player.CityLocationId = location;
            // 三人地图等地图定义可以为首次合法入场点配置额外奖励；
            // 候选已在提交前复验，因此奖励只会随本次入场 Effect 提交一次。
            if (hasInitialEntranceReward)
                player.Resources.Add(entranceReward);
            if (!YC.Domain.Items.ItemZoneLegacyProjection.IsActive(context.State)) BuildFacilityService.EnsureInitialCoreCommandTower(context.State, player);
            if (!context.State.Map.OpenLocationIds.Contains(location)) context.State.Map.OpenLocationIds.Add(location);
            var payload = NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "player", Value = NormalizedValue.CreateStableReference("player", "p" + player.PlayerId) },
                new NormalizedValueEntry { Name = "resourcePoint", Value = NormalizedValue.CreateStableReference("location", location) },
                new NormalizedValueEntry { Name = "initialEntranceRewardApplied", Value = NormalizedValue.CreateBoolean(hasInitialEntranceReward) }
            });
            context.Node.NormalizedResult = payload.Clone();
            return EffectStepResult.Continue("entered").AddEvent(new EffectEventRequest
            {
                EventId = StableIdFactory.Create("event", context.Node.EffectId, EventType), EventType = EventType,
                PlayerId = player.PlayerId, SourceEffectId = context.Node.EffectId, OwnerNodeId = context.Node.EffectId,
                TargetEntityId = location, RouteKey = "entrance", Payload = payload, ResponseKind = RuleEventResponseKind.Effects
            });
        }

        internal static bool TryValidateEntranceContext(GameState state, EffectNodeRuntimeState node,
            bool requirePlacementTurn, out string diagnostic)
        {
            diagnostic = "entrance_outside_mainline";
            var runtime = state?.EffectRuntime;
            var main = runtime?.MainNodes.Find(candidate => candidate.NodeId == runtime.ActiveMainNodeId);
            if (state?.Phase != GamePhase.Entrance || node == null || node.EffectTypeId != TypeId || main == null) return false;
            if (main.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntrance)
            {
                if (main.PlayerId != node.PlayerId || main.ExecutionEffectId != node.ParentEffectId) return false;
                diagnostic = string.Empty;
                return true;
            }
            if (main.NodeTypeId != RoundMainlineNodeTypeIds.PlayerEntranceSetup) return false;
            var execution = runtime.EffectNodes.Find(candidate => candidate.EffectId == main.ExecutionEffectId);
            var task = runtime.EffectNodes.Find(candidate => candidate.EffectId == node.ParentEffectId);
            var group = execution?.ChildSchedulingGroups.FirstOrDefault(candidate => candidate.StableGroupKey == "entrance_player_tasks");
            if (execution == null || task == null || group == null ||
                execution.EffectTypeId != RoundExecutionService.MainlineEffectTypeId ||
                ItemTransferEffectExecutor.Text(execution.NormalizedArguments, "nodeTypeId") != main.NodeTypeId ||
                ItemTransferEffectExecutor.Text(execution.NormalizedArguments, "mainNodeId") != main.NodeId ||
                ItemTransferEffectExecutor.Number(execution.NormalizedArguments, "setupProtocolVersion") != 2 ||
                execution.FlowStage != "awaiting_entrance_tasks" ||
                task.EffectTypeId != RoundExecutionService.PlayerEntranceTaskTypeId || task.ParentEffectId != execution.EffectId ||
                task.PlayerId != node.PlayerId || !task.ChildEffectIds.Contains(node.EffectId) ||
                !execution.ChildEffectIds.Contains(task.EffectId) || group.OwnerEffectId != execution.EffectId ||
                group.ProtocolVersion != 1 || group.Mode != EffectChildSchedulingMode.Independent ||
                !group.ChildEffectIds.Contains(task.EffectId) ||
                ItemTransferEffectExecutor.Text(task.NormalizedArguments, "mainNodeId") != main.NodeId ||
                ItemTransferEffectExecutor.Number(task.NormalizedArguments, "playerIndex", -1) != group.ChildEffectIds.IndexOf(task.EffectId) ||
                ItemTransferEffectExecutor.Number(task.NormalizedArguments, "setupProtocolVersion") != 2) return false;
            if (requirePlacementTurn && RoundExecutionService.GetEntrancePicker(runtime, execution) != node.PlayerId)
            { diagnostic = "entrance_placement_turn_invalid"; return false; }
            diagnostic = string.Empty;
            return true;
        }

        private List<string> GetCandidates(GameState state)
        {
            var result = new List<string>();
            foreach (var location in map.Map.Locations)
            {
                if (state.Players.Count == 2 && location.LocationId == "B-01") continue;
                if (!location.CanDockCity)
                    continue;
                if (map.Map.MapId == StaticMapDefinitions.FourPlayerMapId &&
                    !StaticMapDefinitions.FourPlayerInitialLocationIds.Contains(location.LocationId))
                    continue;
                if (map.Map.MapId == StaticMapDefinitions.ThreePlayerMapId &&
                    location.EventColor != EventColor.Green)
                    continue;
                if (!state.Players.Exists(player => player.CityLocationId == location.LocationId))
                    result.Add(location.LocationId);
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        public static List<EffectSpec> CreateInitialGoldEffects(IList<int> order)
        {
            var effects = new List<EffectSpec>();
            for (int i = 0; i < order.Count; i++)
            {
                int amount = i == 0 ? 10 : order.Count == 4 ? (i == 1 ? 12 : i == 2 ? 14 : 18) :
                    order.Count == 3 ? (i == 1 ? 12 : 18) : 18;
                effects.Add(ResourceEffectSpecFactory.Gain(order[i], ResourceType.GoldVoucher, amount, "setup.initial_gold"));
            }
            return effects;
        }
    }
}
