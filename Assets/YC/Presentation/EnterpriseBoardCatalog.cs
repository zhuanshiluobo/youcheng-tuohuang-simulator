using System;
using UnityEngine;
using UnityEngine.UI;
using Unity.Collections;
using UnityEngine.Rendering;
using UnityEngine.U2D;

namespace YC.Presentation
{
    [CreateAssetMenu(menuName = "YC/企业与科室原板")]
    public sealed class EnterpriseBoardCatalog : ScriptableObject
    {
        [Serializable]
        public sealed class Board
        {
            public string visualKey;
            public Texture2D texture;
            public Rect uv = new Rect(0, 0, 1, 1);
            public Rect logoUv;
            public float logoClipTopLeft;
            // 单板左上原点的归一化区域；独立于主界面企业图标裁切。
            public Rect companyExcerpt;
            public Rect[] levelExcerpts;
            public float aspect;
            public Vector2[] rewardAnchors;
        }
        [SerializeField] private Board[] boards;
        [Serializable]
        public sealed class ExternalLogoCrop
        {
            public string visualKey;
            // 当前内容原板内、左下原点的归一化裁切，不要求StreamingAssets成为Unity贴图资产。
            public Rect uv;
            public float clipTopLeft;
        }
        [SerializeField] private ExternalLogoCrop[] externalLogoCrops;
        private readonly System.Collections.Generic.Dictionary<string, Sprite> logos = new System.Collections.Generic.Dictionary<string, Sprite>();
        public Sprite Logo(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (logos.TryGetValue(key, out var logo)) return logo;
            var board = Find(key);
            if (board?.texture == null || board.logoUv.width <= 0 || board.logoUv.height <= 0) return null;
            var texture = board.texture;
            var crop = board.logoUv;
            logo = Sprite.Create(texture, new Rect(crop.x * texture.width, crop.y * texture.height,
                crop.width * texture.width, crop.height * texture.height), new Vector2(.5f, .5f));
            if (board.logoClipTopLeft > 0)
                ApplyLogoGeometry(logo, board.logoClipTopLeft);
            logo.name = key + "-logo";
            logo.hideFlags = HideFlags.DontSave;
            logos.Add(key, logo);
            return logo;
        }
        private void OnDisable()
        {
            foreach (var logo in logos.Values)
                if (UnityEngine.Application.isPlaying) Destroy(logo); else DestroyImmediate(logo);
            logos.Clear();
        }
        public Board Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var configured = Array.Find(boards ?? Array.Empty<Board>(), b => b.visualKey == key);
            if (configured != null) return configured;
            var sprite = ExternalContentRuntime.GetArtworkSprite("enterprise", key) ?? ExternalContentRuntime.GetArtworkSprite("department", key);
            if (sprite == null) return null;
            var logo = Array.Find(externalLogoCrops ?? Array.Empty<ExternalLogoCrop>(), value => value.visualKey == key);
            var artworkUv = new Rect(sprite.rect.x / sprite.texture.width, sprite.rect.y / sprite.texture.height,
                sprite.rect.width / sprite.texture.width, sprite.rect.height / sprite.texture.height);
            return new Board { visualKey = key, texture = sprite.texture, aspect = sprite.rect.width / sprite.rect.height,
                uv = artworkUv, logoUv = logo == null ? default : MapLogoUv(artworkUv, logo.uv),
                logoClipTopLeft = logo?.clipTopLeft ?? 0 };
        }
        internal static Rect MapLogoUv(Rect artworkUv, Rect crop) => new Rect(
            artworkUv.x + crop.x * artworkUv.width, artworkUv.y + crop.y * artworkUv.height,
            crop.width * artworkUv.width, crop.height * artworkUv.height);

        internal static Vector2[] BuildLogoVertices(Vector2 pixels, float clipTopLeft)
        {
            // 裁切在左下原点的 Sprite.rect 像素坐标中计算；写网格时再按实际 pivot 和 PPU 换算。
            var normalized = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1),
                new Vector2(clipTopLeft, 1), new Vector2(0, 1 - clipTopLeft) };
            return Array.ConvertAll(normalized, value => new Vector2(
                value.x * pixels.x, value.y * pixels.y));
        }

        private static void ApplyLogoGeometry(Sprite logo, float clipTopLeft)
        {
            var rect = logo.rect;
            var pixels = BuildLogoVertices(rect.size, clipTopLeft);
            // 正式原板不保留可读 CPU 像素；直接写入 Sprite 网格，复用原纹理和已有裁切区域。
            using (var positions = new NativeArray<Vector3>(Array.ConvertAll(pixels,
                pixel => (Vector3)((pixel - logo.pivot) / logo.pixelsPerUnit)), Allocator.Temp))
            using (var uvs = new NativeArray<Vector2>(Array.ConvertAll(pixels,
                pixel => new Vector2((rect.x + pixel.x) / logo.texture.width,
                    (rect.y + pixel.y) / logo.texture.height)), Allocator.Temp))
            using (var indices = new NativeArray<ushort>(new ushort[] { 0, 1, 2, 0, 2, 3, 0, 3, 4 }, Allocator.Temp))
            {
                logo.SetVertexCount(pixels.Length);
                logo.SetVertexAttribute(VertexAttribute.Position, positions);
                logo.SetVertexAttribute(VertexAttribute.TexCoord0, uvs);
                logo.SetIndices(indices);
            }
        }
        public bool TryValidateConfiguration(out string reason)
        {
            if (boards == null || boards.Length == 0) { reason = "缺少企业／科室原板。"; return false; }
            var keys = new System.Collections.Generic.HashSet<string>();
            foreach (var board in boards)
                if (board == null || string.IsNullOrEmpty(board.visualKey) || !keys.Add(board.visualKey) ||
                    board.texture == null || board.aspect <= 0 || board.uv.width <= 0 || board.uv.height <= 0)
                { reason = "企业／科室原板映射无效：" + board?.visualKey + "，贴图=" + board?.texture +
                    "，比例=" + board?.aspect + "，UV=" + board?.uv; return false; }
            foreach (var crop in externalLogoCrops ?? Array.Empty<ExternalLogoCrop>())
                if (crop == null || string.IsNullOrEmpty(crop.visualKey) || !keys.Add(crop.visualKey) ||
                    !ValidCrop(crop.uv) || float.IsNaN(crop.clipTopLeft) || crop.clipTopLeft < 0 || crop.clipTopLeft > .5f)
                { reason = "企业原板Logo裁切映射无效：" + crop?.visualKey; return false; }
            reason = string.Empty;
            return true;
        }
        private static bool ValidCrop(Rect crop) => !float.IsNaN(crop.x) && !float.IsNaN(crop.y) &&
            crop.x >= 0 && crop.y >= 0 && crop.width > 0 && crop.height > 0 && crop.xMax <= 1 && crop.yMax <= 1;
        public static void Bind(RawImage image, AspectRatioFitter aspect, Board board)
        {
            image.texture = board?.texture;
            image.uvRect = board?.uv ?? new Rect(0, 0, 1, 1);
            image.enabled = board != null;
            if (board != null) aspect.aspectRatio = board.aspect;
        }

        public static void BindExcerpt(RawImage image, AspectRatioFitter aspect, Board board, Rect local)
        {
            Bind(image, aspect, board);
            if (board == null || local.width <= 0 || local.height <= 0) return;
            image.uvRect = new Rect(board.uv.x + local.x * board.uv.width,
                board.uv.y + (1 - local.y - local.height) * board.uv.height,
                local.width * board.uv.width, local.height * board.uv.height);
            aspect.aspectRatio = board.aspect * local.width / local.height;
        }
    }
}
