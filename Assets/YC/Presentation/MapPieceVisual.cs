using System.Collections.Generic;
using UnityEngine;

namespace YC.Presentation
{
    [DisallowMultipleComponent]
    public sealed class MapPieceVisual : MonoBehaviour
    {
        private static readonly int ColorPropertyId = Shader.PropertyToID("_Color");
        private static readonly int EmissionColorPropertyId = Shader.PropertyToID("_EmissionColor");
        private const float EmissionStrength = 0.08f;

        [SerializeField] private MeshRenderer[] renderers = new MeshRenderer[0];

        private MaterialPropertyBlock propertyBlock;

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
    }
}
