using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace YC.Presentation
{
    [DisallowMultipleComponent]
    public sealed class MapPieceVisual : MonoBehaviour
    {
        private static readonly int ColorPropertyId = Shader.PropertyToID("_Color");
        private static readonly int EmissionColorPropertyId = Shader.PropertyToID("_EmissionColor");
        private const float EmissionStrength = 0.08f;
        private const int GhostSortingOrder = 32000;

        [SerializeField] private MeshRenderer[] renderers = new MeshRenderer[0];

        private MaterialPropertyBlock propertyBlock;
        private bool ghosted;
        private int[] normalSortingOrders;

        public IReadOnlyList<MeshRenderer> Renderers => renderers;

        public bool TryValidateConfiguration(out string reason)
        {
            if (renderers == null || renderers.Length == 0)
            {
                reason = "Map piece visual requires at least one MeshRenderer.";
                return false;
            }

            for (var i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null)
                {
                    reason = "Map piece visual has a missing MeshRenderer at index " + i + ".";
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }

        public void SetVisible(bool visible)
        {
            if (renderers == null)
            {
                return;
            }

            for (var i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                {
                    renderers[i].enabled = visible;
                }
            }
        }

        public void SetPlayerColor(Color color)
        {
            if (renderers == null)
            {
                return;
            }

            if (propertyBlock == null)
            {
                propertyBlock = new MaterialPropertyBlock();
            }

            var emission = new Color(
                color.r * EmissionStrength,
                color.g * EmissionStrength,
                color.b * EmissionStrength,
                color.a);

            for (var i = 0; i < renderers.Length; i++)
            {
                var target = renderers[i];
                if (target == null)
                {
                    continue;
                }

                target.GetPropertyBlock(propertyBlock);
                propertyBlock.SetColor(ColorPropertyId, color);
                propertyBlock.SetColor(EmissionColorPropertyId, emission);
                target.SetPropertyBlock(propertyBlock);
                propertyBlock.Clear();
            }
        }

        public void SetGhosted(bool value)
        {
            if (ghosted == value || renderers == null)
            {
                return;
            }

            if (normalSortingOrders == null || normalSortingOrders.Length != renderers.Length)
            {
                normalSortingOrders = new int[renderers.Length];
            }

            ghosted = value;
            for (var i = 0; i < renderers.Length; i++)
            {
                var target = renderers[i];
                if (target == null)
                {
                    continue;
                }

                var material = UnityEngine.Application.isPlaying ? target.material : target.sharedMaterial;
                if (material == null || !material.HasProperty("_Mode"))
                {
                    continue;
                }

                if (value)
                {
                    normalSortingOrders[i] = target.sortingOrder;
                    // 保持槽位原坐标不变，仅提高透明物体的绘制顺序。
                    target.sortingOrder = GhostSortingOrder;
                    material.SetOverrideTag("RenderType", "Transparent");
                    material.SetFloat("_Mode", 2f);
                    material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                    material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                    material.SetInt("_ZWrite", 0);
                    material.DisableKeyword("_ALPHATEST_ON");
                    material.EnableKeyword("_ALPHABLEND_ON");
                    material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    material.renderQueue = (int)RenderQueue.Transparent + 100;
                }
                else
                {
                    target.sortingOrder = normalSortingOrders[i];
                    material.SetOverrideTag("RenderType", string.Empty);
                    material.SetFloat("_Mode", 0f);
                    material.SetInt("_SrcBlend", (int)BlendMode.One);
                    material.SetInt("_DstBlend", (int)BlendMode.Zero);
                    material.SetInt("_ZWrite", 1);
                    material.DisableKeyword("_ALPHATEST_ON");
                    material.DisableKeyword("_ALPHABLEND_ON");
                    material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    material.renderQueue = -1;
                }
            }
        }
    }
}
