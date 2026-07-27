using System;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>统一角色牌弹窗结算与地图选点，保留两者原有分工。</summary>
    internal sealed class CharacterCardInteraction : InteractionBase
    {
        private readonly CharacterCardEffectInteractionUiCoordinator effectCoordinator;
        private readonly CharacterMapInteractionCoordinator mapCoordinator;
        private readonly Func<bool> hasPendingCharacterResolution;
        private readonly Func<bool> hasFacilitySelection;
        private readonly Func<bool> tryCancelFacilitySelection;

        public CharacterCardInteraction(
            CharacterCardEffectInteractionUiCoordinator effectCoordinator,
            CharacterMapInteractionCoordinator mapCoordinator,
            Func<bool> hasPendingCharacterResolution,
            Func<bool> hasFacilitySelection,
            Func<bool> tryCancelFacilitySelection)
        {
            this.effectCoordinator = effectCoordinator ??
                                     throw new ArgumentNullException(nameof(effectCoordinator));
            this.mapCoordinator = mapCoordinator ??
                                  throw new ArgumentNullException(nameof(mapCoordinator));
            this.hasPendingCharacterResolution = hasPendingCharacterResolution ??
                                                 throw new ArgumentNullException(
                                                     nameof(hasPendingCharacterResolution));
            this.hasFacilitySelection = hasFacilitySelection ??
                                        throw new ArgumentNullException(
                                            nameof(hasFacilitySelection));
            this.tryCancelFacilitySelection = tryCancelFacilitySelection ??
                                              throw new ArgumentNullException(
                                                  nameof(tryCancelFacilitySelection));
        }

        public override string Id => "pending.character-card";

        public override InteractionPriority Priority => InteractionPriority.PendingResolution;

        public override bool IsActive =>
            mapCoordinator.IsActive ||
            hasPendingCharacterResolution() ||
            hasFacilitySelection();

        public override InteractionResult OnLocationClicked(string locationId)
        {
            return mapCoordinator.TryHandleLocationClicked(locationId)
                ? InteractionResult.Consumed
                : InteractionResult.Passthrough;
        }

        public override InteractionResult OnInfluenceSlotClicked(string slotId)
        {
            return mapCoordinator.TryHandleInfluenceSlotClicked(slotId)
                ? InteractionResult.Consumed
                : InteractionResult.Passthrough;
        }

        public override InteractionResult OnMobileCityClicked()
        {
            return mapCoordinator.IsActive
                ? InteractionResult.Consumed
                : InteractionResult.Passthrough;
        }

        public override InteractionResult OnEscape()
        {
            return tryCancelFacilitySelection()
                ? InteractionResult.Consumed
                : InteractionResult.Passthrough;
        }

        public override InteractionPresentation BuildPresentation()
        {
            if (mapCoordinator.Synchronize())
            {
                return InteractionPresentation.Busy;
            }

            if (effectCoordinator.SynchronizePending())
            {
                return InteractionPresentation.Busy;
            }

            return InteractionPresentation.Empty;
        }

        public override void Cancel()
        {
            mapCoordinator.Cancel();
            effectCoordinator.HideDialog();
        }
    }
}
