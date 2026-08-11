using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class BuildInfoPanelView : MonoBehaviour
    {
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
        [SerializeField] private BuildInfoItemView cityStyleCardTemplate;
        [SerializeField] private BuildInfoItemView influenceMarkerTemplate;
        [SerializeField] private BuildInfoItemView dragGhostTemplate;
        [SerializeField] private BuildInfoItemView pendingBuildGhostTemplate;

        public BuildInfoPanel Controller => controller;
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
        public BuildInfoItemView CityStyleCardTemplate => cityStyleCardTemplate;
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

            if (cardContentTemplate == null || cityStyleCardTemplate == null || influenceMarkerTemplate == null ||
                dragGhostTemplate == null || pendingBuildGhostTemplate == null ||
                !cardContentTemplate.TryValidateAs(BuildInfoItemKind.CardContent, out reason) ||
                !cityStyleCardTemplate.TryValidateAs(BuildInfoItemKind.CityStyleCard, out reason) ||
                !influenceMarkerTemplate.TryValidateAs(BuildInfoItemKind.InfluenceMarker, out reason) ||
                !dragGhostTemplate.TryValidateAs(BuildInfoItemKind.DragGhost, out reason) ||
                !pendingBuildGhostTemplate.TryValidateAs(BuildInfoItemKind.PendingBuildGhost, out reason))
            {
                return false;
            }

            if (cardContentTemplate.gameObject.activeSelf || cityStyleCardTemplate.gameObject.activeSelf ||
                influenceMarkerTemplate.gameObject.activeSelf || dragGhostTemplate.gameObject.activeSelf ||
                pendingBuildGhostTemplate.gameObject.activeSelf)
            {
                reason = "建设面板动态模板必须在 Prefab 中默认禁用。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
