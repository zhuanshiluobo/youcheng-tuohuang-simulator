using System;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Presentation;

namespace YC.Presentation
{
    public sealed class EffectDialogOptionRowView : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private Image background;
        [SerializeField] private Text label;
        [SerializeField] private Text sourceLabel;
        [SerializeField] private GameplayTextView labelDocument;
        [SerializeField] private GameplayTextView sourceDocument;
        [SerializeField] private LayoutElement layoutElement;
        [SerializeField] private Image selectedIndicator;
        [SerializeField] private GameObject legacyActionIndicator;
        private GameplayTextHostStandard labelStandard, sourceStandard;

        private void Awake() => CaptureStandards();
        private void CaptureStandards()
        {
            if (label != null && labelStandard == null) { labelStandard = new GameplayTextHostStandard(label); labelStandard.AttachDocument(); }
            if (sourceLabel != null && sourceStandard == null) { sourceStandard = new GameplayTextHostStandard(sourceLabel); sourceStandard.AttachDocument(); }
        }
        private void RestoreStandards()
        { CaptureStandards(); labelStandard?.Apply(true); sourceStandard?.Apply(true); }
        private void LateUpdate() => RestoreStandards();

        public Button Button => button;
        public Image Background => background;
        public Text Label => label;
        public LayoutElement LayoutElement => layoutElement;
        public void SetSelected(bool selected)
        {
            if (legacyActionIndicator != null) legacyActionIndicator.SetActive(false);
            if (selectedIndicator != null) selectedIndicator.enabled = selected;
        }
        public void ResetSelection()
        {
            SetSelected(false); RestoreStandards();
            if (sourceDocument != null) sourceDocument.ClearBinding();
            if (labelDocument != null) labelDocument.ClearBinding();
        }
        public void SetSourceAndDescription(string source, string description)
        {
            RestoreStandards();
            if (sourceDocument != null) sourceDocument.ClearBinding();
            if (labelDocument != null) labelDocument.ClearBinding();
            if (sourceLabel != null) sourceLabel.text = source ?? string.Empty;
            label.text = description ?? string.Empty;
        }

        public bool TryRenderGameplayText(GameplayTextDocument description, GameplayTextDocument source,
            Action<Component, GameplayTextDocument, bool> render, out string reason)
        {
            reason = render == null ? "选项渲染回调缺失。" :
                description != null && (labelDocument == null || labelDocument.GetComponent<Text>() != label) ? "选项正文GameplayTextView缺失或未引用原Text同对象。" :
                source != null && (sourceDocument == null || sourceLabel == null || sourceDocument.GetComponent<Text>() != sourceLabel) ? "选项来源GameplayTextView缺失或未引用既有来源Text同对象。" : string.Empty;
            if (reason.Length > 0) return false;
            RestoreStandards();
            if (description != null) render(labelDocument, description, false);
            else if (labelDocument != null) labelDocument.ClearBinding();
            if (source != null) render(sourceDocument, source, false);
            else if (sourceDocument != null) sourceDocument.ClearBinding();
            return true;
        }

        public bool TryValidateConfiguration(out string reason)
        {
            if (button == null || background == null || label == null || layoutElement == null)
            {
                reason = "效果选项模板引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
