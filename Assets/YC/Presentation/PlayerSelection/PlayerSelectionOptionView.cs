using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Domain.Rules;

namespace YC.Presentation
{
    public sealed class PlayerSelectionOptionView : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler,
        ISelectHandler, IDeselectHandler
    {
        [SerializeField] private Button button;
        [SerializeField] private Image fill, frame, marker, checkbox, check;
        [SerializeField] private RawImage avatar;
        [SerializeField] private GameObject emptyAvatar, selfTag, disabledState;
        [SerializeField] private Text playerName, colorName, disabledReason;
        [SerializeField] private Sprite[] fills, frames, markers, checkboxes;
        [SerializeField] private string[] colorLabels;
        [SerializeField] private string unnamedPlayerFormat = "玩家 {0}", unavailableText = "本次不可选择";
        [SerializeField] private string nameEllipsis = "…";
        [SerializeField] private Color ink = new Color32(48,47,42,255), mutedInk = new Color32(130,121,99,255);
        private PlayerSelectionOption option;
        private Action<int> choose;
        private bool chosen, hovered, focused;
        private string fullName;
        private float lastNameWidth = -1;
        public Button Button => button;
        public PlayerSelectionOption Option => option;
        public bool IsConfigured => button != null && fill != null && frame != null && marker != null && checkbox != null &&
            check != null && avatar != null && emptyAvatar != null && selfTag != null && disabledState != null &&
            playerName != null && colorName != null && disabledReason != null && fills?.Length == 4 && frames?.Length == 4 &&
            markers?.Length == 4 && checkboxes?.Length == 3 && colorLabels?.Length == 4;

        private void Awake() => button.onClick.AddListener(() => { if (option != null && option.Eligible) choose?.Invoke(option.Id); });
        public void Bind(PlayerSelectionOption data, bool selected, Action<int> onChoose)
        {
            option = data; chosen = selected; choose = onChoose;
            button.interactable = data.Eligible;
            var nextName = string.IsNullOrWhiteSpace(data.Name) ? string.Format(unnamedPlayerFormat, data.Id) : data.Name;
            if (fullName != nextName) { fullName = nextName; lastNameWidth = -1; }
            playerName.color = data.Eligible ? ink : mutedInk;
            var index = data.Color == PlayerColor.Green ? 0 : data.Color == PlayerColor.Yellow ? 1 : data.Color == PlayerColor.Blue ? 2 : 3;
            marker.sprite = markers[index]; marker.color = Color.white;
            colorName.text = colorLabels[index];
            selfTag.SetActive(data.IsSelf); disabledState.SetActive(!data.Eligible);
            disabledReason.text = string.IsNullOrEmpty(data.DisabledReason) ? unavailableText : data.DisabledReason;
            avatar.texture = data.Avatar;
            avatar.gameObject.SetActive(data.Avatar != null); emptyAvatar.SetActive(data.Avatar == null);
            avatar.color = new Color(1,1,1,data.Eligible ? 1 : .45f);
            if (data.Avatar != null)
            {
                var aspect = (float)data.Avatar.width / Mathf.Max(1,data.Avatar.height);
                avatar.uvRect = aspect > 1 ? new Rect((1-1/aspect)/2,0,1/aspect,1) : new Rect(0,(1-aspect)/2,1,aspect);
            }
            RefreshState();
        }
        private void LateUpdate()
        {
            var width = playerName.rectTransform.rect.width;
            if (width <= 0 || width == lastNameWidth || fullName == null) return;
            lastNameWidth = width;
            var settings = playerName.GetGenerationSettings(Vector2.zero);
            settings.fontSize = playerName.resizeTextForBestFit ? playerName.resizeTextMinSize : playerName.fontSize;
            settings.resizeTextForBestFit = false;
            settings.horizontalOverflow = HorizontalWrapMode.Overflow; settings.verticalOverflow = VerticalWrapMode.Overflow;
            float Measure(string value) => playerName.cachedTextGeneratorForLayout.GetPreferredWidth(value,settings)/playerName.pixelsPerUnit;
            if (Measure(fullName) <= width) { playerName.text = fullName; return; }
            var length = fullName.Length;
            while (length > 0 && Measure(fullName.Substring(0,length)+nameEllipsis) > width) length--;
            if (length > 0 && char.IsHighSurrogate(fullName[length-1])) length--;
            playerName.text = fullName.Substring(0,length)+nameEllipsis;
        }
        private void RefreshState()
        {
            var state = option == null || !option.Eligible ? 3 : chosen ? 2 : hovered || focused ? 1 : 0;
            fill.sprite = fills[state]; frame.sprite = frames[state];
            checkbox.sprite = checkboxes[state == 3 ? 2 : chosen ? 1 : 0]; check.gameObject.SetActive(chosen);
        }
        public void OnPointerEnter(PointerEventData eventData) { hovered = true; RefreshState(); }
        public void OnPointerExit(PointerEventData eventData) { hovered = false; RefreshState(); }
        public void OnSelect(BaseEventData eventData) { focused = true; RefreshState(); }
        public void OnDeselect(BaseEventData eventData) { focused = false; RefreshState(); }
        private void OnDisable() { hovered = false; focused = false; }
    }
}
