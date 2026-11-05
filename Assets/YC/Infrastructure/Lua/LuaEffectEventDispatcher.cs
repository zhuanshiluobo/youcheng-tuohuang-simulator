using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Domain.Travel;

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
    public sealed partial class LuaEffectEventDispatcher
    {
        // 仅当前线程显式采样；无监听时不取时钟、不创建采样对象，永不落盘或进入规则状态。
        [ThreadStatic] public static Action<string, string, long> CpuTimingSample;
        private readonly struct CpuSample : IDisposable
        {
            private readonly Action<string, string, long> record;
            private readonly string phase, handler;
            private readonly long started;
            internal CpuSample(string phase, LuaHandlerDefinition definition)
            {
                record = CpuTimingSample; this.phase = phase;
                handler = record == null ? null : definition?.Script?.HandlerId;
                started = record == null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
            }
            public void Dispose()
            {
                if (record == null) return;
                try { record(phase, handler, System.Diagnostics.Stopwatch.GetTimestamp() - started); }
                catch (Exception) { } // 诊断监听不得替换业务结果或原始异常。
            }
        }
        [ThreadStatic] private static bool eagerQuerySnapshots, verifyDeferredQuerySnapshots;
        public static IDisposable UseEagerQuerySnapshotsForDiagnostics() => new QuerySnapshotMode(true,false);
        public static IDisposable VerifyDeferredQuerySnapshotsForDiagnostics() => new QuerySnapshotMode(false,true);
        private sealed class QuerySnapshotMode : IDisposable
        {
            private readonly bool eager=eagerQuerySnapshots,verify=verifyDeferredQuerySnapshots;
            private bool closed;
            internal QuerySnapshotMode(bool full,bool compare){eagerQuerySnapshots=full;verifyDeferredQuerySnapshots=compare;}
            public void Dispose(){if(closed)return;closed=true;eagerQuerySnapshots=eager;verifyDeferredQuerySnapshots=verify;}
        }
        private static Func<LuaSnapshotValue> CheckedQueryFactory(Func<LuaSnapshotValue> build)
        {
            if(!verifyDeferredQuerySnapshots)return build;
            var expected=build();
            return ()=> {
                var actual=build();
                if(Newtonsoft.Json.JsonConvert.SerializeObject(actual)!=Newtonsoft.Json.JsonConvert.SerializeObject(expected))
                    throw new InvalidOperationException("延后Lua只读快照与原提前构建结果不一致。");
                return actual;
            };
        }
        private LuaInvocationResult InvokeScript(LuaScriptDefinition definition,LuaInvocationContext context)
        {
            try
            {
                var result=host.Invoke(definition,context);
                int unused=context.FacadeSnapshot.DeferredQueryMask;
                if((unused&1)!=0)using(GameStateCloneService.MeasureDiagnostic("lua.snapshot.deferred.unused_facilities")){}
                if((unused&2)!=0)using(GameStateCloneService.MeasureDiagnostic("lua.snapshot.deferred.unused_map")){}
                if((unused&4)!=0)using(GameStateCloneService.MeasureDiagnostic("lua.snapshot.deferred.unused_content")){}
                if(result.IsSuccess && verifyDeferredQuerySnapshots)context.FacadeSnapshot.MaterializeDeferredQueries();
                return result;
            }
            finally {context.FacadeSnapshot?.ReleaseDeferredQueries();}
        }
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

        public IList<CandidatePatch> InvokePersistentCandidatePatches(LuaHandlerDefinition definition, GameState state, CandidateSetDraft draft, string cardInstanceId)
        {
            if (string.IsNullOrEmpty(cardInstanceId) || !YC.Domain.Cards.CharacterLifecycleService.IsSubscriptionActive(state, cardInstanceId, definition.AbilityId) ||
                state.ItemZones.Items.Find(item => item.ItemId == cardInstanceId)?.ControllerSubject.PlayerId != draft.ExecutingPlayerId)
                throw new InvalidOperationException("facility_candidate_subscription_inactive");
            return InvokeCandidatePatches(definition, state, draft, cardInstanceId);
        }

        private IList<CandidatePatch> InvokeCandidatePatches(
            LuaHandlerDefinition definition,
            GameState state,
            CandidateSetDraft draft, string cardInstanceId = "")
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
                Payload = NormalizedValue.CreateNull()
            };
            var owner = new EffectNodeRuntimeState
            {
                EffectId = string.IsNullOrEmpty(draft.SourceEffectId) ? draft.CandidateSetId : draft.SourceEffectId,
                PlayerId = draft.ExecutingPlayerId
            };
            var handlerContext = new EffectEventHandlerContext(state, ruleEvent, owner, null);
            var invocationContext = cardInstanceId.Length == 0 ? CreateInvocationContext(invocationDefinition, handlerContext) :
                CreatePureCandidateContext(invocationDefinition, state, draft, ruleEvent.Payload);
            // 候选数组只在本地本次调用物化，不塞进受持久化元素预算约束的NormalizedValue。
            var payload = CreateCandidatePayload(draft).Properties.ToList();
            if (cardInstanceId.Length > 0) payload.Add(new LuaSnapshotEntry("cardInstanceRef", LuaSnapshotValue.String(cardInstanceId)));
            invocationContext.FacadeSnapshot.EventPayload = LuaSnapshotValue.Object(payload);
            invocationContext.Memory = LuaEntryMemoryStore.Read(state.EffectRuntime, definition.Script.EntryId, invocationContext.EntryPoint, definition.Script.ContentHash);
            LuaInvocationResult invocation = host.Invoke(invocationDefinition.Script, invocationContext);
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

        // 纯候选快照只含本次来源池和本人载体，不探测 Host 历史或托管钱包。
        private static LuaInvocationContext CreatePureCandidateContext(LuaHandlerDefinition definition, GameState state, CandidateSetDraft draft, NormalizedValue payload)
        {
            int revision = ItemTransferEffectExecutor.Number(draft.Context, "stateRevision", -1);
            if (revision < 0 || state.EffectRuntime != null && revision != state.EffectRuntime.StateRevision)
                throw new InvalidOperationException("nmc_query_revision_mismatch");
            var snapshot = LuaFacadeSnapshot.FromInvocation(ToPlayerKey(draft.ExecutingPlayerId), state.FindPlayer(draft.ExecutingPlayerId)?.Score ?? 0, 0, state.Players.Count, definition.Script.DefinitionVersion);
            snapshot.GameId = state.GameId; snapshot.PhaseId = state.Phase.ToString(); snapshot.Round = state.Round;
            snapshot.MaxRounds = state.MaxRounds; snapshot.ActionRound = state.ActionRound;
            snapshot.EventPayload = ToLuaSnapshot(payload);
            LuaQuerySnapshotBuilder.Stamp(state, snapshot, revision);
            snapshot.GlobalMap = LuaQuerySnapshotBuilder.Map(state, revision);
            snapshot.GlobalContent = LuaQuerySnapshotBuilder.CandidateContent(state, draft, revision);
            return new LuaInvocationContext(StableIdFactory.Create("event", draft.CandidateSetId, "CandidateSetBuilding"), "CandidateSetBuilding",
                ToPlayerKey(draft.ExecutingPlayerId), revision, definition.Script.DefinitionVersion, state.Players.Count,
                state.FindPlayer(draft.ExecutingPlayerId)?.Score ?? 0, 0, snapshot) { ResponseKind = "candidatePatches" };
        }

        private static LuaSnapshotValue CreateCandidatePayload(CandidateSetDraft draft)
        {
            return LuaSnapshotValue.Object(new List<LuaSnapshotEntry>
            {
                new LuaSnapshotEntry("candidateSetId", LuaSnapshotValue.String(draft.CandidateSetId)),
                new LuaSnapshotEntry("candidateKind", LuaSnapshotValue.String(draft.CandidateKind)),
                new LuaSnapshotEntry("sourceEffectId", LuaSnapshotValue.String(draft.SourceEffectId)),
                new LuaSnapshotEntry("executingPlayerId", LuaSnapshotValue.Integer(draft.ExecutingPlayerId)),
                new LuaSnapshotEntry("candidatePoolIds", CandidateIds(draft.CandidatePoolIds)),
                new LuaSnapshotEntry("defaultCandidateIds", CandidateIds(draft.DefaultCandidateIds)),
                new LuaSnapshotEntry("currentIds", CandidateIds(draft.CurrentIds)),
                new LuaSnapshotEntry("context", ToLuaSnapshot(draft.Context))
            });
        }

        private static LuaSnapshotValue CandidateIds(IList<string> ids) =>
            LuaSnapshotValue.Array(ids.Select(LuaSnapshotValue.String).ToList());

        private static NormalizedValueEntry Entry(string name, NormalizedValue value)
        {
            return new NormalizedValueEntry { Name = name, Value = value };
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
            LuaInvocationContext invocationContext;
            using (CpuTimingSample == null ? default(CpuSample) : new CpuSample("lua.context", definition)) invocationContext = CreateInvocationContextCore(definition, handlerContext,true);
            LuaInvocationResult invocation;
            using (CpuTimingSample == null ? default(CpuSample) : new CpuSample("lua.execute", definition)) invocation = InvokeScript(definition.Script, invocationContext);
            if (!invocation.IsSuccess)
            {
                throw new KernelException(
                    MapLuaFault(invocation.FailureCode),
                    invocation.Diagnostic);
            }

            var compilationContext = new LuaEffectCompilationContext
            {
                State = handlerContext.State, Registry = registry,
                SourceId = definition.Script.ContentId,
                ContentInstanceId = definition.ContentInstanceId,
                AbilityId = string.IsNullOrEmpty(definition.AbilityId)
                    ? definition.Script.AbilityId
                    : definition.AbilityId,
                CompletionHandlerId = definition.CompletionHandlerId,
                StableKeyPrefix = definition.SubscriptionId,
                PlayerId = handlerContext.Event.PlayerId
            };
            string abilityId = compilationContext.AbilityId;
            if (registry.TryResolveAbilityExecutionSource(handlerContext.State, handlerContext.OwnerNode.EffectId,
                abilityId, out var authorizedSource, out var authorizedVersion))
            {
                // 来自已提交调用收据及其真实祖先，不能接受Lua载荷自报的来源或版本。
                if (authorizedVersion.HandlerHash == definition.Script.ContentHash)
                {
                    compilationContext.AuthorizedSource = authorizedSource;
                    compilationContext.AuthorizedVersion = authorizedVersion;
                }
            }
            List<EffectSpec> specs;
            LuaEffectCompilationFault fault;
            bool compiled;
            using (CpuTimingSample == null ? default(CpuSample) : new CpuSample("lua.compile", definition)) compiled = compiler.TryCompile(invocation, compilationContext, out specs, out fault);
            if (!compiled)
            {
                throw new KernelException(
                    fault.Code == LuaFailureCode.UnknownEffectType
                        ? EffectFaultCodes.UnknownEffectType
                        : EffectFaultCodes.InvalidEffectSpec,
                    fault.Diagnostic);
            }

            if (invocation.Memory != null && !SourceFactsLuaCatalog.IsPreviewInvocation(handlerContext))
                LuaEntryMemoryStore.Replace(handlerContext.State.EffectRuntime, definition.Script.EntryId,
                    invocationContext.EntryPoint, definition.Script.ContentHash, invocation.Memory);
            return specs;
        }
        internal NormalizedValue InvokeAbilityQuery(LuaHandlerDefinition definition, EffectEventHandlerContext handlerContext)
        {
            LuaInvocationContext context;
            using (CpuTimingSample == null ? default(CpuSample) : new CpuSample("lua.context", definition)) context = CreateInvocationContextCore(definition, handlerContext,true);
            context.ResponseKind = "abilityRuleQuery";
            LuaInvocationResult invocation;
            using (CpuTimingSample == null ? default(CpuSample) : new CpuSample("lua.execute", definition)) invocation = InvokeScript(definition.Script, context);
            if (!invocation.IsSuccess || invocation.AbilityQueryResult == null) throw new InvalidOperationException("ability_query_lua_invalid:" + invocation.Diagnostic);
            return invocation.AbilityQueryResult;
        }

        private LuaInvocationContext CreateInvocationContext(LuaHandlerDefinition definition,EffectEventHandlerContext handlerContext)
            => CreateInvocationContextCore(definition,handlerContext,false);
        private LuaInvocationContext CreateInvocationContextCore(
            LuaHandlerDefinition definition,
            EffectEventHandlerContext handlerContext,bool allowDeferredQueries)
        {
            RuleEvent ruleEvent = handlerContext.Event;
            GameState state = handlerContext.State;
            bool deferQueries=allowDeferredQueries && !eagerQuerySnapshots && !ReadOnlyStateQueryScope.IsActiveFor(state);
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
                EventPayload = ToLuaSnapshot(AbilityEventLuaFacts.Enrich(state, ruleEvent, ResolveLuaPayload(ruleEvent)))
            };

            AttachAllocationResult(snapshot,definition,handlerContext);
            if (state.FinalScoring?.Session != null && YC.Domain.Scoring.FinalScoringSessionService.Within(
                state, handlerContext.OwnerNode.EffectId, state.FinalScoring.Session.OwnerEffectId))
                snapshot.GlobalScoring = FinalScoringLuaProjection.Build(state, handlerContext.OwnerNode, playerId);
            // 仅BuildFacilityService已有的同步只读作用域内复用公共部分。
            // 每次调用仍独立生成事件/版本/来源事实/记忆；不同Dispatcher和玩家视角分别物化。
            var commonKey=Tuple.Create(this,playerId,ruleEvent.PlayerId);
            if (ReadOnlyStateQueryScope.TryRead<LuaFacadeSnapshot>(state,commonKey,out var common))
            {
                snapshot.StateRevision=common.StateRevision;
                snapshot.EnabledDlcIdsAvailable=common.EnabledDlcIdsAvailable;
                snapshot.EnabledDlcIds.AddRange(common.EnabledDlcIds);
                snapshot.GlobalFacilities=common.GlobalFacilities;
                snapshot.TurnOrder.AddRange(common.TurnOrder);
                snapshot.Players.AddRange(common.Players);
                snapshot.GlobalMap=common.GlobalMap;snapshot.GlobalContent=common.GlobalContent;
            }
            else
            {
            using (CpuTimingSample == null ? default(CpuSample) : new CpuSample("lua.snapshot.stamp", definition)) LuaQuerySnapshotBuilder.Stamp(state, snapshot, state.EffectRuntime.StateRevision,!deferQueries);
            if(deferQueries && YC.Domain.Items.ItemZoneLegacyProjection.IsActive(state))
                snapshot.DeferGlobalFacilities(CheckedQueryFactory(()=> {
                    using var sample=CpuTimingSample==null ? default(CpuSample) : new CpuSample("lua.snapshot.facilities",definition);
                    return FacilityLayerLuaCatalog.CreateProjection(state);
                }));
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

            var playerSnapshots = new Dictionary<int, LuaPlayerSnapshot>();
            if (state.Players != null)
            {
                for (int i = 0; i < state.Players.Count; i++)
                {
                    PlayerState source = state.Players[i];
                    if (source == null) continue;
                    var goldBalance = source.Resources == null ? new GoldVoucherBalance() : RouteTollEscrowService.QueryBalance(state, source.PlayerId);
                    var playerSnapshot = new LuaPlayerSnapshot(
                        ToPlayerKey(source.PlayerId),
                        source.Score,
                        goldBalance.Owned,
                        goldBalance.Available,
                        goldBalance.Reserved,
                        source.CityLocationId,
                        source.Resources == null ? 0 : source.Resources.Originium,
                        source.Resources == null ? 0 : source.Resources.OriginiumShard,
                        source.Resources == null ? 0 : source.Resources.Iron,
                        source.Resources == null ? 0 : source.Resources.PureOriginium);
                    if (source.HandCardIds != null) playerSnapshot.HandCardIds.AddRange(source.HandCardIds.ToArray());
                    if (source.DiscardCardIds != null) playerSnapshot.DiscardCardIds.AddRange(source.DiscardCardIds.ToArray());
                    using (CpuTimingSample == null ? default(CpuSample) : new CpuSample("lua.snapshot.player_facts", definition))
                        LuaQuerySnapshotBuilder.AddContinuationFacts(state, playerSnapshot, source.PlayerId, playerId, registry.EnterpriseDefinitions);
                    snapshot.Players.Add(playerSnapshot);
                    playerSnapshots.Add(source.PlayerId, playerSnapshot);
                }
            }

            if(deferQueries) snapshot.DeferGlobalMap(CheckedQueryFactory(()=> {
                using var sample=CpuTimingSample==null ? default(CpuSample) : new CpuSample("lua.snapshot.map",definition);
                return CreateMapSnapshot(state);
            }));
            else using (CpuTimingSample == null ? default(CpuSample) : new CpuSample("lua.snapshot.map", definition)) snapshot.GlobalMap = CreateMapSnapshot(state);
            var facilities = new List<LuaSnapshotValue>();
            // 兼容列表的每次读取都会重新投影。索引仅属于当前调用，不能按 revision 跨调用复用。
            var placements = state.Map?.Facilities?.ToArray() ?? Array.Empty<FacilityPlacement>();
            var facilityItemIds = new HashSet<string>(StringComparer.Ordinal);
            var enterpriseZoneIds = new HashSet<string>(
                state.ItemZones.Zones.Where(zone => zone.LocalZoneId == "enterprises").Select(zone => zone.ZoneId),
                StringComparer.Ordinal);
            foreach (var placement in placements)
            {
                if (placement == null) continue;
                facilityItemIds.Add(placement.ContentInstanceId);
                var facilityDefinition = YC.Domain.Facilities.FacilityCardDatabase.Get(state, placement.FacilityCardId);
                int slot = placement.CityBoardSlotIndex;
                int core = YC.Domain.Facilities.BuildFacilityService.CoreCommandTowerCityBoardSlotIndex;
                int width = YC.Domain.Facilities.BuildFacilityService.CityBoardSlotCountPerRow;
                bool adjacent = Math.Abs(slot / width - core / width) + Math.Abs(slot % width - core % width) == 1;
                var facilitySnapshot = LuaSnapshotValue.Object(new[] {
                    new LuaSnapshotEntry("id", LuaSnapshotValue.String(placement.ContentInstanceId ?? "")),
                    new LuaSnapshotEntry("definitionId", LuaSnapshotValue.String(placement.FacilityCardId)),
                    new LuaSnapshotEntry("owner", LuaSnapshotValue.String(ToPlayerKey(placement.PlayerId))),
                    new LuaSnapshotEntry("color", LuaSnapshotValue.String(facilityDefinition?.Color ?? "")),
                    new LuaSnapshotEntry("hasEntryEffect", LuaSnapshotValue.Boolean(facilityDefinition != null && facilityDefinition.HasEntryEffect)),
                    new LuaSnapshotEntry("slotIndex", LuaSnapshotValue.Integer(slot)),
                    new LuaSnapshotEntry("adjacentToCore", LuaSnapshotValue.Boolean(adjacent))
                });
                facilities.Add(facilitySnapshot);
                if (playerSnapshots.TryGetValue(placement.PlayerId, out var ownerSnapshot))
                    ownerSnapshot.BuiltFacilities.Add(facilitySnapshot);
            }
            // 只读场上公开企业，不暴露盒内储备或尚未选入的板。
            foreach (var item in state.ItemZones.Items)
                if (enterpriseZoneIds.Contains(item.ZoneId))
                    facilities.Add(LuaSnapshotValue.Object(new[] {
                        new LuaSnapshotEntry("id", LuaSnapshotValue.String(item.ItemId)),
                        new LuaSnapshotEntry("definitionId", LuaSnapshotValue.String(item.DefinitionId)),
                        new LuaSnapshotEntry("contentType", LuaSnapshotValue.String("enterprise")) }));
            YC.Domain.Items.ItemZoneStateView visible;
            using (CpuTimingSample == null ? default(CpuSample) : new CpuSample("lua.snapshot.visibility", definition))
                visible = new YC.Domain.Items.ItemZoneQuery().Project(state, GameStateViewer.Player(ruleEvent.PlayerId));
            foreach (var item in visible.Items.Where(item => !facilityItemIds.Contains(item.ItemId) && !enterpriseZoneIds.Contains(item.ZoneId)))
                facilities.Add(LuaSnapshotValue.Object(new[] {
                    new LuaSnapshotEntry("id", LuaSnapshotValue.String(item.ItemId)), new LuaSnapshotEntry("definitionId", LuaSnapshotValue.String(item.DefinitionId)),
                    new LuaSnapshotEntry("zoneId", LuaSnapshotValue.String(item.ZoneId)), new LuaSnapshotEntry("typeId", LuaSnapshotValue.String(item.TypeId)),
                    new LuaSnapshotEntry("owner", LuaSnapshotValue.String(ToPlayerKey(item.ControllerSubject.PlayerId))) }));
            if(deferQueries) snapshot.DeferGlobalContent(CheckedQueryFactory(()=> {
                using var sample=CpuTimingSample==null ? default(CpuSample) : new CpuSample("lua.snapshot.content",definition);
                return LuaQuerySnapshotBuilder.Content(state,visible,facilities);
            }));
            else using (CpuTimingSample == null ? default(CpuSample) : new CpuSample("lua.snapshot.content", definition)) snapshot.GlobalContent = LuaQuerySnapshotBuilder.Content(state, visible, facilities);
            // 宿主仅把这些快照转换为Lua只读门面；后续调用不写其中列表。
            ReadOnlyStateQueryScope.Store(state,commonKey,snapshot);
            }

            var invocationGold = player?.Resources == null ? null : RouteTollEscrowService.QueryBalance(state, player.PlayerId);
            SourceFactsLuaCatalog.BindReceiptInvocation(handlerContext,state,registry,definition);
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
                invocationGold == null ? 0 : invocationGold.Owned,
                invocationGold == null ? 0 : invocationGold.Available,
                invocationGold == null ? 0 : invocationGold.Reserved,
                snapshot)
            {
                AllowOtherPlayerReads = definition.AllowOtherPlayerReads,
                ResponseKind = definition.ResponseKind,
                Memory = LuaEntryMemoryStore.Read(state.EffectRuntime, definition.Script.EntryId, "", definition.Script.ContentHash),
                SourceFacts = ReadSourceFacts(state, definition, handlerContext, playerId),
                TransferReceipts = SourceFactsLuaCatalog.ReceiptsForInvocation(state, registry, definition, handlerContext, playerId)
            };
        }

        private LuaSnapshotValue ReadSourceFacts(GameState state, LuaHandlerDefinition definition, EffectEventHandlerContext context, int player)
        {
            using var sample = CpuTimingSample == null ? default(CpuSample) : new CpuSample("lua.snapshot.source_facts", definition);
            return SourceFactsLuaCatalog.ForInvocation(state, registry, definition, context, player);
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

        private static LuaSnapshotValue CreateMapSnapshot(GameState state) => LuaQuerySnapshotBuilder.Map(state);

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
