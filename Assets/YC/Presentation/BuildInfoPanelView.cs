using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class BuildInfoPanelView : MonoBehaviour
    {
        public void SetExternalFacilitySupplyVisible(bool visible)
        {
            if (externalFacilityArea != null) externalFacilityArea.gameObject.SetActive(visible);
        }

        public void SetLegacyVisible(bool visible)
        {
            if (root == null) return;
            var group = root.GetComponent<CanvasGroup>();
            if (group == null) return;
            group.alpha = visible ? 1f : 0f;
            group.interactable = visible;
            group.blocksRaycasts = visible;
        }

        [SerializeField] private CardInteractionLayoutProfile cardInteractionLayoutProfile;
        [SerializeField] private BuildInfoPanel controller;
        [SerializeField] private RectTransform root;
        [SerializeField] private RectTransform panelTransform;
        [SerializeField] private RectTransform contentArea;
        [SerializeField] private RectTransform contentRoot;
        [SerializeField] private RectTransform externalFacilityArea;
        [SerializeField] private RectTransform externalCityStyleArea;
        [SerializeField] private Text buildAvailabilityText;
        [SerializeField] private RectTransform cityBoardRoot;
        [SerializeField] private RawImage cityBoardImage;
        [SerializeField] private BuildInfoSlotView[] externalFacilitySlots;
        [SerializeField] private BuildInfoSlotView[] cityBoardSlots;
        [SerializeField] private BuildInfoItemView cardContentTemplate;
        [SerializeField] private BuildInfoItemView influenceMarkerTemplate;
        [SerializeField] private BuildInfoItemView dragGhostTemplate;
        [SerializeField] private BuildInfoItemView pendingBuildGhostTemplate;

        [SerializeField] private CityStyleStatusRowView[] cityStyleRowTemplates;
        public CityStyleStatusRowView GetCityStyleRowTemplate(string layout) =>
            System.Array.Find(cityStyleRowTemplates, row => row != null && row.TrackLayout == layout);

        public BuildInfoPanel Controller => controller;
        public CardInteractionLayoutProfile CardInteractionLayoutProfile => cardInteractionLayoutProfile;
        public RectTransform Root => root;
        public RectTransform PanelTransform => panelTransform;
        public RectTransform ContentArea => contentArea;
        public RectTransform ContentRoot => contentRoot;
        public RectTransform ExternalFacilityArea => externalFacilityArea;
        public RectTransform ExternalCityStyleArea => externalCityStyleArea;
        public Text BuildAvailabilityText => buildAvailabilityText;
        public RectTransform CityBoardRoot => cityBoardRoot;
        public RawImage CityBoardImage => cityBoardImage;
        public BuildInfoSlotView[] ExternalFacilitySlots => externalFacilitySlots;
        public BuildInfoSlotView[] CityBoardSlots => cityBoardSlots;
        public BuildInfoItemView CardContentTemplate => cardContentTemplate;
        public BuildInfoItemView InfluenceMarkerTemplate => influenceMarkerTemplate;
        public BuildInfoItemView DragGhostTemplate => dragGhostTemplate;
        public BuildInfoItemView PendingBuildGhostTemplate => pendingBuildGhostTemplate;

        public bool IsBoundTo(BuildInfoPanel candidate)
        {
            return candidate != null && controller == candidate;
        }

        public bool TryValidateConfiguration(out string reason)
        {
            reason = string.Empty;
            // 首次实例化时父控制器的 Awake 可能早于图片组件的 OnEnable。
            if (cityBoardImage != null && cityBoardImage.texture == null)
                cityBoardImage.GetComponent<SharedArtworkImage>()?.RefreshArtwork();
            if (cardInteractionLayoutProfile == null ||
                !cardInteractionLayoutProfile.TryValidateConfiguration(out reason))
            {
                reason = "建设面板缺少有效的卡牌交互布局 Profile：" + reason;
                return false;
            }

            if (controller == null || root == null || panelTransform == null || contentArea == null ||
                contentRoot == null || externalFacilityArea == null || externalCityStyleArea == null ||
                buildAvailabilityText == null || cityBoardRoot == null || cityBoardImage == null ||
                cityBoardImage.texture == null)
            {
                reason = "建设面板固定 View 引用或城市底图不完整。";
                return false;
            }

            if (externalFacilitySlots == null || externalFacilitySlots.Length != 6 ||
                cityBoardSlots == null || cityBoardSlots.Length != 12)
            {
                reason = "建设面板必须配置 6 个公共设施槽与 12 个城市面板槽。";
                return false;
            }

            for (var i = 0; i < externalFacilitySlots.Length; i++)
            {
                if (externalFacilitySlots[i] == null ||
                    !externalFacilitySlots[i].TryValidateAs(BuildInfoSlotKind.ExternalFacility, i, out reason))
                {
                    return false;
                }
            }

            for (var i = 0; i < cityBoardSlots.Length; i++)
            {
                if (cityBoardSlots[i] == null ||
                    !cityBoardSlots[i].TryValidateAs(BuildInfoSlotKind.CityBoard, i, out reason))
                {
                    return false;
                }
            }

            if (cardContentTemplate == null || influenceMarkerTemplate == null ||
                dragGhostTemplate == null || pendingBuildGhostTemplate == null ||
                !cardContentTemplate.TryValidateAs(BuildInfoItemKind.CardContent, out reason) ||
                !influenceMarkerTemplate.TryValidateAs(BuildInfoItemKind.InfluenceMarker, out reason) ||
                !dragGhostTemplate.TryValidateAs(BuildInfoItemKind.DragGhost, out reason) ||
                !pendingBuildGhostTemplate.TryValidateAs(BuildInfoItemKind.PendingBuildGhost, out reason))
            {
                return false;
            }

            if (cardContentTemplate.gameObject.activeSelf ||
                influenceMarkerTemplate.gameObject.activeSelf || dragGhostTemplate.gameObject.activeSelf ||
                pendingBuildGhostTemplate.gameObject.activeSelf)
            {
                reason = "建设面板动态模板必须在 Prefab 中默认禁用。";
                return false;
            }

            if (cityStyleRowTemplates == null || cityStyleRowTemplates.Length != 3 ||
                System.Array.Exists(cityStyleRowTemplates, row => row == null || !row.HasNameText || row.gameObject.activeSelf))
            { reason = "城市样式状态行模板引用不完整或未禁用。"; return false; }
            reason = string.Empty;
            return true;
        }
    }
}
