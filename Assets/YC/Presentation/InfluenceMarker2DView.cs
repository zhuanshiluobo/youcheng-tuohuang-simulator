using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Rules;

namespace YC.Presentation
{
    /// <summary>只投影颜色、数量和状态；尺寸、布局、字体及交互由预制体和宿主拥有。</summary>
    [ExecuteAlways]
    [RequireComponent(typeof(Image))]
    public sealed class InfluenceMarker2DView : MonoBehaviour
    {
        [Header("组件引用")]
        [SerializeField] private InfluenceMarker2DCatalog catalog;
        [SerializeField] private Image face;
        [SerializeField] private Text numberText;
        [SerializeField] private Image stateOverlay;
        [Header("方块配置")]
        [InspectorName("颜色")][SerializeField] private InfluenceMarker2DColor color;
        [InspectorName("显示数字")][SerializeField] private bool showNumber;
        [InspectorName("数字")][Min(0)][SerializeField] private int number = 1;
        [InspectorName("区域色使用平色版")][SerializeField] private bool flatRegion;
        [InspectorName("状态")][SerializeField] private InfluenceMarker2DState state;
        [Range(0, 1)][InspectorName("透明度")][SerializeField] private float opacity = 1f;

        public InfluenceMarker2DCatalog Catalog => catalog;
        public InfluenceMarker2DColor ColorPreset => color;
        public bool ShowNumber => showNumber;
        public Text NumberText => numberText;
        public Image Face => face;

        private void OnEnable() => Refresh();
        private void OnValidate() => Refresh();

        public void SetAppearance(InfluenceMarker2DColor preset, bool numbered, int value, bool flat = false)
        {
            color = preset; showNumber = numbered; number = Mathf.Max(0, value); flatRegion = flat;
            Refresh();
        }

        public void SetCount(int value, float alpha = 1f)
        {
            number = Mathf.Max(0, value); opacity = Mathf.Clamp01(alpha); Refresh();
        }

        public void SetState(InfluenceMarker2DState value) { state = value; Refresh(); }

        public void RenderMask(int mask, int count, bool numbered)
        {
            color = (InfluenceMarker2DColor)Mathf.Clamp(mask, 0, 15);
            showNumber = numbered; number = Mathf.Max(0, count); opacity = 1f;
            Refresh();
        }

        public void RenderPlayer(PlayerColor player, int count)
        {
            color = player == PlayerColor.Red ? InfluenceMarker2DColor.Red :
                player == PlayerColor.Blue ? InfluenceMarker2DColor.Blue :
                player == PlayerColor.Green ? InfluenceMarker2DColor.Green : InfluenceMarker2DColor.Yellow;
            number = Mathf.Max(0, count); opacity = 1f; Refresh();
        }

        public void Refresh()
        {
            var entry = catalog == null ? null : catalog.Find(color);
            if (face == null || entry == null) return;
            face.sprite = flatRegion && entry.flat != null ? entry.flat : showNumber ? entry.numbered : entry.plain;
            face.type = Image.Type.Simple;
            face.preserveAspect = true;
            face.color = new Color(1, 1, 1, opacity);
            if (numberText != null)
            {
                numberText.gameObject.SetActive(showNumber);
                numberText.text = number.ToString();
                var ink = entry.numberInk; ink.a *= opacity;
                numberText.color = ink;
            }
            if (stateOverlay != null)
            {
                stateOverlay.sprite = catalog.Overlay(state);
                stateOverlay.gameObject.SetActive(stateOverlay.sprite != null);
                stateOverlay.color = new Color(1, 1, 1, opacity);
            }
        }

        public bool TryValidateConfiguration(out string reason)
        {
            if (catalog == null || face == null || numberText == null || stateOverlay == null)
            { reason = "二维影响力方块组件引用不完整。"; return false; }
            return catalog.TryValidateConfiguration(out reason);
        }
    }
}
