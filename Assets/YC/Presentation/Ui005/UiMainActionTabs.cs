using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>页签只切换主面中的内容分区；选择在窗口变化和卡面返回后保留。</summary>
    public sealed class UiMainActionTabs : MonoBehaviour
    {
        [SerializeField] private Button[] tabs;
        [SerializeField] private UiMainButtonState[] visuals;
        [SerializeField] private GameObject[] sections;
        [SerializeField] private GameObject[] headerTitles;
        [SerializeField] private int selectedIndex;

        public int SelectedIndex => selectedIndex;

        private void Awake()
        {
            if (tabs == null) return;
            for (var i = 0; i < tabs.Length; i++)
            {
                if (tabs[i] == null) continue;
                var index = i;
                tabs[i].onClick.RemoveAllListeners();
                tabs[i].onClick.AddListener(() => Select(index));
            }
            Select(selectedIndex);
        }

        public void Select(int index)
        {
            if (tabs == null || sections == null || index < 0 ||
                index >= tabs.Length || index >= sections.Length) return;
            selectedIndex = index;
            for (var i = 0; i < tabs.Length; i++)
            {
                if (visuals != null && i < visuals.Length && visuals[i] != null)
                    visuals[i].SetSelected(i == index);
                if (i < sections.Length && sections[i] != null)
                    sections[i].SetActive(i == index);
                if (headerTitles != null && i < headerTitles.Length && headerTitles[i] != null)
                    headerTitles[i].SetActive(i == index);
            }
        }
    }
}
