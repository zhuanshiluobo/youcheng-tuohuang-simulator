using System;
using UnityEngine;

namespace YC.Presentation
{
    /// <summary>由编辑器序列化所有游戏流程对话框 Prefab，并提供显式实例化入口。</summary>
    public sealed class GameplayDialogRegistry : MonoBehaviour
    {
        [SerializeField] private EffectDialogShellView effectDialogShellPrefab;
        [SerializeField] private EffectDialogShellView cardPickerPrefab;
        [SerializeField] private FacilityBuildDialogView facilityBuildPrefab;
        [SerializeField] private CardViewer cardViewerPrefab;
        [SerializeField] private DispatchDecisionDialogView dispatchDecisionPrefab;
        [SerializeField] private EventChoiceDialogView eventChoiceDialogPrefab;
        [SerializeField] private CityStyleDeclarationPreviewView cityStyleDeclarationPreviewPrefab;
        [SerializeField] private CardVisualCatalog cardVisualCatalog;
        [SerializeField] private CardInteractionLayoutProfile cardInteractionLayoutProfile;
        [SerializeField] private UiEffectRowView effectRowPrefab;
        [SerializeField] private EnterpriseSelectionDialogView enterpriseSelectionPrefab;
        [SerializeField] private EnterpriseSelectionDialogView departmentSelectionPrefab;

        public EffectDialogShellView EffectDialogShellPrefab => effectDialogShellPrefab;
        public EffectDialogShellView CardPickerPrefab => cardPickerPrefab;
        public CardViewer CardViewerPrefab => cardViewerPrefab;

        public FacilityBuildDialogView InstantiateFacilityBuild(RectTransform parent)
        {
            if (facilityBuildPrefab == null) throw new InvalidOperationException("缺少设施建设页面预制体。");
            var frame = GameplayHudFrame.Active;
            var instance = Instantiate(facilityBuildPrefab, frame == null ? parent : frame.ContentRect, false);
            NormalizeNestedOverlayRect(instance.transform as RectTransform);
            return instance;
        }

        public CardViewer InstantiateCardViewer(RectTransform parent = null)
        {
            if (cardViewerPrefab == null || !cardViewerPrefab.TryValidateConfiguration(out _))
                throw new InvalidOperationException("未配置完整的卡牌查看器。");
            var host = GameplayHudFrame.Active == null ? parent : GameplayHudFrame.Active.ContentRect;
            if (host == null) host = transform as RectTransform;
            var instance = UnityEngine.Object.Instantiate(cardViewerPrefab, host, false);
            instance.gameObject.SetActive(false);
            instance.transform.SetAsLastSibling();
            return instance;
        }
        public DispatchDecisionDialogView DispatchDecisionPrefab => dispatchDecisionPrefab;
        public EventChoiceDialogView EventChoiceDialogPrefab => eventChoiceDialogPrefab;
        public CityStyleDeclarationPreviewView CityStyleDeclarationPreviewPrefab =>
            cityStyleDeclarationPreviewPrefab;
        public CardVisualCatalog CardVisualCatalog => cardVisualCatalog;
        public CardInteractionLayoutProfile CardInteractionLayoutProfile => cardInteractionLayoutProfile;
        public UiEffectRowView EffectRowPrefab => effectRowPrefab;
        public EnterpriseSelectionDialogView EnterpriseSelectionPrefab => enterpriseSelectionPrefab;
        public EnterpriseSelectionDialogView DepartmentSelectionPrefab => departmentSelectionPrefab;

        public EnterpriseSelectionDialogView InstantiateEnterpriseSelection(RectTransform parent, bool department)
        {
            if (parent == null) return null;
            var prefab = department ? departmentSelectionPrefab : enterpriseSelectionPrefab;
            if (prefab == null) throw new InvalidOperationException("未配置企业／科室选择窗口。");
            if (!prefab.TryValidateConfiguration(out var reason)) throw new InvalidOperationException(reason);
            var frame = GameplayHudFrame.Active;
            var instance = UnityEngine.Object.Instantiate(prefab,
                frame == null || frame.ContentRect == null ? parent : frame.ContentRect, false);
            instance.gameObject.SetActive(false);
            // 只有收到真实请求的调用方才能以 effect 身份 Present；普通组件查看不登记恢复目标。
            return instance;
        }
        internal EffectDialogLayoutProfile EffectDialogLayoutProfile =>
            effectDialogShellPrefab == null ? null : effectDialogShellPrefab.LayoutProfile;

        public bool TryValidateConfiguration(out string reason)
        {
            if (effectDialogShellPrefab == null || cardPickerPrefab == null || cardViewerPrefab == null || dispatchDecisionPrefab == null ||
                eventChoiceDialogPrefab == null || cityStyleDeclarationPreviewPrefab == null ||
                cardVisualCatalog == null || cardInteractionLayoutProfile == null ||
                effectRowPrefab == null || enterpriseSelectionPrefab == null || departmentSelectionPrefab == null)
            {
                reason = "游戏流程对话框 Registry 的 Prefab 引用不完整。";
                return false;
            }

            if (!cardVisualCatalog.TryValidateConfiguration(out reason))
            {
                return false;
            }

            if (!cardInteractionLayoutProfile.TryValidateConfiguration(out reason))
            {
                reason = "游戏流程对话框 Registry 的卡牌交互布局 Profile 无效：" + reason;
                return false;
            }

            if (!cardViewerPrefab.TryValidateConfiguration(out reason) ||
                !enterpriseSelectionPrefab.TryValidateConfiguration(out reason) ||
                !departmentSelectionPrefab.TryValidateConfiguration(out reason) ||
                !effectRowPrefab.TryValidateConfiguration(out reason) ||
                !effectDialogShellPrefab.TryValidateConfiguration(out reason) ||
                !cardPickerPrefab.TryValidateConfiguration(out reason) ||
                !dispatchDecisionPrefab.TryValidateConfiguration(out reason) ||
                !eventChoiceDialogPrefab.TryValidateConfiguration(out reason) ||
                !cityStyleDeclarationPreviewPrefab.TryValidateConfiguration(out reason))
            {
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public EffectDialogShellView InstantiateEffectDialogShell(RectTransform parent, bool effectPage = true,
            bool stagePage = true, bool cardPicker = false)
        {
            if (parent == null)
            {
                return null;
            }

            EnsureConfigured();
            var frame = GameplayHudFrame.Active;
            var instance = UnityEngine.Object.Instantiate(cardPicker ? cardPickerPrefab : effectDialogShellPrefab,
                frame == null || frame.ContentRect == null ? parent : frame.ContentRect, false);
            instance.transform.SetAsLastSibling();
            if (frame != null)
            {
                if (effectPage) frame.ShowPage(instance.gameObject, true);
                else if (stagePage) frame.ShowStagePage(instance.gameObject);
                else frame.ShowPage(instance.gameObject, false);
            }
            else instance.gameObject.SetActive(true);
            return instance;
        }

        public UiEffectRowView InstantiateEffectRow(RectTransform parent)
        {
            if (parent == null) return null;
            EnsureConfigured();
            return UnityEngine.Object.Instantiate(effectRowPrefab, parent, false);
        }

        public DispatchDecisionDialogView InstantiateDispatchDecision(RectTransform parent)
        {
            if (parent == null)
            {
                return null;
            }

            EnsureConfigured();
            var frame = GameplayHudFrame.Active;
            var instance = UnityEngine.Object.Instantiate(dispatchDecisionPrefab,
                frame == null || frame.ContentRect == null ? parent : frame.ContentRect, false);
            instance.transform.SetAsLastSibling();
            if (frame != null) frame.ShowPage(instance.gameObject, false);
            else instance.gameObject.SetActive(true);
            return instance;
        }

        public EventChoiceDialogView InstantiateEventChoiceDialog(RectTransform parent)
        {
            if (parent == null)
            {
                return null;
            }

            EnsureConfigured();
            var frame = GameplayHudFrame.Active;
            var instance = UnityEngine.Object.Instantiate(eventChoiceDialogPrefab,
                frame == null || frame.ContentRect == null ? parent : frame.ContentRect, false);
            NormalizeNestedOverlayRect(instance.OverlayRect);
            instance.transform.SetAsLastSibling();
            if (frame != null) frame.ShowPage(instance.gameObject, true);
            else instance.gameObject.SetActive(true);
            return instance;
        }

        private static void NormalizeNestedOverlayRect(RectTransform rect)
        {
            if (rect == null)
            {
                return;
            }

            // ScreenSpaceOverlay Canvas 作为 Prefab 根节点时会被 Unity 保存为零缩放、零尺寸。
            // 它实例化到现有 HUD Canvas 后已经是嵌套 Canvas，必须显式恢复为全屏可见布局。
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition3D = Vector3.zero;
            rect.sizeDelta = Vector2.zero;
        }

        public CityStyleDeclarationPreviewView InstantiateCityStyleDeclarationPreview(RectTransform parent)
        {
            if (parent == null)
            {
                return null;
            }

            EnsureConfigured();
            var frame = GameplayHudFrame.Active;
            var instance = UnityEngine.Object.Instantiate(cityStyleDeclarationPreviewPrefab,
                frame == null || frame.ContentRect == null ? parent : frame.ContentRect, false);
            instance.transform.SetAsLastSibling();
            if (frame != null) frame.ShowPage(instance.gameObject, false);
            else instance.gameObject.SetActive(true);
            return instance;
        }

        private void EnsureConfigured()
        {
            if (!TryValidateConfiguration(out var reason))
            {
                throw new InvalidOperationException(reason);
            }
        }
    }
}
