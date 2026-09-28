using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public enum UiSelectionVisualState { Available, Selected, Unavailable, Pending }

    public sealed class UiSelectionState : MonoBehaviour
    {
        [SerializeField] private Image stateFrame;
        [SerializeField] private Image stateIcon;
        [SerializeField] private Sprite availableFrame;
        [SerializeField] private Sprite selectedFrame;
        [SerializeField] private Sprite unavailableFrame;
        [SerializeField] private Sprite selectedIcon;
        [SerializeField] private Sprite unavailableIcon;
        [SerializeField] private Color pendingTint = new Color(0.3f, 0.85f, 0.95f, 0.8f);
        [SerializeField] private UiSelectionVisualState state;

        public UiSelectionVisualState State => state;
        private void OnEnable() { Refresh(); }
        public void SetState(UiSelectionVisualState value) { state = value; Refresh(); }

        public void Refresh()
        {
            if (stateFrame == null || stateIcon == null) return;
            stateFrame.raycastTarget = false;
            stateIcon.raycastTarget = false;
            stateFrame.type = Image.Type.Sliced;
            stateFrame.sprite = state == UiSelectionVisualState.Selected ? selectedFrame :
                state == UiSelectionVisualState.Unavailable ? unavailableFrame : availableFrame;
            stateFrame.color = state == UiSelectionVisualState.Pending ? pendingTint : Color.white;
            stateIcon.sprite = state == UiSelectionVisualState.Selected ? selectedIcon :
                state == UiSelectionVisualState.Unavailable ? unavailableIcon : null;
            stateIcon.enabled = stateIcon.sprite != null;
        }
    }
}
