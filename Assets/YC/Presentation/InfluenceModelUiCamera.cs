using UnityEngine;

namespace YC.Presentation
{
    // Overlay 不绘制 MeshRenderer，改由专属 UI 相机直接绘制模型与卡片，无贴图中转。
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class InfluenceModelUiCamera : MonoBehaviour
    {
        private Camera modelCamera;
        public static void Ensure(Canvas canvas)
        {
            var owner = canvas.GetComponent<InfluenceModelUiCamera>();
            if (owner == null) owner = canvas.gameObject.AddComponent<InfluenceModelUiCamera>();
            if (owner.modelCamera == null)
            {
                var go = new GameObject("影响力卡片模型相机", typeof(Camera));
                owner.modelCamera = go.GetComponent<Camera>();
                go.transform.position = new Vector3(0, 0, -10000);
                owner.modelCamera.orthographic = true;
                owner.modelCamera.clearFlags = CameraClearFlags.Depth;
                owner.modelCamera.cullingMask = 1 << canvas.gameObject.layer;
                owner.modelCamera.nearClipPlane = 0.1f;
                owner.modelCamera.farClipPlane = 200f;
                owner.modelCamera.depth = 100 + canvas.sortingOrder;
                var lightObject = new GameObject("影响力模型补光", typeof(Light));
                lightObject.transform.SetParent(go.transform, false);
                lightObject.transform.localRotation = Quaternion.Euler(35f, -30f, 0f);
                var light = lightObject.GetComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 0.85f;
                light.cullingMask = owner.modelCamera.cullingMask;
                light.shadows = LightShadows.None;
            }
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = owner.modelCamera;
            canvas.planeDistance = 100f;
        }
        private void OnEnable() { if (modelCamera != null) modelCamera.gameObject.SetActive(true); }
        private void OnDisable() { if (modelCamera != null) modelCamera.gameObject.SetActive(false); }
        private void OnDestroy()
        {
            if (modelCamera == null) return;
            if (UnityEngine.Application.isPlaying) Destroy(modelCamera.gameObject);
            else DestroyImmediate(modelCamera.gameObject);
        }
    }
}
