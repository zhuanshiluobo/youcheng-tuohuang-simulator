using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Domain.Rules
{
    /// <summary>窗口只在规则进展/命令事务内登记；读取不改变规则或窗口。</summary>
    public static class OperationWindowService
    {
        public const int SchemaVersion = 1;
        public const int MaximumOpenWindows = 128;
        public const int MaximumClosedWindows = 256;
        public const string RequiredResponse = "required_response";

        public static bool TryResolveSingleMainWindowOnWorking(GameState work, RuleEvent sourceEvent, out OperationCommandContext mainContext)
        {
            mainContext = null;
            var cause = sourceEvent?.OperationContext;
            if (work?.EffectRuntime == null || cause == null) return false;
            var window = work.EffectRuntime.OperationWindows.Find(w => w.OperationWindowId == cause.OriginMainOperationWindowId && w.Lifecycle == OperationWindowLifecycle.Open);
            if (window == null || window.OperationWindowId != Id(work.GameId, window) || window.AuthorizedPlayerIds.Count != 1 ||
                window.WindowKind != RoundMainlineNodeTypeIds.PlayerActionWindow && window.WindowKind != RoundMainlineNodeTypeIds.PlayerCleanupWindow) return false;
            var main = work.EffectRuntime.MainNodes.Find(m => m.NodeId == window.SourceMainNodeId && m.ExecutionEffectId == window.SourceEffectId && !Terminal(m.Status));
            if (main == null || main.PlayerId != window.AuthorizedPlayerIds[0] || main.PlayerId < 1 ||
                !work.EffectRuntime.EffectNodes.Any(n => n.EffectId == main.ExecutionEffectId && !Terminal(n.Status)) ||
                !IsWithin(work.EffectRuntime, sourceEvent.SourceEffectId, main.ExecutionEffectId) ||
                cause.WindowFacts == null || cause.WindowFacts.OperationWindowId != cause.OperationWindowId ||
                cause.WindowFacts.WindowGeneration != cause.WindowGeneration ||
                cause.MainWindowPlayerIdAtCreation != main.PlayerId ||
                cause.OperationWindowId == window.OperationWindowId && cause.WindowGeneration != window.WindowGeneration) return false;
            mainContext = new OperationCommandContext { OperationWindowId = window.OperationWindowId, WindowGeneration = window.WindowGeneration,
                WindowKind = window.WindowKind, SourceMainNodeId = main.NodeId, SourceEffectId = window.SourceEffectId,
                OriginMainOperationWindowId = window.OperationWindowId, MainWindowPlayerIdAtCreation = main.PlayerId,
                ActingPlayerId = cause.ActingPlayerId, SourceCommandId = cause.SourceCommandId, WindowFacts = GameStateCloneService.DeepClone(window) };
            return true;
        }

        public static IReadOnlyList<OperationWindowState> ReadOpenWindows(GameState state)
            => (state?.EffectRuntime?.OperationWindows ?? new List<OperationWindowState>())
                .Where(w => w.Lifecycle == OperationWindowLifecycle.Open).OrderBy(w => w.OpenedOrdinal)
                .Select(GameStateCloneService.DeepClone).ToArray();

        public static bool TryResolveCommandContext(GameState state, GameCommand command,
            out OperationCommandContext context, out ValidationResult validation)
        {
            context = null;
            if (state?.EffectRuntime == null || command == null || state.FindPlayer(command.PlayerId) == null || string.IsNullOrEmpty(command.CommandId))
                return Reject(CommandErrorCode.InvalidPlayer, "操作来源缺少有效会话、玩家或命令ID。", out validation);
            var model = new Model(state);
            Scope source = null;
            EffectNodeRuntimeState commandSource = null;
            string interactionId = Parameter(command, "interactionId");
            string preparationOperation = command.Kind == GameCommandKind.PreparePaymentDraft ? Parameter(command, "operation") : "";
            if (preparationOperation == "prepare_effect_build" || preparationOperation == "cancel_effect_build")
            {
                if (state.EffectRuntime.Status != EffectRuntimeStatus.Active ||
                    !int.TryParse(Parameter(command, "expectedRevision"), NumberStyles.None, CultureInfo.InvariantCulture, out int revision) ||
                    revision != state.EffectRuntime.StateRevision)
                    return Reject(CommandErrorCode.StateRevisionMismatch, "牌堆建设准备命令的状态版本已过期。", out validation);
                if (command.Parameters.ContainsKey("interactionId"))
                    return Reject(CommandErrorCode.InvalidTarget, "牌堆建设准备命令不能替换其请求关联字段。", out validation);
                InteractionRequest request;
                if (preparationOperation == "prepare_effect_build")
                {
                    string ownerId = Parameter(command, "selectionOwnerEffectId"), requestId = Parameter(command, "selectionInteractionId");
                    string candidateSetId = Parameter(command, "selectionCandidateSetId"), sourceZoneId = Parameter(command, "expectedSourceZoneId");
                    if (!int.TryParse(Parameter(command, "selectionCandidateSetVersion"), NumberStyles.None, CultureInfo.InvariantCulture, out int version) || version < 1)
                        return Reject(CommandErrorCode.InvalidTarget, "牌堆建设候选版本无效。", out validation);
                    commandSource = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == ownerId);
                    request = state.EffectRuntime.InteractionRequests.Find(r => r.Status == "open" && r.GetStableInteractionId() == requestId);
                    var candidates = state.EffectRuntime.CandidateResolutions.Find(r => r.CandidateSetId == candidateSetId && r.Version == version);
                    var target = state.ItemZones.Items.Find(i => i.ItemId == command.TargetId);
                    if (commandSource?.EffectTypeId != FacilitySelectionEffectExecutor.TypeId || commandSource.PlayerId != command.PlayerId ||
                        Terminal(commandSource.Status) || commandSource.PendingOutcome != EffectPendingOutcome.None || commandSource.FlowStage != "deck_select" ||
                        !FacilitySelectionEffectExecutor.IsNormalBuildSelection(commandSource) ||
                        request?.OwnerEffectId != ownerId || request.AnsweringPlayerId != command.PlayerId ||
                        request.InteractionTypeId != FacilitySelectionEffectExecutor.NormalBuildInteractionType ||
                        request.CandidateSetId != candidateSetId || request.CandidateSetVersion != version || !request.CandidateIds.Contains(command.TargetId) ||
                        candidates?.SourceEffectId != ownerId || candidates.ExecutingPlayerId != command.PlayerId ||
                        candidates.CandidateKind != YC.Domain.Facilities.FacilityBuildCandidateQuery.CandidateKind || !candidates.FinalCandidateIds.SequenceEqual(request.CandidateIds) ||
                        string.IsNullOrEmpty(sourceZoneId) || target?.ZoneId != sourceZoneId ||
                        !ResourcePaymentPreparationService.ValidateEffectBuildCandidate(state, commandSource, request, command.TargetId,
                            sourceZoneId, Parameter(command, "paymentMode"), true))
                        return Reject(CommandErrorCode.InvalidTarget, "牌堆建设准备未指向本人当前候选合同。", out validation);
                }
                else
                {
                    commandSource = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == Parameter(command, "preparationOwnerEffectId"));
                    if (!ResourcePaymentPreparationService.IsCurrentEffectBuildPreparation(state, commandSource, command.PlayerId) ||
                        commandSource.Payment.Preparation.IsPromoted || commandSource.Payment.IsCommitted || commandSource.Payment.IsCommitRequested)
                        return Reject(CommandErrorCode.InvalidTarget, "牌堆建设取消未指向本人当前付款准备。", out validation);
                    if (ResourcePaymentPreparationService.HasPendingResponse(state, commandSource))
                        return Reject(CommandErrorCode.SettlementRequiredPending, "支付前必要响应尚未结束，不能取消准备。", out validation);
                    request = state.EffectRuntime.InteractionRequests.LastOrDefault(r => r.Status == "open" && r.OwnerEffectId == commandSource.EffectId &&
                        r.AnsweringPlayerId == command.PlayerId && r.InteractionTypeId == ResourceEffectExecutor.PaymentConfirmationInteractionTypeId);
                    if (request == null || !commandSource.Payment.BeforePaymentReady)
                        return Reject(CommandErrorCode.InvalidTarget, "牌堆建设准备没有当前可操作的付款请求。", out validation);
                }
                if (request.StateRevision != revision)
                    return Reject(CommandErrorCode.StateRevisionMismatch, "牌堆建设准备请求已过期。", out validation);
                source = model.RequestScope(request);
                if (source == null)
                    return Reject(CommandErrorCode.NotCurrentPlayer, "牌堆建设请求没有真实操作窗口。", out validation);
            }
            else if (command.Kind == GameCommandKind.AnswerInteraction)
            {
                if (string.IsNullOrEmpty(interactionId)) interactionId = !string.IsNullOrEmpty(command.SourceId) ? command.SourceId : command.TargetId;
                var request = state.EffectRuntime.InteractionRequests.Find(r => r.Status == "open" && r.GetStableInteractionId() == interactionId);
                if (request == null || request.AnsweringPlayerId != command.PlayerId)
                    return Reject(CommandErrorCode.InvalidTarget, "命令未指向本人开放请求。", out validation);
                source = model.RequestScope(request);
                commandSource = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == request.OwnerEffectId);
            }
            else if (!string.IsNullOrEmpty(interactionId))
            {
                var request = state.EffectRuntime.InteractionRequests.Find(r => r.Status == "open" && r.GetStableInteractionId() == interactionId && r.AnsweringPlayerId == command.PlayerId);
                if (request != null)
                { source = model.RequestScope(request); commandSource = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == request.OwnerEffectId); }
            }
            if (source == null && command.Kind == GameCommandKind.InvokeSettlementOption)
            {
                string optionId = Parameter(command, "optionId");
                var owner = state.EffectRuntime.EffectNodes.Find(n => n.SettlementWindow != null && !n.SettlementWindow.IsClosed &&
                    n.SettlementWindow.Context.Id == state.EffectRuntime.ActiveSettlementWindowId &&
                    n.SettlementWindow.Context.PlayerId == command.PlayerId && n.SettlementWindow.Entries.Any(e => e.OptionId == optionId));
                if (owner != null) { source = model.NodeScope(owner); commandSource = owner; }
            }
            if (source == null)
            {
                string kind = command.Kind == GameCommandKind.CoverCharacterCard ? RoundMainlineNodeTypeIds.CharacterCover :
                    command.Kind == GameCommandKind.CollectResource ? RoundMainlineNodeTypeIds.Collection :
                    command.Kind == GameCommandKind.ChooseInitialLocation ? "entrance_player" : null;
                source = model.Scopes.Values.FirstOrDefault(s => s.Window.WindowKind != RequiredResponse &&
                    s.Window.AvailablePlayerIds.Contains(command.PlayerId) && (kind == null
                        ? s.Window.WindowKind == RoundMainlineNodeTypeIds.PlayerActionWindow || s.Window.WindowKind == RoundMainlineNodeTypeIds.PlayerCleanupWindow
                        : s.Window.WindowKind == kind));
            }
            var window = source == null ? null : state.EffectRuntime.OperationWindows.Find(w =>
                w.OperationWindowId == source.Window.OperationWindowId && w.Lifecycle == OperationWindowLifecycle.Open);
            if (window == null || !source.Window.AvailablePlayerIds.Contains(command.PlayerId))
                return Reject(CommandErrorCode.NotCurrentPlayer, "当前真实操作窗口未许可该玩家命令。", out validation);
            if (commandSource == null && !string.IsNullOrEmpty(window.SourceSchedulingGroupId))
            {
                var group = state.EffectRuntime.EffectNodes.SelectMany(n => n.ChildSchedulingGroups).FirstOrDefault(g => g.GroupId == window.SourceSchedulingGroupId);
                commandSource = group?.ChildEffectIds.Select(id => state.EffectRuntime.EffectNodes.Find(n => n.EffectId == id))
                    .FirstOrDefault(n => n != null && n.PlayerId == command.PlayerId && !Terminal(n.Status));
            }
            int mainPlayer = source.SourcePlayer;
            if (mainPlayer < 1 && (window.WindowKind == RoundMainlineNodeTypeIds.CharacterCover ||
                window.WindowKind == RoundMainlineNodeTypeIds.Collection || window.WindowKind == "enterprise_selection"))
                mainPlayer = commandSource?.PlayerId ?? -1;
            string origin = source.OriginId ?? window.OperationWindowId;
            context = new OperationCommandContext {
                OperationWindowId = window.OperationWindowId, WindowGeneration = window.WindowGeneration, WindowKind = window.WindowKind,
                SourceMainNodeId = window.SourceMainNodeId, SourceEffectId = commandSource?.EffectId ?? window.SourceEffectId,
                ActingPlayerId = command.PlayerId, SourceCommandId = command.CommandId,
                OriginMainOperationWindowId = origin, MainWindowPlayerIdAtCreation = mainPlayer,
                WindowFacts = GameStateCloneService.DeepClone(window)
            };
            validation = ValidationResult.Success;
            return true;
        }

        public static void Synchronize(GameState state)
        {
            if (state?.EffectRuntime == null) return;
            var runtime = state.EffectRuntime;
            var model = new Model(state);
            if (model.Scopes.Count == 0 && runtime.OperationWindowSchemaVersion == 0) return;
            runtime.OperationWindowSchemaVersion = SchemaVersion;
            foreach (var scope in model.Scopes.Values.OrderBy(s => s.Window.OperationWindowId, StringComparer.Ordinal))
            {
                var existing = runtime.OperationWindows.Find(w => w.OperationWindowId == scope.Window.OperationWindowId);
                if (existing == null)
                {
                    if (runtime.OperationWindows.Count(w => w.Lifecycle == OperationWindowLifecycle.Open) >= MaximumOpenWindows)
                        throw new InvalidOperationException("operation_window_open_capacity");
                    scope.Window.OpenedOrdinal = runtime.NextOperationWindowOrdinal++;
                    scope.Window.OpenedAtStateRevision = runtime.StateRevision;
                    runtime.OperationWindows.Add(scope.Window);
                }
                else
                {
                    if (existing.Lifecycle != OperationWindowLifecycle.Open || !SameContract(existing, scope.Window))
                        throw new InvalidOperationException("operation_window_contract_conflict");
                    existing.AvailablePlayerIds = scope.Window.AvailablePlayerIds;
                }
            }
            foreach (var window in runtime.OperationWindows.Where(w => w.Lifecycle == OperationWindowLifecycle.Open && !model.Scopes.ContainsKey(w.OperationWindowId)))
            {
                window.Lifecycle = OperationWindowLifecycle.Closed;
                window.ClosedAtStateRevision = runtime.StateRevision;
                window.AvailablePlayerIds.Clear();
            }
            var retired = runtime.OperationWindows.Where(w => w.Lifecycle == OperationWindowLifecycle.Closed)
                .OrderByDescending(w => w.OpenedOrdinal).Skip(MaximumClosedWindows).ToArray();
            foreach (var window in retired) runtime.OperationWindows.Remove(window);
        }

        public static bool TryValidateRuntime(EffectRuntimeState runtime, out string diagnostic)
        {
            diagnostic = "operation_window_state_invalid";
            if (runtime == null || runtime.OperationWindows == null || runtime.OperationWindowSchemaVersion < 0 || runtime.OperationWindowSchemaVersion > SchemaVersion ||
                runtime.NextOperationWindowOrdinal < 1 || runtime.OperationWindows.Count > MaximumOpenWindows + MaximumClosedWindows) return false;
            if (runtime.EffectNodes == null || runtime.EffectNodes.Any(n => n == null) || runtime.RuleEvents == null || runtime.RuleEvents.Any(e => e == null) ||
                runtime.InteractionRequests == null || runtime.InteractionRequests.Any(r => r == null)) return false;
            if (runtime.OperationWindowSchemaVersion == 0 && (runtime.OperationWindows.Count != 0 || runtime.CurrentOperationContext != null)) return false;
            var ids = new HashSet<string>(StringComparer.Ordinal); var ordinals = new HashSet<long>();
            foreach (var window in runtime.OperationWindows)
            {
                if (window == null || string.IsNullOrEmpty(window.OperationWindowId) || !ids.Add(window.OperationWindowId) || window.WindowGeneration < 1 ||
                    string.IsNullOrEmpty(window.WindowKind) || string.IsNullOrEmpty(window.SourceEffectId) || window.OpenedOrdinal < 1 ||
                    window.OpenedOrdinal >= runtime.NextOperationWindowOrdinal || !ordinals.Add(window.OpenedOrdinal) || window.OpenedAtStateRevision < 0 ||
                    window.ClosedAtStateRevision < 0 || window.AuthorizedPlayerIds == null || window.AuthorizedPlayerIds.Count == 0 ||
                    !SortedPlayers(window.AuthorizedPlayerIds) || window.AvailablePlayerIds == null || !SortedPlayers(window.AvailablePlayerIds) ||
                    window.AvailablePlayerIds.Any(p => !window.AuthorizedPlayerIds.Contains(p)) || !Enum.IsDefined(typeof(OperationWindowLifecycle), window.Lifecycle) ||
                    window.Lifecycle == OperationWindowLifecycle.Closed && window.AvailablePlayerIds.Count != 0) return false;
            }
            if (runtime.OperationWindows.Count(w => w.Lifecycle == OperationWindowLifecycle.Open) > MaximumOpenWindows ||
                runtime.OperationWindows.Count(w => w.Lifecycle == OperationWindowLifecycle.Closed) > MaximumClosedWindows) return false;
            var contexts = runtime.EffectNodes.SelectMany(n => new[] { n.OperationContext, n.ContinuationOperationContext })
                .Concat(runtime.RuleEvents.Select(e => e.OperationContext)).Concat(runtime.InteractionRequests.Select(r => r.OperationContext));
            if (runtime.CurrentOperationContext != null) contexts = contexts.Concat(new[] { runtime.CurrentOperationContext });
            foreach (var context in contexts.Where(c => c != null))
            {
                var facts = context.WindowFacts;
                if (string.IsNullOrEmpty(context.OperationWindowId) || context.WindowGeneration < 1 || string.IsNullOrEmpty(context.WindowKind) ||
                    string.IsNullOrEmpty(context.SourceEffectId) || context.ActingPlayerId < 1 || string.IsNullOrEmpty(context.SourceCommandId) ||
                    string.IsNullOrEmpty(context.OriginMainOperationWindowId) || context.MainWindowPlayerIdAtCreation < -1 || context.MainWindowPlayerIdAtCreation == 0 ||
                    runtime.OperationWindowSchemaVersion != SchemaVersion || facts == null || facts.OperationWindowId != context.OperationWindowId ||
                    facts.WindowGeneration != context.WindowGeneration || facts.WindowKind != context.WindowKind || facts.SourceMainNodeId != context.SourceMainNodeId ||
                    facts.Lifecycle != OperationWindowLifecycle.Open || facts.OpenedOrdinal < 1 || facts.OpenedOrdinal >= runtime.NextOperationWindowOrdinal ||
                    facts.AuthorizedPlayerIds == null || !SortedPlayers(facts.AuthorizedPlayerIds) || !facts.AuthorizedPlayerIds.Contains(context.ActingPlayerId) ||
                    facts.AvailablePlayerIds == null || !SortedPlayers(facts.AvailablePlayerIds) || !facts.AvailablePlayerIds.Contains(context.ActingPlayerId)) return false;
                if (context.OriginMainOperationWindowId != (string.IsNullOrEmpty(facts.ParentOperationWindowId)
                    ? facts.OperationWindowId : facts.ParentOperationWindowId)) return false;
            }
            diagnostic = string.Empty; return true;
        }

        public static bool TryValidate(GameState state, out string diagnostic)
        {
            if (!TryValidateRuntime(state?.EffectRuntime, out diagnostic)) return false;
            var runtime = state.EffectRuntime;
            if (runtime.CurrentOperationContext != null) { diagnostic = "operation_command_in_flight"; return false; }
            foreach (var window in runtime.OperationWindows)
            {
                if (window.OperationWindowId != Id(state.GameId, window) || window.AuthorizedPlayerIds.Any(p => state.FindPlayer(p) == null) ||
                    !runtime.EffectNodes.Any(n => n.EffectId == window.SourceEffectId) ||
                    !ValidSource(runtime, window) ||
                    !string.IsNullOrEmpty(window.SourceMainNodeId) && !runtime.MainNodes.Any(n => n.NodeId == window.SourceMainNodeId) ||
                    !string.IsNullOrEmpty(window.SourceSchedulingGroupId) && !runtime.EffectNodes.Any(n => n.ChildSchedulingGroups.Any(g => g.GroupId == window.SourceSchedulingGroupId)) ||
                    window.OpenedAtStateRevision > runtime.StateRevision || window.Lifecycle == OperationWindowLifecycle.Closed &&
                    (window.ClosedAtStateRevision < window.OpenedAtStateRevision || window.ClosedAtStateRevision > runtime.StateRevision))
                { diagnostic = "operation_window_source_invalid"; return false; }
            }
            foreach (var context in runtime.EffectNodes.SelectMany(n => new[] { n.OperationContext, n.ContinuationOperationContext })
                .Concat(runtime.RuleEvents.Select(e => e.OperationContext)).Concat(runtime.InteractionRequests.Select(r => r.OperationContext)).Where(c => c != null))
                if (state.FindPlayer(context.ActingPlayerId) == null || context.MainWindowPlayerIdAtCreation > 0 && state.FindPlayer(context.MainWindowPlayerIdAtCreation) == null ||
                    !runtime.EffectNodes.Any(n => n.EffectId == context.SourceEffectId) || context.WindowFacts.OperationWindowId != Id(state.GameId, context.WindowFacts) ||
                    !ValidSource(runtime, context.WindowFacts) ||
                    runtime.MainNodes.Any(m => m.NodeId == context.SourceMainNodeId && m.PlayerId > 0 && m.PlayerId != context.MainWindowPlayerIdAtCreation) ||
                    runtime.OperationWindows.Any(w => w.OperationWindowId == context.OperationWindowId && !SameContract(w, context.WindowFacts)))
                { diagnostic = "operation_command_source_invalid"; return false; }
            // 版本0旧档没有窗口合同；恢复读取不自动登记。新档开放记录必须对应现有真实输入来源。
            if (runtime.OperationWindowSchemaVersion == SchemaVersion)
            {
                var model = new Model(state);
                foreach (var window in runtime.OperationWindows.Where(w => w.Lifecycle == OperationWindowLifecycle.Open))
                    if (!model.Scopes.TryGetValue(window.OperationWindowId, out var scope) || !SameContract(window, scope.Window) ||
                        !window.AvailablePlayerIds.SequenceEqual(scope.Window.AvailablePlayerIds))
                    { diagnostic = "operation_window_open_fact_mismatch"; return false; }
                if (model.Scopes.Values.Any(s => !runtime.OperationWindows.Any(w => w.OperationWindowId == s.Window.OperationWindowId && w.Lifecycle == OperationWindowLifecycle.Open)))
                { diagnostic = "operation_window_open_fact_missing"; return false; }
            }
            diagnostic = string.Empty; return true;
        }

        private static bool SortedPlayers(List<int> players) => players.All(p => p > 0) && players.SequenceEqual(players.Distinct().OrderBy(p => p));
        private static bool IsWithin(EffectRuntimeState runtime, string nodeId, string ancestorId)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(nodeId) && seen.Add(nodeId))
            {
                if (nodeId == ancestorId) return true;
                nodeId = runtime.EffectNodes.Find(n => n.EffectId == nodeId)?.ParentEffectId;
            }
            return false;
        }
        private static bool ValidSource(EffectRuntimeState runtime, OperationWindowState window)
        {
            var source = runtime.EffectNodes.Find(n => n.EffectId == window.SourceEffectId);
            if (source == null) return false;
            var deferred = runtime.DeferredResponses?.Find(r => r.ResponseRootEffectId == source.EffectId);
            if (deferred != null)
                return window.WindowKind == RequiredResponse && string.IsNullOrEmpty(source.ParentEffectId) &&
                    window.ParentOperationWindowId == deferred.OriginMainOperationWindowId &&
                    window.AuthorizedPlayerIds.SequenceEqual(new[] { deferred.AnsweringPlayerId }) &&
                    source.OperationContext?.SourceMainNodeId == window.SourceMainNodeId;
            var main = runtime.MainNodes.Find(m => m.NodeId == window.SourceMainNodeId);
            if (!string.IsNullOrEmpty(window.SourceMainNodeId) && (main == null || !IsWithin(runtime, source.EffectId, main.ExecutionEffectId))) return false;
            if (window.WindowKind == RoundMainlineNodeTypeIds.PlayerActionWindow || window.WindowKind == RoundMainlineNodeTypeIds.PlayerCleanupWindow)
                return main != null && main.NodeTypeId == window.WindowKind && main.ExecutionEffectId == source.EffectId && window.AuthorizedPlayerIds.SequenceEqual(new[] { main.PlayerId });
            if (window.WindowKind == RequiredResponse)
                return !string.IsNullOrEmpty(window.ParentOperationWindowId) && window.AuthorizedPlayerIds.Count == 1 &&
                    runtime.InteractionRequests.Any(r => r.AnsweringPlayerId == window.AuthorizedPlayerIds[0] && IsWithin(runtime, r.OwnerEffectId, source.EffectId));
            if (window.WindowKind == "effect_input")
                return string.IsNullOrEmpty(source.ParentEffectId) && window.AuthorizedPlayerIds.Count == 1 &&
                    (source.PlayerId < 1 || window.AuthorizedPlayerIds[0] == source.PlayerId);
            string key = window.WindowKind == "entrance_player" ? "entrance_player_tasks" :
                window.WindowKind == "enterprise_selection" ? "initial_enterprise_selections" :
                window.WindowKind == RoundMainlineNodeTypeIds.CharacterCover ? "character_cover_players" :
                window.WindowKind == RoundMainlineNodeTypeIds.Collection ? "collection_players" : null;
            if (key == null) return false;
            var group = runtime.EffectNodes.SelectMany(n => n.ChildSchedulingGroups).FirstOrDefault(g => g.GroupId == window.SourceSchedulingGroupId &&
                g.StableGroupKey == key && g.Mode == EffectChildSchedulingMode.Independent);
            if (group == null) return false;
            if (window.WindowKind == "entrance_player") return group.ChildEffectIds.Contains(source.EffectId) &&
                source.EffectTypeId == RoundExecutionService.PlayerEntranceTaskTypeId && window.AuthorizedPlayerIds.SequenceEqual(new[] { source.PlayerId });
            return group.OwnerEffectId == source.EffectId && window.AuthorizedPlayerIds.SequenceEqual(group.ChildEffectIds
                .Select(id => runtime.EffectNodes.Find(n => n.EffectId == id)?.PlayerId ?? -1).Distinct().OrderBy(p => p));
        }
        private static bool Reject(CommandErrorCode code, string message, out ValidationResult validation)
        { validation = ValidationResult.Failure(code, message); return false; }
        private static string Parameter(GameCommand command, string key) => command.Parameters != null && command.Parameters.TryGetValue(key, out var v) ? v : string.Empty;
        private static bool Terminal(EffectNodeStatus status) => status == EffectNodeStatus.Completed || status == EffectNodeStatus.Failed || status == EffectNodeStatus.Faulted;
        private static string Id(string gameId, OperationWindowState w) => StableIdFactory.Create("operation_window", gameId ?? string.Empty,
            w.WindowKind, w.SourceMainNodeId, w.SourceEffectId, w.SourceSchedulingGroupId, w.ParentOperationWindowId, w.WindowGeneration.ToString(CultureInfo.InvariantCulture),
            string.Join(",", w.AuthorizedPlayerIds.Select(p => p.ToString(CultureInfo.InvariantCulture))));
        private static bool SameSource(OperationWindowState a, OperationWindowState b) => a.WindowKind == b.WindowKind &&
            a.SourceMainNodeId == b.SourceMainNodeId && a.SourceEffectId == b.SourceEffectId && a.SourceSchedulingGroupId == b.SourceSchedulingGroupId &&
            a.ParentOperationWindowId == b.ParentOperationWindowId && a.AuthorizedPlayerIds.SequenceEqual(b.AuthorizedPlayerIds);
        private static bool SameContract(OperationWindowState a, OperationWindowState b) => a.WindowGeneration == b.WindowGeneration && a.WindowKind == b.WindowKind &&
            a.SourceMainNodeId == b.SourceMainNodeId && a.SourceEffectId == b.SourceEffectId && a.SourceSchedulingGroupId == b.SourceSchedulingGroupId &&
            a.ParentOperationWindowId == b.ParentOperationWindowId && a.AuthorizedPlayerIds.SequenceEqual(b.AuthorizedPlayerIds);

        private sealed class Scope
        {
            public OperationWindowState Window;
            public int SourcePlayer = -1;
            public string OriginId;
            public DeferredResponseState DeferredResponse;
        }
        private sealed class Model
        {
            private readonly GameState state;
            private readonly EffectRuntimeState runtime;
            private readonly Dictionary<string, EffectNodeRuntimeState> nodes;
            private readonly Dictionary<string, Scope> effectScopes = new Dictionary<string, Scope>(StringComparer.Ordinal);
            public readonly Dictionary<string, Scope> Scopes = new Dictionary<string, Scope>(StringComparer.Ordinal);
            public Model(GameState state)
            {
                this.state = state; runtime = state.EffectRuntime;
                nodes = runtime.EffectNodes.ToDictionary(n => n.EffectId, StringComparer.Ordinal);
                // 回答和后继执行之间仍保留来源窗口，直到来源树终止。
                foreach (var known in runtime.OperationWindows.Where(w => w.Lifecycle == OperationWindowLifecycle.Open &&
                    (w.WindowKind == RequiredResponse || w.WindowKind == "effect_input")))
                {
                    var owner = Node(known.SourceEffectId);
                    if (owner == null || Terminal(owner.Status)) continue;
                    if (known.WindowKind == "effect_input" && owner.PlayerId < 1 && runtime.InteractionRequests.Any(r => r.Status == "open" &&
                        !known.AuthorizedPlayerIds.Contains(r.AnsweringPlayerId) && Ancestors(Node(r.OwnerEffectId)).Any(n => n.EffectId == owner.EffectId))) continue;
                    var retained = Add(known.WindowKind, known.SourceMainNodeId, known.SourceEffectId, known.SourceSchedulingGroupId,
                        known.ParentOperationWindowId, known.AuthorizedPlayerIds);
                    if (known.WindowKind == "effect_input")
                    {
                        retained.SourcePlayer = owner.PlayerId;
                        if (owner.PlayerId > 0) effectScopes[owner.EffectId] = retained;
                    }
                }
                foreach (var main in runtime.MainNodes.Where(m => !Terminal(m.Status) && !string.IsNullOrEmpty(m.ExecutionEffectId)))
                {
                    var execution = Node(main.ExecutionEffectId);
                    if (execution == null || Terminal(execution.Status)) continue;
                    if (main.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow || main.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow)
                    {
                        var scope = Add(main.NodeTypeId, main.NodeId, execution.EffectId, "", "", new[] { main.PlayerId });
                        scope.SourcePlayer = main.PlayerId; scope.Window.AvailablePlayerIds.Add(main.PlayerId); effectScopes[execution.EffectId] = scope;
                    }
                }
                foreach (var owner in runtime.EffectNodes.Where(n => !Terminal(n.Status)))
                    foreach (var group in owner.ChildSchedulingGroups.Where(g => g.Mode == EffectChildSchedulingMode.Independent && g.OwnerEffectId == owner.EffectId))
                    {
                        var children = group.ChildEffectIds.Select(Node).Where(n => n != null).ToArray();
                        string mainId = MainId(owner);
                        if (group.StableGroupKey == "entrance_player_tasks")
                        {
                            foreach (var task in children.Where(n => !Terminal(n.Status)))
                            {
                                var scope = Add("entrance_player", mainId, task.EffectId, group.GroupId, "", new[] { task.PlayerId });
                                scope.SourcePlayer = task.PlayerId; effectScopes[task.EffectId] = scope;
                            }
                            continue;
                        }
                        string kind = group.StableGroupKey == "initial_enterprise_selections" ? "enterprise_selection" :
                            group.StableGroupKey == "character_cover_players" ? RoundMainlineNodeTypeIds.CharacterCover :
                            group.StableGroupKey == "collection_players" ? RoundMainlineNodeTypeIds.Collection : null;
                        if (kind == null || !children.Any(n => !Terminal(n.Status))) continue;
                        var shared = Add(kind, mainId, owner.EffectId, group.GroupId, "", children.Select(n => n.PlayerId));
                        foreach (var child in children)
                        {
                            effectScopes[child.EffectId] = shared;
                            if (kind == RoundMainlineNodeTypeIds.CharacterCover && !Terminal(child.Status) &&
                                !runtime.MainNodes.Any(m => m.NodeId == mainId && m.CompletedPlayerIds.Contains(child.PlayerId)))
                                shared.Window.AvailablePlayerIds.Add(child.PlayerId);
                        }
                    }
                foreach (var response in runtime.DeferredResponses.Where(r => r.Status == DeferredResponseStatus.Pending))
                {
                    var root = Node(response.ResponseRootEffectId);
                    if (root == null || Terminal(root.Status) || !EffectTreeExecutor.TryFindDeferredResponse(state, root.EffectId, out _)) continue;
                    var scope = Add(RequiredResponse, root.OperationContext.SourceMainNodeId, root.EffectId, "", response.OriginMainOperationWindowId,
                        new[] { response.AnsweringPlayerId });
                    scope.SourcePlayer = response.MainWindowPlayerIdAtCreation; scope.OriginId = response.OriginMainOperationWindowId;
                    scope.DeferredResponse = response; effectScopes[root.EffectId] = scope;
                }
                var directInputs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var request in runtime.InteractionRequests.Where(r => r.Status == "open" && r.AnsweringPlayerId > 0))
                {
                    var scope = RequestScope(request);
                    if (scope != null && !request.IsInternalMainlineCompletion())
                    {
                        scope.Window.AvailablePlayerIds.Add(request.AnsweringPlayerId);
                        directInputs.Add(scope.Window.OperationWindowId + ":" + request.AnsweringPlayerId.ToString(CultureInfo.InvariantCulture));
                    }
                }
                // 必要他人响应只暂停其真实源参与者；同组其他参与者仍可继续，同人的真实输入仍可回答。
                foreach (var response in Scopes.Values.Where(s => s.Window.WindowKind == RequiredResponse && s.Window.AvailablePlayerIds.Count > 0))
                    if (response.DeferredResponse == null && response.SourcePlayer > 0 && Scopes.TryGetValue(response.Window.ParentOperationWindowId, out var parent) &&
                        !directInputs.Contains(parent.Window.OperationWindowId + ":" + response.SourcePlayer.ToString(CultureInfo.InvariantCulture)))
                        parent.Window.AvailablePlayerIds.Remove(response.SourcePlayer);
                foreach (var scope in Scopes.Values)
                    scope.Window.AvailablePlayerIds = scope.Window.AvailablePlayerIds.Distinct().OrderBy(p => p).ToList();
            }
            private EffectNodeRuntimeState Node(string id) => !string.IsNullOrEmpty(id) && nodes.TryGetValue(id, out var node) ? node : null;
            private IEnumerable<EffectNodeRuntimeState> Ancestors(EffectNodeRuntimeState node)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (node != null && visited.Add(node.EffectId)) { yield return node; node = Node(node.ParentEffectId); }
            }
            private string MainId(EffectNodeRuntimeState node)
            {
                var ancestors = new HashSet<string>(Ancestors(node).Select(n => n.EffectId), StringComparer.Ordinal);
                return runtime.MainNodes.FirstOrDefault(m => ancestors.Contains(m.ExecutionEffectId))?.NodeId ?? string.Empty;
            }
            public Scope NodeScope(EffectNodeRuntimeState node)
            {
                var ancestors = Ancestors(node).ToArray();
                var scope = ancestors.Select(n => effectScopes.TryGetValue(n.EffectId, out var s) ? s : null).FirstOrDefault(s => s != null);
                if (scope != null) return scope;
                return ancestors.Select(n => n.OperationContext?.OriginMainOperationWindowId)
                    .Where(id => !string.IsNullOrEmpty(id)).Select(id => Scopes.TryGetValue(id, out var s) ? s : null).FirstOrDefault(s => s != null);
            }
            public Scope RequestScope(InteractionRequest request)
            {
                var owner = Node(request.OwnerEffectId);
                if (owner == null || Terminal(owner.Status)) return null;
                var ancestors = Ancestors(owner).ToArray();
                var baseScope = NodeScope(owner);
                if (baseScope?.DeferredResponse != null)
                    return request.AnsweringPlayerId == baseScope.DeferredResponse.AnsweringPlayerId ? baseScope : null;
                int sourcePlayer = baseScope?.SourcePlayer ?? -1;
                if (baseScope != null && sourcePlayer < 1)
                    sourcePlayer = ancestors.FirstOrDefault(n => effectScopes.TryGetValue(n.EffectId, out var s) && ReferenceEquals(s, baseScope))?.PlayerId ?? -1;
                if (baseScope == null)
                {
                    var root = ancestors.Last();
                    sourcePlayer = root.PlayerId;
                    baseScope = Add("effect_input", MainId(owner), root.EffectId, "", "", new[] { sourcePlayer > 0 ? sourcePlayer : request.AnsweringPlayerId });
                    baseScope.SourcePlayer = sourcePlayer;
                    if (sourcePlayer > 0) effectScopes[root.EffectId] = baseScope;
                }
                if (sourcePlayer < 1 || sourcePlayer == request.AnsweringPlayerId)
                {
                    // 并发窗口每次来源仍是本次真实玩家任务，而不是主链的CurrentPlayerId。
                    return new Scope { Window = baseScope.Window, SourcePlayer = sourcePlayer, OriginId = baseScope.Window.OperationWindowId };
                }
                var branch = ancestors.TakeWhile(n => !effectScopes.ContainsKey(n.EffectId)).LastOrDefault(n => n.PlayerId == request.AnsweringPlayerId) ?? owner;
                var response = Add(RequiredResponse, baseScope.Window.SourceMainNodeId, branch.EffectId, "", baseScope.Window.OperationWindowId, new[] { request.AnsweringPlayerId });
                response.SourcePlayer = sourcePlayer; response.OriginId = baseScope.Window.OperationWindowId;
                return response;
            }
            private Scope Add(string kind, string mainId, string effectId, string groupId, string parentId, IEnumerable<int> players)
            {
                var window = new OperationWindowState { WindowKind = kind, SourceMainNodeId = mainId, SourceEffectId = effectId,
                    SourceSchedulingGroupId = groupId, ParentOperationWindowId = parentId, AuthorizedPlayerIds = players.Distinct().OrderBy(p => p).ToList() };
                var history = runtime.OperationWindows.Where(w => SameSource(w, window)).ToArray();
                var open = history.FirstOrDefault(w => w.Lifecycle == OperationWindowLifecycle.Open);
                if (open != null) window.WindowGeneration = open.WindowGeneration;
                else
                {
                    int priorGeneration = history.Select(w => w.WindowGeneration).DefaultIfEmpty(0).Max();
                    // 关闭索引已退休时，由永久因果快照续代；不生成/登记新记录的读取仍是纯函数。
                    if (history.Length == 0)
                    {
                        var facts = runtime.RuleEvents.Select(e => e.OperationContext?.WindowFacts)
                            .Concat(runtime.InteractionRequests.Select(r => r.OperationContext?.WindowFacts))
                            .Concat(runtime.EffectNodes.SelectMany(n => new[] { n.OperationContext?.WindowFacts, n.ContinuationOperationContext?.WindowFacts }));
                        priorGeneration = facts.Where(w => w != null && SameSource(w, window)).Select(w => w.WindowGeneration).DefaultIfEmpty(0).Max();
                    }
                    window.WindowGeneration = checked(priorGeneration + 1);
                }
                window.OperationWindowId = Id(state.GameId, window);
                if (Scopes.TryGetValue(window.OperationWindowId, out var existing)) return existing;
                var scope = new Scope { Window = window }; Scopes.Add(window.OperationWindowId, scope); return scope;
            }
        }
    }
}
