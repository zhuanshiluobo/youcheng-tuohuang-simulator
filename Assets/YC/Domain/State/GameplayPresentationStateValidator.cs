using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Presentation;

namespace YC.Domain.State
{
    public static partial class GameStateViewProjector
    {
        public static Func<GameplayTextArgument, bool> CreateGameplayTextArgumentVisibility(GameStateView view)
        {
            if (view?.ItemZones?.Items == null || view.CharacterSettlementOptions == null || view.Players == null ||
                view.Map?.OpenLocationIds == null || view.Map.ResourceTokens == null) return _ => false;
            var visibleItems = new HashSet<string>(view.ItemZones.Items.Select(i => i.ItemId), StringComparer.Ordinal);
            var visibleDefinitions = new HashSet<string>(view.ItemZones.Items.Select(i => i.DefinitionId), StringComparer.Ordinal);
            foreach (var option in view.CharacterSettlementOptions)
                if (option.AnsweringPlayerId == view.ViewerPlayerId && !string.IsNullOrEmpty(option.SourceDefinitionId)) visibleDefinitions.Add(option.SourceDefinitionId);
            bool CanSee(GameplayTextArgument argument)
            {
                if (argument?.Value == null) return false;
                string id = argument.Value.ReferenceId;
                switch (argument.Kind)
                {
                    case "item": return visibleItems.Contains(id);
                    case "definition": return visibleDefinitions.Contains(id);
                    case "player": return view.Players.Any(p => id == "p" + p.PlayerId || id == p.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    case "resource": return new[] { "originium", "originium_shard", "iron", "pure_originium", "gold_voucher", "Originium", "OriginiumShard", "Iron", "PureOriginium", "GoldVoucher" }.Contains(id);
                    case "location": return view.Map.OpenLocationIds.Contains(id) || view.Map.ResourceTokens.Any(p => p.LocationId == id);
                    default: return false;
                }
            }
            return CanSee;
        }
        private static void ProjectGameplayPresentation(GameState state, GameStateViewer viewer, GameStateView view)
        {
            var CanSee = CreateGameplayTextArgumentVisibility(view);
            foreach (var interaction in view.Interactions)
            {
                var rows = ItemTransferEffectExecutor.Value(interaction.PromptParameters, "rowIds");
                interaction.Presentation = GameplayPresentationNormalizer.ProjectInteraction(interaction.Presentation, true, interaction.CandidateIds,
                    rows?.Kind == NormalizedValueKind.Array ? rows.Items.Select(v => v.Kind == NormalizedValueKind.StableReference ? v.ReferenceId : v.StringValue) : null, CanSee);
            }
            foreach (var effect in view.Effects) effect.Presentation = GameplayPresentationNormalizer.ProjectEffect(effect.Presentation, CanSee);
            foreach (var option in view.CharacterSettlementOptions)
            {
                var visibleSource = view.ItemZones.Items.Find(i => i.ItemId == option.SourceItemId);
                option.CanInspectSource = visibleSource != null;
                if (string.IsNullOrEmpty(option.SourceDefinitionId)) option.SourceDefinitionId = visibleSource?.DefinitionId ?? string.Empty;
                if (visibleSource == null) option.SourceItemId = string.Empty;
                if (option.Presentation != null)
                {
                    option.Presentation = GameplayPresentationNormalizer.Clone(option.Presentation);
                    option.Presentation.Label = GameplayPresentationNormalizer.ProjectText(option.Presentation.Label, CanSee);
                    option.Presentation.Description = GameplayPresentationNormalizer.ProjectText(option.Presentation.Description, CanSee);
                    option.Presentation.AriaLabel = GameplayPresentationNormalizer.ProjectText(option.Presentation.AriaLabel, CanSee);
                }
                option.DisabledReason = GameplayPresentationNormalizer.ProjectText(option.DisabledReason, CanSee);
                if (option.DisabledReason == null && GameplayPresentationNormalizer.IsKey(option.DisabledReasonCode)) option.DisabledReason = new GameplayTextRef(option.DisabledReasonCode);
            }
            string active = state.EffectRuntime?.ActiveSettlementWindowId ?? string.Empty;
            view.ActiveSettlementWindowId = view.CharacterSettlementOptions.Any(o => o.WindowId == active && o.AnsweringPlayerId == viewer.PlayerId) ? active : string.Empty;
            if (viewer.Role != GameStateViewerRole.Player) return;
            view.SettlementEndAction = SettlementHostProtocol.GetEndActionView(state, viewer.PlayerId);
            if (view.SettlementEndAction != null) view.ActiveSettlementWindowId = view.SettlementEndAction.WindowId;
            var blocking = view.Interactions.LastOrDefault(i => i.Status == "open" && i.AnsweringPlayerId == viewer.PlayerId && i.Presentation?.Prompt != null);
            var current = view.Effects.LastOrDefault(e => e.PlayerId == viewer.PlayerId && e.Presentation != null && (e.Status == EffectNodeStatus.Running || e.Status == EffectNodeStatus.Blocked || e.Status == EffectNodeStatus.Ready));
            GameplayTextRef message = blocking?.Presentation?.Prompt ?? current?.Presentation?.Waiting ?? current?.Presentation?.Started;
            string effectId = blocking?.OwnerEffectId ?? current?.EffectId ?? string.Empty;
            // 没有显示声明的合法当前交互/节点也占有提示上下文，不能用历史反馈抢占。
            bool hasCurrentContext = view.Interactions.Any(i => i.Status == "open" && i.AnsweringPlayerId == viewer.PlayerId) ||
                view.Effects.Any(e => e.PlayerId == viewer.PlayerId && (e.Status == EffectNodeStatus.Running || e.Status == EffectNodeStatus.Blocked || e.Status == EffectNodeStatus.Ready));
            if (message == null && !hasCurrentContext)
            {
                var recent = state.EffectRuntime?.EffectNodes.Where(e => e.PlayerId == viewer.PlayerId && e.LastCommitSequence > 0 &&
                    e.LastCommitSequence == state.EffectRuntime.NextCommitSequence - 1 && view.Effects.Any(v => v.EffectId == e.EffectId) && (e.Status == EffectNodeStatus.Completed || e.Status == EffectNodeStatus.Failed))
                    .OrderByDescending(e => e.LastCommitSequence).FirstOrDefault();
                var presentation = GameplayPresentationNormalizer.ProjectEffect(recent?.Presentation, CanSee);
                bool stopped = recent?.FailureReason == "player_declined" || recent?.FailureReason == "whole_effect_declined";
                message = stopped ? presentation?.Stopped : recent?.Status == EffectNodeStatus.Completed ? presentation?.Succeeded : presentation?.Failed;
                effectId = recent?.EffectId ?? string.Empty;
            }
            if (message != null) view.ResolutionMessage = new ResolutionMessageView { OwnerEffectId = effectId, InteractionId = blocking?.InteractionId ?? string.Empty, ViewerPlayerId = viewer.PlayerId, StateRevision = view.StateRevision, Message = GameplayPresentationNormalizer.Clone(message) };
        }
    }
    public static class GameplayPresentationStateValidator
    {
        // 这里只校验共享显示元数据；Window/Payment业务约束复用各自唯一规则校验器。
        public static bool TryValidate(EffectRuntimeState runtime, out string reason)
        {
            reason = string.Empty;
            foreach (var node in runtime.EffectNodes)
                if (!TryValidateSpecMetadata(node.Presentation, node.Settlement, out reason) || !ValidateNested(node.NestedEffects, 0, out reason)) return false;
            foreach (var request in runtime.InteractionRequests)
                if (!GameplayPresentationNormalizer.TryValidate(request.Presentation, out reason)) return false;
            if (!string.IsNullOrEmpty(runtime.ActiveSettlementWindowId) && !runtime.EffectNodes.Any(n => n.SettlementWindow?.Context?.Id == runtime.ActiveSettlementWindowId && !n.SettlementWindow.IsClosed))
            { reason = "active_settlement_window_missing"; return false; }
            return true;
        }
        public static bool TryValidateSpecMetadata(EffectPresentationDefinition presentation, SettlementEffectDefinition settlement, out string reason)
        {
            if (!GameplayPresentationNormalizer.TryValidate(presentation, out reason)) return false;
            if (settlement == null) return true;
            if (settlement.EntryKind != SettlementEntryService.CleanupOrder || string.IsNullOrEmpty(settlement.AbilityId) || string.IsNullOrEmpty(settlement.SourceItemId) ||
                !(settlement.LocalId ?? "").StartsWith(settlement.AbilityId + ".", StringComparison.Ordinal) || !Enum.IsDefined(typeof(SettlementWholeEffectPolicy), settlement.WholeEffectPolicy) || settlement.Presentation == null)
            { reason = "settlement_spec_metadata_invalid"; return false; }
            return GameplayPresentationNormalizer.TryValidate(settlement.Presentation, out reason);
        }
        private static bool ValidateNested(IEnumerable<EffectSpecRuntimeState> specs, int depth, out string reason)
        {
            reason = string.Empty;
            if (depth > 64 || specs == null) { reason = "presentation_nested_spec_invalid"; return false; }
            foreach (var spec in specs)
                if (spec == null || !TryValidateSpecMetadata(spec.Presentation, spec.Settlement, out reason) || !ValidateNested(spec.NestedEffects, depth + 1, out reason)) return false;
            return true;
        }
    }
}
