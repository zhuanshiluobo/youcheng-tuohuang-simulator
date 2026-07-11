using System.Collections.Generic;
using YC.Domain.Cards;
using YC.Domain.Commands;

namespace YC.Presentation
{
    public sealed class EventInfluenceTargetSelectionController
    {
        private readonly List<string> selectedSlotIds = new List<string>();

        public int ChoiceIndex { get; private set; } = -1;

        public IReadOnlyList<string> SelectedSlotIds
        {
            get { return selectedSlotIds; }
        }

        public bool IsSelecting
        {
            get { return ChoiceIndex >= 0; }
        }

        public void Begin(int choiceIndex)
        {
            ChoiceIndex = choiceIndex;
            selectedSlotIds.Clear();
        }

        public void Clear()
        {
            ChoiceIndex = -1;
            selectedSlotIds.Clear();
        }

        public bool ContainsSlot(string slotId)
        {
            return selectedSlotIds.Contains(slotId);
        }

        public bool TrySelectSlot(
            EventCardDefinition card,
            string slotId,
            out bool completed)
        {
            completed = false;
            if (card == null || ChoiceIndex < 0 || string.IsNullOrEmpty(slotId))
            {
                return false;
            }

            if (!selectedSlotIds.Contains(slotId))
            {
                selectedSlotIds.Add(slotId);
            }

            completed = selectedSlotIds.Count >= GetRequiredSlotCount(card, ChoiceIndex);
            return true;
        }

        public void AddCommandParameter(GameCommand command, string parameterName)
        {
            if (command == null ||
                string.IsNullOrEmpty(parameterName) ||
                selectedSlotIds.Count <= 0)
            {
                return;
            }

            command.Parameters[parameterName] = EncodeIds(selectedSlotIds);
        }

        public static int GetRequiredSlotCount(EventCardDefinition card, int choiceIndex)
        {
            if (card == null ||
                choiceIndex < 0 ||
                choiceIndex >= card.ChoicePendingEffects.Count ||
                card.ChoicePendingEffects[choiceIndex] == null)
            {
                return 0;
            }

            var count = 0;
            var effects = card.ChoicePendingEffects[choiceIndex];
            for (var i = 0; i < effects.Count; i++)
            {
                var effect = effects[i];
                if (effect != null &&
                    effect.Kind == EventEffectKind.PlaceInfluence &&
                    effect.Amount > 0)
                {
                    count += effect.Amount;
                }
            }

            return count;
        }

        private static string EncodeIds(IReadOnlyList<string> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                return string.Empty;
            }

            var value = ids[0] ?? string.Empty;
            for (var i = 1; i < ids.Count; i++)
            {
                value += "," + (ids[i] ?? string.Empty);
            }

            return value;
        }
    }
}
