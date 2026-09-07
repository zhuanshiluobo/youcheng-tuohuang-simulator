using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace YC.Presentation
{
    // Image 只保留点击区域；显示使用原影响力 prefab 的真实网格。
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class InfluenceModelUiAnchor : MonoBehaviour
    {
        private Image image;
        private Transform model;
        private MapPieceVisual visual;
        private Bounds modelBounds;
        private bool boundsReady;
        private MeshRenderer[] modelRenderers;
        private readonly List<Material> materials = new List<Material>();

        public static void Configure(Image image, GameObject prefab, Color color)
        {
            var anchor = image.GetComponent<InfluenceModelUiAnchor>();
            if (anchor == null) anchor = image.gameObject.AddComponent<InfluenceModelUiAnchor>();
            anchor.image = image;
            var canvas = image.GetComponentInParent<Canvas>();
            if (canvas != null && canvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay)
                InfluenceModelUiCamera.Ensure(canvas.rootCanvas);
            if (anchor.model == null && prefab != null)
            {
                anchor.model = Instantiate(prefab, image.transform, false).transform;
                anchor.model.name = "影响力模型";
                foreach (var child in anchor.model.GetComponentsInChildren<Transform>(true))
                    child.gameObject.layer = canvas != null ? canvas.rootCanvas.gameObject.layer : image.gameObject.layer;
                anchor.visual = anchor.model.GetComponentInChildren<MapPieceVisual>();
                // UI 背板为透明队列。独立材质保证网格能叠在背板上，不修改原 prefab 材质。
                anchor.modelRenderers = anchor.model.GetComponentsInChildren<MeshRenderer>();
                foreach (var renderer in anchor.modelRenderers)
                {
                    var copies = renderer.sharedMaterials;
                    for (var i = 0; i < copies.Length; i++)
                    {
                        if (copies[i] == null) continue;
                        var material = new Material(copies[i]);
                        anchor.materials.Add(material);
                        material.SetOverrideTag("RenderType", "Transparent");
                        material.SetFloat("_Mode", 2);
                        material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                        material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                        material.SetInt("_ZWrite", 0);
                        material.DisableKeyword("_ALPHATEST_ON");
                        material.EnableKeyword("_ALPHABLEND_ON");
                        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                        material.renderQueue = (int)RenderQueue.Transparent;
                        copies[i] = material;
                    }
                    renderer.sharedMaterials = copies;
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                }
            }
            if (anchor.visual != null) anchor.visual.SetPlayerColor(color);
            anchor.LateUpdate();
        }

        public void SetVisible(bool visible)
        {
            if (visual != null) visual.SetVisible(visible);
        }

        private void LateUpdate()
        {
            if (image == null || model == null) return;
            image.canvasRenderer.SetAlpha(0f);
            if (modelRenderers == null) modelRenderers = model.GetComponentsInChildren<MeshRenderer>();
            model.localPosition = Vector3.zero;
            model.localRotation = Quaternion.Euler(25f, -30f, 0f);
            model.localScale = Vector3.one;
            var bounds = modelBounds;
            var hasBounds = boundsReady;
            if (!boundsReady)
            {
                foreach (var filter in model.GetComponentsInChildren<MeshFilter>())
                {
                    if (filter.sharedMesh == null) continue;
                    var b = filter.sharedMesh.bounds;
                    for (var i = 0; i < 8; i++)
                    {
                        var corner = b.center + Vector3.Scale(b.extents,
                            new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                        var point = image.transform.InverseTransformPoint(filter.transform.TransformPoint(corner));
                        if (!hasBounds) { bounds = new Bounds(point, Vector3.zero); hasBounds = true; }
                        else bounds.Encapsulate(point);
                    }
                }
                modelBounds = bounds;
                boundsReady = hasBounds;
            }
            if (!hasBounds) return;
            var rect = image.rectTransform.rect;
            var scale = Mathf.Min(rect.width / Mathf.Max(bounds.size.x, 0.001f),
                rect.height / Mathf.Max(bounds.size.y, 0.001f));
            model.localScale = Vector3.one * scale;
            model.localPosition = new Vector3(rect.center.x, rect.center.y, -1f) - bounds.center * scale;
            var canvas = image.GetComponentInParent<Canvas>();
            if (canvas != null)
                foreach (var renderer in modelRenderers)
                {
                    renderer.sortingLayerID = canvas.sortingLayerID;
                    renderer.sortingOrder = canvas.sortingOrder + 1;
                }
        }

        private void OnDestroy()
        {
            foreach (var material in materials)
                if (UnityEngine.Application.isPlaying) Destroy(material); else DestroyImmediate(material);
        }
    }
}

