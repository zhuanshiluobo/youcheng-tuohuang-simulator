using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.State;

namespace YC.Infrastructure.Lua
{
    public sealed class LuaHandlerDefinition
    {
        public LuaHandlerDefinition(
            string subscriptionId,
            string eventType,
            string routeKey,
            LuaScriptDefinition script,
            EffectHandlerRole role = EffectHandlerRole.Observer,
            int priority = 0,
            string contentInstanceId = "",
            string abilityId = "",
            string completionHandlerId = "",
            bool allowOtherPlayerReads = false,
            string responseKind = "effects")
        {
            if (string.IsNullOrEmpty(subscriptionId)) throw new ArgumentException("Lua 订阅 ID 不能为空。", nameof(subscriptionId));
            if (string.IsNullOrEmpty(eventType)) throw new ArgumentException("Lua Event 类型不能为空。", nameof(eventType));
            if (script == null) throw new ArgumentNullException(nameof(script));
            SubscriptionId = subscriptionId;
            EventType = eventType;
            RouteKey = routeKey ?? string.Empty;
            Script = script;
            Role = role;
            Priority = priority;
            ContentInstanceId = contentInstanceId ?? string.Empty;
            AbilityId = abilityId ?? string.Empty;
            CompletionHandlerId = completionHandlerId ?? string.Empty;
            AllowOtherPlayerReads = allowOtherPlayerReads;
            ResponseKind = responseKind ?? "effects";
        }

        public string SubscriptionId { get; }
        public string EventType { get; }
        public string RouteKey { get; }
        public LuaScriptDefinition Script { get; }
        public EffectHandlerRole Role { get; }
        public int Priority { get; }
        public string ContentInstanceId { get; }
        public string AbilityId { get; }
        public string CompletionHandlerId { get; }
        public bool AllowOtherPlayerReads { get; }
        public string ResponseKind { get; }
    }

    /// <summary>
    /// 把稳定的 Lua 内容定义接入 Domain 的 Event handler 索引。
    /// 每次回调只产生临时 DTO；EffectSpec 编译完成后，Lua VM 和 table 都丢弃。
    /// </summary>
    public sealed class LuaEffectEventDispatcher
    {
        private readonly MoonSharpLuaRuntimeHost host;
        private readonly LuaEffectSpecCompiler compiler;
        private readonly EffectRegistry registry;

        public LuaEffectEventDispatcher(
            EffectRegistry registry,
            MoonSharpLuaRuntimeHost host = null,
            LuaEffectSpecCompiler compiler = null)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.host = host ?? new MoonSharpLuaRuntimeHost();
            this.compiler = compiler ?? new LuaEffectSpecCompiler();
        }

        public EffectRegistry Registry { get { return registry; } }
        public LuaEffectSpecCompiler Compiler { get { return compiler; } }

        public void Register(LuaHandlerDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            var registration = new EffectEventHandlerRegistration(
                definition.SubscriptionId,
                definition.EventType,
                definition.RouteKey,
                definition.Script.HandlerId,
                context => Invoke(definition, context),
                definition.Role,
                definition.Priority,
                definition.ContentInstanceId,
                string.IsNullOrEmpty(definition.AbilityId) ? definition.Script.AbilityId : definition.AbilityId,
                definition.Script.DefinitionVersion,
                definition.Script.ContentHash,
                definition.Script.ContentId);
            registry.RegisterEventHandler(registration);
        }

        /// <summary>
        /// 注册 CandidateSetBuilding 的 Lua 补丁处理器。候选处理器与普通 Event
        /// handler 共用同一个 Host，但返回值只允许 Candidate.Add/Remove/Intersect。
        /// </summary>
        public void RegisterCandidatePolicy(
            CandidatePolicyRegistry candidatePolicies,
            LuaHandlerDefinition definition,
            string candidateKind,
            int routeTier = 1)
        {
            if (candidatePolicies == null) throw new ArgumentNullException(nameof(candidatePolicies));
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (string.IsNullOrEmpty(candidateKind)) throw new ArgumentException("候选类型不能为空。", nameof(candidateKind));

            candidatePolicies.Register(new CandidatePolicyRegistration(
                definition.SubscriptionId,
                candidateKind,
                candidateContext => InvokeCandidatePatches(definition, candidateContext.State, candidateContext.Draft),
                routeTier,
                definition.Priority,
                definition.ContentInstanceId,
                definition.AbilityId,
                definition.Script.HandlerId));
        }

        private IList<CandidatePatch> InvokeCandidatePatches(
            LuaHandlerDefinition definition,
            GameState state,
            CandidateSetDraft draft)
        {
            if (state == null) throw new InvalidOperationException("候选 Lua handler 缺少权威 GameState。");
            if (draft == null) throw new InvalidOperationException("候选 Lua handler 缺少 CandidateSetDraft。");

            var invocationDefinition = new LuaHandlerDefinition(
                definition.SubscriptionId,
                "CandidateSetBuilding",
                definition.RouteKey,
                definition.Script,
                definition.Role,
                definition.Priority,
                definition.ContentInstanceId,
                definition.AbilityId,
                definition.CompletionHandlerId,
                definition.AllowOtherPlayerReads,
                "candidatePatches");
            var ruleEvent = new RuleEvent
            {
                EventId = StableIdFactory.Create("event", draft.CandidateSetId, "CandidateSetBuilding"),
                EventType = "CandidateSetBuilding",
                SourceEffectId = draft.SourceEffectId ?? string.Empty,
                OwnerNodeId = draft.SourceEffectId ?? string.Empty,
                RouteKey = draft.CandidateKind ?? string.Empty,
                PlayerId = draft.ExecutingPlayerId,
                ResponseKind = RuleEventResponseKind.CandidatePatches,
                Payload = CreateCandidatePayload(draft)
            };
            var owner = new EffectNodeRuntimeState
            {
                EffectId = string.IsNullOrEmpty(draft.SourceEffectId) ? draft.CandidateSetId : draft.SourceEffectId,
                PlayerId = draft.ExecutingPlayerId
            };
            var handlerContext = new EffectEventHandlerContext(state, ruleEvent, owner, null);
            LuaInvocationResult invocation = host.Invoke(
                invocationDefinition.Script,
                CreateInvocationContext(invocationDefinition, handlerContext));
            if (!invocation.IsSuccess)
            {
                throw new InvalidOperationException(invocation.Diagnostic);
            }

            var result = new List<CandidatePatch>();
            for (int i = 0; i < invocation.CandidatePatches.Count; i++)
            {
                LuaCandidatePatch patch = invocation.CandidatePatches[i];
                CandidatePatchOperation operation;
                if (!Enum.TryParse(patch.Operation, true, out operation))
                {
                    throw new InvalidOperationException("候选 Lua handler 返回未知操作：" + patch.Operation);
                }

                result.Add(new CandidatePatch
                {
                    Operation = operation,
                    CandidateIds = new List<string>(patch.Ids),
                    ReasonCode = patch.ReasonCode
                });
            }

            return result;
        }

        private static NormalizedValue CreateCandidatePayload(CandidateSetDraft draft)
        {
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                Entry("candidateSetId", NormalizedValue.CreateString(draft.CandidateSetId)),
                Entry("candidateKind", NormalizedValue.CreateString(draft.CandidateKind)),
                Entry("sourceEffectId", NormalizedValue.CreateString(draft.SourceEffectId)),
                Entry("executingPlayerId", NormalizedValue.CreateInteger(draft.ExecutingPlayerId)),
                Entry("candidatePoolIds", StringArray(draft.CandidatePoolIds)),
                Entry("defaultCandidateIds", StringArray(draft.DefaultCandidateIds)),
                Entry("currentIds", StringArray(draft.CurrentIds))
            });
        }

        private static NormalizedValueEntry Entry(string name, NormalizedValue value)
        {
            return new NormalizedValueEntry { Name = name, Value = value };
        }

        private static NormalizedValue StringArray(IList<string> values)
        {
            var items = new List<NormalizedValue>();
            if (values != null)
            {
                for (int i = 0; i < values.Count; i++) items.Add(NormalizedValue.CreateString(values[i] ?? string.Empty));
            }

            return NormalizedValue.CreateArray(items);
        }

        public void RegisterContinuation(
            LuaHandlerDefinition definition,
            string eventType = "EffectCompleted")
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (definition.Role != EffectHandlerRole.Continuation)
            {
                throw new ArgumentException("continuation 必须使用 Continuation role。", nameof(definition));
            }

            Register(new LuaHandlerDefinition(
                definition.SubscriptionId,
                eventType,
                string.Empty,
                definition.Script,
                EffectHandlerRole.Continuation,
                definition.Priority,
                definition.ContentInstanceId,
                definition.AbilityId,
                definition.CompletionHandlerId,
                definition.AllowOtherPlayerReads));
        }

        internal IList<EffectSpec> Invoke(
            LuaHandlerDefinition definition,
            EffectEventHandlerContext handlerContext)
        {
            LuaInvocationContext invocationContext = CreateInvocationContext(definition, handlerContext);
            LuaInvocationResult invocation = host.Invoke(definition.Script, invocationContext);
            if (!invocation.IsSuccess)
            {
                throw new KernelException(
                    MapLuaFault(invocation.FailureCode),
                    invocation.Diagnostic);
            }

            var compilationContext = new LuaEffectCompilationContext
            {
                SourceId = definition.Script.ContentId,
                ContentInstanceId = definition.ContentInstanceId,
                AbilityId = string.IsNullOrEmpty(definition.AbilityId)
                    ? definition.Script.AbilityId
                    : definition.AbilityId,
                CompletionHandlerId = definition.CompletionHandlerId,
                StableKeyPrefix = definition.SubscriptionId,
                PlayerId = handlerContext.Event.PlayerId
            };
            List<EffectSpec> specs;
            LuaEffectCompilationFault fault;
            if (!compiler.TryCompile(invocation, compilationContext, out specs, out fault))
            {
                throw new KernelException(
                    fault.Code == LuaFailureCode.UnknownEffectType
                        ? EffectFaultCodes.UnknownEffectType
                        : EffectFaultCodes.InvalidEffectSpec,
                    fault.Diagnostic);
            }

            return specs;
        }

        private static LuaInvocationContext CreateInvocationContext(
            LuaHandlerDefinition definition,
            EffectEventHandlerContext handlerContext)
        {
            RuleEvent ruleEvent = handlerContext.Event;
            GameState state = handlerContext.State;
            int playerId = ruleEvent.PlayerId >= 0 ? ruleEvent.PlayerId : handlerContext.OwnerNode.PlayerId;
            string playerKey = ToPlayerKey(playerId);
            PlayerState player = state.FindPlayer(playerId);
            var snapshot = new LuaFacadeSnapshot
            {
                GameId = state.GameId ?? string.Empty,
                PhaseId = state.Phase.ToString(),
                Round = state.Round,
                MaxRounds = state.MaxRounds,
                ActionRound = state.ActionRound,
                StartPlayerId = ToPlayerKey(state.StartPlayerId),
                CurrentPlayerId = ToPlayerKey(state.CurrentPlayerId),
                ContentVersion = definition.Script.DefinitionVersion,
                EventPayload = ToLuaSnapshot(ResolveLuaPayload(ruleEvent))
            };

            IList<int> turnOrder = state.EffectRuntime.PlayerOrderSnapshot;
            if (turnOrder != null && turnOrder.Count > 0)
            {
                for (int i = 0; i < turnOrder.Count; i++) snapshot.TurnOrder.Add(ToPlayerKey(turnOrder[i]));
            }
            else if (state.Players != null)
            {
                for (int i = 0; i < state.Players.Count; i++)
                {
                    if (state.Players[i] != null) snapshot.TurnOrder.Add(ToPlayerKey(state.Players[i].PlayerId));
                }
            }

            if (state.Players != null)
            {
                for (int i = 0; i < state.Players.Count; i++)
                {
                    PlayerState source = state.Players[i];
                    if (source == null) continue;
                    var playerSnapshot = new LuaPlayerSnapshot(
                        ToPlayerKey(source.PlayerId),
                        source.Score,
                        source.Resources == null ? 0 : source.Resources.GoldVoucher,
                        source.CityLocationId,
                        source.Resources == null ? 0 : source.Resources.Originium,
                        source.Resources == null ? 0 : source.Resources.OriginiumShard,
                        source.Resources == null ? 0 : source.Resources.Iron,
                        source.Resources == null ? 0 : source.Resources.PureOriginium);
                    if (source.HandCardIds != null) playerSnapshot.HandCardIds.AddRange(source.HandCardIds);
                    if (source.DiscardCardIds != null) playerSnapshot.DiscardCardIds.AddRange(source.DiscardCardIds);
                    snapshot.Players.Add(playerSnapshot);
                }
            }

            snapshot.GlobalMap = CreateMapSnapshot(state);
            var facilities = new List<LuaSnapshotValue>();
            if (state.Map?.Facilities != null)
                foreach (var placement in state.Map.Facilities)
                {
                    if (placement == null) continue;
                    var facilityDefinition = YC.Domain.Facilities.FacilityCardDatabase.Get(placement.FacilityCardId);
                    int slot = placement.CityBoardSlotIndex;
                    int core = YC.Domain.Facilities.BuildFacilityService.CoreCommandTowerCityBoardSlotIndex;
                    int width = YC.Domain.Facilities.BuildFacilityService.CityBoardSlotCountPerRow;
                    bool adjacent = Math.Abs(slot / width - core / width) + Math.Abs(slot % width - core % width) == 1;
                    facilities.Add(LuaSnapshotValue.Object(new[] {
                        new LuaSnapshotEntry("id", LuaSnapshotValue.String(placement.ContentInstanceId ?? "")),
                        new LuaSnapshotEntry("definitionId", LuaSnapshotValue.String(placement.FacilityCardId)),
                        new LuaSnapshotEntry("owner", LuaSnapshotValue.String(ToPlayerKey(placement.PlayerId))),
                        new LuaSnapshotEntry("color", LuaSnapshotValue.String(facilityDefinition?.Color ?? "")),
                        new LuaSnapshotEntry("hasEntryEffect", LuaSnapshotValue.Boolean(facilityDefinition != null && facilityDefinition.HasEntryEffect)),
                        new LuaSnapshotEntry("slotIndex", LuaSnapshotValue.Integer(slot)),
                        new LuaSnapshotEntry("adjacentToCore", LuaSnapshotValue.Boolean(adjacent))
                    }));
                }
            snapshot.GlobalContent = LuaSnapshotValue.Object(new[] { new LuaSnapshotEntry("instances", LuaSnapshotValue.Array(facilities)) });

            return new LuaInvocationContext(
                ruleEvent.EventId,
                ruleEvent.EventType,
                playerKey,
                state.EffectRuntime.StateRevision,
                // Event 的版本属于发出它的通用执行器；Lua 内容版本属于已验证的 handler。
                // Choice 1.0.0 完成后恢复角色 1.1.0 不应被误判成内容版本不匹配。
                definition.Script.DefinitionVersion,
                state.Players == null ? 0 : state.Players.Count,
                player == null ? 0 : player.Score,
                player == null || player.Resources == null ? 0 : player.Resources.GoldVoucher,
                snapshot)
            {
                AllowOtherPlayerReads = definition.AllowOtherPlayerReads,
                ResponseKind = definition.ResponseKind
            };
        }

        private static string ToPlayerKey(int playerId)
        {
            return playerId < 0 ? "none" : "p" + playerId.ToString(CultureInfo.InvariantCulture);
        }

        private static NormalizedValue ResolveLuaPayload(RuleEvent ruleEvent)
        {
            if (ruleEvent == null) return NormalizedValue.CreateNull();
            if (ruleEvent.HostOnlyPayload != null &&
                ruleEvent.HostOnlyPayload.Kind != NormalizedValueKind.Null)
            {
                return ruleEvent.HostOnlyPayload;
            }

            return ruleEvent.Payload;
        }

        private static LuaSnapshotValue CreateMapSnapshot(GameState state)
        {
            GameMapDefinition map = state.MapId == StaticMapDefinitions.ThreePlayerMapId
                ? StaticMapDefinitions.CreateThreePlayerPlaceholder()
                : StaticMapDefinitions.CreateFourPlayerMap();
            var locations = new List<LuaSnapshotValue>();
            var resourcePoints = new List<LuaSnapshotValue>();
            var redZoneLocationIds = new List<LuaSnapshotValue>();
            var openLocationIds = new HashSet<string>(StringComparer.Ordinal);
            if (state.Map != null && state.Map.OpenLocationIds != null)
            {
                for (int i = 0; i < state.Map.OpenLocationIds.Count; i++)
                {
                    if (!string.IsNullOrEmpty(state.Map.OpenLocationIds[i]))
                    {
                        openLocationIds.Add(state.Map.OpenLocationIds[i]);
                    }
                }
            }

            var definitions = new List<MapLocationDefinition>(map.Locations ?? new List<MapLocationDefinition>());
            definitions.Sort((left, right) => StringComparer.Ordinal.Compare(
                left == null ? string.Empty : left.LocationId,
                right == null ? string.Empty : right.LocationId));
            for (int i = 0; i < definitions.Count; i++)
            {
                MapLocationDefinition location = definitions[i];
                if (location == null) continue;
                var locationSnapshot = LuaSnapshotValue.Object(new[]
                {
                new LuaSnapshotEntry("isOpen", LuaSnapshotValue.Boolean(openLocationIds.Contains(location.LocationId))),
                new LuaSnapshotEntry("isRedZone", LuaSnapshotValue.Boolean(location.IsRedZone)),
                new LuaSnapshotEntry("locationId", LuaSnapshotValue.String(location.LocationId)),
                new LuaSnapshotEntry("regionId", LuaSnapshotValue.String(location.RegionId)),
                new LuaSnapshotEntry("resourceType", LuaSnapshotValue.String(location.ResourceType.ToString())),
                new LuaSnapshotEntry("hasResourcePointIndicator", LuaSnapshotValue.Boolean(
                    HasResourcePointIndicator(state, location.LocationId)))
                });
                locations.Add(locationSnapshot);
                resourcePoints.Add(locationSnapshot);
                if (location.IsRedZone)
                {
                    redZoneLocationIds.Add(LuaSnapshotValue.String(location.LocationId));
                }
            }

            var openIds = new List<string>(openLocationIds);
            openIds.Sort(StringComparer.Ordinal);
            var openSnapshots = new List<LuaSnapshotValue>();
            for (int i = 0; i < openIds.Count; i++) openSnapshots.Add(LuaSnapshotValue.String(openIds[i]));

            var influences = new List<LuaSnapshotValue>();
            if (state.Map != null && state.Map.Influences != null)
            {
                var ordered = new List<InfluencePlacement>(state.Map.Influences);
                ordered.Sort((a, b) => string.CompareOrdinal(a.InfluenceId, b.InfluenceId));
                foreach (var influence in ordered)
                    influences.Add(LuaSnapshotValue.Object(new[]
                    {
                        new LuaSnapshotEntry("influenceId", LuaSnapshotValue.String(influence.InfluenceId)),
                        new LuaSnapshotEntry("ownerPlayerId", LuaSnapshotValue.String(ToPlayerKey(influence.PlayerId))),
                        new LuaSnapshotEntry("slotId", LuaSnapshotValue.String(influence.SlotId))
                    }));
            }

            return LuaSnapshotValue.Object(new[]
            {
                new LuaSnapshotEntry("influences", LuaSnapshotValue.Array(influences)),
                new LuaSnapshotEntry("locations", LuaSnapshotValue.Array(locations)),
                new LuaSnapshotEntry("mapId", LuaSnapshotValue.String(
                    string.IsNullOrEmpty(state.MapId) ? map.MapId : state.MapId)),
                new LuaSnapshotEntry("openLocationIds", LuaSnapshotValue.Array(openSnapshots)),
                new LuaSnapshotEntry("redZoneLocationIds", LuaSnapshotValue.Array(redZoneLocationIds)),
                new LuaSnapshotEntry("resourcePoints", LuaSnapshotValue.Array(resourcePoints))
            });
        }

        private static bool HasResourcePointIndicator(GameState state, string locationId)
        {
            if (state == null || state.Map == null || state.Map.ResourceTokens == null) return false;
            for (int i = 0; i < state.Map.ResourceTokens.Count; i++)
            {
                var token = state.Map.ResourceTokens[i];
                if (token != null && token.LocationId == locationId) return true;
            }

            return false;
        }

        private static LuaSnapshotValue ToLuaSnapshot(NormalizedValue value)
        {
            if (value == null || value.Kind == NormalizedValueKind.Null) return LuaSnapshotValue.Null;
            switch (value.Kind)
            {
                case NormalizedValueKind.Boolean:
                    return LuaSnapshotValue.Boolean(value.BooleanValue);
                case NormalizedValueKind.Integer:
                    return LuaSnapshotValue.Integer(value.IntegerValue);
                case NormalizedValueKind.String:
                case NormalizedValueKind.StableReference:
                    return LuaSnapshotValue.String(value.Kind == NormalizedValueKind.String
                        ? value.StringValue
                        : value.ReferenceId);
                case NormalizedValueKind.Array:
                    var items = new List<LuaSnapshotValue>();
                    if (value.Items != null)
                    {
                        for (int i = 0; i < value.Items.Count; i++) items.Add(ToLuaSnapshot(value.Items[i]));
                    }

                    return LuaSnapshotValue.Array(items);
                case NormalizedValueKind.Object:
                    var properties = new List<LuaSnapshotEntry>();
                    if (value.Properties != null)
                    {
                        for (int i = 0; i < value.Properties.Count; i++)
                        {
                            if (value.Properties[i] != null)
                            {
                                properties.Add(new LuaSnapshotEntry(
                                    value.Properties[i].Name,
                                    ToLuaSnapshot(value.Properties[i].Value)));
                            }
                        }
                    }

                    return LuaSnapshotValue.Object(properties);
                default:
                    return LuaSnapshotValue.Null;
            }
        }

        private static string MapLuaFault(LuaFailureCode code)
        {
            switch (code)
            {
                case LuaFailureCode.ContentHashMismatch:
                    return EffectFaultCodes.ContinuationContentHashMismatch;
                case LuaFailureCode.VersionMismatch:
                    return EffectFaultCodes.DefinitionVersionMismatch;
                case LuaFailureCode.UnknownEffectType:
                case LuaFailureCode.InvalidEffectSpec:
                case LuaFailureCode.InvalidReturnShape:
                case LuaFailureCode.MissingField:
                case LuaFailureCode.InvalidFieldType:
                case LuaFailureCode.UnexpectedField:
                    return EffectFaultCodes.InvalidEffectSpec;
                default:
                    return EffectFaultCodes.EventDispatchException;
            }
        }
    }
}
