using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    public static class UguiUtility
    {
        public static void EnsureEventSystem()
        {
            if (EventSystem.current != null || Object.FindObjectOfType<EventSystem>() != null)
            {
                return;
            }

            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }

        public static Canvas CreateCanvas(string name, int sortingOrder, Transform parent = null)
        {
            var canvasObject = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            if (parent != null)
            {
                canvasObject.transform.SetParent(parent, false);
            }

            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            return canvas;
        }

        public static Sprite CreateCircleSprite(int size, float radius, float thickness)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            var center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var distance = Vector2.Distance(new Vector2(x, y), center);
                    var alpha = distance <= radius && distance >= radius - thickness ? 1f : 0f;
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
        }

        public static Sprite CreateFilledSquareSprite(int size, float sideLength)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Point;

            var center = (size - 1) * 0.5f;
            var halfSide = sideLength * 0.5f;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var alpha = Mathf.Abs(x - center) <= halfSide &&
                                Mathf.Abs(y - center) <= halfSide
                        ? 1f
                        : 0f;
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
        }

        public static Sprite CreateSquareOutlineSprite(int size, float sideLength, float thickness)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Point;

            var center = (size - 1) * 0.5f;
            var outerHalfSide = sideLength * 0.5f;
            var innerHalfSide = Mathf.Max(0f, outerHalfSide - thickness);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = Mathf.Abs(x - center);
                    var dy = Mathf.Abs(y - center);
                    var insideOuter = dx <= outerHalfSide && dy <= outerHalfSide;
                    var insideInner = dx <= innerHalfSide && dy <= innerHalfSide;
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, insideOuter && !insideInner ? 1f : 0f));
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
        }
    }
}
