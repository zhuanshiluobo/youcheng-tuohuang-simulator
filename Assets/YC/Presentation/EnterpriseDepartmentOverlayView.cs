using System;
using UnityEngine;
using UnityEngine.UI;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>已激活科室的原图叠层；相对企业板的几何只来自共用预制体。</summary>
    public sealed class EnterpriseDepartmentOverlayView : MonoBehaviour
    {
        [SerializeField] private RawImage artwork;
        [SerializeField] private CardPickerCardButton previewButton;
        public bool IsConfigured => artwork != null && previewButton != null;

        public void Bind(EnterpriseDepartmentProjection department, EnterpriseBoardCatalog catalog,
            Action inspect = null, Action preview = null, Func<bool> isCurrent = null)
        {
            previewButton.onClick.RemoveAllListeners();
            previewButton.ConfigurePreview(preview, isCurrent);
            var image = department == null ? null : catalog.Find(department.VisualKey);
            artwork.texture = image?.texture;
            artwork.uvRect = image?.uv ?? new Rect(0, 0, 1, 1);
            artwork.enabled = image?.texture != null;
            bool interactive = department != null && (inspect != null || preview != null);
            artwork.raycastTarget = interactive;
            previewButton.interactable = interactive;
            previewButton.enabled = interactive;
            if (inspect != null)
                previewButton.onClick.AddListener(() => { if (isCurrent == null || isCurrent()) inspect(); });
            gameObject.SetActive(department != null);
        }
    }
}
