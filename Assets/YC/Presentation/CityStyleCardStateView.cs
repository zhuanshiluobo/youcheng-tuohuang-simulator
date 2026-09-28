using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class CityStyleCardStateView : MonoBehaviour
    {
        [SerializeField] private Image stateFrame;
        [SerializeField] private Sprite availableFrame, selectedFrame, unavailableFrame;
        [SerializeField] private GameObject checkIcon;
        private bool available;
        public void Bind(bool canDeclare)
        {
            available = canDeclare;
        }
        public void SetSelected(bool selected)
        {
            stateFrame.sprite = selected ? selectedFrame : available ? availableFrame : unavailableFrame;
            checkIcon.SetActive(selected);
        }
    }
}
