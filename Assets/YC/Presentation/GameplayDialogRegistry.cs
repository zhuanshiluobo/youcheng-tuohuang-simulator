using System;
using UnityEngine;

namespace YC.Presentation
{
    /// <summary>由编辑器序列化所有游戏流程对话框 Prefab，并提供显式实例化入口。</summary>
    public sealed class GameplayDialogRegistry : MonoBehaviour
    {
        [SerializeField] private EffectDialogShellView effectDialogShellPrefab;
        [SerializeField] private DispatchDecisionDialogView dispatchDecisionPrefab;
        [SerializeField] private EventChoiceDialogView eventChoiceDialogPrefab;
        [SerializeField] private CityStyleDeclarationPreviewView cityStyleDeclarationPreviewPrefab;
        [SerializeField] private CardVisualCatalog cardVisualCatalog;
        [SerializeField] private CardInteractionLayoutProfile cardInteractionLayoutProfile;

        public EffectDialogShellView EffectDialogShellPrefab => effectDialogShellPrefab;
        public DispatchDecisionDialogView DispatchDecisionPrefab => dispatchDecisionPrefab;
        public EventChoiceDialogView EventChoiceDialogPrefab => eventChoiceDialogPrefab;
        public CityStyleDeclarationPreviewView CityStyleDeclarationPreviewPrefab =>
            cityStyleDeclarationPreviewPrefab;
        public CardVisualCatalog CardVisualCatalog => cardVisualCatalog;
        public CardInteractionLayoutProfile CardInteractionLayoutProfile => cardInteractionLayoutProfile;
        internal EffectDialogLayoutProfile EffectDialogLayoutProfile =>
            effectDialogShellPrefab == null ? null : effectDialogShellPrefab.LayoutProfile;

        public bool TryValidateConfiguration(out string reason)
        {
            if (effectDialogShellPrefab == null || dispatchDecisionPrefab == null ||
                eventChoiceDialogPrefab == null || cityStyleDeclarationPreviewPrefab == null ||
                cardVisualCatalog == null || cardInteractionLayoutProfile == null)
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

            if (!effectDialogShellPrefab.TryValidateConfiguration(out reason) ||
                !dispatchDecisionPrefab.TryValidateConfiguration(out reason) ||
                !eventChoiceDialogPrefab.TryValidateConfiguration(out reason) ||
                !cityStyleDeclarationPreviewPrefab.TryValidateConfiguration(out reason))
            {
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public EffectDialogShellView InstantiateEffectDialogShell(RectTransform parent)
        {
            if (parent == null)
            {
                return null;
            }

            EnsureConfigured();
            var instance = UnityEngine.Object.Instantiate(effectDialogShellPrefab, parent, false);
            instance.transform.SetAsLastSibling();
            instance.gameObject.SetActive(true);
            return instance;
        }

        public DispatchDecisionDialogView InstantiateDispatchDecision(RectTransform parent)
        {
            if (parent == null)
            {
                return null;
            }

            EnsureConfigured();
            var instance = UnityEngine.Object.Instantiate(dispatchDecisionPrefab, parent, false);
            instance.transform.SetAsLastSibling();
            instance.gameObject.SetActive(true);
            return instance;
        }

        public EventChoiceDialogView InstantiateEventChoiceDialog(RectTransform parent)
        {
            if (parent == null)
            {
                return null;
            }

            EnsureConfigured();
            var instance = UnityEngine.Object.Instantiate(eventChoiceDialogPrefab, parent, false);
            NormalizeNestedOverlayRect(instance.OverlayRect);
            instance.transform.SetAsLastSibling();
            instance.gameObject.SetActive(true);
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
            var instance = UnityEngine.Object.Instantiate(cityStyleDeclarationPreviewPrefab, parent, false);
            instance.transform.SetAsLastSibling();
            instance.gameObject.SetActive(true);
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
