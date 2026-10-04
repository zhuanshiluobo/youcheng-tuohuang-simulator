using System;
using UnityEngine;
using UnityEngine.UI;
namespace YC.Presentation
{
    [RequireComponent(typeof(Button))]
    public sealed class CollectionItemView : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private Text nameLabel;
        [SerializeField] private Text[] numberLabels = new Text[0];
        [SerializeField] private Image icon;
        [SerializeField] private GameObject selectedMarker;
        private Action<CollectionItemDefinition> onSelected;
        public CollectionItemDefinition Item { get; private set; }
        public void Bind(CollectionItemDefinition item, Action<CollectionItemDefinition> select)
        {
            Item = item;
            onSelected = select;
            if (nameLabel != null) nameLabel.text = item.DisplayName;
            foreach (var label in numberLabels) if (label != null) label.text = item.DisplayNumber;
            if (icon != null) { icon.sprite = item.Icon; icon.enabled = item.Icon != null; }
            if (button == null) button = GetComponent<Button>();
            button.onClick.RemoveListener(Select);
            button.onClick.AddListener(Select);
            SetSelected(false);
        }
        public void SetSelected(bool selected)
        {
            if (selectedMarker != null) selectedMarker.SetActive(selected);
        }
        private void Select() => onSelected?.Invoke(Item);
        private void OnDestroy()
        {
            if (button != null) button.onClick.RemoveListener(Select);
            onSelected = null;
        }
    }
}

