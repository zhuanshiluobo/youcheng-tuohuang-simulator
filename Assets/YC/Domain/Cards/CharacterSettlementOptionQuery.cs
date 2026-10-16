using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Cards
{
    [Serializable]
    public sealed class CharacterSettlementOption
    {
        public string WindowId = string.Empty, LocalId = string.Empty, EntryKind = string.Empty, TargetEffectId = string.Empty, SettlementState = string.Empty;
        public long RegistrationSequence;
        public bool CanDeclineWholeEffect;
        public YC.Domain.Presentation.SettlementPresentationDefinition Presentation;
        public string SourceDefinitionId = string.Empty;
        public bool CanInspectSource;
        public YC.Domain.Presentation.GameplayTextRef DisabledReason;
        public string OptionId = string.Empty, InteractionId = string.Empty, OwnerEffectId = string.Empty, SourceItemId = string.Empty, TargetUseId = string.Empty, CandidateId = string.Empty;
        public int AnsweringPlayerId = -1, StateRevision, CandidateSetVersion, RequiredGoldVoucherAmount;
        public string TitleKey = string.Empty, DescriptionKey = string.Empty, DisabledReasonCode = string.Empty;
        public bool IsEnabled, VisibleToViewer;
    }

    public static class CharacterSettlementOptionQuery
    {
        public const string DoubleUseActionId = "character.grant_current_double_use";
        public static string OptionId(string ownerEffectId, string sourceItemId, string actionId) => StableIdFactory.Create("settlement_option", ownerEffectId, sourceItemId, actionId);
        public static bool IsInvoked(EffectNodeRuntimeState owner, string optionId) => Value(owner?.ContinuationData, "invokedOptionIds")?.Items.Any(value => value.StringValue == optionId) == true;
        public static IReadOnlyList<CharacterSettlementOption> GetOptions(GameState state, int viewerPlayerId, string currentUseId)
        {
            var result = new List<CharacterSettlementOption>();
            if (state?.EffectRuntime == null) return result;
            if (string.IsNullOrEmpty(currentUseId)) result.AddRange(YC.Domain.Effects.ContentLifecycleEffectExecutor.GetAwardOptions(state, viewerPlayerId));
            if (string.IsNullOrEmpty(currentUseId)) result.AddRange(CharacterContentEffectExecutor.GetCleanupOptions(state, viewerPlayerId));
            var use = state.CharacterUses.Find(value => value.UseId == currentUseId && value.ExecutingPlayerId == viewerPlayerId);
            if (use == null) return result;
            var owner = state.EffectRuntime.EffectNodes.Find(value => value.EffectId == use.OwnerEffectId);
            if (owner == null) return result;
            var request = state.EffectRuntime.InteractionRequests.LastOrDefault(value => value.OwnerEffectId == owner.EffectId && value.Status == "open" && value.InteractionTypeId == CharacterLifecycleService.ModeInteractionTypeId);
            var sources = state.CharacterUses.SelectMany(value => value.Modes.Where(mode => mode.SettlementActionId == DoubleUseActionId).Select(mode => new { value.CardInstanceId, Mode = mode })).GroupBy(value => value.CardInstanceId).Select(group => group.First());
            foreach (var source in sources)
            {
                var item = state.ItemZones.Items.Find(value => value.ItemId == source.CardInstanceId);
                if (item == null || item.ControllerSubject.PlayerId != viewerPlayerId) continue;
                string optionId = OptionId(owner.EffectId, item.ItemId, DoubleUseActionId);
                bool invoked = IsInvoked(owner, optionId);
                bool persistent = state.ItemZones.Zones.Any(zone => zone.ZoneId == item.ZoneId && zone.TemplateId == "core.zone.character_persistent") && CharacterLifecycleService.IsSubscriptionActive(state, item.ItemId, source.Mode.AbilityId);
                if (!persistent && !invoked) continue;
                string reason = invoked ? "settlement_already_invoked" :
                    use.Finished || request == null || use.CompletedModes.Count > 0 ? "character_window_closed" :
                    state.Phase != YC.Domain.Rules.GamePhase.ActionRound1 && state.Phase != YC.Domain.Rules.GamePhase.ActionRound2 ? "wrong_phase" :
                    state.CurrentPlayerId != viewerPlayerId ? "not_current_player" : state.FindPlayer(viewerPlayerId).CharacterCardLockedThisTurn ? "character_action_round_locked" :
                    use.DoubleUseGranted || use.SourceConsumed ? "character_double_use_already_granted" :
                    use.CardInstanceId == item.ItemId ? "character_double_use_source_invalid" : "";
                result.Add(new CharacterSettlementOption { OptionId = optionId, InteractionId = request?.GetStableInteractionId() ?? "", OwnerEffectId = owner.EffectId,
                    SourceItemId = item.ItemId, TargetUseId = use.UseId, CandidateId = item.ItemId, AnsweringPlayerId = viewerPlayerId,
                    StateRevision = state.EffectRuntime.StateRevision, CandidateSetVersion = request?.CandidateSetVersion ?? 0,
                    TitleKey = source.Mode.TitleKey, DescriptionKey = source.Mode.DescriptionKey, DisabledReasonCode = reason, IsEnabled = reason.Length == 0, VisibleToViewer = true });
            }
            return result;
        }
    }
}
