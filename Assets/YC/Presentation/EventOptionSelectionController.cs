using YC.Domain.Cards;

namespace YC.Presentation
{
    public sealed class EventOptionSelectionController
    {
        public EventCardDefinition PendingEventCard { get; private set; }

        public bool HasPendingCard
        {
            get { return PendingEventCard != null; }
        }

        public void Begin(EventCardDefinition card)
        {
            PendingEventCard = card;
        }

        public void Clear()
        {
            PendingEventCard = null;
        }

        public bool TrySelectChoice(int choiceIndex, out EventOptionSelection selection)
        {
            selection = EventOptionSelection.Invalid;
            if (PendingEventCard == null ||
                choiceIndex < 0 ||
                choiceIndex >= PendingEventCard.ChoiceRewards.Count)
            {
                return false;
            }

            selection = new EventOptionSelection(
                choiceIndex,
                EventInfluenceTargetSelectionController.GetRequiredSlotCount(PendingEventCard, choiceIndex));
            return true;
        }
    }

    public struct EventOptionSelection
    {
        public static readonly EventOptionSelection Invalid = new EventOptionSelection(-1, 0);

        public EventOptionSelection(int choiceIndex, int requiredInfluenceSlotCount)
        {
            ChoiceIndex = choiceIndex;
            RequiredInfluenceSlotCount = requiredInfluenceSlotCount;
        }

        public int ChoiceIndex { get; private set; }
        public int RequiredInfluenceSlotCount { get; private set; }

        public bool RequiresInfluenceTargets
        {
            get { return RequiredInfluenceSlotCount > 0; }
        }
    }
}
