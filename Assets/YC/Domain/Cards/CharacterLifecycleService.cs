using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Influence;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Cards
{
    /// <summary>真实角色区域、使用上下文及结束策略。所有变更只作用于内核工作副本。</summary>
    public sealed class CharacterLifecycleService
    {
        public const string UseTypeId = "character.use", TransferTypeId = "effect.character.transfer";
        public const string QuickActionTypeId = "character.quick_action";
        public const string SecondModeInteractionTypeId = "character.use.second_mode";
        public const string FinishChoiceId = "finish", CancelChoiceId = "cancel";
        public const string ModeStoppedReason = "character_mode_stopped";
        public const string StoppedUseKey = "stoppedCharacterUseId";
        public const string ConsumeSourceTypeId = "character.consume_double_use_source";
        public const string ModeInteractionTypeId = "character.use.mode", OrderInteractionTypeId = "character.use.order";
        public const string DeferredDependencyStage = "awaiting_deferred_dependencies";
        private const string DeferredResumeStageKey = "deferredResumeStage";
        public static string GetResumableFlowStage(EffectNodeRuntimeState node) =>
            node.FlowStage == DeferredDependencyStage ? Value(node.ContinuationData, DeferredResumeStageKey)?.Kind == NormalizedValueKind.String
                ? Text(node.ContinuationData, DeferredResumeStageKey) : "deferred_resume_stage_invalid" : node.FlowStage;
        public static void ResumeDeferredDependencies(EffectExecutionContext context)
        {
            if (context.Node.FlowStage != DeferredDependencyStage) return;
            var resume = Value(context.Node.ContinuationData, DeferredResumeStageKey);
            if (resume?.Kind != NormalizedValueKind.String) throw new InvalidOperationException("deferred_resume_stage_missing");
            context.Node.FlowStage = resume.StringValue;
            context.Node.ContinuationData.Properties.RemoveAll(entry => entry.Name == DeferredResumeStageKey);
        }
        public static bool TryAwaitDeferredDependencies(EffectExecutionContext context, params DeferredDependencyQuery[] queries)
        {
            var dependencies = new List<DeferredResponseDependency>();
            foreach (var query in queries)
            {
                query.OwnerEffectId = context.Node.EffectId;
                dependencies.AddRange(EffectTreeExecutor.GetDeferredResponseDependencies(context.State, query));
            }
            var unique = dependencies.GroupBy(value => value.DeferredResponseId, StringComparer.Ordinal).Select(group => group.First()).ToList();
            if (unique.Count == 0 || !context.TryAttachDeferredResponseDependenciesOnWorking(unique)) return false;
            Set(context.Node, DeferredResumeStageKey, NormalizedValue.CreateString(context.Node.FlowStage ?? string.Empty));
            return true;
        }
        public static bool TryAwaitCardMoves(EffectExecutionContext context, IEnumerable<string> itemIds, string destinationZoneId)
        {
            var queries = CardMoveDependencyQueries(context.State, itemIds, destinationZoneId).ToArray();
            return TryAwaitDeferredDependencies(context, queries);
        }
        private static IEnumerable<DeferredDependencyQuery> CardMoveDependencyQueries(GameState state, IEnumerable<string> itemIds, string destinationZoneId)
        {
            var destination = state.ItemZones.Zones.Find(zone => zone.ZoneId == destinationZoneId);
            foreach (string id in itemIds.Distinct(StringComparer.Ordinal))
            {
                var item = state.ItemZones.Items.Find(value => value.ItemId == id);
                if (item == null || destination == null || item.ZoneId == destinationZoneId) continue;
                yield return new DeferredDependencyQuery { DependencyKind = DeferredDependencyKind.ExactItemUse, ItemIds = new List<string> { id } };
                bool remains = (destination.TemplateId == "core.zone.character_covered" || destination.TemplateId == "core.zone.character_persistent") &&
                    destination.OwnerPlayerId == item.ControllerSubject.PlayerId;
                if (!remains) yield return new DeferredDependencyQuery { DependencyKind = DeferredDependencyKind.CarrierExit, SourceCardInstanceId = id };
            }
        }
        public static EffectStepResult DeferredDependencyWait() => EffectStepResult.Continue(DeferredDependencyStage);
        public static bool TryAwaitEmptyCarrierExits(EffectExecutionContext context, IEnumerable<string> sourceZoneIds, IEnumerable<string> leavingItemIds)
        {
            var leaving = new HashSet<string>(leavingItemIds, StringComparer.Ordinal);
            var queries = new List<DeferredDependencyQuery>();
            foreach (string id in sourceZoneIds.Distinct(StringComparer.Ordinal))
            {
                var zone = context.State.ItemZones.Zones.Find(value => value.ZoneId == id);
                if (zone?.ParentKind != ZoneParentKind.Item || zone.Configuration?.EmptyExitPolicyId != "core.discard_carrier_when_empty" ||
                    context.State.ItemZones.Items.Any(item => item.ZoneId == id && !leaving.Contains(item.ItemId))) continue;
                var card = context.State.ItemZones.Items.Find(item => item.ItemId == zone.ParentId);
                if (card != null && context.State.ItemZones.Zones.Any(value => value.ZoneId == card.ZoneId && value.TemplateId == "core.zone.character_persistent"))
                    queries.Add(new DeferredDependencyQuery { DependencyKind = DeferredDependencyKind.CarrierExit, SourceCardInstanceId = card.ItemId });
            }
            return TryAwaitDeferredDependencies(context, queries.ToArray());
        }
        public static void Register(EffectRegistry registry)
        {
            CharacterAbilityEffectExecutor.Register(registry);
            RegisterOnce(registry, UseTypeId, ExecuteUse, EffectExecutorKind.IntrinsicFlow);
            RegisterOnce(registry, QuickActionTypeId, ExecuteQuickAction, EffectExecutorKind.IntrinsicFlow);
            RegisterOnce(registry, TransferTypeId, context => {
                string error = ValidateArguments(TransferTypeId, context.Node.NormalizedArguments);
                if (error.Length > 0) return EffectStepResult.Failed(error);
                if (ParsePlayer(Text(context.Node.NormalizedArguments, "player")) != context.Node.PlayerId) return EffectStepResult.Failed("invalid_player");
                return new CharacterLifecycleService().Transfer(context, Text(context.Node.NormalizedArguments, "cardInstanceId"), ParsePlayer(Text(context.Node.NormalizedArguments, "recipientPlayer")));
            });
            RegisterOnce(registry, ConsumeSourceTypeId, ConsumeSource);
            RegisterOnce(registry, LuaDomainEffectTypeIds.SetCharacterDoubleUseRule, SetDoubleUse);
            OperatorDispatchFlow.Register(registry);
        }
        private static void RegisterOnce(EffectRegistry registry, string id, Func<EffectExecutionContext, EffectStepResult> execute, EffectExecutorKind kind = EffectExecutorKind.Atomic)
        {
            if (!registry.TryGet(id, out _)) registry.Register(new EffectRegistration(id, context => { ResumeDeferredDependencies(context); return execute(context); }, kind));
        }
        public static int GetActionRoundSequence(GameState state)
        {
            if (state.Round < 1 || state.ActionRound != 1 && state.ActionRound != 2) throw new InvalidOperationException("action_round_invalid");
            return checked((state.Round - 1) * 2 + state.ActionRound);
        }
        public static CharacterCardDefinition GetDefinition(GameState state, string itemId)
        {
            var item = state.ItemZones.Items.Find(value => value.ItemId == itemId);
            return CharacterCardDatabase.Get(state, item == null ? itemId : item.DefinitionId);
        }
        public static CharacterAvailability GetAvailability(GameState state, int playerId, string itemId)
        {
            var item = state.ItemZones.Items.Find(value => value.ItemId == itemId);
            var slot = state.CharacterCoverSlots.Find(value => value.CardInstanceId == itemId);
            var zone = state.ItemZones.Zones.Find(value => value.ZoneId == item?.ZoneId);
            string reason = state.FindPlayer(playerId) == null ? "invalid_player" :
                item == null ? "unknown_item" : item.ControllerSubject.Kind != ItemSubjectKind.Player || item.ControllerSubject.PlayerId != playerId ? "not_current_holder" :
                state.Phase != GamePhase.ActionRound1 && state.Phase != GamePhase.ActionRound2 ? "wrong_phase" :
                state.CurrentPlayerId != playerId ? "not_current_player" : state.FindPlayer(playerId).CharacterCardLockedThisTurn ? "character_action_round_locked" :
                zone?.TemplateId != "core.zone.character_covered" || slot == null || slot.PlayerId != playerId || item.SlotId != slot.SlotId ? "character_not_covered" :
                !slot.AllowedActionRounds.Contains(state.ActionRound) ? "character_window_closed" :
                slot.BlockedActionRoundSequence >= GetActionRoundSequence(state) ? "character_instance_blocked" :
                state.CharacterUses.Any(use => !use.Finished && !use.SelectionCancelled && use.ExecutingPlayerId == playerId) ? "character_use_in_progress" :
                state.CharacterUses.Any(use => !use.SelectionCancelled && use.CardInstanceId == itemId && use.ActionRoundSequence == GetActionRoundSequence(state)) ? "character_already_used" : "";
            var result = new CharacterAvailability { ItemId = itemId, DefinitionId = item?.DefinitionId ?? "", ZoneId = item?.ZoneId ?? "", SlotId = item?.SlotId ?? "",
                ControllerPlayerId = item?.ControllerSubject.PlayerId ?? -1, BlockedActionRoundSequence = slot?.BlockedActionRoundSequence ?? 0, CanUse = reason.Length == 0, DisabledReasonCode = reason };
            var current = state.CharacterUses.LastOrDefault(use => use.CardInstanceId == itemId && use.ExecutingPlayerId == playerId && !use.Finished);
            if (current != null)
                foreach (var request in state.EffectRuntime.InteractionRequests.Where(value => value.Status == "open" && value.AnsweringPlayerId == playerId))
                    if (TryGetModeStopScope(state, playerId, current.UseId, request.GetStableInteractionId(), state.EffectRuntime.StateRevision, out _, out _))
                    { result.CanStopPendingMode = true; result.PendingUseId = current.UseId; result.PendingModeInteractionId = request.GetStableInteractionId(); break; }
            return result;
        }
        // 只穿过本模式主处理器、普通子式和本模式 continuation；外部事件响应整树保留。
        public static HashSet<string> GetModeOwnedEffects(GameState state, CharacterUseRuntimeState use, EffectNodeRuntimeState activation)
        {
            var owned = new HashSet<string>(StringComparer.Ordinal) { activation.EffectId };
            string ability = use.Modes.Single(mode => mode.Mode == Text(activation.NormalizedArguments, "mode")).AbilityId;
            var pending = new Queue<EffectNodeRuntimeState>(); pending.Enqueue(activation);
            while (pending.Count > 0)
            {
                var parent = pending.Dequeue();
                foreach (string id in parent.ChildEffectIds)
                {
                    var child = state.EffectRuntime.EffectNodes.Single(node => node.EffectId == id);
                    var receipt = state.EffectRuntime.DispatchReceipts.Find(value => value.AttachedEffectIds.Contains(id));
                    if (receipt != null)
                    {
                        var ruleEvent = state.EffectRuntime.RuleEvents.Find(value => value.EventId == receipt.EventId);
                        bool primary = parent.EffectId == activation.EffectId && ruleEvent?.EventType == CharacterAbilityEffectExecutor.CharacterEffectActivatedEventType &&
                            ruleEvent.RouteKey == ability && receipt.RouteTier == (int)EffectRouteTier.Targeted;
                        bool continuation = receipt.HandlerId == parent.ContinuationHandlerId && !string.IsNullOrEmpty(parent.ContinuationBindingId) &&
                            parent.ContinuationAbilityId == ability && receipt.AbilityId == ability &&
                            receipt.ContentInstanceId == parent.ContinuationContentInstanceId && receipt.RouteTier == (int)EffectRouteTier.Targeted;
                        if (!primary && !continuation) continue;
                    }
                    if (owned.Add(id)) pending.Enqueue(child);
                }
            }
            return owned;
        }
        public static bool TryGetModeStopScope(GameState state, int playerId, string useId, string interactionId, int expectedRevision,
            out EffectNodeRuntimeState activation, out HashSet<string> owned)
        {
            activation = null; owned = null;
            var runtime = state?.EffectRuntime;
            if (runtime == null || runtime.Status != EffectRuntimeStatus.Active || runtime.StateRevision != expectedRevision || state.CurrentPlayerId != playerId) return false;
            var use = state.CharacterUses.Find(value => value.UseId == useId);
            if (use == null || use.ExecutingPlayerId != playerId || use.Finished || use.SelectionProtocolVersion < 2 || use.ModeOrder.Count != 2 ||
                use.SkippedModes.Count != 0 || !use.CompletedModes.Contains(use.ModeOrder[0]) || use.CompletedModes.Contains(use.ModeOrder[1])) return false;
            var window = runtime.MainNodes.Find(value => value.NodeId == runtime.ActiveMainNodeId && value.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow && value.PlayerId == playerId);
            var owner = runtime.EffectNodes.Find(value => value.EffectId == use.OwnerEffectId);
            var quick = runtime.EffectNodes.Find(value => value.EffectId == owner?.ParentEffectId && value.EffectTypeId == QuickActionTypeId);
            if (window == null || quick == null || quick.ParentEffectId != window.ExecutionEffectId || owner.FlowStage != "resolving_modes") return false;
            var first = runtime.EffectNodes.Find(value => value.ParentEffectId == use.OwnerEffectId && value.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId && Text(value.NormalizedArguments, "mode") == use.ModeOrder[0]);
            if (first == null || first.Status != EffectNodeStatus.Completed && first.Status != EffectNodeStatus.Failed) return false;
            activation = runtime.EffectNodes.Find(value => value.ParentEffectId == use.OwnerEffectId && value.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId &&
                Text(value.NormalizedArguments, "useId") == useId && Text(value.NormalizedArguments, "mode") == use.ModeOrder[1]);
            if (activation == null || activation.Status == EffectNodeStatus.Completed || activation.Status == EffectNodeStatus.Failed || activation.Status == EffectNodeStatus.Faulted) return false;
            owned = GetModeOwnedEffects(state, use, activation);
            var scope = owned; string activationId = activation.EffectId;
            return runtime.InteractionRequests.Any(request => request.GetStableInteractionId() == interactionId && request.Status == "open" &&
                request.AnsweringPlayerId == playerId && scope.Contains(request.OwnerEffectId) && request.OwnerEffectId != activationId);
        }

        public static EffectSpec CreateUseSpec(int playerId, string cardInstanceId, string mode = "", string order = "")
        {
            var spec = EffectSpec.Create(UseTypeId, Object("cardInstanceId", NormalizedValue.CreateString(cardInstanceId), "mode", NormalizedValue.CreateString(mode), "effectOrder", NormalizedValue.CreateString(order)), playerId);
            spec.Visibility = GameStateVisibilityPolicy.Owner; return spec;
        }

        public static EffectSpec CreateQuickActionSpec(int playerId, string cardInstanceId, string mode = "", string order = "", bool offerSecondEffect = false)
        {
            var spec = CreateUseSpec(playerId, cardInstanceId, mode, order);
            spec.EffectTypeId = QuickActionTypeId;
            SetArgument(spec, "selectionProtocolVersion", NormalizedValue.CreateInteger(3));
            SetArgument(spec, "offerSecondEffect", NormalizedValue.CreateBoolean(offerSecondEffect));
            return spec;
        }
        private static void SetArgument(EffectSpec spec, string name, NormalizedValue value)
        {
            spec.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = name, Value = value });
            spec.NormalizedArguments.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        }
        private static EffectStepResult ExecuteQuickAction(EffectExecutionContext context)
        {
            var node = context.Node;
            if (string.IsNullOrEmpty(node.FlowStage))
            {
                var spec = EffectSpec.Create(UseTypeId, GameStateCloneService.DeepClone(node.NormalizedArguments), node.PlayerId);
                spec.Visibility = GameStateVisibilityPolicy.Owner; spec.StableKey = "character_use";
                return EffectStepResult.Continue("awaiting_use").AddChild(spec);
            }
            if (node.FlowStage != "awaiting_use") throw new InvalidOperationException("character_quick_action_stage_invalid");
            var child = context.State.EffectRuntime.EffectNodes.Single(value => value.ParentEffectId == node.EffectId && value.EffectTypeId == UseTypeId);
            return child.Status == EffectNodeStatus.Failed ? EffectStepResult.Failed(child.FailureReason) :
                EffectStepResult.Completed(GameStateCloneService.DeepClone(child.NormalizedResult));
        }

        private static EffectStepResult ExecuteUse(EffectExecutionContext context)
        {
            context.Node.Visibility = GameStateVisibilityPolicy.Owner;
            var state = context.State; var node = context.Node;
            var use = state.CharacterUses.Find(value => value.OwnerEffectId == node.EffectId);
            if (use == null)
            {
                string id = Text(node.NormalizedArguments, "cardInstanceId");
                var available = GetAvailability(state, node.PlayerId, id);
                if (!available.CanUse) return EffectStepResult.Failed(available.DisabledReasonCode);
                var definition = GetDefinition(state, id);
                if (definition == null) return EffectStepResult.Failed("character_definition_missing");
                use = new CharacterUseRuntimeState { UseId = StableIdFactory.Create("character_use", node.EffectId), OwnerEffectId = node.EffectId,
                    CardInstanceId = id, ExecutingPlayerId = node.PlayerId, ActionRoundSequence = GetActionRoundSequence(state),
                    SelectionProtocolVersion = Number(node.NormalizedArguments, "selectionProtocolVersion") >= 2 ? Number(node.NormalizedArguments, "selectionProtocolVersion") : 1,
                    OfferSecondEffect = Value(node.NormalizedArguments, "offerSecondEffect")?.BooleanValue == true };
                foreach (string mode in new[] { "strategy", "tactic" })
                {
                    string ability = mode == "strategy" ? definition.StrategyAbilityId : definition.TacticAbilityId;
                    if (!context.Registry.CharacterActivationSubscriptions.TryGetValue(ability, out string subscription) || !context.Registry.HasEventHandler(subscription)) return EffectStepResult.Failed("character_ability_missing");
                    if (!context.Registry.CharacterAbilityContracts.TryGetValue(ability, out var configured)) return EffectStepResult.Failed("character_mode_contract_missing");
                    if (configured.Mode != mode || configured.AbilityId != ability || !new[] { "normal", "persistent", "one_shot" }.Contains(configured.SkillType)) return EffectStepResult.Failed("character_mode_contract_invalid");
                    var contract = GameStateCloneService.DeepClone(configured);
                    contract.Activated = false; contract.SubscriptionActive = false;
                    use.Modes.Add(contract);
                }
                use.DoubleUseGranted = state.CharacterActionRoundPermissions.Any(permission => permission.PlayerId == node.PlayerId && permission.ExpiresActionRoundSequence == use.ActionRoundSequence);
                use.CanOfferSecondEffect = use.SelectionProtocolVersion >= 2 && (use.DoubleUseGranted || state.StartPlayerId == node.PlayerId);
                string selectedMode = Text(node.NormalizedArguments, "mode");
                if (selectedMode.Length > 0)
                {
                    if (selectedMode == "both")
                    {
                        if (!CanSelectBoth(context, use)) return EffectStepResult.Failed("character_double_use_not_granted");
                        use.DoubleUseGranted = true;
                        if (!SetOrder(use, Text(node.NormalizedArguments, "effectOrder"))) return EffectStepResult.Failed("character_order_invalid");
                    }
                    else if (selectedMode == "strategy" || selectedMode == "tactic") use.ModeOrder.Add(selectedMode);
                    else return EffectStepResult.Failed("character_mode_invalid");
                }
                state.CharacterUses.Add(use); Set(node, "currentUseId", NormalizedValue.CreateString(use.UseId));
                if (use.SelectionProtocolVersion >= 2) Set(node, "canOfferSecondEffect", NormalizedValue.CreateBoolean(use.CanOfferSecondEffect));
                return use.ModeOrder.Count > 0 ? StartNextMode(context, use) : ChooseMode(context, use);
            }
            if (use.Finished) return EffectStepResult.Completed(Object("useId", NormalizedValue.CreateString(use.UseId)));
            if (node.FlowStage == "awaiting_insertion")
            {
                if (!use.SourceConsumed || !use.DoubleUseGranted) return ChooseMode(context, use);
                return ChooseOrder(context, use);
            }
            if (node.FlowStage == "choose_mode")
            {
                string selected = Answer(context);
                if (selected == CancelChoiceId) return CancelSelection(context, use);
                if (selected == "both" && CanSelectBoth(context, use)) { use.DoubleUseGranted = true; return ChooseOrder(context, use); }
                if (selected != "strategy" && selected != "tactic") return EffectStepResult.Failed("character_mode_invalid");
                use.ModeOrder = new List<string> { selected }; return StartNextMode(context, use);
            }
            if (node.FlowStage == "choose_order")
            {
                string selected = Answer(context);
                if (selected == CancelChoiceId) return CancelSelection(context, use);
                if (!SetOrder(use, selected)) return EffectStepResult.Failed("character_order_invalid");
                return StartNextMode(context, use);
            }
            if (node.FlowStage == "choose_second_mode")
            {
                string selected = Answer(context);
                if (selected == FinishChoiceId) return new CharacterLifecycleService().FinishUse(context, use.UseId);
                if (!ShouldOfferSecondMode(use) || selected != RemainingMode(use)) return EffectStepResult.Failed("character_second_mode_invalid");
                use.DoubleUseGranted = true;
                use.ModeOrder.Add(selected);
                return StartNextMode(context, use);
            }
            if (node.FlowStage == "resolving_modes")
            {
                if (!use.ModeOrder.All(mode => use.CompletedModes.Contains(mode) || use.SkippedModes.Contains(mode))) return StartNextMode(context, use);
                if (use.SelectionProtocolVersion >= 3 && use.CanOfferSecondEffect && use.ModeOrder.Count == 1)
                {
                    use.DoubleUseGranted = true; use.ModeOrder.Add(RemainingMode(use));
                    return StartNextMode(context, use);
                }
                return ShouldOfferSecondMode(use) ? Choice(context, "choose_second_mode", SecondModeInteractionTypeId, new[] { RemainingMode(use), FinishChoiceId }) :
                    new CharacterLifecycleService().FinishUse(context, use.UseId);
            }
            throw new InvalidOperationException("character_use_stage_invalid");
        }
        private static EffectStepResult ChooseMode(EffectExecutionContext context, CharacterUseRuntimeState use)
        {
            if (context.Node.SettlementWindow == null)
                context.Node.SettlementWindow = SettlementEntryService.CreateWindow(context.State, context.Node,
                    SettlementEntryService.CharacterUse, use.ExecutingPlayerId, use.UseId, "");
            if (!context.Node.SettlementWindow.IsClosed)
                context.State.EffectRuntime.ActiveSettlementWindowId = context.Node.SettlementWindow.Context.Id;
            var choices = new List<string> { "strategy", "tactic" };
            if (CanSelectBoth(context, use)) choices.Add("both");
            if (CanCancelSelection(context, use)) choices.Add(CancelChoiceId);
            return Choice(context, "choose_mode", ModeInteractionTypeId, choices);
        }
        private static EffectStepResult ChooseOrder(EffectExecutionContext context, CharacterUseRuntimeState use)
        {
            CloseModeInsertionWindow(context);
            var choices = new List<string> { "strategy-first", "tactic-first" };
            if (CanCancelSelection(context, use)) choices.Add(CancelChoiceId);
            return Choice(context, "choose_order", OrderInteractionTypeId, choices);
        }
        private static bool CanSelectBoth(EffectExecutionContext context, CharacterUseRuntimeState use) =>
            use.DoubleUseGranted || (use.SelectionProtocolVersion >= 2 ? use.CanOfferSecondEffect : context.State.StartPlayerId == use.ExecutingPlayerId);
        private static bool ShouldOfferSecondMode(CharacterUseRuntimeState use) => use.SelectionProtocolVersion == 2 &&
            use.OfferSecondEffect && use.CanOfferSecondEffect && use.ModeOrder.Count == 1 && use.CompletedModes.Count == 1;
        private static string RemainingMode(CharacterUseRuntimeState use) => use.ModeOrder[0] == "strategy" ? "tactic" : "strategy";
        private static bool CanCancelSelection(EffectExecutionContext context, CharacterUseRuntimeState use) =>
            use.SelectionProtocolVersion >= 2 && !use.Finished && !use.SourceConsumed && use.ModeOrder.Count == 0 && use.CompletedModes.Count == 0 &&
            !context.State.EffectRuntime.EffectNodes.Any(node => node.ParentEffectId == use.OwnerEffectId && node.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId);
        private static EffectStepResult CancelSelection(EffectExecutionContext context, CharacterUseRuntimeState use)
        {
            if (!CanCancelSelection(context, use)) return EffectStepResult.Failed("character_selection_cannot_cancel");
            CloseModeInsertionWindow(context);
            use.SelectionCancelled = true; use.Finished = true;
            return EffectStepResult.Completed(Object("useId", NormalizedValue.CreateString(use.UseId), "cancelled", NormalizedValue.CreateBoolean(true)));
        }
        private static EffectStepResult Choice(EffectExecutionContext context, string stage, string type, IEnumerable<string> choices)
        {
            var request = new EffectInteractionSpec { InteractionTypeId = type, AnsweringPlayerId = context.Node.PlayerId, Visibility = "owner", PromptKey = type, MinSelections = 1, MaxSelections = 1, AnswerSchema = "candidate_id" };
            request.CandidateIds.AddRange(choices);
            return EffectStepResult.Continue(stage).AddInteraction(PrepareInteraction(context, request));
        }
        private static bool SetOrder(CharacterUseRuntimeState use, string order)
        {
            if (order != "strategy-first" && order != "tactic-first") return false;
            use.ModeOrder = order == "strategy-first" ? new List<string> { "strategy", "tactic" } : new List<string> { "tactic", "strategy" }; return true;
        }
        private static EffectStepResult StartNextMode(EffectExecutionContext context, CharacterUseRuntimeState use)
        {
            CloseModeInsertionWindow(context);
            string mode = use.ModeOrder.First(value => !use.CompletedModes.Contains(value) && !use.SkippedModes.Contains(value));
            var contract = use.Modes.Single(value => value.Mode == mode);
            var spec = CharacterAbilityEffectExecutor.CreateActivationSpec(use.CardInstanceId, contract.AbilityId, mode, use.ExecutingPlayerId, "mode:" + mode);
            spec.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "useId", Value = NormalizedValue.CreateString(use.UseId) });
            spec.NormalizedArguments.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            return EffectStepResult.Continue("resolving_modes").AddChild(spec);
        }
        private static void CloseModeInsertionWindow(EffectExecutionContext context)
        {
            var window = context.Node.SettlementWindow;
            if (window?.Context?.Kind != SettlementEntryService.CharacterUse) return;
            window.IsClosed = true;
            if (context.State.EffectRuntime.ActiveSettlementWindowId == window.Context.Id)
                context.State.EffectRuntime.ActiveSettlementWindowId = "";
        }
        public EffectStepResult FinishUse(EffectExecutionContext context, string useId)
        {
            ResumeDeferredDependencies(context);
            var use = context.State.CharacterUses.Find(value => value.UseId == useId);
            if (use == null || use.ExecutingPlayerId != context.Node.PlayerId || use.OwnerEffectId != context.Node.EffectId) return EffectStepResult.Failed("character_use_owner_mismatch");
            if (use.Finished) return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(false)));
            if (use.ModeOrder.Count == 0 || use.ModeOrder.Any(mode => !use.CompletedModes.Contains(mode) && !use.SkippedModes.Contains(mode))) return EffectStepResult.Failed("character_modes_incomplete");
            if (context.State.EffectRuntime.EffectNodes.Any(node => node.ParentEffectId == use.OwnerEffectId &&
                node.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId && node.Status != EffectNodeStatus.Completed && node.Status != EffectNodeStatus.Failed))
                return EffectStepResult.Failed("character_mode_responses_incomplete");
            var item = context.State.ItemZones.Items.Single(value => value.ItemId == use.CardInstanceId);
            // 本次执行者是历史事实；已转交、由卡面自行离场或已回盒的牌不再重复收走。
            var zone = context.State.ItemZones.Zones.Single(value => value.ZoneId == item.ZoneId);
            if (item.ControllerSubject.PlayerId == use.ExecutingPlayerId && zone.TemplateId == "core.zone.character_covered")
            {
                string destination = use.CompletedModes.Concat(use.SkippedModes).Any(mode => use.Modes.Single(value => value.Mode == mode).SkillType == "one_shot") ? "retired" :
                    use.Modes.Any(mode => mode.SkillType == "persistent" && mode.Activated) ? "persistent" : "discard";
                if (destination == "persistent" && context.State.ItemZones.Zones.Any(value => value.ParentKind == ZoneParentKind.Item && value.ParentId == item.ItemId &&
                    value.Configuration?.EmptyExitPolicyId == "core.discard_carrier_when_empty" && !context.State.ItemZones.Items.Any(content => content.ZoneId == value.ZoneId))) destination = "discard";
                var target = FindZone(context.State, destination, destination == "retired" ? -1 : use.ExecutingPlayerId);
                if (TryAwaitCardMoves(context, new[] { item.ItemId }, target.ZoneId)) return DeferredDependencyWait();
                string error = MoveCard(context, item.ItemId, target.ZoneId);
                if (error.Length > 0) return EffectStepResult.Failed(error);
            }
            use = context.State.CharacterUses.Single(value => value.UseId == useId);
            item = context.State.ItemZones.Items.Single(value => value.ItemId == use.CardInstanceId);
            use.FinishDestinationZoneId = item.ZoneId; use.Finished = true;
            var player = context.State.FindPlayer(use.ExecutingPlayerId); player.UsedCharacterThisRound = true; player.UsedCharacterThisTurn = true;
            DetachExitedSubscriptions(context.State, item.ItemId);
            return EffectStepResult.Completed(Object("useId", NormalizedValue.CreateString(useId), "changed", NormalizedValue.CreateBoolean(true))).AddEvent(new EffectEventRequest { EventType = "CharacterUseFinished", PlayerId = use.ExecutingPlayerId, Payload = Object("useId", NormalizedValue.CreateString(useId)) });
        }
        public EffectStepResult Transfer(EffectExecutionContext context, string cardInstanceId, int recipientPlayerId)
        {
            ResumeDeferredDependencies(context);
            context.Node.Visibility = GameStateVisibilityPolicy.Owner;
            var item = context.State.ItemZones.Items.Find(value => value.ItemId == cardInstanceId);
            if (recipientPlayerId <= 0 || recipientPlayerId == context.Node.PlayerId || context.State.FindPlayer(recipientPlayerId) == null) return EffectStepResult.Failed("invalid_recipient");
            if (item == null || item.ControllerSubject.Kind != ItemSubjectKind.Player || item.ControllerSubject.PlayerId != context.Node.PlayerId) return EffectStepResult.Failed("not_current_holder");
            string destination = FindZone(context.State, "hand", recipientPlayerId).ZoneId;
            if (TryAwaitCardMoves(context, new[] { cardInstanceId }, destination)) return DeferredDependencyWait();
            string error = MoveCard(context, cardInstanceId, destination);
            if (error.Length > 0) return EffectStepResult.Failed(error);
            return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(true))).AddEvent(new EffectEventRequest {
                EventType = "character.transferred", PlayerId = context.Node.PlayerId, Visibility = "public", ResponseKind = RuleEventResponseKind.Effects,
                Payload = Object("recipientPlayerId", NormalizedValue.CreateInteger(recipientPlayerId)),
                HostOnlyPayload = Object("itemId", NormalizedValue.CreateString(cardInstanceId), "recipientPlayerId", NormalizedValue.CreateInteger(recipientPlayerId)) });
        }
        public static string MoveCard(EffectExecutionContext context, string itemId, string destinationZoneId)
        {
            if (CardMoveDependencyQueries(context.State, new[] { itemId }, destinationZoneId)
                .Any(query => { query.OwnerEffectId = context.Node.EffectId; return EffectTreeExecutor.GetDeferredResponseDependencies(context.State, query).Count > 0; }))
                return "deferred_response_pending";
            var preview = GameStateCloneService.DeepClone(context.State);
            var work = new EffectExecutionContext(preview, preview.EffectRuntime.EffectNodes.Single(value => value.EffectId == context.Node.EffectId), context.Registry);
            string reason = MoveCardCore(work, itemId, destinationZoneId);
            if (reason.Length == 0) GameStateCloneService.CopyTo(context.State, preview);
            return reason;
        }
        private static string MoveCardCore(EffectExecutionContext context, string itemId, string destinationZoneId)
        {
            var item = context.State.ItemZones.Items.Find(value => value.ItemId == itemId);
            if (item == null) return "unknown_item";
            var destinationZone = context.State.ItemZones.Zones.Find(zone => zone.ZoneId == destinationZoneId);
            if (destinationZone == null) return "destination_rejected";
            bool remainsInPlay = (destinationZone.TemplateId == "core.zone.character_covered" || destinationZone.TemplateId == "core.zone.character_persistent") && destinationZone.OwnerPlayerId == item.ControllerSubject.PlayerId;
            // 卡载离场政策逐实例执行；未定义去向的类别明确拒绝，不能销毁剩余内容。
            foreach (var zone in context.State.ItemZones.Zones.Where(value => !remainsInPlay && value.ParentKind == ZoneParentKind.Item && value.ParentId == itemId && value.Configuration?.CarrierExitPolicyId == "core.return_contents_before_exit"))
            foreach (var content in context.State.ItemZones.Items.Where(value => value.ZoneId == zone.ZoneId).ToList())
            {
                string destination; string profile; string insertion = "unordered";
                if (content.PhysicalKind == ItemPhysicalKind.Cube)
                { destination = CubeSupply(context.State, content).ZoneId; profile = "core.cube_storage"; }
                else if (content.TypeId == "core.facility_card")
                { destination = FindZone(context.State, "facility_deck").ZoneId; profile = "core.facility_card_transfer"; insertion = "bottom"; }
                else return "carrier_content_destination_undefined";
                var request = TransferRequest(context.Node.PlayerId, content, destination, profile, insertion);
                var validation = new ItemTransferService().Validate(context.State, request);
                if (!validation.IsValid) return validation.ReasonCode;
                new ItemTransferService().RelocateItems(context, request);
            }
            var move = TransferRequest(context.Node.PlayerId, item, destinationZoneId, "core.character_card_transfer");
            var valid = new ItemTransferService().Validate(context.State, move);
            if (!valid.IsValid) return valid.ReasonCode;
            new ItemTransferService().RelocateItems(context, move);
            foreach (var slot in context.State.CharacterCoverSlots.Where(value => value.CardInstanceId == itemId)) slot.CardInstanceId = "";
            DetachExitedSubscriptions(context.State, itemId);
            return "";
        }
        public static ZoneInstanceState CubeSupply(GameState state, ItemInstanceState cube)
        {
            if (cube.OriginalSupplyZoneId.Length > 0) return state.ItemZones.Zones.Single(zone => zone.ZoneId == cube.OriginalSupplyZoneId);
            if (cube.OwnerSubject.Kind == ItemSubjectKind.Player && cube.TypeId == "core.player_marker") return FindZone(state, "player_markers", cube.OwnerSubject.PlayerId);
            if (cube.OwnerSubject.Kind == ItemSubjectKind.Company) return FindZone(state, "company_markers");
            throw new InvalidOperationException("carrier_content_destination_undefined");
        }
        public static void ExitEmptyCarriers(EffectExecutionContext context, IEnumerable<string> sourceZoneIds)
        {
            foreach (string source in sourceZoneIds.Distinct().ToList())
            {
                var zone = context.State.ItemZones.Zones.Find(value => value.ZoneId == source);
                if (zone?.ParentKind != ZoneParentKind.Item || zone.Configuration?.EmptyExitPolicyId != "core.discard_carrier_when_empty" || context.State.ItemZones.Items.Any(item => item.ZoneId == source)) continue;
                var card = context.State.ItemZones.Items.Single(item => item.ItemId == zone.ParentId);
                if (!context.State.ItemZones.Zones.Any(value => value.ZoneId == card.ZoneId && value.TemplateId == "core.zone.character_persistent")) continue;
                if (card.ControllerSubject.PlayerId != context.Node.PlayerId) throw new InvalidOperationException("carrier_exit_permission_denied");
                string reason = MoveCard(context, card.ItemId, FindZone(context.State, "discard", card.ControllerSubject.PlayerId).ZoneId);
                if (reason.Length > 0) throw new InvalidOperationException(reason);
            }
        }
        private static ItemTransferRequest TransferRequest(int playerId, ItemInstanceState item, string destination, string profile, string insertion = "unordered") => new ItemTransferRequest {
            ExecutingPlayerId = playerId, ItemIds = new List<string> { item.ItemId }, ExpectedSource = new ItemPositionRef { ZoneId = item.ZoneId }, Destination = new ZoneRef { ZoneId = destination }, InsertionKind = insertion, OperationProfileId = profile, RequiredCount = 1 };
        public static void DetachExitedSubscriptions(GameState state, string itemId)
        {
            var item = state.ItemZones.Items.Single(value => value.ItemId == itemId);
            var zone = state.ItemZones.Zones.Single(value => value.ZoneId == item.ZoneId);
            if (zone.TemplateId == "core.zone.character_persistent") return;
            foreach (var use in state.CharacterUses.Where(value => value.CardInstanceId == itemId))
            foreach (var mode in use.Modes) mode.SubscriptionActive = false;
            foreach (var binding in state.FacilityCharacterBindings.Where(value => value.CharacterItemId == itemId)) binding.Active = false;
            // TimingBindings是已经登记的独立义务，不在此注销。
        }
        public static bool IsSubscriptionActive(GameState state, string itemId, string abilityId) =>
            state.ItemZones.Items.Any(item => item.ItemId == itemId && state.ItemZones.Zones.Any(zone => zone.ZoneId == item.ZoneId && zone.TemplateId == "core.zone.character_persistent")) &&
            state.CharacterUses.Any(use => use.CardInstanceId == itemId && use.Modes.Any(mode => mode.AbilityId == abilityId && mode.SubscriptionActive));
        /// <summary>持续订阅按真实实例查询，每次触发使用该实例的激活上下文；源牌离场后不再挂新子树。</summary>
        public static void RegisterPersistentObserver(EffectRegistry registry, string abilityId, string eventType, string version, string contentHash, Func<EffectEventHandlerContext, IList<EffectSpec>> handler, string observerSubjectPolicy = "controller",
            EventResponseAttachmentKind responseAttachment = EventResponseAttachmentKind.RequiredChild,
            Func<EffectEventHandlerContext, EffectSpec, DeferredEventResponseDescriptor> describeDeferredResponse = null)
        {
            string id = abilityId + ".persistent." + eventType;
            if (registry.HasEventHandler(id)) return;
            registry.RegisterEventHandler(new EffectEventHandlerRegistration(id, eventType, "*", id, context => {
                var effects = new List<EffectSpec>();
                foreach (var item in context.State.ItemZones.Items.Where(item => item.ControllerSubject.Kind == ItemSubjectKind.Player && (observerSubjectPolicy == "departed_owner" || eventType == "CollectionStarted" || item.ControllerSubject.PlayerId == context.Event.PlayerId) && IsSubscriptionActive(context.State, item.ItemId, abilityId)))
                {
                    if (observerSubjectPolicy == "departed_owner")
                    {
                        var departure = context.State.EffectRuntime.EffectNodes.Find(node => node.EffectId == context.Event.SourceEffectId);
                        var cube = context.State.ItemZones.Items.Find(value => value.ItemId == Text(departure?.ContinuationData, "pendingItemId"));
                        if (cube == null || cube.PhysicalKind != ItemPhysicalKind.Cube || cube.OwnerSubject.Kind == ItemSubjectKind.Player && cube.OwnerSubject.PlayerId != item.ControllerSubject.PlayerId ||
                            !context.State.ItemZones.Zones.Any(zone => zone.ParentKind == ZoneParentKind.Item && zone.ParentId == item.ItemId && zone.Configuration?.EntryPolicyId == "core.removed_owned_influence")) continue;
                    }
                    if (eventType == "PlayerCleanupStarted")
                    {
                        string key = context.State.Round.ToString(CultureInfo.InvariantCulture) + ":" + context.State.ActionRound.ToString(CultureInfo.InvariantCulture);
                        var modes = context.State.CharacterUses.Where(use => use.CardInstanceId == item.ItemId).SelectMany(use => use.Modes).Where(mode => mode.AbilityId == abilityId && mode.SubscriptionActive).ToList();
                        if (modes.Any(mode => mode.LastCleanupKey == key)) continue;
                        foreach (var mode in modes) mode.LastCleanupKey = key;
                    }
                    var source = context.State.EffectRuntime.EffectNodes.Last(node => node.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId && Text(node.NormalizedArguments, "abilityId") == abilityId && Value(node.NormalizedArguments, "cardInstanceId")?.ReferenceId == item.ItemId);
                    var ruleEvent = GameStateCloneService.DeepClone(context.Event);
                    ruleEvent.PlayerId = item.ControllerSubject.PlayerId;
                    ruleEvent.HostOnlyPayload = context.Event.HostOnlyPayload?.Kind == NormalizedValueKind.Object ? context.Event.HostOnlyPayload.Clone() : context.Event.Payload?.Kind == NormalizedValueKind.Object ? context.Event.Payload.Clone() : Object();
                    foreach (var entry in new[] { new NormalizedValueEntry { Name = "cardInstanceRef", Value = NormalizedValue.CreateStableReference("character_card", item.ItemId) }, new NormalizedValueEntry { Name = "useId", Value = NormalizedValue.CreateString(Text(source.NormalizedArguments, "useId")) } })
                    { ruleEvent.HostOnlyPayload.Properties.RemoveAll(existing => existing.Name == entry.Name); ruleEvent.HostOnlyPayload.Properties.Add(entry); }
                    ruleEvent.HostOnlyPayload.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
                    var scoped = new EffectEventHandlerContext(context.State, ruleEvent, source, context.Registration);
                    var specs = handler(scoped);
                    for (int index = 0; index < specs.Count; index++) { specs[index].SourceId = "character-card:" + item.ItemId; specs[index].StableKey = StableIdFactory.Create("persistent_trigger", item.ItemId, abilityId, context.Event.EventId, index.ToString(CultureInfo.InvariantCulture)); effects.Add(specs[index]); }
                }
                return effects;
            }, EffectHandlerRole.Observer, definitionVersion: version, contentHash: contentHash, abilityId: abilityId)
            { AttachmentKind = responseAttachment, DescribeDeferredResponse = describeDeferredResponse });
        }
        public static EffectStepResult Cover(EffectExecutionContext context, string cardId, bool additional, int blockedSequence = 0, string acceptedTypeId = "core.character_card", bool reusable = false)
        {
            var state = context.State; var item = state.ItemZones.Items.Find(value => value.ItemId == cardId);
            var types = ItemTypeRegistry.CreateBuiltIn();
            if (item == null || !types.IsAssignableTo(item.TypeId, acceptedTypeId) || !types.HasOperationProfile(item.TypeId, "core.character_card_use")) return EffectStepResult.Failed("character_type_rejected");
            var source = state.ItemZones.Zones.Single(value => value.ZoneId == item.ZoneId);
            if (item.ControllerSubject.PlayerId != context.Node.PlayerId || source.OwnerPlayerId != context.Node.PlayerId ||
                source.LocalZoneId != "hand" && (!additional || source.LocalZoneId != "discard")) return EffectStepResult.Failed("character_cover_source_invalid");
            var target = FindZone(state, "covered", context.Node.PlayerId);
            if (!additional && state.ItemZones.Items.Any(value => value.ZoneId == target.ZoneId)) return EffectStepResult.Failed("character_primary_slot_occupied");
            string sourceId = context.Node.SourceId.Length > 0 ? context.Node.SourceId : context.Node.EffectId;
            var slot = reusable ? state.CharacterCoverSlots.Find(value => value.PlayerId == context.Node.PlayerId && value.SourceEffectId == sourceId && value.Reusable && value.CardInstanceId.Length == 0) : null;
            if (slot != null && slot.AcceptedTypeId != acceptedTypeId) return EffectStepResult.Failed("character_slot_type_changed");
            if (slot == null) { slot = new CoverSlotState { SlotId = StableIdFactory.Create("cover_slot", context.Node.EffectId, additional ? "additional" : "primary"), PlayerId = context.Node.PlayerId, SourceEffectId = sourceId, AcceptedTypeId = acceptedTypeId, Reusable = reusable }; state.CharacterCoverSlots.Add(slot); }
            if (!target.LegalSlotIds.Contains(slot.SlotId)) target.LegalSlotIds.Add(slot.SlotId);
            var request = TransferRequest(context.Node.PlayerId, item, target.ZoneId, "core.character_card_transfer");
            var validation = new ItemTransferService().Validate(state, request);
            if (!validation.IsValid) return EffectStepResult.Failed(validation.ReasonCode);
            new ItemTransferService().RelocateItems(context, request);
            slot.CardInstanceId = item.ItemId; slot.BlockedActionRoundSequence = blockedSequence; item.SlotId = slot.SlotId;
            return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(true)));
        }
        // 主链显式指定回收范围；持续区从不参加回收，重复调用由真实区域自然幂等。
        public static EffectStepResult Recycle(EffectExecutionContext context, int playerId, bool includeDiscard, IEnumerable<string> coveredItemIds)
        {
            ResumeDeferredDependencies(context);
            if (playerId != context.Node.PlayerId) return EffectStepResult.Failed("character_recycle_permission_denied");
            var pendingIds = new List<string>(coveredItemIds ?? Array.Empty<string>());
            if (includeDiscard) pendingIds.AddRange(context.State.ItemZones.Items.Where(item => item.ZoneId == FindZone(context.State, "discard", playerId).ZoneId).Select(item => item.ItemId));
            if (TryAwaitCardMoves(context, pendingIds, FindZone(context.State, "hand", playerId).ZoneId)) return DeferredDependencyWait();
            var preview = GameStateCloneService.DeepClone(context.State);
            var work = new EffectExecutionContext(preview, preview.EffectRuntime.EffectNodes.Single(node => node.EffectId == context.Node.EffectId), context.Registry);
            var hand = FindZone(preview, "hand", playerId);
            var ids = new List<string>(coveredItemIds ?? Array.Empty<string>());
            if (includeDiscard) ids.AddRange(preview.ItemZones.Items.Where(item => item.ZoneId == FindZone(preview, "discard", playerId).ZoneId).Select(item => item.ItemId));
            int moved = 0;
            foreach (string id in ids.Distinct().ToList())
            {
                var item = preview.ItemZones.Items.Find(value => value.ItemId == id);
                if (item == null) return EffectStepResult.Failed("unknown_item");
                if (item.ZoneId == hand.ZoneId) continue;
                var source = preview.ItemZones.Zones.Single(zone => zone.ZoneId == item.ZoneId);
                if (source.OwnerPlayerId != playerId || source.LocalZoneId != "covered" && source.LocalZoneId != "discard") return EffectStepResult.Failed("character_recycle_source_invalid");
                string reason = MoveCard(work, id, hand.ZoneId); if (reason.Length > 0) return EffectStepResult.Failed(reason); moved++;
            }
            GameStateCloneService.CopyTo(context.State, preview);
            return EffectStepResult.Completed(Object("movedCount", NormalizedValue.CreateInteger(moved), "changed", NormalizedValue.CreateBoolean(moved > 0)));
        }
        public static EffectSpec CreateDoubleUseInsertion(int player, string useId, string sourceId)
        {
            var args = Object("useId", NormalizedValue.CreateString(useId), "sourceItemId", NormalizedValue.CreateString(sourceId));
            var spec = EffectSpec.Condition(new[] { EffectSpec.Create(ConsumeSourceTypeId, args, player) }, new[] { EffectSpec.Create(LuaDomainEffectTypeIds.SetCharacterDoubleUseRule,
                Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString(player.ToString(CultureInfo.InvariantCulture)), "scope", NormalizedValue.CreateString("current_use"), "useId", NormalizedValue.CreateString(useId), "sourceItemId", NormalizedValue.CreateString(sourceId)), player) });
            spec.Visibility = GameStateVisibilityPolicy.Owner; return spec;
        }
        private static EffectStepResult ConsumeSource(EffectExecutionContext context)
        {
            var use = context.State.CharacterUses.Find(value => value.UseId == Text(context.Node.NormalizedArguments, "useId"));
            string sourceId = Text(context.Node.NormalizedArguments, "sourceItemId");
            var source = context.State.ItemZones.Items.Find(value => value.ItemId == sourceId);
            if (use == null || use.Finished || use.ExecutingPlayerId != context.Node.PlayerId || use.DoubleUseSourceItemId != sourceId || sourceId == use.CardInstanceId ||
                use.ModeOrder.Count != 0 || use.CompletedModes.Count != 0) return EffectStepResult.Failed("character_double_use_target_invalid");
            if (use.SourceConsumed) return EffectStepResult.Completed();
            if (source == null || source.ControllerSubject.PlayerId != context.Node.PlayerId || source.ZoneId != FindZone(context.State, "persistent", context.Node.PlayerId).ZoneId) return EffectStepResult.Failed("character_double_use_source_invalid");
            if (TryAwaitCardMoves(context, new[] { sourceId }, FindZone(context.State, "discard", context.Node.PlayerId).ZoneId)) return DeferredDependencyWait();
            string reason = MoveCard(context, sourceId, FindZone(context.State, "discard", context.Node.PlayerId).ZoneId);
            if (reason.Length > 0) return EffectStepResult.Failed(reason);
            context.State.CharacterUses.Single(value => value.UseId == Text(context.Node.NormalizedArguments, "useId")).SourceConsumed = true;
            return EffectStepResult.Completed();
        }
        private static EffectStepResult SetDoubleUse(EffectExecutionContext context)
        {
            string error = ValidateArguments(context.Node.EffectTypeId, context.Node.NormalizedArguments);
            if (error.Length > 0) return EffectStepResult.Failed(error);
            var args = context.Node.NormalizedArguments;
            if (ParsePlayer(Text(args, "player")) != context.Node.PlayerId) return EffectStepResult.Failed("invalid_player");
            if (Text(args, "scope") == "action_round")
            {
                int sequence = GetActionRoundSequence(context.State);
                if (Number(args, "expiresActionRoundSequence") != sequence) return EffectStepResult.Failed("character_permission_expiry_invalid");
                if (!context.State.CharacterActionRoundPermissions.Any(value => value.SourceEffectId == context.Node.EffectId)) context.State.CharacterActionRoundPermissions.Add(new CharacterActionRoundPermission { PlayerId = context.Node.PlayerId, ExpiresActionRoundSequence = sequence, SourceEffectId = context.Node.EffectId });
                return EffectStepResult.Completed();
            }
            var use = context.State.CharacterUses.Find(value => value.UseId == Text(args, "useId"));
            if (use == null || use.Finished || use.ExecutingPlayerId != context.Node.PlayerId || !use.SourceConsumed || use.DoubleUseSourceItemId != Text(args, "sourceItemId")) return EffectStepResult.Failed("character_double_use_source_not_consumed");
            use.DoubleUseGranted = true; return EffectStepResult.Completed();
        }
        public static int ParsePlayer(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int id) && id > 0 ? id : -1;
        public static int GetRetainedBasicCharacterCount(GameState state, int playerId)
        {
            if (state == null || state.FindPlayer(playerId) == null) throw new ArgumentException("invalid_player");
            var types = ItemTypeRegistry.CreateBuiltIn();
            return state.ItemZones.Items.Count(item => item.ControllerSubject.Kind == ItemSubjectKind.Player && item.ControllerSubject.PlayerId == playerId &&
                types.IsAssignableTo(item.TypeId, "core.basic_character_card") && IsRetainedPlayerCardZone(state, item.ZoneId, playerId));
        }
        private static bool IsRetainedPlayerCardZone(GameState state, string zoneId, int player)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (visited.Add(zoneId))
            {
                var zone = state.ItemZones.Zones.Find(z => z.ZoneId == zoneId);
                if (zone == null) return false;
                if (zone.TemplateId == "core.zone.effect_staging" || zone.TemplateId == "core.zone.player_panel" && zone.OwnerPlayerId == player) return true;
                if (zone.ParentKind == ZoneParentKind.Item) zoneId = state.ItemZones.Items.Find(i => i.ItemId == zone.ParentId)?.ZoneId ?? "";
                else if (zone.ParentKind == ZoneParentKind.Zone) zoneId = zone.ParentId;
                else return false;
            }
            return false;
        }
        public static bool ValidateRetainedBasicCharacters(GameState state, out string reason)
        {
            reason = string.Empty;
            if (state.SetupRecipe == null) return true;
            foreach (var player in state.Players)
                if (state.EffectRuntime.EffectNodes.Any(node => node.EffectTypeId == ExpansionSetupFlow.RetentionTypeId && node.PlayerId == player.PlayerId && Flag(node.ContinuationData, "basicCharacterRetentionCompleted")) &&
                    GetRetainedBasicCharacterCount(state, player.PlayerId) > state.SetupRecipe.MaxRetainedBasicCharacterCount)
                { reason = "basic_character_limit_exceeded"; return false; }
            return true;
        }
        private static bool IsCleanupKeyValid(string key)
        {
            if (key == null) return false;
            if (key.Length == 0) return true;
            var parts = key.Split(':');
            return parts.Length == 2 && parts.All(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value >= 0);
        }
        public static bool TryValidate(GameState state, out string reason)
        {
            reason = "character_runtime_invalid";
            foreach (var node in state.EffectRuntime.EffectNodes)
            {
                var resume = Value(node.ContinuationData, DeferredResumeStageKey);
                if (node.FlowStage != DeferredDependencyStage) { if (resume != null) return false; continue; }
                if (resume?.Kind != NormalizedValueKind.String || resume.StringValue == DeferredDependencyStage ||
                    node.PendingOutcome != EffectPendingOutcome.None || node.Status == EffectNodeStatus.Completed || node.Status == EffectNodeStatus.Failed || node.Status == EffectNodeStatus.Faulted ||
                    !state.EffectRuntime.Blockers.Any(blocker => blocker.OwnerEffectId == node.EffectId && blocker.BlockerKind == "deferred_response_dependency" &&
                        state.EffectRuntime.DeferredResponses.Any(response => response.ResponseRootEffectId == blocker.TargetEffectId))) return false;
            }
            if (!ValidateRetainedBasicCharacters(state, out var limitReason)) { reason = limitReason; return false; }
            if (state.CharacterUses == null || state.CharacterCoverSlots == null || state.FacilityCharacterBindings == null || state.CharacterActionRoundPermissions == null) return false;
            if (state.CharacterUses.Any(use => use == null) || state.CharacterUses.Select(use => use.UseId).Distinct().Count() != state.CharacterUses.Count) return false;
            foreach (var use in state.CharacterUses)
            {
                if (string.IsNullOrEmpty(use.UseId) || !state.EffectRuntime.EffectNodes.Any(node => node.EffectId == use.OwnerEffectId && node.EffectTypeId == UseTypeId && node.PlayerId == use.ExecutingPlayerId && Text(node.ContinuationData, "currentUseId") == use.UseId) || !state.ItemZones.Items.Any(item => item.ItemId == use.CardInstanceId) || state.FindPlayer(use.ExecutingPlayerId) == null || use.ActionRoundSequence < 1 ||
                    use.ModeOrder == null || use.CompletedModes == null || use.SkippedModes == null || use.Modes == null || use.Modes.Count != 2 || use.Modes.Any(mode => mode == null) || use.Modes.Select(mode => mode.Mode).Distinct().Count() != 2 ||
                    use.Modes.Any(mode => mode == null || mode.Mode != "strategy" && mode.Mode != "tactic" || string.IsNullOrEmpty(mode.AbilityId) || !new[] { "normal", "persistent", "one_shot" }.Contains(mode.SkillType) || mode.SubscriptionActive && (mode.SkillType != "persistent" || !mode.Activated) || !IsCleanupKeyValid(mode.LastCleanupKey)) ||
                    use.ModeOrder.Distinct().Count() != use.ModeOrder.Count || use.ModeOrder.Any(mode => mode != "strategy" && mode != "tactic") || use.CompletedModes.Distinct().Count() != use.CompletedModes.Count || use.CompletedModes.Any(mode => !use.ModeOrder.Contains(mode)) ||
                    use.SourceConsumed && string.IsNullOrEmpty(use.DoubleUseSourceItemId) || use.Finished && !use.SelectionCancelled && (use.ModeOrder.Count == 0 || string.IsNullOrEmpty(use.FinishDestinationZoneId) || use.ModeOrder.Any(mode => !use.CompletedModes.Contains(mode) && !use.SkippedModes.Contains(mode)))) return false;
                if (use.SelectionProtocolVersion < 1 || use.SelectionProtocolVersion > 3) return false;
                if (use.SelectionProtocolVersion == 1 && (use.OfferSecondEffect || use.CanOfferSecondEffect || use.SelectionCancelled)) return false;
                var owner = state.EffectRuntime.EffectNodes.Single(node => node.EffectId == use.OwnerEffectId);
                if (use.SkippedModes.Count > 1 || use.SkippedModes.Count == 0 && (!string.IsNullOrEmpty(use.StopCommandId) || !string.IsNullOrEmpty(use.StoppedActivationEffectId))) return false;
                if (use.SkippedModes.Count == 1)
                {
                    if (use.SelectionProtocolVersion < 2 || use.ModeOrder.Count != 2 || use.SkippedModes[0] != use.ModeOrder[1] ||
                        !use.CompletedModes.Contains(use.ModeOrder[0]) || use.CompletedModes.Contains(use.ModeOrder[1]) || string.IsNullOrEmpty(use.StopCommandId)) return false;
                    var stopped = state.EffectRuntime.EffectNodes.Find(node => node.EffectId == use.StoppedActivationEffectId && node.ParentEffectId == use.OwnerEffectId &&
                        node.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId && Text(node.NormalizedArguments, "useId") == use.UseId && Text(node.NormalizedArguments, "mode") == use.SkippedModes[0]);
                    if (stopped == null || use.Finished && (stopped.Status != EffectNodeStatus.Failed || stopped.FailureReason != ModeStoppedReason)) return false;
                    var scope = GetModeOwnedEffects(state, use, stopped);
                    if (!state.EffectRuntime.Journal.Any(entry => entry.CommandId == use.StopCommandId && entry.Detail == ModeStoppedReason && scope.Contains(entry.EntityId)) ||
                        !state.EffectRuntime.EffectNodes.Any(node => node.ParentEffectId == use.OwnerEffectId && node.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId &&
                            Text(node.NormalizedArguments, "mode") == use.ModeOrder[0] && (node.Status == EffectNodeStatus.Completed || node.Status == EffectNodeStatus.Failed))) return false;
                    foreach (var stoppedNode in state.EffectRuntime.EffectNodes.Where(node => Text(node.ContinuationData, StoppedUseKey) == use.UseId))
                        if (!scope.Contains(stoppedNode.EffectId) || stoppedNode.FailureReason != ModeStoppedReason || stoppedNode.PendingOutcome != EffectPendingOutcome.Failed ||
                            state.EffectRuntime.InteractionRequests.Any(request => request.OwnerEffectId == stoppedNode.EffectId && request.AnsweringPlayerId == use.ExecutingPlayerId && request.Status == "open") ||
                            !string.IsNullOrEmpty(stoppedNode.ContinuationBindingId) && !state.EffectRuntime.ContinuationBindings.Any(binding => binding.BindingId == stoppedNode.ContinuationBindingId && binding.Status == ContinuationBindingStatus.Cancelled)) return false;
                }
                if (Number(owner.NormalizedArguments, "selectionProtocolVersion") >= 2 && use.SelectionProtocolVersion != Number(owner.NormalizedArguments, "selectionProtocolVersion")) return false;
                if (use.SelectionProtocolVersion >= 2)
                {
                    if (Number(owner.NormalizedArguments, "selectionProtocolVersion") != use.SelectionProtocolVersion ||
                        !state.EffectRuntime.EffectNodes.Any(node => node.EffectId == owner.ParentEffectId && node.EffectTypeId == QuickActionTypeId && node.PlayerId == use.ExecutingPlayerId && node.ChildEffectIds.Contains(owner.EffectId))) return false;
                    if (use.OfferSecondEffect != Flag(owner.NormalizedArguments, "offerSecondEffect") ||
                        Value(owner.ContinuationData, "canOfferSecondEffect")?.Kind != NormalizedValueKind.Boolean ||
                        use.CanOfferSecondEffect != Flag(owner.ContinuationData, "canOfferSecondEffect")) return false;
                    if (use.ModeOrder.Count == 2 && !use.DoubleUseGranted) return false;
                    if (!use.Finished && owner.FlowStage == "choose_second_mode" && !ShouldOfferSecondMode(use)) return false;
                    if (use.SelectionCancelled && (!use.Finished || use.SourceConsumed || use.ModeOrder.Count != 0 || use.CompletedModes.Count != 0 ||
                        !string.IsNullOrEmpty(use.FinishDestinationZoneId) || use.Modes.Any(mode => mode.Activated || mode.SubscriptionActive) ||
                        state.EffectRuntime.EffectNodes.Any(node => node.ParentEffectId == owner.EffectId && node.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId))) return false;
                }
            }
            if (state.EffectRuntime.EffectNodes.Any(node => Text(node.ContinuationData, StoppedUseKey).Length > 0 &&
                !state.CharacterUses.Any(use => use.UseId == Text(node.ContinuationData, StoppedUseKey) && use.SkippedModes.Count == 1))) return false;
            if (state.CharacterCoverSlots.Any(slot => slot == null) || state.CharacterCoverSlots.Select(slot => slot.SlotId).Distinct().Count() != state.CharacterCoverSlots.Count) return false;
            foreach (var slot in state.CharacterCoverSlots)
            {
                if (string.IsNullOrEmpty(slot.SlotId) || slot.CardInstanceId == null || string.IsNullOrEmpty(slot.SourceEffectId) || slot.BlockedActionRoundSequence < 0 || slot.AllowedActionRounds == null || slot.AllowedActionRounds.Any(round => round != 1 && round != 2) || state.FindPlayer(slot.PlayerId) == null) return false;
                if (slot.CardInstanceId.Length == 0) continue;
                var item = state.ItemZones.Items.Find(value => value.ItemId == slot.CardInstanceId);
                if (item == null || item.SlotId != slot.SlotId || !state.ItemZones.Zones.Any(zone => zone.ZoneId == item.ZoneId && zone.OwnerPlayerId == slot.PlayerId && zone.TemplateId == "core.zone.character_covered")) return false;
            }
            foreach (var item in state.ItemZones.Items.Where(item => state.ItemZones.Zones.Any(zone => zone.ZoneId == item.ZoneId && zone.TemplateId == "core.zone.character_covered")))
                if (state.CharacterCoverSlots.Count(slot => slot.CardInstanceId == item.ItemId) != 1) return false;
            if (state.CharacterActionRoundPermissions.Any(permission => permission == null || permission.ExpiresActionRoundSequence < 1 || state.FindPlayer(permission.PlayerId) == null || !state.EffectRuntime.EffectNodes.Any(node => node.EffectId == permission.SourceEffectId))) return false;
            if (state.FacilityCharacterBindings.Any(binding => binding == null || string.IsNullOrEmpty(binding.BindingId) || !state.ItemZones.Items.Any(item => item.ItemId == binding.CharacterItemId) || !state.ItemZones.Items.Any(item => item.ItemId == binding.FacilityItemId))) return false;
            reason = ""; return true;
        }
        public static EffectStepResult BindFacility(EffectExecutionContext context, string characterItemId, string facilityItemId)
        {
            var character = context.State.ItemZones.Items.Find(item => item.ItemId == characterItemId);
            var facility = context.State.ItemZones.Items.Find(item => item.ItemId == facilityItemId);
            if (character == null || facility == null || character.ControllerSubject.PlayerId != context.Node.PlayerId || facility.ControllerSubject.PlayerId != context.Node.PlayerId ||
                !context.State.ItemZones.Zones.Any(zone => zone.ZoneId == character.ZoneId && (zone.TemplateId == "core.zone.character_persistent" || zone.TemplateId == "core.zone.character_covered")) ||
                !context.State.ItemZones.Zones.Any(zone => zone.ZoneId == facility.ZoneId && zone.TemplateId == "core.zone.city_facility_cell") ||
                context.State.FacilityCharacterBindings.Any(binding => binding.Active && binding.CharacterItemId == characterItemId)) return EffectStepResult.Failed("character_facility_binding_invalid");
            context.State.FacilityCharacterBindings.Add(new FacilityCharacterBindingState { BindingId = StableIdFactory.Create("character_facility_binding", context.Node.EffectId, characterItemId, facilityItemId), CharacterItemId = characterItemId, FacilityItemId = facilityItemId, SourceEffectId = context.Node.EffectId, ControllerPlayerId = context.Node.PlayerId, Active = true });
            return EffectStepResult.Completed();
        }
        public static EffectStepResult ReleaseFacilityBinding(EffectExecutionContext context, string bindingId)
        {
            var binding = context.State.FacilityCharacterBindings.Find(value => value.BindingId == bindingId && value.ControllerPlayerId == context.Node.PlayerId);
            if (binding == null) return EffectStepResult.Failed("character_facility_binding_invalid");
            bool changed = binding.Active; binding.Active = false;
            return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(changed)));
        }
        public static string ValidateArguments(string typeId, NormalizedValue args)
        {
            string[] allowed = typeId == TransferTypeId ? new[] { "apiVersion", "player", "cardInstanceId", "recipientPlayer" } :
                new[] { "apiVersion", "player", "scope", "useId", "sourceItemId", "expiresActionRoundSequence" };
            if (args?.Kind != NormalizedValueKind.Object || args.Properties.Any(entry => entry.Name != "kind" && entry.Name != "effectTypeId" && !allowed.Contains(entry.Name))) return "character_arguments_invalid";
            if (Number(args, "apiVersion") != 1 || ParsePlayer(Text(args, "player")) < 1) return "character_arguments_invalid";
            if (typeId == TransferTypeId) return Text(args, "cardInstanceId").Length > 0 && ParsePlayer(Text(args, "recipientPlayer")) > 0 ? "" : "character_arguments_invalid";
            if (Text(args, "scope") == "current_use") return Text(args, "useId").Length > 0 && Text(args, "sourceItemId").Length > 0 && Value(args, "expiresActionRoundSequence") == null ? "" : "character_arguments_invalid";
            return Text(args, "scope") == "action_round" && Value(args, "expiresActionRoundSequence")?.Kind == NormalizedValueKind.Integer && Number(args, "expiresActionRoundSequence") > 0 && Value(args, "useId") == null && Value(args, "sourceItemId") == null ? "" : "character_arguments_invalid";
        }
    }
}
