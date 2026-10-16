using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace YC.Presentation
{
    // 只更新收藏室内含物的反射，不包含核心标志，也不改变场景资产。
    internal sealed class CollectionInteriorReflection : IDisposable
    {
        private readonly Renderer shell;
        private readonly Light keyLight;
        private readonly bool[] linkedLighting;
        private readonly MaterialPropertyBlock[] originalLighting;
        private readonly Renderer[] renderers;
        private readonly bool[] inclusion;
        private readonly bool[] enabledStates;
        private readonly ShadowCastingMode[] shadowStates;
        private readonly MaterialPropertyBlock originalProperties = new MaterialPropertyBlock();
        private readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
        private readonly Camera capture;
        private readonly RenderTexture cube;
        private Quaternion capturedRotation;
        private Vector3 capturedPosition;
        private float nextCaptureTime;
        private bool hasCapture;

        public CollectionInteriorReflection(Transform pivot, Camera preview, Light light)
        {
            keyLight = light;
            foreach (var renderer in pivot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.sharedMaterial != null &&
                    renderer.sharedMaterial.shader.name == "YC/Collection/Crystal")
                {
                    shell = renderer;
                    break;
                }
            }
            if (shell == null || preview == null) return;
            var candidates = UnityEngine.Object.FindObjectsOfType<Renderer>(true);
            renderers = Array.FindAll(candidates,
                renderer => (preview.cullingMask & (1 << renderer.gameObject.layer)) != 0);
            inclusion = new bool[renderers.Length];
            linkedLighting = new bool[renderers.Length];
            originalLighting = new MaterialPropertyBlock[renderers.Length];
            enabledStates = new bool[renderers.Length];
            shadowStates = new ShadowCastingMode[renderers.Length];
            for (int i = 0; i < renderers.Length; i++)
            {
                var material = renderers[i].sharedMaterial;
                // 反射只更新藏品自身；房间灯光、地板和雾由 CollectionRoomLighting 独立管理。
                linkedLighting[i] = renderers[i].transform.IsChildOf(pivot) &&
                    material != null && material.HasProperty("_KeyLightFactor");
                if (linkedLighting[i])
                {
                    originalLighting[i] = new MaterialPropertyBlock();
                    renderers[i].GetPropertyBlock(originalLighting[i]);
                }
                inclusion[i] = material != null &&
                    material.shader.name == "YC/Collection/Crystal Inclusions" &&
                    renderers[i].transform.IsChildOf(pivot);
            }
            shell.GetPropertyBlock(originalProperties);
            cube = new RenderTexture(128, 128, 24, RenderTextureFormat.ARGBHalf)
            {
                name = "收藏室实时晶簇反射",
                dimension = TextureDimension.Cube,
                hideFlags = HideFlags.DontSave,
                useMipMap = false
            };
            cube.Create();
            var cameraObject = new GameObject("收藏室内部反射相机")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            capture = cameraObject.AddComponent<Camera>();
            capture.enabled = false;
            capture.clearFlags = CameraClearFlags.SolidColor;
            capture.backgroundColor = Color.black;
            capture.nearClipPlane = 0.005f;
            capture.farClipPlane = 5f;
            capture.cullingMask = preview.cullingMask;
            capture.allowHDR = true;
            capture.renderingPath = RenderingPath.Forward;
        }

        public void Update()
        {
            if (capture == null || shell == null) return;
            // 材质按现有顶灯强度4校准；只联动外部照明，不压暗核心自发光。
            float lightFactor = keyLight == null ? 1f :
                (keyLight.isActiveAndEnabled ? Mathf.Max(0f, keyLight.intensity) / 4f : 0f);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (!linkedLighting[i] || renderers[i] == null) continue;
                renderers[i].GetPropertyBlock(properties);
                properties.SetFloat("_KeyLightFactor", lightFactor);
                if (keyLight != null)
                {
                    var position = keyLight.transform.position;
                    var direction = keyLight.transform.forward;
                    properties.SetVector("_SpotPositionRange", new Vector4(position.x, position.y, position.z, keyLight.range));
                    properties.SetVector("_SpotDirectionCos", new Vector4(direction.x, direction.y, direction.z,
                        Mathf.Cos(keyLight.spotAngle * 0.5f * Mathf.Deg2Rad)));
                    properties.SetFloat("_SpotInnerCos", Mathf.Cos(keyLight.innerSpotAngle * 0.5f * Mathf.Deg2Rad));
                    properties.SetColor("_SpotColor", keyLight.color);
                }
                renderers[i].SetPropertyBlock(properties);
            }
            if (Time.unscaledTime < nextCaptureTime) return;
            // 静止时低频更新也能响应顶灯开关；旋转时最多每秒十次，六面保持同一姿态。
            bool moved = !hasCapture || Quaternion.Angle(capturedRotation, shell.transform.rotation) > 0.5f ||
                (capturedPosition - shell.transform.position).sqrMagnitude > 0.000001f;
            if (!moved && Time.unscaledTime < nextCaptureTime + 0.9f) return;
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null) continue;
                enabledStates[i] = renderer.enabled;
                shadowStates[i] = renderer.shadowCastingMode;
                renderer.enabled = enabledStates[i] && (inclusion[i] || renderer == shell);
                if (renderer == shell) renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            }
            bool rendered;
            try
            {
                capture.transform.SetPositionAndRotation(shell.transform.position, Quaternion.identity);
                rendered = capture.RenderToCubemap(cube);
            }
            finally
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] == null) continue;
                    renderers[i].enabled = enabledStates[i];
                    renderers[i].shadowCastingMode = shadowStates[i];
                }
            }
            nextCaptureTime = Time.unscaledTime + 0.1f;
            if (!rendered) return;
            capturedRotation = shell.transform.rotation;
            capturedPosition = shell.transform.position;
            hasCapture = true;
            shell.GetPropertyBlock(properties);
            properties.SetTexture("_InteriorCube", cube);
            properties.SetVector("_CaptureRotation", new Vector4(capturedRotation.x,
                capturedRotation.y, capturedRotation.z, capturedRotation.w));
            shell.SetPropertyBlock(properties);
        }

        public void Dispose()
        {
            if (renderers != null)
            {
                for (int i = 0; i < renderers.Length; i++)
                    if (linkedLighting[i] && renderers[i] != null)
                        renderers[i].SetPropertyBlock(originalLighting[i]);
            }
            if (shell != null && hasCapture) shell.SetPropertyBlock(originalProperties);
            if (capture != null) UnityEngine.Object.Destroy(capture.gameObject);
            if (cube != null)
            {
                cube.Release();
                UnityEngine.Object.Destroy(cube);
            }
        }
    }
}
