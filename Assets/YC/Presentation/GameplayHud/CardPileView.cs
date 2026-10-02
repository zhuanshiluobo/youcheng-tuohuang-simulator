using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public enum CardPileKind { Hand, Discard, Covered }

    /// <summary>按实际内容区和旋转包围盒排布动态卡牌。静态结构与样式由预制体提供。</summary>
    public sealed class CardPileView : MonoBehaviour
    {
        [SerializeField] private CardPileKind kind;
        [SerializeField] private GameplayCardSlotView slotPrefab;
        [SerializeField, Min(.01f)] private float slotScale = 1f;
        [SerializeField] private RectTransform viewport;
        [SerializeField] private RectTransform content;
        [SerializeField] private float padding = 8f;
        [SerializeField] private float edgeOverhang = 12f;
        [SerializeField] private float tiltDegrees = 12f;
        [SerializeField, Range(0f, .45f)] private float horizontalJitter = .3f;
        [SerializeField] private RectTransform protectedHeader;
        [SerializeField] private float headerGap = 4f;
        [SerializeField, Min(0f)] private float headerOverlap;
        [SerializeField] private bool protectHeaderTextOnly;
        private readonly Vector3[] headerCorners = new Vector3[4];
        private Rect lastHeaderBounds;
        private readonly List<GameplayCardSlotView> slots = new List<GameplayCardSlotView>();
        private readonly List<string> keys = new List<string>();
        private readonly List<string> regionKeys = new List<string>();
        private uint arrangementSeed;
        private Vector2 lastSize;
        private bool dirty = true;
        public GameplayCardSlotView SlotPrefab => slotPrefab;
        public float SlotScale => Mathf.Max(.01f, slotScale);
        public RectTransform Content => content;
        public bool IsConfigured => slotPrefab != null && viewport != null && content != null;

        public void RenderSprites(IReadOnlyList<string> ids, Func<string, Sprite> sprite, Action<string> click)
        {
            RenderCore(ids, sprite, null, click);
        }

        public void RenderTextures(IReadOnlyList<string> ids, Func<string, Texture> texture, Action<string> click)
        {
            RenderCore(ids, null, texture, click);
        }

        private void RenderCore(
            IReadOnlyList<string> ids,
            Func<string, Sprite> sprite,
            Func<string, Texture> texture,
            Action<string> click)
        {
            var count = ids == null ? 0 : ids.Count;
            UpdateArrangementSeed(ids, count);
            var first = kind == CardPileKind.Discard ? Mathf.Max(0, count - 5) : 0;
            var visibleCount = count - first;
            while (slots.Count > visibleCount)
            {
                var index = slots.Count - 1;
                var removed = slots[index];
                slots.RemoveAt(index);
                keys.RemoveAt(index);
                removed.gameObject.SetActive(false);
                if (UnityEngine.Application.isPlaying) Destroy(removed.gameObject);
                else DestroyImmediate(removed.gameObject);
                dirty = true;
            }
            for (var i = 0; i < visibleCount; i++)
            {
                var id = ids[first + i];
                if (i == slots.Count)
                {
                    slots.Add(Instantiate(slotPrefab, content, false));
                    keys.Add(null);
                    dirty = true;
                }
                if (keys[i] != id) dirty = true;
                keys[i] = id;
                var card = slots[i];
                card.gameObject.name = "Card Slot: " + id;
                card.gameObject.SetActive(true);
                if (sprite != null) card.Bind(sprite(id), click == null ? (Action)null : () => click(id));
                else card.BindTexture(texture == null ? null : texture(id), click == null ? (Action)null : () => click(id));
                if (kind == CardPileKind.Hand)
                    YC.PlayerJourney.PlayerAutomationId.Attach(card.gameObject, "character.hand." + id);
            }
        }

        private void UpdateArrangementSeed(IReadOnlyList<string> ids, int count)
        {
            var changed = regionKeys.Count != count;
            for (var i = 0; !changed && i < count; i++)
                changed = !string.Equals(regionKeys[i], ids[i], StringComparison.Ordinal);
            if (!changed) return;

            regionKeys.Clear();
            unchecked
            {
                var hash = (2166136261u ^ (uint)kind) * 16777619u;
                hash = (hash ^ (uint)count) * 16777619u;
                for (var i = 0; i < count; i++)
                {
                    var id = ids[i];
                    regionKeys.Add(id);
                    // 带长度的完整列表参与种子，包含弃牌区未显示的旧牌，避免拼接歧义。
                    hash = (hash ^ (id == null ? 0u : (uint)id.Length + 1u)) * 16777619u;
                    foreach (var c in id ?? string.Empty) hash = (hash ^ c) * 16777619u;
                }
                arrangementSeed = hash;
            }
            dirty = true;
        }

        private void OnEnable()
        {
            dirty = true;
            // 原生布局在提交画面前更新尺寸，随后使用最终内容区重新排牌。
            Canvas.willRenderCanvases += RefreshLayout;
        }

        private void OnDisable() => Canvas.willRenderCanvases -= RefreshLayout;
        private void OnValidate() => dirty = true;
        private void OnRectTransformDimensionsChange() => dirty = true;
        private void LateUpdate() => RefreshLayout();

        private void RefreshLayout()
        {
            if (!IsConfigured || !isActiveAndEnabled) return;
            var headerBounds = HeaderBounds();
            if (!dirty && lastSize == viewport.rect.size && lastHeaderBounds == headerBounds) return;
            lastHeaderBounds = headerBounds;
            Arrange();
        }

        private Rect HeaderBounds()
        {
            if (protectedHeader == null || !protectedHeader.gameObject.activeInHierarchy) return Rect.zero;
            var rect = protectedHeader.rect;
            var label = protectHeaderTextOnly ? protectedHeader.GetComponent<Text>() : null;
            if (label != null)
            {
                var width = Mathf.Min(rect.width, label.preferredWidth);
                var alignment = (int)label.alignment % 3;
                rect.x += (rect.width - width) * alignment * .5f;
                rect.width = width;
            }
            headerCorners[0] = new Vector3(rect.xMin, rect.yMin);
            headerCorners[1] = new Vector3(rect.xMin, rect.yMax);
            headerCorners[2] = new Vector3(rect.xMax, rect.yMax);
            headerCorners[3] = new Vector3(rect.xMax, rect.yMin);
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            foreach (var corner in headerCorners)
            {
                var local = viewport.InverseTransformPoint(protectedHeader.TransformPoint(corner));
                var point = new Vector2(local.x - viewport.rect.xMin, viewport.rect.yMax - local.y);
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        private struct Pose
        {
            public Vector2 Center;
            public float Angle;
            public int Z;
        }

        private Pose[] MakePoses(float aspect)
        {
            var poses = new Pose[slots.Count];
            for (var i = 0; i < poses.Length; i++)
            {
                // 整区种子决定每张牌的三个独立样本；不读写游戏或 Unity 的随机状态。
                var p = new Pose
                {
                    Center = new Vector2(RandomSigned(keys[i], i, 1), RandomSigned(keys[i], i, 2)),
                    Angle = RandomSigned(keys[i], i, 3) * tiltDegrees,
                    Z = i
                };
                if (kind == CardPileKind.Discard)
                {
                    p.Center = Vector2.Scale(p.Center, new Vector2(.07f * aspect, .03f));
                    p.Angle *= .5f;
                }
                else
                {
                    if (kind == CardPileKind.Covered && poses.Length == 3)
                        p.Z = i == 1 ? 2 : i == 2 ? 1 : 0;
                }
                poses[i] = p;
            }
            return poses;
        }

        private static uint StableHash(string id)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var c in id ?? string.Empty) hash = (hash ^ c) * 16777619;
                return hash;
            }
        }

        private float RandomSigned(string id, int index, uint channel)
        {
            unchecked
            {
                var hash = StableHash(id) ^ arrangementSeed ^ ((uint)index * 0x27d4eb2du) ^
                    (channel * 0x9e3779b9u) ^ ((uint)kind * 0x85ebca6bu);
                // 打散相似卡牌 ID 的相关性，避免相邻牌获得近似姿态。
                hash ^= hash >> 16;
                hash *= 0x7feb352du;
                hash ^= hash >> 15;
                hash *= 0x846ca68bu;
                hash ^= hash >> 16;
                return (hash & 0xffffffu) / 16777215f * 2f - 1f;
            }
        }

        private static Vector2 RotatedHalfSize(float aspect, float angle)
        {
            var a = angle * Mathf.Deg2Rad;
            return new Vector2(aspect * Mathf.Abs(Mathf.Cos(a)) + Mathf.Abs(Mathf.Sin(a)),
                aspect * Mathf.Abs(Mathf.Sin(a)) + Mathf.Abs(Mathf.Cos(a))) * .5f;
        }

        private void Arrange()
        {
            dirty = false;
            lastSize = viewport.rect.size;
            var width = Mathf.Max(0f, lastSize.x);
            var height = Mathf.Max(0f, lastSize.y);
            content.sizeDelta = Vector2.zero;
            content.anchoredPosition = Vector2.zero;
            if (slots.Count == 0) return;

            var source = slotPrefab.Root.sizeDelta * SlotScale;
            var aspect = source.x / source.y;
            var poses = MakePoses(aspect);
            if (kind == CardPileKind.Discard)
            {
                var offset = new Vector2(width, height) * .5f;
                for (var i = 0; i < poses.Length; i++)
                {
                    var center = poses[i].Center * source.y + offset;
                    poses[i].Center = new Vector2(Mathf.Clamp(center.x, 0f, width), Mathf.Clamp(center.y, 0f, height));
                }
            }
            else
            {
                var widestHalf = 0f;
                foreach (var pose in poses)
                    widestHalf = Mathf.Max(widestHalf, RotatedHalfSize(aspect, pose.Angle).x * source.y);
                var inset = Mathf.Min(width * .5f, Mathf.Max(padding, widestHalf - edgeOverhang));
                var span = Mathf.Max(0f, width - inset * 2f);
                var step = poses.Length > 1 ? span / (poses.Length - 1) : 0f;
                for (var i = 0; i < poses.Length; i++)
                {
                    var sample = poses[i].Center;
                    var half = RotatedHalfSize(aspect, poses[i].Angle) * source.y;
                    // 中心直接约束到当前视口，最终不再按整组包围盒平移。
                    var x = poses.Length == 1 ? Mathf.Lerp(inset, width - inset, (sample.x + 1f) * .5f) :
                        inset + i * step + sample.x * step * horizontalJitter;
                    var minX = Mathf.Min(width * .5f, Mathf.Max(0f, half.x - edgeOverhang));
                    var maxX = Mathf.Max(width * .5f, Mathf.Min(width, width + edgeOverhang - half.x));
                    x = Mathf.Clamp(x, minX, maxX);
                    var top = -Mathf.Max(0f, edgeOverhang) - Mathf.Max(0f, headerOverlap);
                    // 标题栏空白可用于散落；只有横向碰到标题文字的卡牌才需要下移。
                    if (lastHeaderBounds.width > 0f &&
                        x + half.x >= lastHeaderBounds.xMin - headerGap &&
                        x - half.x <= lastHeaderBounds.xMax + headerGap)
                        top = Mathf.Max(top, lastHeaderBounds.yMax + headerGap);
                    var minY = Mathf.Clamp(top + half.y, 0f, height);
                    var maxY = Mathf.Clamp(height + edgeOverhang - half.y, 0f, height);
                    // 空间紧张时优先避让标题，将多余部分留在下沿。
                    maxY = Mathf.Max(minY, maxY);
                    var y = Mathf.Lerp(minY, maxY, (sample.y + 1f) * .5f);
                    poses[i].Center = new Vector2(x, Mathf.Clamp(y, 0f, height));
                }
            }

            for (var i = 0; i < slots.Count; i++)
            {
                var rect = slots[i].Root;
                rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
                rect.pivot = Vector2.one * .5f;
                rect.sizeDelta = slotPrefab.Root.sizeDelta;
                rect.anchoredPosition = new Vector2(poses[i].Center.x, -poses[i].Center.y);
                rect.localRotation = Quaternion.Euler(0, 0, -poses[i].Angle);
                rect.localScale = Vector3.one * SlotScale;
            }
            var order = new List<int>();
            for (var i = 0; i < poses.Length; i++) order.Add(i);
            order.Sort((a, b) => poses[a].Z.CompareTo(poses[b].Z));
            foreach (var i in order) slots[i].Root.SetAsLastSibling();
        }
    }
}
