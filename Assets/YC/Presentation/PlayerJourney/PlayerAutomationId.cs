using UnityEngine;

namespace YC.PlayerJourney
{
    /// <summary>仅标注屏幕上的控件和实际绘制的高亮，不提供任何规则信息。</summary>
    public sealed class PlayerAutomationId : MonoBehaviour
    {
        public string Id;
        public bool Highlighted;

        [System.Diagnostics.Conditional("UNITY_EDITOR"), System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        public static void Attach(GameObject target, string id, bool highlighted = false)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (target == null) return;
            var marker = target.GetComponent<PlayerAutomationId>() ?? target.AddComponent<PlayerAutomationId>();
            marker.Id = id; marker.Highlighted = highlighted;
#endif
        }
    }
}
