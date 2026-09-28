using UnityEngine;
using UnityEngine.UI;
using YC.Domain.State;

namespace YC.Presentation
{
    /// <summary>费用用资产中的资源图标和数字显示；不把资源名称拼进文案。</summary>
    public sealed class UiResourceCostView : MonoBehaviour
    {
        [SerializeField] private GameObject[] items;
        [SerializeField] private Text[] amounts;
        [SerializeField] private GameObject emptyLabel;
        [SerializeField] private int resourceMask = 31;
        [SerializeField] private string unknownAmount = "—";

        [SerializeField] private bool showZeroAmounts;
        [SerializeField] private bool compareWithOriginal;
        [SerializeField] private Color lowerCostColor = new Color(0f, .5f, .15f, 1f);
        [SerializeField] private Color higherCostColor = new Color(.8f, .1f, .1f, 1f);
        [SerializeField] private Color equalCostColor = Color.black;

        public void SetCost(ResourceSet cost, ResourceSet originalCost = null)
        {
            var values = cost == null ? null : new[] {
                cost.Originium, cost.OriginiumShard, cost.Iron, cost.PureOriginium, cost.GoldVoucher };
            var original = originalCost == null ? null : new[] {
                originalCost.Originium, originalCost.OriginiumShard, originalCost.Iron,
                originalCost.PureOriginium, originalCost.GoldVoucher };
            var visible = false;
            for (var i = 0; i < amounts.Length; i++)
            {
                // 减免到零也保留该项，让玩家看得到绿色的 0。
                var show = (resourceMask & (1 << i)) != 0 && (showZeroAmounts || values == null || values[i] != 0 ||
                    (compareWithOriginal && original != null && original[i] != 0));
                items[i].SetActive(show);
                amounts[i].text = values == null ? unknownAmount : values[i].ToString();
                if (compareWithOriginal)
                    amounts[i].color = values == null || original == null || values[i] == original[i]
                        ? equalCostColor : values[i] < original[i] ? lowerCostColor : higherCostColor;
                visible |= show;
            }
            emptyLabel.SetActive(!visible);
        }
    }
}
