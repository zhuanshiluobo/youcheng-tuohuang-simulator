using System;
using System.Collections.Generic;
using YC.Domain.Cards;
using YC.Domain.Facilities;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

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
            var main = context.State.EffectRuntime.MainNodes.Find(node => node.NodeId == context.State.EffectRuntime.ActiveMainNodeId);
            if (context.State.Phase != GamePhase.Entrance || main == null || main.NodeTypeId != RoundMainlineNodeTypeIds.PlayerEntrance ||
                main.PlayerId != player.PlayerId || main.ExecutionEffectId != context.Node.ParentEffectId)
                return EffectStepResult.Failed("entrance_outside_mainline");
            if (context.Node.FlowStage == "entered")
            {
                foreach (var child in context.ChildNodes)
                {
                    if (child.Status == EffectNodeStatus.Failed)
                        throw new InvalidOperationException("入场响应效果失败：" + child.FailureReason);
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
                    PromptKey = "entrance.choose_location", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1
                };
                request.CandidateIds.AddRange(candidates);
                return EffectStepResult.Continue("choosing_location").AddInteraction(request);
            }
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
            player.CityLocationId = location;
            // 三人地图等地图定义可以为首次合法入场点配置额外奖励；
            // 候选已在提交前复验，因此奖励只会随本次入场 Effect 提交一次。
            if (hasInitialEntranceReward)
                player.Resources.Add(entranceReward);
            BuildFacilityService.EnsureInitialCoreCommandTower(context.State, player);
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

        private List<string> GetCandidates(GameState state)
        {
            var result = new List<string>();
            foreach (var location in map.Map.Locations)
            {
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
