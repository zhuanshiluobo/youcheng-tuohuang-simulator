using System;
using UnityEngine;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>角色牌盖放拖拽的纯展示协调器；最终盖放仍由命令提交回调完成。</summary>
    internal sealed class CharacterCardCoverDragCoordinator
    {
        private readonly Func<CharacterCardPanelViewModel> buildView;
        private readonly Func<ActionPanelController> getActionPanel;
        private readonly Action<string> submitCover;
        private readonly Action<string> showPrompt;
        private string draggedCardId = string.Empty;
        private string draggedCardImagePath = string.Empty;
        private string pendingCardId = string.Empty;
        private string pendingCardImagePath = string.Empty;

        public CharacterCardCoverDragCoordinator(
            Func<CharacterCardPanelViewModel> buildView,
            Func<ActionPanelController> getActionPanel,
            Action<string> submitCover,
            Action<string> showPrompt)
        {
            this.buildView = buildView;
            this.getActionPanel = getActionPanel;
            this.submitCover = submitCover;
            this.showPrompt = showPrompt;
        }

        public void Begin(string cardId, Vector2 screenPosition)
        {
            draggedCardId = cardId ?? string.Empty;
            draggedCardImagePath = FindHandCardImagePath(draggedCardId);
            pendingCardId = string.Empty;
            pendingCardImagePath = string.Empty;
            Update(screenPosition);
        }

        public void Update(Vector2 screenPosition)
        {
            var actionPanel = getActionPanel == null ? null : getActionPanel();
            if (actionPanel == null || string.IsNullOrEmpty(draggedCardId))
            {
                return;
            }

            if (actionPanel.IsPointerNearPanel(screenPosition))
            {
                actionPanel.ShowCharacterCoverDropZone(draggedCardImagePath);
            }
            else if (actionPanel.CurrentFace == ActionPanelFace.CharacterCover &&
                     string.IsNullOrEmpty(pendingCardId))
            {
                actionPanel.ShowMainFace();
            }
        }

        public void End(string cardId, Vector2 screenPosition)
        {
            var actionPanel = getActionPanel == null ? null : getActionPanel();
            var matchesDrag = !string.IsNullOrEmpty(cardId) && cardId == draggedCardId;
            var droppedNearPanel =
                matchesDrag &&
                actionPanel != null &&
                actionPanel.IsPointerNearPanel(screenPosition);
            if (droppedNearPanel)
            {
                pendingCardId = draggedCardId;
                pendingCardImagePath = draggedCardImagePath;
                actionPanel.ShowCharacterCoverConfirmation(
                    pendingCardImagePath,
                    Confirm,
                    Cancel);
            }
            else
            {
                pendingCardId = string.Empty;
                pendingCardImagePath = string.Empty;
                actionPanel?.ShowMainFace();
            }

            draggedCardId = string.Empty;
            draggedCardImagePath = string.Empty;
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
            pendingCardImagePath = string.Empty;
            var actionPanel = getActionPanel == null ? null : getActionPanel();
            actionPanel?.ShowMainFace();
            submitCover?.Invoke(cardId);
        }

        private void Cancel()
        {
            pendingCardId = string.Empty;
            pendingCardImagePath = string.Empty;
            var actionPanel = getActionPanel == null ? null : getActionPanel();
            actionPanel?.ShowMainFace();
        }

        private string FindHandCardImagePath(string cardId)
        {
            var view = buildView == null ? null : buildView();
            if (view == null)
            {
                return string.Empty;
            }

            for (var i = 0; i < view.HandCards.Count; i++)
            {
                if (view.HandCards[i].CardId == cardId && view.HandCards[i].CanCover)
                {
                    return view.HandCards[i].FrontImageRelativePath;
                }
            }

            return string.Empty;
        }
    }
}
