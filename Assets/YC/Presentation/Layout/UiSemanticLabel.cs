using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>保留原 Text 数据入口；动态内容按数字和特殊词分段，字体角色由资产配置。</summary>
    [RequireComponent(typeof(Text))]
    public sealed class UiSemanticLabel : MonoBehaviour
    {
        [SerializeField] private RectTransform host;
        [SerializeField] private Text fragmentTemplate;
        [SerializeField] private UiFontRoles fonts;
        [SerializeField] private Color ink = Color.white;
        [SerializeField] private bool wrapCharacters;
        private Text source;
        private string previous;
        private readonly List<Text> fragments = new List<Text>();
        private static readonly Regex Runs = new Regex(@"策略|计谋|[0-9]+|[^0-9策计]+|.");
        private static readonly Regex InlineRuns = new Regex(@"策略|计谋|[0-9]+|\r\n|\r|\n|.");
        private void OnEnable()
        {
            source = GetComponent<Text>();
            source.canvasRenderer.SetAlpha(0);
            source.RegisterDirtyVerticesCallback(Refresh);
            previous = null;
            Refresh();
        }
        private void OnDisable() { if (source != null) source.UnregisterDirtyVerticesCallback(Refresh); }
        private void Refresh()
        {
            if (source != null) source.canvasRenderer.SetAlpha(0);
            if (source == null || host == null || fragmentTemplate == null || fonts == null || previous == source.text) return;
            previous = source.text;
            var runs = (wrapCharacters ? InlineRuns : Runs).Matches(previous ?? string.Empty);
            for (int i = 0; i < runs.Count; i++)
            {
                if (i >= fragments.Count) fragments.Add(Instantiate(fragmentTemplate, host, false));
                var text = fragments[i];
                text.gameObject.SetActive(true); text.text = runs[i].Value;
                text.font = char.IsDigit(text.text[0]) ? fonts.EffectNumber :
                    text.text == "策略" || text.text == "计谋" ? fonts.SpecialWord : fonts.Regular;
                text.fontStyle = FontStyle.Normal; text.fontSize = source.fontSize;
                text.color = ink; text.raycastTarget = false;
            }
            for (int i = runs.Count; i < fragments.Count; i++) fragments[i].gameObject.SetActive(false);
            LayoutRebuilder.MarkLayoutForRebuild(host);
        }
    }
}
