using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public enum BuildInfoSlotKind
    {
        ExternalFacility,
        CityBoard
    }

    public sealed class BuildInfoSlotView : MonoBehaviour
    {
        [SerializeField] private BuildInfoSlotKind kind;
        [SerializeField] private int slotIndex = -1;
        [SerializeField] private RectTransform root;
        [SerializeField] private Image background;
        [SerializeField] private Button button;
        [SerializeField] private Outline outline;
        [SerializeField] private RectTransform contentRoot;
        [SerializeField] private Text emptyLabel;
        [SerializeField] private CityBoardSlotDropTarget dropTarget;
        [SerializeField] private CardPointerInteraction pointerInteraction;

        public BuildInfoSlotKind Kind => kind;
        public int SlotIndex => slotIndex;
        public RectTransform Root => root;
        public Image Background => background;
        public Button Button => button;
        public Outline Outline => outline;
        public RectTransform ContentRoot => contentRoot;
        public Text EmptyLabel => emptyLabel;
        public CityBoardSlotDropTarget DropTarget => dropTarget;
        public CardPointerInteraction PointerInteraction => pointerInteraction;

        public bool TryValidateAs(BuildInfoSlotKind expected, int expectedIndex, out string reason)
        {
            var valid = kind == expected && slotIndex == expectedIndex && root != null && background != null &&
                        button != null && outline != null && contentRoot != null && emptyLabel != null &&
                        pointerInteraction != null;
            if (expected == BuildInfoSlotKind.CityBoard)
            {
                valid = valid && dropTarget != null;
            }

            reason = valid
                ? string.Empty
                : "建设面板固定槽位引用无效：" + expected + " #" + expectedIndex;
            return valid;
        }
    }
}
