using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>从外部内容包显示共享图片，编辑器预览和运行时使用同一来源。</summary>
    [ExecuteAlways]
    [RequireComponent(typeof(RawImage))]
    public sealed class SharedArtworkImage : MonoBehaviour
    {
        [SerializeField] private string sharedArtworkId = string.Empty;

        private void OnEnable() => RefreshArtwork();

#if UNITY_EDITOR
        private void OnValidate() => RefreshArtwork();
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RefreshAfterSceneLoad()
        {
            // 关闭场景重载时 OnEnable 不会重跑，但内容缓存仍会在进入 Play 时重置。
            foreach (var image in FindObjectsOfType<SharedArtworkImage>(true))
                image.RefreshArtwork();
        }

        public void RefreshArtwork()
        {
            if (!string.IsNullOrEmpty(sharedArtworkId))
                GetComponent<RawImage>().texture = ExternalContentRuntime.GetSharedArtwork(sharedArtworkId);
        }
    }
}
