using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Presentation;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class EffectDialogResourceRowView : MonoBehaviour
    {
        [SerializeField] private Text label;
        [SerializeField] private GameplayTextView labelDocument;
        [SerializeField] private Button decreaseButton;
        [SerializeField] private Text decreaseLabel;
        [SerializeField] private Text valueText;
        [SerializeField] private Button increaseButton;
        [SerializeField] private Text increaseLabel;
        [SerializeField] private InputField valueInput;
        [SerializeField] private Image resourceIcon;
        [SerializeField] private Text draftPreview;
        [SerializeField] private string[] resourceNames;
        [SerializeField] private Sprite[] resourceIcons;
        [SerializeField] private string inventoryPreviewFormat = "库存 {0} / 剩余 {1}";
        [SerializeField] private string salePreviewFormat = "库存 {0} → {1} / 本批 {2} 金券";
        private readonly List<GameplayTextHostStandard> standards = new List<GameplayTextHostStandard>();
        private GameplayTextDocument originalLabelDocument, renderedLabelDocument;
        private Action<Component, GameplayTextDocument, bool> renderLabel;
        private int resourceIndex = -1, renderedResourceIndex = -2;
        public InputField ValueInput => valueInput;

        private void Awake() => CaptureStandards();
        private void CaptureStandards()
        {
            if (standards.Count != 0) return;
            foreach (var text in new[] { label, valueText, decreaseLabel, increaseLabel, draftPreview })
                if (text != null)
                {
                    var standard = new GameplayTextHostStandard(text);
                    standards.Add(standard); standard.AttachDocument();
                }
        }
        private void RestoreStandards()
        { CaptureStandards(); foreach (var standard in standards) standard.Apply(true); }
        private void LateUpdate()
        {
            RestoreStandards();
            if (originalLabelDocument != null && labelDocument != null && !labelDocument.HasDocument)
            { originalLabelDocument = renderedLabelDocument = null; renderLabel = null; renderedResourceIndex = -2; }
        }
        private void OnDisable()
        {
            originalLabelDocument = renderedLabelDocument = null; renderLabel = null;
            renderedResourceIndex = -2; resourceIndex = -1;
            if (labelDocument != null) labelDocument.ClearBinding();
        }

        public void RenderDraft(string resource, int inventory, int amount, int? price, bool showInventoryPreview = true)
        {
            RestoreStandards();
            if (resourceIcon != null)
            {
                var index = resourceNames == null ? -1 : System.Array.IndexOf(resourceNames, resource);
                resourceIndex = index;
                resourceIcon.sprite = index < 0 || resourceIcons == null || index >= resourceIcons.Length ? null : resourceIcons[index];
                resourceIcon.gameObject.SetActive(resourceIcon.sprite != null);
            }
            RenderLabelDocument();
            if (draftPreview != null)
            {
                draftPreview.gameObject.SetActive(showInventoryPreview);
                if (showInventoryPreview) draftPreview.text = string.Format(price.HasValue ? salePreviewFormat : inventoryPreviewFormat,
                    inventory, Mathf.Max(0, inventory - amount), price.GetValueOrDefault() * amount);
            }
        }

        public Text Label => label;
        public bool TryRenderGameplayLabel(GameplayTextDocument document,
            Action<Component, GameplayTextDocument, bool> render, out string reason)
        {
            reason = labelDocument == null || labelDocument.GetComponent<Text>() != label
                ? "资源行GameplayTextView缺失或未引用原Text同对象。"
                : render == null ? "资源行正文渲染回调缺失。" : string.Empty;
            if (reason.Length > 0) return false;
            RestoreStandards();
            originalLabelDocument = document; renderedLabelDocument = null; renderLabel = render;
            if (document == null) { labelDocument.ClearBinding(); return true; }
            RenderLabelDocument();
            return true;
        }
        private void RenderLabelDocument()
        {
            if (originalLabelDocument == null || renderLabel == null) return;
            int index = resourceIcon != null && resourceIcon.sprite != null ? resourceIndex : -1;
            if (renderedLabelDocument != null && renderedResourceIndex == index) return;
            renderedResourceIndex = index;
            renderedLabelDocument = index >= 0 && resourceNames != null && index < resourceNames.Length
                ? ResourceNameWithoutDuplicateIcon(originalLabelDocument, resourceNames[index]) : originalLabelDocument;
            renderLabel(labelDocument, renderedLabelDocument, false);
        }

        /// <summary>只合并正式文档中的单纯资源名称标签；有数值/条件/其他业务文案时保留原文档。</summary>
        internal static GameplayTextDocument ResourceNameWithoutDuplicateIcon(GameplayTextDocument document, string resource)
        {
            if (document == null || string.IsNullOrEmpty(resource)) return document;
            var icons = document.Runs.OfType<IconRun>().ToArray();
            if (icons.Length != 1 || icons[0].NumberValue.HasValue ||
                !string.Equals(icons[0].AltText, resource, StringComparison.Ordinal) ||
                document.Runs.Any(run => !(run is TextRun) && !(run is IconRun))) return document;
            var literal = string.Concat(document.Runs.OfType<TextRun>().Select(run => run.Text)).Trim();
            if (literal.Length != 0 && !string.Equals(literal, resource, StringComparison.Ordinal)) return document;
            var runs = new List<GameplayTextRun>();
            foreach (var run in document.Runs)
            {
                if (!ReferenceEquals(run, icons[0])) runs.Add(run);
                else if (literal.Length == 0) runs.Add(new TextRun(icons[0].AltText, icons[0].Style));
            }
            string suffix = "|quantity-resource-name:" + resource.Length + ":" + resource;
            return new GameplayTextDocument(runs, string.IsNullOrEmpty(document.Identity) ? string.Empty : document.Identity + suffix,
                string.IsNullOrEmpty(document.MeasurementIdentity) ? string.Empty : document.MeasurementIdentity + suffix);
        }
        public Button DecreaseButton => decreaseButton;
        public Text DecreaseLabel => decreaseLabel;
        public Text ValueText => valueText;
        public Button IncreaseButton => increaseButton;
        public Text IncreaseLabel => increaseLabel;

        public bool TryValidateConfiguration(out string reason)
        {
            if (label == null || decreaseButton == null || decreaseLabel == null ||
                valueText == null || increaseButton == null || increaseLabel == null)
            {
                reason = "资源分配行模板引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
