using UnityEngine;

namespace YC.Presentation
{
    [ExecuteAlways]
    public sealed class MapDisplayController : MonoBehaviour
    {
        [SerializeField] private SpriteRenderer mapRenderer;
        [SerializeField] private Camera targetCamera;
        [SerializeField] private float cameraPadding = 1.03f;

        private void Awake()
        {
            FitCameraToMap();
        }

        private void OnEnable()
        {
            FitCameraToMap();
        }

        private void OnValidate()
        {
            FitCameraToMap();
        }

        public void FitCameraToMap()
        {
            if (mapRenderer == null)
            {
                mapRenderer = GetComponent<SpriteRenderer>();
            }

            if (targetCamera == null)
            {
                targetCamera = Camera.main;
            }

            if (targetCamera != null)
            {
                targetCamera.clearFlags = CameraClearFlags.SolidColor;
                if (UiTheme.IsInitialized)
                {
                    targetCamera.backgroundColor = UiTheme.TacticalMapBg;
                }
            }

            if (mapRenderer == null || mapRenderer.sprite == null || targetCamera == null)
            {
                return;
            }

            var bounds = mapRenderer.bounds;
            var aspect = targetCamera.aspect > 0f ? targetCamera.aspect : 16f / 9f;
            var verticalSize = bounds.size.y * 0.5f;
            var horizontalSize = bounds.size.x / aspect * 0.5f;

            targetCamera.orthographic = true;
            targetCamera.orthographicSize = Mathf.Max(verticalSize, horizontalSize) * cameraPadding;
            targetCamera.transform.position = new Vector3(bounds.center.x, bounds.center.y, targetCamera.transform.position.z);
        }
    }
}
