using UnityEngine;

namespace YC.Presentation
{
    /// <summary>仅绘制在预览专用层，不修改相机、UI 或藏品材质。</summary>
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(MeshRenderer))]
    public sealed class PreviewFadeMask : MonoBehaviour
    {
        [SerializeField, Range(0, 1), Tooltip("0 完全透明，1 完全遮黑；只作用于能看到此对象所在层的相机。")]
        private float opacity;
        private Renderer surface;
        private MaterialPropertyBlock properties;
        public float Opacity => opacity;
        public void SetOpacity(float value) { opacity = Mathf.Clamp01(value); Apply(); }
        private void OnEnable() => Apply();
        private void LateUpdate() => Apply();
        private void Apply()
        {
            if (surface == null) surface = GetComponent<Renderer>();
            if (properties == null) properties = new MaterialPropertyBlock();
            surface.GetPropertyBlock(properties);
            properties.SetFloat("_Opacity", opacity);
            surface.SetPropertyBlock(properties);
            surface.enabled = opacity > 0;
        }
        private void OnDisable() { if (surface != null) surface.enabled = false; }
    }
}