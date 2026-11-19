using UnityEngine;
using UnityEngine.UI;
using NoMadCity.SelectionFX;

namespace YC.Presentation
{
    public sealed class CityStyleCardStateView : MonoBehaviour
    {
        [SerializeField] private Image stateFrame;
        [SerializeField] private Sprite availableFrame, unavailableFrame;
        [SerializeField] private GameObject checkIcon;
        [SerializeField] private SelectionEffectGraphic selectionEffect;
        private bool available;
        public void SetSelectionEffectEnabled(bool enabled)
        {
            if (selectionEffect != null) selectionEffect.gameObject.SetActive(enabled);
        }
        public void Bind(bool canDeclare)
        {
            available = canDeclare;
            if (selectionEffect != null) selectionEffect.SetState(available
                ? SelectionEffectGraphic.SelectionState.Available : SelectionEffectGraphic.SelectionState.Unavailable);
        }
        public void SetSelected(bool selected)
        {
            stateFrame.sprite = available ? availableFrame : unavailableFrame;
            checkIcon.SetActive(selected);
            if (selectionEffect != null) selectionEffect.SetState(selected ? SelectionEffectGraphic.SelectionState.Selected : available
                ? SelectionEffectGraphic.SelectionState.Available : SelectionEffectGraphic.SelectionState.Unavailable);
        }
    }
}
