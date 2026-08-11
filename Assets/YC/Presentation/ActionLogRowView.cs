using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class ActionLogRowView : MonoBehaviour
    {
        [SerializeField] private Image background;
        [SerializeField] private LayoutElement layout;
        [SerializeField] private Text label;

        public bool TryValidateConfiguration(out string reason)
        {
            if (background == null || layout == null || label == null)
            {
                reason = "行动日志行模板引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public void Bind(string value, Color accent, float height)
        {
            background.color = new Color(accent.r, accent.g, accent.b, 0.18f);
            layout.preferredHeight = height;
            label.text = value ?? string.Empty;
        }
    }
}
