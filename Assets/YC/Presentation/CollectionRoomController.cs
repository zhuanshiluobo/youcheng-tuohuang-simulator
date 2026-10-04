using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
namespace YC.Presentation
{
    public sealed class CollectionRoomController : MonoBehaviour
    {
        [Header("公共展台（不包含具体藏品）")]
        [SerializeField] private Transform modelPivot;
        [SerializeField] private Camera previewCamera;
        [SerializeField] private Light keyLight;
        [SerializeField] private SpotlightArea spotlightArea;
        [SerializeField] private LoadingModelDragController dragController;
        [SerializeField] private Button backButton;
        [SerializeField] private Button resetViewButton;
        [SerializeField] private string returnSceneName = "StartScene";
        [Header("藏品目录与列表")]
        [SerializeField] private CollectionCatalog catalog;
        [SerializeField] private CollectionItemView itemViewPrefab;
        [SerializeField] private Transform listContent;
        [SerializeField] private Text collectionCountLabel;
        [Header("随选中藏品更新的资料")]
        [SerializeField] private Text nameLabel;
        [SerializeField] private Text englishNameLabel;
        [SerializeField, InspectorName("稀有度文本")] private Text categoryLabel;
        [SerializeField] private Text descriptionLabel;
        [SerializeField] private Text previewNumberLabel;
        [SerializeField] private Text previewStatusLabel;
        [Header("可编辑的动态文案")]
        [SerializeField] private string countFormat = "模型藏品   {0:00} / {1:00}";
        [SerializeField] private string previewNumberFormat = "COLLECTION  /  {0}";
        [SerializeField] private string emptyCatalogMessage = "暂无藏品";
        [SerializeField] private string missingModelMessage = "暂无模型";
        private readonly List<CollectionItemView> itemViews = new List<CollectionItemView>();
        private Quaternion initialRotation;
        private Color initialLightColor = Color.white;
        private readonly HashSet<string> obtainedItemIds = new HashSet<string>(System.StringComparer.Ordinal);
        private bool catalogRefreshPending;
        private CollectionInteriorReflection interiorReflection;
        private CollectionItemDefinition pendingItem;
        private bool switching;
        private bool initialized;
        private int selectionVersion;
        public CollectionItemDefinition CurrentItem { get; private set; }
        public GameObject CurrentModel { get; private set; }
        public bool IsSwitching => switching;
        private void Awake()
        {
            // 返回按钮始终可用，即使目录或展示引用尚未配置完整。
            if (backButton != null) backButton.onClick.AddListener(ReturnToStart);
            if (resetViewButton != null) resetViewButton.onClick.AddListener(ResetView);
            if (previewCamera != null) previewCamera.depthTextureMode |= DepthTextureMode.Depth;
            if (modelPivot != null) initialRotation = modelPivot.localRotation;
            if (keyLight != null) initialLightColor = keyLight.color;
        }
        private void Start()
        {
            if (modelPivot == null || dragController == null || listContent == null || itemViewPrefab == null)
            {
                Debug.LogError("收藏室缺少公共旋转中心、交互区或藏品列表引用。", this);
                SetStatus(emptyCatalogMessage);
                if (dragController != null) dragController.enabled = false;
                if (resetViewButton != null) resetViewButton.interactable = false;
                return;
            }
            initialized = true;
            RefreshCatalog();
        }
        /// <summary>供未来存档/获取系统传入已获取编号；不判断成就，不读写存档。</summary>
        public void SetObtainedItems(IEnumerable<string> itemIds)
        {
            obtainedItemIds.Clear();
            if (itemIds != null)
                foreach (string id in itemIds)
                    if (!string.IsNullOrWhiteSpace(id)) obtainedItemIds.Add(id);
            catalogRefreshPending = true;
        }
        /// <summary>从检查器目录生成条目；只销毁本控制器创建的运行时实例。</summary>
        public void RefreshCatalog()
        {
            if (!initialized || !isActiveAndEnabled || switching || (spotlightArea != null && spotlightArea.IsTransitioning)) return;
            catalogRefreshPending = false;
            foreach (var view in itemViews) { view.gameObject.SetActive(false); Destroy(view.gameObject); }
            itemViews.Clear();
            var ids = new HashSet<string>(System.StringComparer.Ordinal);
            if (catalog != null)
            {
                foreach (var item in catalog.Items)
                {
                    if (item == null || (!item.DefaultDisplay && !obtainedItemIds.Contains(item.ItemId))) continue;
                    if (string.IsNullOrWhiteSpace(item.ItemId) || !ids.Add(item.ItemId))
                    {
                        Debug.LogWarning("跳过空标识或重复标识的藏品配置：" + item.name, item);
                        continue;
                    }
                    var view = Instantiate(itemViewPrefab, listContent, false);
                    view.Bind(item, SelectItem);
                    itemViews.Add(view);
                }
            }
            CollectionItemDefinition selected = null;
            foreach (var view in itemViews)
                if (view.Item == CurrentItem) selected = CurrentItem;
            if (selected == null && itemViews.Count > 0) selected = itemViews[0].Item;
            ShowItem(selected);
        }
        public void SelectItem(CollectionItemDefinition item)
        {
            if (!initialized || !isActiveAndEnabled || item == null || !itemViews.Exists(view => view.Item == item)) return;
            pendingItem = item;
            if (!switching) StartCoroutine(SwitchPendingItems());
        }
        private IEnumerator SwitchPendingItems()
        {
            switching = true;
            try
            {
                while (pendingItem != null)
                {
                    var item = pendingItem;
                    pendingItem = null;
                    if (item == CurrentItem) continue;
                    dragController.StopMotion();
                    dragController.enabled = false;
                    if (resetViewButton != null) resetViewButton.interactable = false;
                    while (spotlightArea != null && spotlightArea.IsTransitioning) yield return null;
                    // 复用既有过渡；只传入稀有度背景色，保留灯位、强度和过渡参数。
                    int version = selectionVersion;
                    var transition = spotlightArea != null && keyLight != null
                        ? spotlightArea.SwitchExhibit(GetSpotlightColor(item), () =>
                        {
                            if (this != null && isActiveAndEnabled && version == selectionVersion) ShowItem(item);
                        }) : null;
                    if (transition != null) yield return transition;
                    else ShowItem(item);
                }
            }
            finally { switching = false; SetInteractionEnabled(); }
        }
        private Color GetSpotlightColor(CollectionItemDefinition item)
        {
            if (item == null) return initialLightColor;
            Color color;
            if (CollectionRarityPalette.TryGetSpotlightColor(item.Rarity, out color)) return color;
            return item.OverrideSpotlightColor ? item.SpotlightColor : initialLightColor;
        }
        private string GetDescriptionText(CollectionItemDefinition item)
        {
            if (item == null) return string.Empty;
            if (obtainedItemIds.Contains(item.ItemId) || string.IsNullOrWhiteSpace(item.AcquisitionRequirementText))
                return item.Description;
            return string.IsNullOrWhiteSpace(item.Description) ? item.AcquisitionRequirementText
                : item.Description + "\n\n" + item.AcquisitionRequirementText;
        }
        private void ShowItem(CollectionItemDefinition item)
        {
            dragController.StopMotion();
            ReleaseModel();
            modelPivot.localRotation = initialRotation;
            CurrentItem = item;
            if (spotlightArea != null) spotlightArea.SetBackgroundColor(GetSpotlightColor(item));
            if (item != null && item.DisplayPrefab != null)
            {
                // 保留预制体中编辑好的局部位置、旋转与比例。
                CurrentModel = Instantiate(item.DisplayPrefab, modelPivot, false);
                // 展示图层由公共展台决定，只修改实例，不覆盖模型资产。
                foreach (var part in CurrentModel.GetComponentsInChildren<Transform>(true))
                    part.gameObject.layer = modelPivot.gameObject.layer;
                CurrentModel.SetActive(true);
                if (item.UseInteriorReflection)
                    interiorReflection = new CollectionInteriorReflection(CurrentModel.transform, previewCamera, keyLight);
            }
            if (nameLabel != null) nameLabel.text = item != null ? item.DisplayName : string.Empty;
            if (englishNameLabel != null) englishNameLabel.text = item != null ? item.EnglishName : string.Empty;
            if (categoryLabel != null) categoryLabel.text = item != null ? item.Rarity : string.Empty;
            if (descriptionLabel != null) descriptionLabel.text = GetDescriptionText(item);
            if (previewNumberLabel != null) previewNumberLabel.text = item != null
                ? string.Format(previewNumberFormat, item.DisplayNumber) : string.Empty;
            int index = 0;
            for (int i = 0; i < itemViews.Count; i++)
            {
                bool selected = itemViews[i].Item == item;
                itemViews[i].SetSelected(selected);
                if (selected) index = i + 1;
            }
            if (collectionCountLabel != null)
                collectionCountLabel.text = string.Format(countFormat, index, itemViews.Count);
            SetStatus(item == null ? emptyCatalogMessage : CurrentModel == null ? missingModelMessage : string.Empty);
            SetInteractionEnabled();
        }
        private void SetStatus(string message)
        {
            if (previewStatusLabel == null) return;
            previewStatusLabel.text = message;
            previewStatusLabel.gameObject.SetActive(!string.IsNullOrEmpty(message));
        }
        private void SetInteractionEnabled()
        {
            bool available = isActiveAndEnabled && CurrentModel != null && !switching;
            if (dragController != null) dragController.enabled = available;
            if (resetViewButton != null) resetViewButton.interactable = available;
        }
        private void ReleaseModel()
        {
            interiorReflection?.Dispose();
            interiorReflection = null;
            if (CurrentModel == null) return;
            CurrentModel.SetActive(false);
            Destroy(CurrentModel);
            CurrentModel = null;
        }
        private void Update() { if (Input.GetKeyDown(KeyCode.Escape)) ReturnToStart(); }
        private void LateUpdate()
        {
            if (catalogRefreshPending) RefreshCatalog();
            interiorReflection?.Update();
        }
        private void OnEnable()
        {
            if (!initialized) return;
            CurrentItem = null; // 重新进入时选中第一件可见藏品。
            catalogRefreshPending = true;
            SetInteractionEnabled();
        }
        private void OnDisable()
        {
            pendingItem = null;
            selectionVersion++;
            StopAllCoroutines();
            switching = false;
            if (dragController != null) dragController.StopMotion();
            SetInteractionEnabled();
        }
        private void OnDestroy()
        {
            ReleaseModel();
            if (backButton != null) backButton.onClick.RemoveListener(ReturnToStart);
            if (resetViewButton != null) resetViewButton.onClick.RemoveListener(ResetView);
        }
        public void ResetView()
        {
            if (modelPivot == null || dragController == null || switching) return;
            dragController.StopMotion();
            modelPivot.localRotation = initialRotation;
        }
        public void ReturnToStart()
        {
            if (SceneTransitionContext.IsTransitionInProgress) return;
            try { SceneTransitionContext.TryBeginBlackTransition(returnSceneName); }
            catch (System.Exception ex)
            {
                SceneTransitionContext.Clear();
                Debug.LogError("返回主页的过场加载失败：" + ex.Message, this);
            }
        }
#if UNITY_EDITOR
        public void ConfigureForEditor(Transform pivot, LoadingModelDragController drag, Button back, Button reset)
        {
            modelPivot = pivot; dragController = drag; backButton = back; resetViewButton = reset;
        }
#endif
    }
}
