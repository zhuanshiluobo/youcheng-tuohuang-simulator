using System;
using UnityEngine;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>角色牌盖放拖拽的纯展示协调器；最终盖放仍由命令提交回调完成。</summary>
    internal sealed class CharacterCardCoverDragCoordinator
    {
        private readonly Func<ActionPanelController> getActionPanel;
        private readonly Action<string> submitCover;
        private readonly Action<string> showPrompt;
        private readonly Action<bool> resolvePendingCover;
        private string draggedCardId = string.Empty;
        private string pendingCardId = string.Empty;

        public CharacterCardCoverDragCoordinator(
            Func<ActionPanelController> getActionPanel,
            Action<string> submitCover,
            Action<string> showPrompt,
            Action<bool> resolvePendingCover)
        {
            this.getActionPanel = getActionPanel;
            this.submitCover = submitCover;
            this.showPrompt = showPrompt;
            this.resolvePendingCover = resolvePendingCover;
        }

        public void Begin(string cardId, Vector2 screenPosition)
        {
            if (!string.IsNullOrEmpty(pendingCardId))
            {
                pendingCardId = string.Empty;
                resolvePendingCover?.Invoke(false);
                var actionPanel = getActionPanel == null ? null : getActionPanel();
                actionPanel?.ClearCharacterCoverPreview();
            }

            draggedCardId = cardId ?? string.Empty;
            Update(screenPosition);
        }

        public void Update(Vector2 screenPosition)
        {
            var actionPanel = getActionPanel == null ? null : getActionPanel();
            if (actionPanel == null || string.IsNullOrEmpty(draggedCardId))
            {
                return;
            }

            if (actionPanel.IsPointerOverCharacterCoverRegion(screenPosition))
            {
                actionPanel.ShowCharacterCoverDropZone(draggedCardId);
            }
            else if (string.IsNullOrEmpty(pendingCardId))
            {
                actionPanel.ClearCharacterCoverPreview();
            }
        }

        public RectTransform End(string cardId, Vector2 screenPosition)
        {
            var actionPanel = getActionPanel == null ? null : getActionPanel();
            var matchesDrag = !string.IsNullOrEmpty(cardId) && cardId == draggedCardId;
            var droppedNearPanel =
                matchesDrag &&
                actionPanel != null &&
                actionPanel.IsPointerOverCharacterCoverRegion(screenPosition);
            if (droppedNearPanel)
            {
                pendingCardId = draggedCardId;
                actionPanel.ShowCharacterCoverConfirmation(
                    pendingCardId,
                    Confirm,
                    Cancel);
                draggedCardId = string.Empty;
                return actionPanel.CharacterCoverDropTarget;
            }

            pendingCardId = string.Empty;
            actionPanel?.ClearCharacterCoverPreview();
            draggedCardId = string.Empty;
            return null;
        }

        private void Confirm()
        {
            if (string.IsNullOrEmpty(pendingCardId))
            {
                showPrompt?.Invoke("当前没有等待确认的角色牌。");
                return;
            }

            var cardId = pendingCardId;
            pendingCardId = string.Empty;
            resolvePendingCover?.Invoke(true);
            var actionPanel = getActionPanel == null ? null : getActionPanel();
            actionPanel?.ClearCharacterCoverPreview();
            submitCover?.Invoke(cardId);
        }

        public void Cancel()
        {
            pendingCardId = string.Empty;
            draggedCardId = string.Empty;
            resolvePendingCover?.Invoke(false);
            getActionPanel?.Invoke()?.ClearCharacterCoverPreview();
        }

        public void Synchronize(CharacterCardPanelViewModel model)
        {
            if (string.IsNullOrEmpty(pendingCardId)) return;
            if (model != null && model.CanCover)
                foreach (var card in model.HandCards)
                    if (card.CardId == pendingCardId) return;
            Cancel();
        }
    }
}
