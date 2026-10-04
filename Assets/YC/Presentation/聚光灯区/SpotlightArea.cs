using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Events;
namespace YC.Presentation
{
    /// <summary>收藏室自身的照明，与展示模型及其反射实现无关。</summary>
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class SpotlightArea : MonoBehaviour
    {
        [Header("收藏室固定引用（不要放在藏品预制体内）")]
        [SerializeField] private Light keyLight;
        [SerializeField] private Renderer floor;
        [SerializeField] private Renderer beamVolume;
        [SerializeField] private Renderer groundMist;
        [Header("背景与模型照明")]
        [SerializeField, ColorUsage(false), Tooltip("背景按稀有度换色，模型使用独立的中性照明色。")]
        private Color modelLightColor = Color.white;
        [SerializeField, Range(0f, 1f), Tooltip("模型主光的能量比例，不改变背景亮度和切换过渡。")]
        private float modelLightStrength = 0.2f;
        [Header("藏品切换过渡")]
        [SerializeField] private PreviewFadeMask fadeMask;
        [SerializeField, Min(0)] private float fadeOutSeconds = 0.45f;
        [SerializeField, Min(0)] private float darkHoldSeconds = 0.15f;
        [SerializeField, Min(0)] private float fadeInSeconds = 0.65f;
        [SerializeField, ColorUsage(false)] private Color previewNextColor = new Color(0.35f, 0.65f, 1f);
        [SerializeField, Tooltip("完全遮黑时触发。由外部收藏品管理器负责替换模型，此组件不写入模型参数。")]
        private UnityEvent onFullyDark = new UnityEvent();
        private MaterialPropertyBlock properties;
        private float originalIntensity;
        private Color backgroundColor;
        private bool hasBackgroundColor;
        public bool IsTransitioning { get; private set; }
        public Color BackgroundColor => hasBackgroundColor ? backgroundColor : (keyLight != null ? keyLight.color : Color.white);

        /// <summary>只改变背景雾和地板的色相，不把稀有度颜色染到藏品材质上。</summary>
        public void SetBackgroundColor(Color color)
        {
            backgroundColor = color;
            hasBackgroundColor = true;
            if (keyLight != null)
            {
                // 主光原强度用于背景和过渡；模型独立减小能量，避免金属高光整片过曝。
                var linearColor = modelLightColor.linear * modelLightStrength;
                keyLight.color = QualitySettings.activeColorSpace == ColorSpace.Linear ? linearColor.gamma : modelLightColor * modelLightStrength;
            }
            RefreshLighting();
        }

        /// <summary>外部传入新颜色和切换动作；只有完全遮黑后才执行替换。</summary>
        public Coroutine SwitchExhibit(Color color, Action replaceExhibit = null)
        {
            if (!UnityEngine.Application.isPlaying || !isActiveAndEnabled || keyLight == null || fadeMask == null || IsTransitioning)
                return null;
            return StartCoroutine(Transition(color, replaceExhibit));
        }
        [ContextMenu("预览渐暗并换色亮起（运行模式）")]
        private void PreviewTransition() => SwitchExhibit(previewNextColor);
        private IEnumerator Transition(Color color, Action replaceExhibit)
        {
            IsTransitioning = true;
            originalIntensity = keyLight.intensity;
            try
            {
                yield return Fade(1, 0, fadeOutSeconds);
                replaceExhibit?.Invoke();
                onFullyDark.Invoke();
                SetBackgroundColor(color);
                yield return new WaitForSecondsRealtime(darkHoldSeconds);
                yield return Fade(0, 1, fadeInSeconds);
            }
            finally
            {
                RestoreVisibility();
            }
        }
        private IEnumerator Fade(float from, float to, float seconds)
        {
            float elapsed = 0;
            while (elapsed < seconds)
            {
                SetVisibility(Mathf.Lerp(from, to, Mathf.SmoothStep(0, 1, elapsed / seconds)));
                yield return null;
                elapsed += Time.unscaledDeltaTime;
            }
            SetVisibility(to);
        }
        private void SetVisibility(float value)
        {
            keyLight.intensity = originalIntensity * value;
            fadeMask.SetOpacity(1 - value);
            RefreshLighting();
        }
        private void RestoreVisibility()
        {
            if (!IsTransitioning) return;
            if (keyLight != null) keyLight.intensity = originalIntensity;
            if (fadeMask != null) fadeMask.SetOpacity(0);
            IsTransitioning = false;
        }
        private void OnDisable()
        {
            StopAllCoroutines();
            RestoreVisibility();
        }

        private void OnEnable() => RefreshLighting();
        private void LateUpdate() => RefreshLighting();
        public void RefreshLighting()
        {
            if (keyLight == null) return;
            FitBeamBounds();
            ApplyTo(floor); ApplyTo(beamVolume); ApplyTo(groundMist);
        }
        private void ApplyTo(Renderer target)
        {
            if (target == null) return;
            if (properties == null) properties = new MaterialPropertyBlock();
            target.GetPropertyBlock(properties);
            var p = keyLight.transform.position;
            var d = keyLight.transform.forward;
            properties.SetVector("_SpotPositionRange", new Vector4(p.x,p.y,p.z,keyLight.range));
            properties.SetVector("_SpotDirectionCos", new Vector4(d.x,d.y,d.z,Mathf.Cos(keyLight.spotAngle*0.5f*Mathf.Deg2Rad)));
            properties.SetFloat("_SpotInnerCos", Mathf.Cos(Mathf.Min(keyLight.innerSpotAngle,keyLight.spotAngle)*0.5f*Mathf.Deg2Rad));
            // 自定义 uniform 不依赖 Color 属性的隐式转换，显式匹配项目色彩空间。
            var shaderColor = QualitySettings.activeColorSpace == ColorSpace.Linear ? BackgroundColor.linear : BackgroundColor;
            properties.SetVector("_SpotColor", new Vector4(shaderColor.r,shaderColor.g,shaderColor.b,shaderColor.a));
            properties.SetFloat("_UseRarityBackground", hasBackgroundColor ? 1f : 0f);
            properties.SetFloat("_KeyLightFactor", keyLight.isActiveAndEnabled ? Mathf.Max(0f,keyLight.intensity)/4f : 0f);
            if (floor != null)
            {
                var n = floor.transform.up;
                properties.SetVector("_FloorPlane", new Vector4(n.x,n.y,n.z,-Vector3.Dot(n,floor.transform.position)));
            }
            target.SetPropertyBlock(properties);
        }
        private void FitBeamBounds()
        {
            if (beamVolume == null) return;
            // 这是光雾的采样包围盒，不是光束宽度。位置和宽度只在 Light 上调。
            var t = beamVolume.transform;
            t.SetPositionAndRotation(keyLight.transform.position, Quaternion.identity);
            var parentScale = t.parent != null ? t.parent.lossyScale : Vector3.one;
            float diameter = Mathf.Max(0.02f, keyLight.range * 2f);
            t.localScale = new Vector3(diameter / Mathf.Max(Mathf.Abs(parentScale.x),0.0001f),
                diameter / Mathf.Max(Mathf.Abs(parentScale.y),0.0001f),
                diameter / Mathf.Max(Mathf.Abs(parentScale.z),0.0001f));
        }
    }
}
