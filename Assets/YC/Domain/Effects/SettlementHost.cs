using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    // 受控构造的封闭参数协议；业务消费仍由原 CharacterLifecycleService 实施。
    public static class SettlementHostProtocol
    {
        public static SettlementWindowRuntimeState FindActiveWindow(GameState state)
            => state?.EffectRuntime?.EffectNodes.Select(n => n.SettlementWindow).FirstOrDefault(w => w?.Context?.Id == state.EffectRuntime.ActiveSettlementWindowId && !w.IsClosed);
        public static CommandErrorCode ErrorCode(string reason)
        {
            switch (reason)
            {
                case "": return CommandErrorCode.None;
                case "state_revision_mismatch": return CommandErrorCode.StateRevisionMismatch;
                case "settlement_permission_denied": return CommandErrorCode.NotCurrentPlayer;
                case "settlement_required_pending": return CommandErrorCode.SettlementRequiredPending;
                case "settlement_current_node_stop_forbidden": return CommandErrorCode.SettlementCurrentNodeStopForbidden;
                case "settlement_window_not_active": return CommandErrorCode.SettlementWindowMismatch;
                case "settlement_current_node_required":
                case "settlement_execution_pending": return CommandErrorCode.PendingChoiceRequired;
                default: return CommandErrorCode.InvalidTarget;
            }
        }
        public static bool CanStopOriginalNode(GameState state, EffectNodeRuntimeState node, int playerId, string interactionId)
        {
            if (ResourcePaymentPreparationService.FindDeclinableRulePayment(state, node, playerId) != null) return true;
            if (node == null || node.PlayerId != playerId || node.PendingOutcome != EffectPendingOutcome.None ||
                node.Status != EffectNodeStatus.Ready && node.Status != EffectNodeStatus.Running && node.Status != EffectNodeStatus.Blocked) return false;
            if (!string.IsNullOrEmpty(interactionId)) return state.EffectRuntime.InteractionRequests.Any(r => r.GetStableInteractionId() == interactionId &&
                r.OwnerEffectId == node.EffectId && r.Status == "open" && r.AnsweringPlayerId == playerId && r.AllowDecline);
            return node.AllowDecline && (node.DecisionPlayerId < 0 || node.DecisionPlayerId == playerId) && node.ChildEffectIds.Count == 0;
        }
        public static SettlementEndActionView GetEndActionView(GameState state, int playerId)
        {
            var window = FindActiveWindow(state);
            if (window?.Context.PlayerId != playerId) return null;
            var runtime = state.EffectRuntime;
            var request = runtime.InteractionRequests.LastOrDefault(r => r.Status == "open" && r.AnsweringPlayerId == playerId &&
                !string.IsNullOrEmpty(window.ActiveTargetEffectId) && SettlementEntryService.IsWithin(runtime, r.OwnerEffectId, window.ActiveTargetEffectId));
            var node = request == null ? runtime.EffectNodes.LastOrDefault(n => n.PlayerId == playerId &&
                (n.Status == EffectNodeStatus.Ready || n.Status == EffectNodeStatus.Running || n.Status == EffectNodeStatus.Blocked) &&
                !string.IsNullOrEmpty(window.ActiveTargetEffectId) && SettlementEntryService.IsWithin(runtime, n.EffectId, window.ActiveTargetEffectId) &&
                !n.ChildEffectIds.Any(id => runtime.EffectNodes.Any(c => c.EffectId == id && c.Status != EffectNodeStatus.Completed && c.Status != EffectNodeStatus.Failed)))
                : runtime.EffectNodes.Find(n => n.EffectId == request.OwnerEffectId);
            var view = new SettlementEndActionView { WindowId = window.Context.Id, OwnerEffectId = window.Context.OwnerEffectId,
                CurrentNodeEffectId = node?.EffectId ?? string.Empty, InteractionId = request?.GetStableInteractionId() ?? string.Empty, StateRevision = runtime.StateRevision };
            view.CanStopCurrentNode = node != null && SettlementEntryService.TryPrepareEndAction(state, window, playerId, node.EffectId, view.InteractionId,
                runtime.StateRevision, n => CanStopOriginalNode(state, n, playerId, view.InteractionId), out _, out _);
            if (!view.CanStopCurrentNode)
            {
                view.StopCurrentNodeReasonCode = "settlement_current_node_stop_forbidden";
                view.StopCurrentNodeErrorCode = ErrorCode(view.StopCurrentNodeReasonCode);
            }
            view.CanCloseCleanupWindow = SettlementEntryService.TryPrepareEndAction(state, window, playerId, "", "", runtime.StateRevision, null, out _, out var closeReason);
            view.CloseCleanupWindowReasonCode = closeReason; view.CloseCleanupWindowErrorCode = ErrorCode(closeReason);
            return view;
        }
        internal static string FindAncestorWindowId(EffectRuntimeState runtime, string nodeId)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(nodeId) && visited.Add(nodeId))
            {
                var node = runtime.EffectNodes.Find(n => n.EffectId == nodeId);
                if (node == null) break;
                if (node.SettlementWindow?.Context != null && !node.SettlementWindow.IsClosed) return node.SettlementWindow.Context.Id;
                nodeId = node.ParentEffectId;
            }
            return string.Empty;
        }
        public static string ValidateConsumeSourceArguments(NormalizedValue args)
        {
            var fields = new[] { "apiVersion", "player", "useId", "sourceItemId", "kind", "effectTypeId" };
            return args?.Kind != NormalizedValueKind.Object || args.Properties.Any(p => !fields.Contains(p.Name)) ||
                ItemTransferEffectExecutor.Value(args, "apiVersion")?.IntegerValue != 1 ||
                YC.Domain.Cards.CharacterLifecycleService.ParsePlayer(ItemTransferEffectExecutor.Text(args, "player")) < 1 ||
                string.IsNullOrEmpty(ItemTransferEffectExecutor.Text(args, "useId")) || string.IsNullOrEmpty(ItemTransferEffectExecutor.Text(args, "sourceItemId"))
                ? "character_double_use_source_arguments_invalid" : string.Empty;
        }
        // 结构与费用规则由各自唯一校验器负责；这里只追溯正式声明和真实事件/来源收据。
        public static bool TryValidateRecoveryOrigins(GameState state, EffectRegistry registry, out string reason)
        {
            reason = "settlement_origin_invalid";
            var runtime = state.EffectRuntime;
            if (!CleanupSourceRecovery.TryValidate(state, registry, out reason)) return false;
            reason = "settlement_origin_invalid";
            var registeredTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var owner in runtime.EffectNodes.Where(n => n.SettlementWindow != null))
            {
                reason = "settlement_origin_invalid:" + owner.EffectId;
                var window = owner.SettlementWindow;
                var context = window.Context;
                var trigger = context.Kind == SettlementEntryService.PlayerCleanup ? runtime.RuleEvents.Find(e =>
                    e.EventId == context.TriggerId && e.EventType == "PlayerCleanupStarted" && e.OwnerNodeId == owner.EffectId && e.PlayerId == context.PlayerId) : null;
                if (context.Kind == SettlementEntryService.PlayerCleanup && trigger == null) return false;
                if (context.Kind == SettlementEntryService.CharacterUse && !state.CharacterUses.Any(u =>
                    u.UseId == context.TargetUseId && u.OwnerEffectId == owner.EffectId && u.ExecutingPlayerId == context.PlayerId)) return false;
                foreach (var entry in window.Entries)
                {
                    if (entry.PaymentInsertion != null)
                    {
                        if (!ResourcePaymentInsertionService.ValidateEntry(state, window, entry, registry)) return false;
                        continue;
                    }
                    reason = "settlement_origin_invalid:" + entry.TargetEffectId;
                    if (runtime.EffectNodes.Any(n => n.EffectId == entry.TargetEffectId && n.EffectTypeId == CleanupObligation.InvokeType))
                    {
                        if (!CleanupObligation.ValidateSettlementEntry(state, window, entry) || !registeredTargets.Add(entry.TargetEffectId))
                        { reason = "cleanup_obligation_settlement_origin_invalid"; return false; }
                        continue;
                    }
                    if (entry.CleanupSource != null)
                    {
                        if (!CleanupSourceRecovery.TryValidateEntry(state, window, entry, registry, out reason)) return false;
                        if (!registeredTargets.Add(entry.TargetEffectId))
                        { reason = "settlement_target_duplicate:" + entry.TargetEffectId; return false; }
                        continue;
                    }
                    if (registry != null && (!registry.TryGetSettlementSourceContract(entry.AbilityId, out var contract) ||
                        contract.DefinitionId != entry.SourceDefinitionId || contract.LocalId != entry.LocalId ||
                        contract.EntryKind != entry.EntryKind || contract.AvailabilityHandlerId != entry.AvailabilityHandlerId ||
                        entry.EntryKind == SettlementEntryService.CleanupOrder && contract.WholeEffectPolicy != entry.WholeEffectPolicy))
                    { reason = "settlement_source_contract_mismatch"; return false; }
                    var item = state.ItemZones.Items.Find(i => i.ItemId == entry.SourceItemId && i.DefinitionId == entry.SourceDefinitionId);
                    var source = runtime.EffectNodes.Find(n => n.EffectId == entry.SourceEffectId && n.PlayerId == context.PlayerId);
                    if (item == null || source == null) return false;
                    bool activation = source.EffectTypeId == YC.Domain.Cards.CharacterAbilityEffectExecutor.ActivationEffectTypeId &&
                        source.Status == EffectNodeStatus.Completed && ItemTransferEffectExecutor.Text(source.NormalizedArguments, "abilityId") == entry.AbilityId &&
                        ItemTransferEffectExecutor.Value(source.NormalizedArguments, "cardInstanceId")?.ReferenceId == entry.SourceItemId &&
                        state.CharacterUses.Any(u => u.UseId == ItemTransferEffectExecutor.Text(source.NormalizedArguments, "useId") &&
                            u.CardInstanceId == entry.SourceItemId && u.ExecutingPlayerId == context.PlayerId && u.Modes.Any(m => m.AbilityId == entry.AbilityId && m.Activated));
                    bool award = item.TypeId == "core.award_card" && ReferenceEquals(source, ContentLifecycleEffectExecutor.Acquisition(state, entry.SourceItemId)) &&
                        ItemTransferEffectExecutor.Flag(source.ContinuationData, "acquisitionCompleted") &&
                        ItemTransferEffectExecutor.Text(source.ContinuationData, "selectedRewardItemId") == entry.SourceItemId;
                    var unifiedTarget = runtime.EffectNodes.Find(n => n.EffectId == entry.TargetEffectId && n.EffectTypeId == AbilitySourceEffectExecutor.TypeId);
                    bool unified = unifiedTarget != null && ItemTransferEffectExecutor.Text(unifiedTarget.NormalizedArguments, "itemId") == entry.SourceItemId &&
                        ItemTransferEffectExecutor.Text(unifiedTarget.NormalizedArguments, "abilityId") == entry.AbilityId &&
                        ItemTransferEffectExecutor.Text(unifiedTarget.NormalizedArguments, "definitionId") == item.DefinitionId &&
                        ItemTransferEffectExecutor.Text(unifiedTarget.NormalizedArguments, "triggerEventId") == context.TriggerId &&
                        (registry?.AbilityDeclarations ?? state.ContentDefinitions?.AbilitySources)?.Any(d => d.DefinitionId == item.DefinitionId && d.AbilityId == entry.AbilityId && d.Kind == "cleanup" &&
                            d.DeclarationVersion == ItemTransferEffectExecutor.Text(unifiedTarget.NormalizedArguments, "declarationVersion")) == true;
                    if (!activation && !award && !unified) return false;
                    if (entry.EntryKind != SettlementEntryService.CleanupOrder) continue;
                    var target = runtime.EffectNodes.Find(n => n.EffectId == entry.TargetEffectId);
                    var declaration = target?.Settlement;
                    if (!registeredTargets.Add(entry.TargetEffectId) || declaration == null || declaration.AbilityId != entry.AbilityId ||
                        declaration.LocalId != entry.LocalId || declaration.EntryKind != entry.EntryKind || declaration.SourceItemId != entry.SourceItemId ||
                        declaration.SourceEffectId != entry.SourceEffectId || declaration.WholeEffectPolicy != entry.WholeEffectPolicy ||
                        !runtime.DispatchReceipts.Any(r => r.EventId == trigger.EventId && r.Succeeded && r.AttachedEffectIds.Contains(target.EffectId) &&
                            (r.AbilityId == entry.AbilityId || award && r.HandlerId == "content.reward.player_cleanup" ||
                                unified && r.HandlerId == "ability-source.event.PlayerCleanupStarted"))) return false;
                    if (target.EffectTypeId.StartsWith("timing.handler.", StringComparison.Ordinal))
                    {
                        var binding = source.TimingBindings.Find(b => b.BindingId == ItemTransferEffectExecutor.Text(target.NormalizedArguments, "bindingId") &&
                            b.SourceEffectId == source.EffectId && b.AbilityId == entry.AbilityId && b.HandlerSlot == "playerCleanup" && b.ScopedPlayerId == context.PlayerId);
                        if (binding == null || ItemTransferEffectExecutor.Text(target.NormalizedArguments, "sourceEffectId") != source.EffectId ||
                            !string.IsNullOrEmpty(binding.AttachedEffectId) && binding.AttachedEffectId != target.EffectId ||
                            binding.Status != "attached" && binding.Status != "completed" && binding.Status != "cancelled") return false;
                    }
                    // 统一能力来源已由声明、目标参数和派发收据证明；旧奖励路由另验其专属目标。
                    else if (award && !unified && (target.EffectTypeId != ContentLifecycleEffectExecutor.AwardCleanupTypeId ||
                        ItemTransferEffectExecutor.Text(target.NormalizedArguments, "rewardItemId") != entry.SourceItemId))
                    { reason = "settlement_award_cleanup_target_invalid:" + entry.TargetEffectId; return false; }
                }
            }
            var unregistered = runtime.EffectNodes.FirstOrDefault(n => n.Settlement != null && !registeredTargets.Contains(n.EffectId));
            if (unregistered != null) { reason = "settlement_target_unregistered:" + unregistered.EffectId; return false; }
            reason = string.Empty; return true;
        }
        public static void Register(EffectRegistry registry)
        {
            YC.Domain.Cards.CharacterLifecycleService.Register(registry);
            if (registry.TryGet(YC.Domain.Cards.CharacterLifecycleService.ConsumeSourceTypeId, out var consume))
                consume.Validator = ValidateConsumeSourceSpec;
            SettlementEntryService.Register(registry);
            ResourcePaymentSubstitutionEffectExecutor.Register(registry);
        }
        public static string ValidateConsumeSourceSpec(EffectSpec spec)
        {
            var args = spec?.NormalizedArguments;
            if (ItemTransferEffectExecutor.Value(args, "apiVersion") != null)
            {
                string reason = ValidateConsumeSourceArguments(args);
                return reason.Length == 0 && YC.Domain.Cards.CharacterLifecycleService.ParsePlayer(ItemTransferEffectExecutor.Text(args, "player")) != spec.PlayerId
                    ? "invalid_player" : reason;
            }
            // 原在途插入定义只有这两个字段，继续沿原 Use/DoubleUseSourceItemId 消费许可。
            return args?.Kind == NormalizedValueKind.Object && args.Properties.All(p => p.Name == "useId" || p.Name == "sourceItemId") &&
                !string.IsNullOrEmpty(ItemTransferEffectExecutor.Text(args, "useId")) && !string.IsNullOrEmpty(ItemTransferEffectExecutor.Text(args, "sourceItemId"))
                ? string.Empty : "character_double_use_source_arguments_invalid";
        }
    }
    public partial class EffectTreeExecutor
    {
        private void AddResourcePaymentFacts(GameState work, EffectNodeRuntimeState owner, InteractionRequest request)
        {
            if (owner.Payment == null || request.InteractionTypeId == ResourcePaymentSubstitutionEffectExecutor.InteractionTypeId) return;
            var parameters = request.PromptParameters?.Kind == NormalizedValueKind.Object ? request.PromptParameters.Clone() :
                NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
            foreach (var entry in ResourcePaymentPreparationService.Facts(work, owner, registry).Properties)
            {
                parameters.Properties.RemoveAll(value => value.Name == entry.Name); parameters.Properties.Add(entry);
            }
            parameters.Properties.Sort((first, second) => StringComparer.Ordinal.Compare(first.Name, second.Name));
            request.PromptParameters = parameters;
        }
        private void ReopenSuspendedPaymentOnWorking(GameState work, EffectNodeRuntimeState owner, SettlementWindowRuntimeState window)
        {
            var previous = work.EffectRuntime.InteractionRequests.Find(r => r.GetStableInteractionId() == window.SuspendedInteractionId &&
                r.OwnerEffectId == owner.EffectId && r.Status == "invalidated");
            if (previous == null) throw new KernelException(EffectFaultCodes.InteractionInvalid, "payment_confirmation_resume_missing");
            var resumed = new EffectInteractionSpec { InteractionTypeId = previous.InteractionTypeId,
                AnsweringPlayerId = previous.AnsweringPlayerId, Visibility = previous.Visibility, PromptKey = previous.PromptKey,
                PromptParameters = previous.PromptParameters.Clone(), Presentation = GameplayPresentationNormalizer.Clone(previous.Presentation),
                CandidateSetId = previous.CandidateSetId, CandidateSetVersion = previous.CandidateSetVersion,
                CandidateResolutionId = previous.CandidateResolutionId,
                MinSelections = previous.MinSelections, MaxSelections = previous.MaxSelections, AllowDecline = previous.AllowDecline,
                AnswerSchema = previous.AnswerSchema };
            resumed.CandidateIds.AddRange(previous.CandidateIds);
            AddInteractionOnWorking(work, owner, resumed, 0);
            window.SuspendedInteractionId = string.Empty;
        }

        private static bool CanChangePaymentDraft(GameState work, EffectNodeRuntimeState owner)
        {
            if (work?.EffectRuntime == null || owner == null || owner.PendingOutcome != EffectPendingOutcome.None ||
                owner.Status == EffectNodeStatus.Completed || owner.Status == EffectNodeStatus.Failed || owner.Status == EffectNodeStatus.Faulted ||
                owner.Payment?.IsCommitted == true || owner.Payment?.IsCommitRequested == true || owner.Payment?.SelectedSubstitution != null)
                return false;
            var window = owner.SettlementWindow;
            if (window != null && (window.IsClosed || window.Context?.Kind != SettlementEntryService.ResourcePayment ||
                !string.IsNullOrEmpty(window.ActiveTargetEffectId) || !string.IsNullOrEmpty(window.SuspendedInteractionId) ||
                window.Entries.Any(e => e.AcceptedUseCount != 0 || !string.IsNullOrEmpty(e.TargetEffectId) || e.SettlementState != "available")))
                return false;
            return !work.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ResourcePaymentSubstitutionEffectExecutor.TypeId &&
                SettlementEntryService.IsWithin(work.EffectRuntime, n.EffectId, owner.EffectId));
        }

        private static void AddPaymentChoiceFacts(GameState work, EffectNodeRuntimeState owner, InteractionRequest request)
        {
            if (owner.EffectTypeId != ResourcePaymentChoiceEffectExecutor.TypeId || request.InteractionTypeId != ResourcePaymentChoiceEffectExecutor.InteractionTypeId) return;
            request.Presentation = request.Presentation ?? new GameplayInteractionPresentation();
            request.Presentation.Prompt = request.Presentation.Prompt ?? new GameplayTextRef("core:resource.payment.choice.prompt");
            var options = ResourcePaymentChoiceEffectExecutor.GetPaymentOptionsForPreview(work, owner)
                .Where(p => request.CandidateIds.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
                    ItemTransferEffectExecutor.Object("candidateId", NormalizedValue.CreateString(p.Key), "cost", ItemTransferEffectExecutor.Object(
                        "originium", NormalizedValue.CreateInteger(p.Value.Originium), "originiumShard", NormalizedValue.CreateInteger(p.Value.OriginiumShard),
                        "iron", NormalizedValue.CreateInteger(p.Value.Iron), "pureOriginium", NormalizedValue.CreateInteger(p.Value.PureOriginium),
                        "goldVoucher", NormalizedValue.CreateInteger(p.Value.GoldVoucher)))).ToList();
            var parameters = request.PromptParameters?.Kind == NormalizedValueKind.Object ? request.PromptParameters.Clone()
                : NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
            parameters.Properties.RemoveAll(p => p.Name == "paymentOptions");
            parameters.Properties.Add(new NormalizedValueEntry { Name = "paymentOptions", Value = NormalizedValue.CreateArray(options) });
            parameters.Properties.RemoveAll(p => p.Name == "paymentDraft");
            parameters.Properties.Add(new NormalizedValueEntry { Name = "paymentDraft", Value = ItemTransferEffectExecutor.Object(
                "candidateId", NormalizedValue.CreateString(ItemTransferEffectExecutor.Text(owner.ContinuationData, "selectedPaymentOptionId")),
                "canChange", NormalizedValue.CreateBoolean(CanChangePaymentDraft(work, owner)),
                "paymentId", NormalizedValue.CreateString(owner.Payment?.PaymentId ?? string.Empty),
                "windowId", NormalizedValue.CreateString(owner.SettlementWindow?.Context?.Id ?? string.Empty)) });
            parameters.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            request.PromptParameters = parameters;
        }

        public bool TryPreparePaymentDraft(int playerId, string ownerEffectId, string interactionId, string candidateId,
            int expectedRevision, string commandId, out string diagnostic)
        {
            diagnostic = "payment_draft_context_invalid";
            if (expectedRevision != Runtime.StateRevision) { diagnostic = "state_revision_mismatch"; return false; }
            var source = Runtime.EffectNodes.Find(n => n.EffectId == ownerEffectId);
            if (source?.PlayerId != playerId) { diagnostic = "settlement_permission_denied"; return false; }
            var request = Runtime.InteractionRequests.Find(r => r.GetStableInteractionId() == interactionId && r.OwnerEffectId == ownerEffectId &&
                r.Status == "open" && r.AnsweringPlayerId == playerId && r.InteractionTypeId == ResourcePaymentChoiceEffectExecutor.InteractionTypeId);
            if (request == null || !request.CandidateIds.Contains(candidateId) || source.EffectTypeId != ResourcePaymentChoiceEffectExecutor.TypeId) return false;
            if (!CanChangePaymentDraft(state, source)) { diagnostic = "payment_draft_already_consumed"; return false; }
            var active = SettlementHostProtocol.FindActiveWindow(state);
            if (active != null && active.Context.OwnerEffectId != ownerEffectId &&
                (string.IsNullOrEmpty(active.ActiveTargetEffectId) || !SettlementEntryService.IsWithin(Runtime, ownerEffectId, active.ActiveTargetEffectId)))
            { diagnostic = "settlement_window_not_active"; return false; }
            var work = GameStateCloneService.DeepClone(state);
            var owner = work.EffectRuntime.EffectNodes.Find(n => n.EffectId == ownerEffectId);
            if (!ResourcePaymentChoiceEffectExecutor.TryInitializeSelectedPayment(work, owner, candidateId, out diagnostic, registry)) return false;
            var workRequest = work.EffectRuntime.InteractionRequests.Find(r => r.GetStableInteractionId() == interactionId);
            if (!owner.Payment.BeforePaymentReady)
            {
                workRequest.Status = "invalidated";
                ResolveInteractionBlockerOnWorking(work.EffectRuntime, workRequest.RequestId);
                ItemTransferEffectExecutor.Set(owner, "paymentChoiceResumeInteractionId", NormalizedValue.CreateString(interactionId));
                var before = ResourcePaymentPreparationService.AwaitBeforePayment(new EffectExecutionContext(work, owner, registry, limits), owner.Payment.PaymentCost);
                if (before == null) { diagnostic = "payment_before_event_missing"; return false; }
                ApplyStepResult(work, owner, before, new List<RuleJournalEntry>());
                diagnostic = ""; return true;
            }
            if (!ResourcePaymentSubstitutionEffectExecutor.TryOpenPaymentWindow(work, owner, owner.Payment.PaymentCost, interactionId, out var window, out diagnostic)) return false;
            window.Context.PaymentCost = GameStateCloneService.DeepClone(owner.Payment.PaymentCost);
            registry.RefreshSettlementOffers(work, window);
            AddResourcePaymentFacts(work, owner, workRequest);
            AddPaymentChoiceFacts(work, owner, workRequest);
            workRequest.StateRevision = work.EffectRuntime.StateRevision + 1;
            CommitPreparedWork(work, commandId, new List<RuleJournalEntry> { new RuleJournalEntry {
                Kind = RuleJournalEntryKind.EffectNode, EntityId = ownerEffectId, Detail = "payment_draft_prepared" } }, -1, -1, new[] { ownerEffectId });
            diagnostic = string.Empty; return true;
        }

        private void RegisterAttachedSettlementSegments(GameState work, EffectNodeRuntimeState owner, IReadOnlyList<string> attached, string triggerId)
        {
            var runtime = work.EffectRuntime;
            var targets = attached.Select(id => runtime.EffectNodes.Find(n => n.EffectId == id)).Where(n => n.Settlement != null).ToList();
            if (targets.Count == 0) return;
            if (string.IsNullOrEmpty(triggerId)) throw new KernelException(EffectFaultCodes.InvalidEffectSpec, "settlement_cleanup_event_missing");
            var window = owner.SettlementWindow;
            if (window == null) owner.SettlementWindow = window = SettlementEntryService.CreateWindow(work, owner, SettlementEntryService.PlayerCleanup, owner.PlayerId, "", triggerId);
            if (window.IsClosed || window.Context.Kind != SettlementEntryService.PlayerCleanup || window.Context.TriggerId != triggerId)
                throw new KernelException(EffectFaultCodes.InvalidEffectSpec, "settlement_cleanup_event_context_mismatch");
            foreach (var target in targets)
            {
                var declaration = target.Settlement;
                if (declaration.EntryKind != SettlementEntryService.CleanupOrder || target.PlayerId != owner.PlayerId ||
                    !registry.TryGetSettlementSourceContract(declaration.AbilityId, out var contract) || contract.EntryKind != declaration.EntryKind ||
                    contract.LocalId != declaration.LocalId || contract.WholeEffectPolicy != declaration.WholeEffectPolicy)
                    throw new KernelException(EffectFaultCodes.InvalidEffectSpec, "settlement_source_contract_mismatch");
                if (target.EffectTypeId == CleanupSourceInvocationExecutor.RegistrationTypeId)
                { registry.RegisterAttachedCleanupSource(work, owner, target, contract); continue; }
                if (target.EffectTypeId == CleanupObligation.InvokeType)
                { CleanupObligation.RegisterSettlementEntry(work, owner, target); continue; }
                var entry = SettlementEntryService.RegisterCleanup(work, window, target.EffectId, declaration.LocalId, declaration.AbilityId,
                    declaration.SourceItemId, declaration.SourceEffectId, declaration.WholeEffectPolicy, declaration.Presentation, contract.AvailabilityHandlerId);
                entry.SourceDefinitionId = contract.DefinitionId;
            }
            // 同一Event的前一订阅可能暂置Ready，登记完成前尚未提交/执行，统一回到门控起点。
            foreach (var child in runtime.EffectNodes.Where(n => n.ParentEffectId == owner.EffectId && n.Status == EffectNodeStatus.Ready && n.LastCommitSequence == 0)) child.Status = EffectNodeStatus.Created;
            runtime.ActiveSettlementWindowId = window.Context.Id;
        }

        private void OpenInteractionSettlementWindow(GameState work, EffectNodeRuntimeState owner, InteractionRequest request)
        {
            if (registry.SettlementCatalogContentHash.Length == 0) return;
            string kind = string.Empty, useId = string.Empty;
            if (owner.EffectTypeId == YC.Domain.Cards.CharacterLifecycleService.UseTypeId && request.InteractionTypeId == YC.Domain.Cards.CharacterLifecycleService.ModeInteractionTypeId)
            {
                var use = work.CharacterUses.Find(u => u.OwnerEffectId == owner.EffectId && u.ExecutingPlayerId == owner.PlayerId && u.ModeOrder.Count == 0 && !u.Finished);
                if (use != null) { kind = SettlementEntryService.CharacterUse; useId = use.UseId; }
            }
            else if (owner.Payment != null && owner.Payment.BeforePaymentReady && owner.Payment.AllowsSubstitution && !owner.Payment.IsCommitted && request.InteractionTypeId != ResourcePaymentSubstitutionEffectExecutor.InteractionTypeId) kind = SettlementEntryService.ResourcePayment;
            if (kind.Length == 0 || owner.SettlementWindow?.IsClosed == true) return;
            var window = owner.SettlementWindow ?? SettlementEntryService.CreateWindow(work, owner, kind, owner.PlayerId, useId, "");
            registry.RefreshSettlementOffers(work, window);
            if (window.Entries.Count == 0) return;
            owner.SettlementWindow = window; work.EffectRuntime.ActiveSettlementWindowId = window.Context.Id;
        }
        public bool TryEndSettlementAction(int playerId, string windowId, string currentNodeEffectId, string interactionId, int expectedRevision,
            string commandId, out string diagnostic)
        {
            diagnostic = "settlement_window_not_active";
            var source = Runtime.EffectNodes.Find(n => n.SettlementWindow?.Context?.Id == windowId);
            if (source == null || windowId != Runtime.ActiveSettlementWindowId || source.SettlementWindow.IsClosed) return false;
            if (source.SettlementWindow.Context.PlayerId != playerId) { diagnostic = "settlement_permission_denied"; return false; }
            if (!SettlementEntryService.TryPrepareEndAction(state, source.SettlementWindow, playerId, currentNodeEffectId, interactionId, expectedRevision,
                n => SettlementHostProtocol.CanStopOriginalNode(state, n, playerId, interactionId), out var plan, out diagnostic)) return false;
            if (plan.Kind == SettlementEndActionKind.StopCurrentNode)
                return string.IsNullOrEmpty(plan.InteractionId) ? TryDeclineEffect(playerId, plan.NodeEffectId, out diagnostic)
                    : TrySubmitInteraction(plan.InteractionId, playerId, expectedRevision, NormalizedValue.CreateBoolean(false), out diagnostic);
            try
            {
                var work = GameStateCloneService.DeepClone(state);
                var owner = work.EffectRuntime.EffectNodes.Find(n => n.EffectId == source.EffectId);
                if (!CleanupSourceService.CanClose(work, owner.SettlementWindow, out diagnostic) ||
                    !SettlementEntryService.TryCloseCleanup(owner.SettlementWindow, out var declined, out diagnostic)) return false;
                var targets = declined.Select(id => work.EffectRuntime.EffectNodes.Find(n => n.EffectId == id)).ToList();
                if (targets.Any(n => n == null || n.Status != EffectNodeStatus.Created || n.ChildEffectIds.Count != 0)) { diagnostic = "settlement_cleanup_target_invalid"; return false; }
                var entries = new List<RuleJournalEntry>();
                int eventStart = work.EffectRuntime.RuleEvents.Count, receiptStart = work.EffectRuntime.DispatchReceipts.Count;
                work.EffectRuntime.ActiveSettlementWindowId = SettlementHostProtocol.FindAncestorWindowId(work.EffectRuntime, owner.ParentEffectId);
                foreach (var target in targets)
                {
                    target.PendingOutcome = EffectPendingOutcome.Failed; target.FailureReason = "whole_effect_declined"; target.Status = EffectNodeStatus.Blocked;
                    entries.Add(new RuleJournalEntry { Kind = RuleJournalEntryKind.EffectNode, EntityId = target.EffectId, Detail = "whole_effect_declined" });
                    TryCompleteNodeOnWorking(work, target, entries);
                }
                ActivateNextChild(work.EffectRuntime, owner);
                CommitPreparedWork(work, string.IsNullOrEmpty(commandId) ? "settlement.end:" + windowId : commandId, entries, eventStart, receiptStart,
                    new[] { owner.EffectId }.Concat(declined).ToArray());
                diagnostic = string.Empty; return true;
            }
            catch (KernelException error) { diagnostic = error.Message; return false; }
        }
        private bool TryInvokeSettlementEntry(int playerId, string optionId, string useId, int expectedRevision, out bool alreadyInvoked, out string diagnostic)
        {
            alreadyInvoked = false; diagnostic = "settlement_option_missing";
            var source = Runtime.EffectNodes.Find(n => n.SettlementWindow?.Entries.Exists(e => e.OptionId == optionId) == true);
            if (source == null) return false;
            var window = source.SettlementWindow;
            if (window.Context.Id != Runtime.ActiveSettlementWindowId) { diagnostic = "settlement_window_not_active"; return false; }
            if (!SettlementEntryService.TryPrepareInvocation(state, window, playerId, optionId, useId, expectedRevision,
                e => registry.QuerySettlementEntry(state, window, e, true), out alreadyInvoked, out var plan, out diagnostic)) return false;
            if (alreadyInvoked) return true;
            var work = GameStateCloneService.DeepClone(state);
            var owner = work.EffectRuntime.EffectNodes.Find(n => n.EffectId == source.EffectId); var savedWindow = owner.SettlementWindow;
            var entries = new List<RuleJournalEntry>(); string targetId = plan.TargetEffectId;
            if (plan.EntryKind == SettlementEntryService.OptionalInsertion)
            {
                var segment = SettlementEntryService.CreateCleanupSegment(playerId, optionId + ".body", plan.Effects);
                segment.SourceId = owner.SettlementWindow.Entries.Find(e => e.OptionId == optionId).SourceItemId;
                ValidateSpec(segment, 0, 0);
                var request = work.EffectRuntime.InteractionRequests.Find(r => r.GetStableInteractionId() == plan.InteractionId && r.Status == "open");
                if (request != null)
                {
                    savedWindow.SuspendedInteractionId = request.GetStableInteractionId(); savedWindow.ResumeFlowStage = owner.FlowStage;
                    request.Status = "invalidated"; ResolveInteractionBlockerOnWorking(work.EffectRuntime, request.GetStableInteractionId());
                }
                if (savedWindow.Context.Kind == SettlementEntryService.CharacterUse)
                {
                    var use = work.CharacterUses.Find(u => u.UseId == savedWindow.Context.TargetUseId && u.ExecutingPlayerId == playerId);
                    if (use == null) { diagnostic = "character_use_missing"; return false; }
                    use.DoubleUseSourceItemId = segment.SourceId; owner.FlowStage = "awaiting_insertion";
                }
                targetId = AppendChildEffects(work, owner, new[] { segment }, "settlement", optionId, entries)[0];
            }
            SettlementEntryService.RecordInvocation(savedWindow, plan, targetId);
            ActivateNextChild(work.EffectRuntime, owner);
            CommitPreparedWork(work, "settlement.invoke:" + optionId, entries, -1, -1, new[] { owner.EffectId, targetId });
            diagnostic = string.Empty; return true;
        }
    }
}
