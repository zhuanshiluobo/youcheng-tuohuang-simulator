using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Presentation;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public static partial class CleanupObligation
    {
        // 表现是登记上下文的一部分，已经被RegistrationDigest绑定；不据文案键选择规则。
        private static SettlementPresentationDefinition FrozenPresentation(GameState state, CleanupObligationRecord record)
        {
            var value = Value(record.Context, "settlementPresentation");
            if (value != null && value.Kind != NormalizedValueKind.Null)
                return GameplayPresentationNormalizer.FromNormalized<SettlementPresentationDefinition>(value);
            // 旧有行动义务保已归档声明的表现；不重新发现当前来源或补造调用证明。
            var action = state.EffectRuntime.AbilityActionInvocations.SingleOrDefault(i => i.InvocationId == record.OriginId && record.OriginKind == "action");
            var declaration = state.ContentDefinitions.AbilitySources.SingleOrDefault(d => d.AbilityId == record.SourceRef.AbilityId && d.DefinitionId == record.SourceRef.DefinitionId);
            var presentation = declaration?.Actions.SingleOrDefault(a => a.ActionId == action?.ActionId)?.Presentation;
            if (presentation?.Name == null || presentation.Description == null)
                throw new InvalidOperationException("cleanup_obligation_presentation_missing");
            return new SettlementPresentationDefinition {
                Label = GameplayPresentationNormalizer.Clone(presentation.Name),
                Description = GameplayPresentationNormalizer.Clone(presentation.Description),
                AriaLabel = GameplayPresentationNormalizer.Clone(presentation.Name)
            };
        }

        private static SettlementEffectDefinition SettlementDeclaration(GameState state, CleanupObligationRecord record) =>
            new SettlementEffectDefinition {
                EntryKind = SettlementEntryService.CleanupOrder, LocalId = SettlementLocalId, AbilityId = InvokeType,
                WholeEffectPolicy = SettlementWholeEffectPolicy.Required,
                SourceItemId = record.SourceRef.ItemId, SourceEffectId = record.ScheduleEffectId,
                Presentation = FrozenPresentation(state, record)
            };

        private static bool SameSettlement(SettlementEffectDefinition expected, SettlementEffectDefinition actual) =>
            actual != null && expected.EntryKind == actual.EntryKind && expected.LocalId == actual.LocalId &&
            expected.AbilityId == actual.AbilityId && expected.WholeEffectPolicy == actual.WholeEffectPolicy &&
            expected.SourceItemId == actual.SourceItemId && expected.SourceEffectId == actual.SourceEffectId &&
            GameplayPresentationNormalizer.ToNormalized(expected.Presentation).ToDeterministicString() == GameplayPresentationNormalizer.ToNormalized(actual.Presentation).ToDeterministicString();

        internal static SettlementEntryRuntimeState RegisterSettlementEntry(GameState state, EffectNodeRuntimeState owner, EffectNodeRuntimeState target)
        {
            var record = state.EffectRuntime.CleanupObligations.Single(r => r.ObligationId == Text(target.NormalizedArguments, "obligationId"));
            // Event的DispatchReceipt在整批挂接后才写入；这里只验证当前工作事务内的来源/事件形状，提交及恢复仍用完整收据校验。
            if (!ValidateInvocation(state, record, target, attaching: true) || record.DueRound != state.Round || owner.SettlementWindow.Context.TriggerId != Text(target.NormalizedArguments, "triggerEventId"))
                throw new InvalidOperationException("cleanup_obligation_settlement_origin_invalid");
            var expected = SettlementDeclaration(state, record);
            if (!SameSettlement(expected, target.Settlement))
                throw new InvalidOperationException("cleanup_obligation_settlement_metadata_invalid");
            var entry = SettlementEntryService.RegisterCleanup(state, owner.SettlementWindow, target.EffectId, expected.LocalId, expected.AbilityId,
                expected.SourceItemId, expected.SourceEffectId, expected.WholeEffectPolicy, expected.Presentation);
            entry.SourceDefinitionId = record.SourceRef.DefinitionId;
            return entry;
        }

        internal static bool ValidateSettlementEntry(GameState state, SettlementWindowRuntimeState window, SettlementEntryRuntimeState entry)
        {
            var target = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == entry.TargetEffectId);
            var record = target == null ? null : state.EffectRuntime.CleanupObligations.SingleOrDefault(r => r.ObligationId == Text(target.NormalizedArguments, "obligationId"));
            if (record == null || target.EffectTypeId != InvokeType || target.ParentEffectId != window.Context.OwnerEffectId ||
                entry.EntryKind != SettlementEntryService.CleanupOrder || entry.AbilityId != InvokeType || entry.LocalId != SettlementLocalId ||
                entry.WholeEffectPolicy != SettlementWholeEffectPolicy.Required || entry.AvailabilityHandlerId != "" ||
                entry.SourceItemId != record.SourceRef.ItemId || entry.SourceDefinitionId != record.SourceRef.DefinitionId ||
                entry.SourceEffectId != record.ScheduleEffectId || entry.TriggerId != window.Context.TriggerId || !ValidateInvocation(state, record, target)) return false;
            try
            {
                var expected = SettlementDeclaration(state, record);
                return SameSettlement(expected, target.Settlement) &&
                    GameplayPresentationNormalizer.ToNormalized(expected.Presentation).ToDeterministicString() == GameplayPresentationNormalizer.ToNormalized(entry.Presentation).ToDeterministicString();
            }
            catch (ArgumentException) { return false; }
        }
    }

    public partial class EffectTreeExecutor
    {
        private void RegisterScheduledCleanupOnWorking(GameState work, EffectNodeRuntimeState schedule, List<RuleJournalEntry> entries)
        {
            if (schedule.EffectTypeId != CleanupObligation.ScheduleType) return;
            var record = work.EffectRuntime.CleanupObligations.SingleOrDefault(r => r.ScheduleEffectId == schedule.EffectId);
            if (record == null || record.DueRound != work.Round) return;
            var trigger = CleanupObligation.CurrentCleanup(work, schedule, record.PlayerId);
            if (trigger == null) return;
            var previous = work.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectTypeId == CleanupObligation.InvokeType && Text(n.NormalizedArguments, "obligationId") == record.ObligationId);
            if (previous != null)
            {
                if (!CleanupObligation.ValidateInvocation(work, record, previous)) throw new InvalidOperationException("cleanup_obligation_attachment_conflict");
                return;
            }
            var owner = work.EffectRuntime.EffectNodes.Single(n => n.EffectId == trigger.OwnerNodeId);
            AppendChildEffects(work, owner, new List<EffectSpec> { CleanupObligation.InvokeSpec(work, record, trigger, work.Round) },
                "cleanup_obligation", record.ObligationId, entries, settlementTriggerId: trigger.EventId);
        }
    }
}
