using UnityEngine;
namespace YC.Presentation
{
    [CreateAssetMenu(fileName = "CollectionItem", menuName = "游城/收藏室/藏品")]
    public sealed class CollectionItemDefinition : ScriptableObject
    {
        [SerializeField, Tooltip("稳定且唯一的标识，不随显示名称改变。")] private string itemId;
        [SerializeField] private string displayName;
        [SerializeField] private string englishName;
        [SerializeField, InspectorName("稀有度")] private string category;
        [SerializeField] private string displayNumber;
        [SerializeField, InspectorName("描述文本"), TextArea(3, 8)] private string description;
        [SerializeField, InspectorName("默认展示"), Tooltip("未获取时是否显示卡片；不是进入收藏室时默认选中的藏品。")] private bool defaultDisplay = true;
        [SerializeField, InspectorName("获取条件文本"), TextArea(2, 4)] private string acquisitionRequirementText;
        [SerializeField, InspectorName("使用自定义背景色")] private bool overrideSpotlightColor;
        [SerializeField, InspectorName("背景颜色"), ColorUsage(false)] private Color spotlightColor = Color.white;
        [SerializeField] private Sprite icon;
        [SerializeField, Tooltip("根节点保存模型默认姿态与比例；不要包含公共灯光、地板或摄像机。")] private GameObject displayPrefab;
        [SerializeField, Tooltip("仅使用收藏室水晶着色器的藏品需要开启。")] private bool useInteriorReflection;
        [Header("模型视角零位")]
        [SerializeField, Tooltip("保存展示旋转的节点相对路径；留空使用预制体根节点。")] private string viewPosePath;
        [SerializeField, Tooltip("此节点完全正视相机时的旋转，包含模型的导入坐标补偿；不是默认展示姿态。")] private Vector3 frontViewEuler;
        public string ItemId => itemId;
        public string DisplayName => displayName;
        public string EnglishName => englishName;
        public string Rarity => category;
        public string DisplayNumber => displayNumber;
        public string Description => description;
        public bool DefaultDisplay => defaultDisplay;
        public string AcquisitionRequirementText => acquisitionRequirementText;
        public bool OverrideSpotlightColor => overrideSpotlightColor;
        public Color SpotlightColor => spotlightColor;
        public Sprite Icon => icon;
        public GameObject DisplayPrefab => displayPrefab;
        public bool UseInteriorReflection => useInteriorReflection;
        public string ViewPosePath => viewPosePath;
        public Vector3 FrontViewEuler => frontViewEuler;
    }
}
