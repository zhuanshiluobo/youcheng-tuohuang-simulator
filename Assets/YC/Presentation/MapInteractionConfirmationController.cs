using System;

namespace YC.Presentation
{
    public sealed class MapInteractionConfirmationController
    {
        private Action callback;

        public string ActionKey { get; private set; } = string.Empty;
        public string TargetId { get; private set; } = string.Empty;
        public string LocationId { get; private set; } = string.Empty;
        public string SlotId { get; private set; } = string.Empty;

        public bool HasPending
        {
            get { return !string.IsNullOrEmpty(ActionKey); }
        }

        public bool Matches(string actionKey, string targetId)
        {
            return ActionKey == (actionKey ?? string.Empty) &&
                   TargetId == (targetId ?? string.Empty);
        }

        public bool Request(
            string actionKey,
            string targetId,
            string locationId,
            string slotId,
            Action confirmedAction,
            out Action confirmedCallback)
        {
            confirmedCallback = null;
            if (Matches(actionKey, targetId) && HasPending)
            {
                confirmedCallback = callback;
                Clear();
                return true;
            }

            Clear();
            ActionKey = actionKey ?? string.Empty;
            TargetId = targetId ?? string.Empty;
            LocationId = locationId ?? string.Empty;
            SlotId = slotId ?? string.Empty;
            callback = confirmedAction;
            return false;
        }

        public void Clear()
        {
            ActionKey = string.Empty;
            TargetId = string.Empty;
            LocationId = string.Empty;
            SlotId = string.Empty;
            callback = null;
        }
    }
}
