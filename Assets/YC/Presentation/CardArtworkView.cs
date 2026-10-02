using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>把一张 Sprite 的纹理区域显示在既有 RawImage 布局中。</summary>
    public static class CardArtworkView
    {
        public static void Set(RawImage image, Sprite sprite)
        {
            if (image == null) return;
            var faceLayout = image.GetComponentInParent<UiCardFaceLayoutGroup>();
            if (faceLayout != null) faceLayout.SetArtworkAspect(image.rectTransform, AspectRatio(sprite));
            var fitter = image.GetComponent<AspectRatioFitter>();
            if (fitter != null && sprite != null) fitter.aspectRatio = AspectRatio(sprite);
            image.texture = sprite == null ? null : sprite.texture;
            if (sprite == null || sprite.texture == null)
            {
                image.uvRect = new Rect(0f, 0f, 1f, 1f);
                return;
            }

            var rect = sprite.textureRect;
            // 双线性采样限制在首尾像素中心，避免放大时取到相邻牌面的像素。
            image.uvRect = new Rect(
                (rect.x + 0.5f) / sprite.texture.width,
                (rect.y + 0.5f) / sprite.texture.height,
                (rect.width - 1f) / sprite.texture.width,
                (rect.height - 1f) / sprite.texture.height);
        }

        public static float AspectRatio(Sprite sprite) =>
            sprite == null || sprite.rect.height <= 0f ? 0f : sprite.rect.width / sprite.rect.height;

        public static void SetTexture(RawImage image, Texture texture)
        {
            if (image == null) return;
            image.texture = texture;
            image.uvRect = new Rect(0f, 0f, 1f, 1f);
        }
    }
}
