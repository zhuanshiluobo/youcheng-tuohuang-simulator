using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using YC.Domain.Cards;
using YC.Domain.Interactions;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    [Serializable] public sealed class PaymentInsertionInvocation
    {
        public string TargetEffectId = "";
        public List<EffectSpecRuntimeState> Effects = new List<EffectSpecRuntimeState>();
    }
    [Serializable] public sealed class PaymentInsertionRuntimeState
    {
        public string Version = ResourcePaymentInsertionService.Version;
        public PaymentSourceRule Rule;
        public List<PaymentInsertionInvocation> Invocations = new List<PaymentInsertionInvocation>();
    }

    // 真实纯规则可选声明入口；不从只读页面或任意source对象产生授权。
    public static class ResourcePaymentInsertionService
    {
        public const string Version = "payment_insertion.v2";
        public const string LegacyVersion = "payment_insertion.v1";
        private static bool SupportedVersion(string version) => version == Version || version == LegacyVersion;
        public const string AvailabilityId = "payment_insertion.current.v1";
        public static bool ValidDeclaration(PaymentSourceInsertion d)
        {
            if (d?.ApiVersion != 1 || d.MaxUses < 1 || d.MaxUses > 99 || d.Presentation?.Label == null ||
                d.Presentation.Description == null || d.Presentation.AriaLabel == null) return false;
            try { GameplayPresentationNormalizer.Validate(d.Presentation); return true; } catch (ArgumentException) { return false; }
        }
        public static bool TryReadDeclaration(NormalizedValue v, out PaymentSourceInsertion result)
        {
            result = null;
            if (v?.Kind != NormalizedValueKind.Object || !v.IsValid() || v.Properties.Count != 3 || Value(v, "presentation") == null || v.Properties.Any(p => !new[] { "apiVersion", "maxUses", "presentation" }.Contains(p.Name)) ||
                Value(v, "apiVersion")?.Kind != NormalizedValueKind.Integer || Number(v, "apiVersion") != 1 || Value(v, "maxUses")?.Kind != NormalizedValueKind.Integer) return false;
            try { result = new PaymentSourceInsertion { ApiVersion = 1, MaxUses = Number(v, "maxUses"),
                Presentation = GameplayPresentationNormalizer.FromNormalized<SettlementPresentationDefinition>(Value(v, "presentation")) }; }
            catch (ArgumentException) { return false; }
            return ValidDeclaration(result);
        }
        // 受理历史不删改；从真实子节点的配置结果派生当前有效配置次数。
        public static int ConfiguredUseCount(GameState state, SettlementEntryRuntimeState entry)
        {
            int used = 0; if (entry?.PaymentInsertion?.Invocations == null) return int.MaxValue;
            foreach (var invocation in entry.PaymentInsertion.Invocations)
            {
                var leaves = state.EffectRuntime.EffectNodes.Where(n => n.ParentEffectId == invocation.TargetEffectId &&
                    n.EffectTypeId == ResourcePaymentSubstitutionEffectExecutor.TypeId && Number(n.NormalizedArguments, "apiVersion") == 2).ToList();
                if (leaves.Count > 1) return int.MaxValue;
                var leaf = leaves.SingleOrDefault();
                if (leaf == null || leaf.Status != EffectNodeStatus.Completed && leaf.PendingOutcome != EffectPendingOutcome.Completed) continue;
                if (invocation.Effects.Count != 1 || JsonConvert.SerializeObject(leaf.NormalizedArguments) != JsonConvert.SerializeObject(invocation.Effects[0].NormalizedArguments)) return int.MaxValue;
                var result = leaf.NormalizedResult;
                if (result == null || result.Kind == NormalizedValueKind.Null) continue; // 旧API2取消/零选择没有配置结果。
                if (result.Kind != NormalizedValueKind.Object || Text(result, "paymentId") != Text(leaf.NormalizedArguments, "paymentOwnerEffectId")) return int.MaxValue;
                string outcome = Text(result, "configurationOutcome");
                if (outcome == "withdrawn") used = 0;
                else if (outcome == "reconfigured") used = Math.Max(1, used);
                else if (outcome == "" || outcome == "configured") used = checked(used + 1);
                else return int.MaxValue; if (used > entry.MaxUses) return int.MaxValue;
            }
            return used;
        }
        public static bool HasConfiguration(GameState state, SettlementEntryRuntimeState entry)
        {
            if (entry?.PaymentInsertion?.Rule == null || entry.Effects?.Count != 1) return false;
            var owner = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == Text(entry.Effects[0].NormalizedArguments, "paymentOwnerEffectId"));
            var draft = owner?.Payment == null ? null : Draft(owner.Payment, entry.PaymentInsertion.Rule);
            return draft?.Status == "valid" && draft.PaymentGeneration == owner.Payment.PaymentGeneration && PaymentResources.Total(draft.Allocation) > 0;
        }
        public static bool ConfigurationBudgetAllows(GameState state, SettlementEntryRuntimeState entry) =>
            ConfiguredUseCount(state, entry) <= entry.MaxUses && (ConfiguredUseCount(state, entry) < entry.MaxUses || HasConfiguration(state, entry));
        public static bool CanStartConfiguration(GameState state, SettlementEntryRuntimeState entry)
        {
            if (entry.PaymentInsertion == null || entry.AcceptedUseCount == int.MaxValue || !ConfigurationBudgetAllows(state, entry)) return false;
            if (entry.SettlementState == "available") return true;
            // 兼容旧v1已取消而被标resolved的来源；不改写受理历史或旧收据。
            return entry.SettlementState == "resolved" && state.EffectRuntime.EffectNodes.Any(n => n.EffectId == entry.TargetEffectId && n.Status == EffectNodeStatus.Completed);
        }
        private static string LocalId(PaymentSourceRule r) => StableIdFactory.Create("payment-insertion-slot", r.SourceRef.SourceKey, r.RuleId);
        private static PaymentSourceDraft Draft(ResourcePaymentRuntimeState p, PaymentSourceRule r) => p.SourceDrafts.SingleOrDefault(d => ResourcePaymentSourceService.Key(d.Rule) == ResourcePaymentSourceService.Key(r));
        private static EffectSpec Call(EffectNodeRuntimeState owner, PaymentSourceRule rule)
        {
            return EffectSpec.Create(ResourcePaymentSubstitutionEffectExecutor.TypeId, Object("apiVersion", NormalizedValue.CreateInteger(2),
                "player", NormalizedValue.CreateString(owner.PlayerId.ToString(CultureInfo.InvariantCulture)),
                "paymentOwnerEffectId", NormalizedValue.CreateString(owner.EffectId), "sourceKey", NormalizedValue.CreateString(rule.SourceRef.SourceKey),
                "ruleId", NormalizedValue.CreateString(rule.RuleId), "paymentGeneration", NormalizedValue.CreateInteger(owner.Payment.PaymentGeneration),
                "draftRevision", NormalizedValue.CreateInteger(Draft(owner.Payment, rule)?.DraftRevision ?? 0)), owner.PlayerId);
        }
        public static string ValidateArguments(NormalizedValue a) => a?.Kind != NormalizedValueKind.Object || Number(a, "apiVersion") != 2 ||
            CharacterLifecycleService.ParsePlayer(Text(a, "player")) < 1 || string.IsNullOrEmpty(Text(a, "paymentOwnerEffectId")) || string.IsNullOrEmpty(Text(a, "sourceKey")) ||
            string.IsNullOrEmpty(Text(a, "ruleId")) || Value(a, "paymentGeneration")?.Kind != NormalizedValueKind.Integer || Number(a, "paymentGeneration") < 1 ||
            Value(a, "draftRevision")?.Kind != NormalizedValueKind.Integer || Number(a, "draftRevision") < 0 ||
            a.Properties.Any(e => !new[] { "apiVersion", "player", "paymentOwnerEffectId", "sourceKey", "ruleId", "paymentGeneration", "draftRevision", "kind", "effectTypeId" }.Contains(e.Name))
            ? "payment_insertion_arguments_invalid" : "";

        public static void Refresh(GameState state, EffectRegistry registry, SettlementWindowRuntimeState window)
        {
            if (window?.Context?.Kind != SettlementEntryService.ResourcePayment || window.IsClosed) return;
            var owner = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == window.Context.OwnerEffectId);
            var p = owner?.Payment;
            if (!ResourcePaymentDraftService.Editable(p) || !p.BeforePaymentReady || !p.AllowsSubstitution) return;
            foreach (var rule in ResourcePaymentSourceService.Query(state, registry, owner, p.PaymentCost).Where(r => r.Insertion != null))
            {
                string localId = LocalId(rule);
                var old = window.Entries.SingleOrDefault(e => e.LocalId == localId && e.SourceItemId == rule.SourceRef.ItemId);
                if (old == null && Maximum(state, registry, owner, rule, out _, out _) < 1) continue;
                // 身份/规则版本变化不改写既成受理记录；原项会被当前资格查询禁用。
                if (old != null && (old.PaymentInsertion == null || ResourcePaymentSourceService.Fingerprint(old.PaymentInsertion.Rule) != ResourcePaymentSourceService.Fingerprint(rule))) continue;
                var entry = SettlementEntryService.RegisterOffer(window, new SettlementOfferDefinition { ApiVersion = 2,
                    PlayerId = owner.PlayerId, SourceItemId = rule.SourceRef.ItemId, LocalId = localId, WindowId = window.Context.Id, OwnerEffectId = owner.EffectId,
                    MaxUses = rule.Insertion.MaxUses, AvailabilityHandlerId = AvailabilityId, Presentation = rule.Insertion.Presentation,
                    PaymentInsertion = new PaymentInsertionRuntimeState { Rule = GameStateCloneService.DeepClone(rule) },
                    Effects = new List<EffectSpecRuntimeState> { Call(owner, rule).ToPersistentDefinition() } });
                entry.AbilityId = rule.SourceRef.AbilityId; entry.SourceDefinitionId = rule.SourceRef.DefinitionId;
            }
        }
        public static SettlementAvailabilityResult Availability(GameState state, EffectRegistry registry, SettlementWindowRuntimeState window, SettlementEntryRuntimeState entry)
        {
            bool enabled = TryCurrent(state, registry, window, entry, out var owner, out var rule) &&
                CanStartConfiguration(state, entry) && entry.Effects?.Count == 1 && ValidBody(owner, rule, entry.Effects[0]) &&
                Number(entry.Effects[0].NormalizedArguments, "paymentGeneration") == owner.Payment.PaymentGeneration &&
                Number(entry.Effects[0].NormalizedArguments, "draftRevision") == (Draft(owner.Payment, rule)?.DraftRevision ?? 0) &&
                (Maximum(state, registry, owner, rule, out _, out _) > 0 || HasConfiguration(state, entry));
            return new SettlementAvailabilityResult { Enabled = enabled, DisabledReasonKey = enabled ? "" : "core:presentation.unavailable" };
        }
        // 已选择新合同的同一储存来源不再同时投影旧zone-only入口，避免绕开同源受理次数。
        public static bool SupersedesLegacyOffer(GameState s, EffectRegistry registry, SettlementWindowRuntimeState w, SettlementOfferDefinition offer)
        {
            if (w.Context.Kind != SettlementEntryService.ResourcePayment) return false;
            var owner = s.EffectRuntime.EffectNodes.Find(n => n.EffectId == w.Context.OwnerEffectId);
            if (owner?.Payment?.PaymentProtocolVersion != 2) return false;
            var calls = offer.Effects.Where(e => e.EffectTypeId == ResourcePaymentSubstitutionEffectExecutor.TypeId && Number(e.NormalizedArguments, "apiVersion") == 1).ToList();
            if (calls.Count == 0) return false;
            var rules = ResourcePaymentSourceService.Query(s, registry, owner, owner.Payment.PaymentCost);
            return calls.Any(call => rules.Any(r => r.Insertion != null && r.SourceRef.ItemId == offer.SourceItemId && r.ItemCost != null &&
                s.ItemZones.Zones.Any(z => z.ZoneId == Text(call.NormalizedArguments, "sourceZoneId") && z.ParentId == r.ItemCost.CardInstanceId && z.LocalZoneId == r.ItemCost.LocalZoneId)));
        }
        private static bool ValidBody(EffectNodeRuntimeState owner, PaymentSourceRule rule, EffectSpecRuntimeState body)
        {
            if (body == null || ValidateArguments(body.NormalizedArguments).Length != 0 ||
                Text(body.NormalizedArguments, "sourceKey") != rule.SourceRef.SourceKey || Text(body.NormalizedArguments, "ruleId") != rule.RuleId ||
                Text(body.NormalizedArguments, "paymentOwnerEffectId") != owner.EffectId || CharacterLifecycleService.ParsePlayer(Text(body.NormalizedArguments, "player")) != owner.PlayerId) return false;
            var expected = Call(owner, rule).ToPersistentDefinition(); expected.NormalizedArguments = body.NormalizedArguments.Clone();
            return JsonConvert.SerializeObject(expected) == JsonConvert.SerializeObject(body);
        }
        private static bool TryCurrent(GameState s, EffectRegistry registry, SettlementWindowRuntimeState window, SettlementEntryRuntimeState entry,
            out EffectNodeRuntimeState owner, out PaymentSourceRule rule)
        {
            owner = s.EffectRuntime.EffectNodes.Find(n => n.EffectId == window?.Context?.OwnerEffectId); rule = null;
            var p = owner?.Payment; var saved = entry?.PaymentInsertion;
            if (!SupportedVersion(saved?.Version) || window.Context.Kind != SettlementEntryService.ResourcePayment || window.Context.PlayerId != owner?.PlayerId ||
                !ResourcePaymentDraftService.Editable(p) || !p.BeforePaymentReady || !p.AllowsSubstitution || p.SourceEdits.Any(e => e.Status == "open")) return false;
            rule = ResourcePaymentSourceService.Query(s, registry, owner, p.PaymentCost).SingleOrDefault(r => ResourcePaymentSourceService.Key(r) == ResourcePaymentSourceService.Key(saved.Rule));
            return rule?.Insertion != null && ResourcePaymentSourceService.Fingerprint(rule) == ResourcePaymentSourceService.Fingerprint(saved.Rule);
        }
        private static int Maximum(GameState s, EffectRegistry registry, EffectNodeRuntimeState owner, PaymentSourceRule r,
            out Dictionary<string, int> rows, out List<YC.Domain.Items.ItemInstanceState> items, bool replace = false)
        {
            var p = owner.Payment; var current = Draft(p, r);
            var drafts = p.SourceDrafts.Where(d => d.Status == "valid" && (!replace || d != current)).ToList();
            int used = replace ? 0 : PaymentResources.Total(Draft(p, r)?.Allocation ?? new ResourceSet());
            int quotaUsed = drafts.Where(d => d.Rule.QuotaGroupId == r.QuotaGroupId && d.Rule.QuotaScope == r.QuotaScope).Sum(d => PaymentResources.Total(d.Allocation));
            if (r.QuotaScope == "build") quotaUsed += s.EffectRuntime.EffectNodes.Where(n => n.EffectId != owner.EffectId && n.Payment?.IsCommitted == true)
                .SelectMany(n => n.Payment.SourceReceipts).Where(x => x.BuildIdentity == ResourcePaymentSourceService.BuildIdentity(s, owner) && x.Rule.QuotaGroupId == r.QuotaGroupId)
                .Sum(x => PaymentResources.Total(x.Allocation));
            int max = Math.Max(0, Math.Min(r.MaxUnits - used, r.QuotaLimit - quotaUsed));
            var reserved = new HashSet<string>(drafts.SelectMany(d => d.SelectedItemIds), StringComparer.Ordinal);
            items = r.ItemCost == null ? new List<YC.Domain.Items.ItemInstanceState>() : ResourcePaymentSourceService.TransferableItems(s, registry, owner, r).Where(i => !reserved.Contains(i.ItemId)).ToList();
            if (r.ItemCost != null) max = Math.Min(max, items.Count / r.ItemCost.CountPerUnit);
            rows = r.AllowedResourceTypes.ToDictionary(t => t.ToString(), t => Math.Min(max, Math.Max(0, p.PaymentCost.Get(t) - drafts.Sum(d => d.Allocation.Get(t)))), StringComparer.Ordinal);
            return Math.Min(max, rows.Values.Sum());
        }
        private static string Inventory(GameState s, IEnumerable<YC.Domain.Items.ItemInstanceState> items) => StableIdFactory.Create("payment-insertion-inventory",
            string.Join("|", items.Select(i => i.ItemId + ":" + ResourcePaymentSourceService.ItemSelectionGroup(s, i))));

        public static EffectStepResult Execute(EffectExecutionContext c, BoundedResourceAllocationDecoder decoder)
        {
            var args = c.Node.NormalizedArguments;
            if (ValidateArguments(args).Length != 0) return EffectStepResult.Failed("payment_insertion_arguments_invalid");
            var owner = c.State.EffectRuntime.EffectNodes.Find(n => n.EffectId == Text(args, "paymentOwnerEffectId"));
            var window = owner?.SettlementWindow;
            var entry = window?.Entries.SingleOrDefault(e => e.PaymentInsertion != null && e.SettlementState == "executing" &&
                SettlementEntryService.IsWithin(c.State.EffectRuntime, c.Node.EffectId, e.TargetEffectId));
            if (entry == null || !TryCurrent(c.State, c.Registry, window, entry, out owner, out var rule) || owner.PlayerId != c.Node.PlayerId ||
                CharacterLifecycleService.ParsePlayer(Text(args, "player")) != owner.PlayerId || Text(args, "sourceKey") != rule.SourceRef.SourceKey || Text(args, "ruleId") != rule.RuleId ||
                Number(args, "paymentGeneration") != owner.Payment.PaymentGeneration || Number(args, "draftRevision") != (Draft(owner.Payment, rule)?.DraftRevision ?? 0))
                return EffectStepResult.Failed("payment_insertion_context_invalid");
            var previous = Draft(owner.Payment, rule);
            bool replace = c.Node.FlowStage == "" ? HasConfiguration(c.State, entry) &&
                (ConfiguredUseCount(c.State, entry) >= entry.MaxUses || Maximum(c.State, c.Registry, owner, rule, out _, out _) == 0) :
                Text(c.Node.ContinuationData, "insertionConfigurationMode") == "replace";
            int maximum = Maximum(c.State, c.Registry, owner, rule, out var rows, out var items, replace);
            string inventory = Inventory(c.State, items);
            if (c.Node.FlowStage.Length != 0 && (Text(c.Node.ContinuationData, "insertionRule") != ResourcePaymentSourceService.Fingerprint(rule) ||
                Text(c.Node.ContinuationData, "insertionInventory") != inventory)) return EffectStepResult.Failed("payment_insertion_resume_identity_invalid");
            if (c.Node.FlowStage == "")
            {
                if (maximum < 1 && !replace) return EffectStepResult.Failed("payment_insertion_no_capacity");
                Set(c.Node, "insertionConfigurationMode", NormalizedValue.CreateString(replace ? "replace" : "append"));
                Set(c.Node, "insertionRule", NormalizedValue.CreateString(ResourcePaymentSourceService.Fingerprint(rule)));
                Set(c.Node, "insertionInventory", NormalizedValue.CreateString(inventory));
                var request = new EffectInteractionSpec {
                    InteractionTypeId = ResourcePaymentSubstitutionEffectExecutor.InteractionTypeId, AnswerSchema = ResourcePaymentSubstitutionEffectExecutor.AnswerSchema,
                    AnsweringPlayerId = owner.PlayerId, Visibility = "owner", AllowDecline = true, MinSelections = 0, MaxSelections = 1,
                    PromptParameters = Object("paymentId", NormalizedValue.CreateString(owner.EffectId), "minAmount", NormalizedValue.CreateInteger(0), "maxAmount", NormalizedValue.CreateInteger(maximum),
                        "rowIds", Strings(rows.Keys), "rowMaximums", NormalizedValue.CreateObject(rows.Select(x => new NormalizedValueEntry { Name = x.Key, Value = NormalizedValue.CreateInteger(x.Value) }).ToList())),
                    Presentation = new GameplayInteractionPresentation { Prompt = rule.Insertion.Presentation.Description } };
                request.CandidateIds.AddRange(rows.Keys);
                return EffectStepResult.Continue("allocating").AddInteraction(request);
            }
            var answer = c.GetLatestInteractionAnswer();
            if (answer?.Kind == NormalizedValueKind.Boolean && !answer.BooleanValue) return EffectStepResult.Completed();
            ResourceSet added;
            IReadOnlyList<string> selected;
            if (c.Node.FlowStage == "allocating")
            {
                if (answer?.Kind != NormalizedValueKind.String || !decoder(answer.StringValue, rows.Keys.ToList(), 0, maximum, rows, out var amounts, out _))
                    return EffectStepResult.Failed("payment_insertion_allocation_invalid");
                added = new ResourceSet(); foreach (var type in rule.AllowedResourceTypes) added.Set(type, amounts.TryGetValue(type.ToString(), out int n) ? n : 0);
                int units = PaymentResources.Total(added);
                if (units == 0)
                {
                    if (!replace) return EffectStepResult.Completed();
                    if (!ResourcePaymentDraftService.TrySetSourceDraft(c.State, c.Registry, owner, rule.SourceRef.SourceKey, rule.RuleId,
                        owner.Payment.PaymentGeneration, previous.DraftRevision, new ResourceSet(), Array.Empty<string>(), "", out string withdrawalReason)) return EffectStepResult.Failed(withdrawalReason);
                    return EffectStepResult.Completed(Object("paymentId", NormalizedValue.CreateString(owner.EffectId), "configurationOutcome", NormalizedValue.CreateString("withdrawn")));
                }
                selected = Array.Empty<string>();
                if (rule.ItemCost != null && !ResourcePaymentSourceService.TrySelectItems(c.State, c.Registry, owner, rule, units,
                    owner.Payment.SourceDrafts.Where(d => d.Status == "valid" && (!replace || d != previous)).SelectMany(d => d.SelectedItemIds), out selected, out string why))
                {
                    if (why != "payment_token_selection_required") return EffectStepResult.Failed(why);
                    Set(c.Node, "insertionAdded", PaymentResources.Write(added));
                    int count = checked(units * rule.ItemCost.CountPerUnit);
                    var request = new EffectInteractionSpec { InteractionTypeId = "lua.choice", AnswerSchema = "candidate_ids",
                        AnsweringPlayerId = owner.PlayerId, Visibility = "owner", AllowDecline = true, MinSelections = count, MaxSelections = count,
                        Presentation = new GameplayInteractionPresentation { Prompt = rule.Insertion.Presentation.Description,
                            Options = items.Select(i => new GameplayOptionPresentation { CandidateId = i.ItemId, Label = new GameplayTextRef("core:interaction.entity.item",
                                new GameplayTextArgument { Name = "entity", Kind = "item", Value = NormalizedValue.CreateStableReference("item", i.ItemId) }) }).ToList() } };
                    request.CandidateIds.AddRange(items.Select(i => i.ItemId));
                    return EffectStepResult.Continue("selecting_items").AddInteraction(request);
                }
            }
            else if (c.Node.FlowStage == "selecting_items")
            {
                if (!PaymentResources.TryRead(Value(c.Node.ContinuationData, "insertionAdded"), out added) || answer?.Kind != NormalizedValueKind.Array ||
                    answer.Items.Any(v => v.Kind != NormalizedValueKind.String)) return EffectStepResult.Failed("payment_insertion_items_invalid");
                selected = answer.Items.Select(v => v.StringValue).ToList();
                if (selected.Count != (long)PaymentResources.Total(added) * rule.ItemCost.CountPerUnit || selected.Distinct().Count() != selected.Count || selected.Any(id => !items.Any(i => i.ItemId == id)))
                    return EffectStepResult.Failed("payment_insertion_items_invalid");
            }
            else return EffectStepResult.Failed("payment_insertion_stage_invalid");
            var allocation = replace ? added : PaymentResources.Add(previous?.Status == "valid" ? previous.Allocation : new ResourceSet(), added);
            var ids = (previous?.Status == "valid" && !replace ? previous.SelectedItemIds : new List<string>()).Concat(selected).ToList();
            if (!ResourcePaymentDraftService.TrySetSourceDraft(c.State, c.Registry, owner, rule.SourceRef.SourceKey, rule.RuleId,
                owner.Payment.PaymentGeneration, previous?.DraftRevision ?? 0, allocation, ids, "", out string reason)) return EffectStepResult.Failed(reason);
            return EffectStepResult.Completed(Object("paymentId", NormalizedValue.CreateString(owner.EffectId),
                "configurationOutcome", NormalizedValue.CreateString(replace ? "reconfigured" : "configured")));
        }

        public static bool ValidateEntry(GameState state, SettlementWindowRuntimeState w, SettlementEntryRuntimeState e, EffectRegistry registry)
        {
            var saved = e.PaymentInsertion; var r = saved?.Rule; var owner = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == w.Context.OwnerEffectId);
            if (!SupportedVersion(saved?.Version) || !ResourcePaymentRecovery.ValidRule(r) || !ValidDeclaration(r.Insertion) || w.Context.Kind != SettlementEntryService.ResourcePayment ||
                owner?.Payment?.PaymentProtocolVersion != 2 || !state.ItemZones.Items.Any(i => i.ItemId == r.SourceRef.ItemId && i.DefinitionId == r.SourceRef.DefinitionId) ||
                r.SourceRef.AuthorizedPlayerId != w.Context.PlayerId || e.SourceItemId != r.SourceRef.ItemId || e.SourceDefinitionId != r.SourceRef.DefinitionId ||
                e.AbilityId != r.SourceRef.AbilityId || e.EntryKind != SettlementEntryService.OptionalInsertion || e.LocalId != LocalId(r) || e.AvailabilityHandlerId != AvailabilityId || e.Visibility != "owner" ||
                e.OptionId != StableIdFactory.Create("settlement_option", w.Context.Id, e.SourceItemId, e.LocalId) || e.MaxUses != r.Insertion.MaxUses || e.AcceptedUseCount < 0 ||
                e.RegistrationSequence < 0 || e.RegistrationSequence >= w.NextRegistrationSequence || saved.Invocations == null || saved.Invocations.Count != e.AcceptedUseCount ||
                saved.Invocations.Select(i => i.TargetEffectId).Distinct().Count() != saved.Invocations.Count ||
                !new[] { "available", "executing", "resolved" }.Contains(e.SettlementState) ||
                JsonConvert.SerializeObject(e.Presentation) != JsonConvert.SerializeObject(r.Insertion.Presentation) ||
                e.Effects?.Count != 1 || !ValidBody(owner, r, e.Effects[0])) return false;
            if (registry != null && (r.VersionRef.ContentPackHash != registry.ContentPackHash || !registry.AbilityDeclarations.Any(d => d.DefinitionId == r.SourceRef.DefinitionId && d.AbilityId == r.SourceRef.AbilityId &&
                d.Kind == "ruleQuery" && d.QueryKinds.Contains(ResourcePaymentSourceService.QueryKind) && d.DeclarationVersion == r.VersionRef.DefinitionVersion && registry.GetAbilityHandlerHash(d.HandlerRef) == r.VersionRef.HandlerHash))) return false;
            foreach (var i in saved.Invocations)
            {
                var target = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == i.TargetEffectId);
                if (target?.ParentEffectId != owner.EffectId || target.PlayerId != w.Context.PlayerId || !owner.ChildEffectIds.Contains(target.EffectId) || target.EffectTypeId != SettlementEntryService.SegmentTypeId ||
                    i.Effects == null || i.Effects.Count != 1 || !ValidBody(owner, r, i.Effects[0]) ||
                    JsonConvert.SerializeObject(target.NestedEffects) != JsonConvert.SerializeObject(i.Effects) ||
                    Text(i.Effects[0].NormalizedArguments, "sourceKey") != r.SourceRef.SourceKey || Text(i.Effects[0].NormalizedArguments, "ruleId") != r.RuleId ||
                    Text(i.Effects[0].NormalizedArguments, "paymentOwnerEffectId") != owner.EffectId) return false;
                bool active = e.SettlementState == "executing" && e.TargetEffectId == target.EffectId;
                bool terminal = target.Status == EffectNodeStatus.Completed || target.Status == EffectNodeStatus.Failed;
                if (active ? terminal || target.Status == EffectNodeStatus.Faulted : !terminal) return false;
            }
            if (ConfiguredUseCount(state, e) > e.MaxUses) return false;
            return e.AcceptedUseCount == 0 ? e.TargetEffectId.Length == 0 && e.SettlementState == "available" : e.TargetEffectId == saved.Invocations.Last().TargetEffectId;
        }
    }
}
