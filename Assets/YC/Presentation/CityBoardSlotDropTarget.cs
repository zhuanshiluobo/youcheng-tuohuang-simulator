using UnityEngine;

namespace YC.Presentation
{
    /// <summary>
    /// 城市建设面板槽位的稳定落点标记。拖拽逻辑读取组件数据，避免依赖对象名称或层级结构。
    /// </summary>
    public sealed class CityBoardSlotDropTarget : MonoBehaviour
    {
        public int SlotIndex { get; private set; } = -1;

        public void Configure(int configuredSlotIndex)
        {
            SlotIndex = configuredSlotIndex;
        }
    }
}
