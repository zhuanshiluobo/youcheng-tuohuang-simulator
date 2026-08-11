using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class RoundTrackerScoreBarView : MonoBehaviour
    {
        [SerializeField] private Image background;
        [SerializeField] private Text labelText;
        [SerializeField] private RectTransform fillTransform;
        [SerializeField] private Image fillImage;
        [SerializeField] private Text valueText;

        public void Bind(string scoreKey, string label, int value, int maxMagnitude, Color fillColor)
        {
            gameObject.name = "Final Score Detail " + scoreKey + " Row";
            background.color = scoreKey == "Total"
                ? new Color(0.3f, 0.22f, 0.09f, 0.58f)
                : new Color(0.04f, 0.025f, 0.015f, 0.34f);
            labelText.gameObject.name = "Final Score Detail " + scoreKey + " Label";
            labelText.text = label;
            labelText.fontStyle = scoreKey == "Total" ? FontStyle.Bold : FontStyle.Normal;
            fillTransform.gameObject.name = "Final Score Detail " + scoreKey + " Bar";
            fillTransform.anchorMax = new Vector2(
                Mathf.Clamp01(Mathf.Abs(value) / (float)Mathf.Max(1, maxMagnitude)),
                1f);
            fillImage.color = value < 0 ? new Color(0.72f, 0.25f, 0.19f, 1f) : fillColor;
            valueText.gameObject.name = "Final Score Detail " + scoreKey + " Value";
            valueText.text = value.ToString();
        }
    }
}
