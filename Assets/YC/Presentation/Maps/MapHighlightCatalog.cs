using System;
using System.Collections.Generic;
using UnityEngine;

namespace YC.Presentation.Maps
{
    // 尺寸和轮廓由交付 assets.json 导入；采样倍率不参与地图显示尺寸。
    [CreateAssetMenu(menuName = "YC/Maps/Map Highlight Catalog")]
    public sealed class MapHighlightCatalog : ScriptableObject
    {
        public TextAsset SourceManifest;
        public string Version;
        public Vector2 SourceMapSize;
        public List<MapHighlightShape> Shapes = new List<MapHighlightShape>();

        public MapHighlightShape GetShape(string id)
        {
            var shape = Shapes.Find(item => item != null && item.Id == id);
            if (shape == null) throw new InvalidOperationException("地图按钮缺少轮廓配置：" + id);
            return shape;
        }
    }

    [Serializable]
    public sealed class MapHighlightShape
    {
        public string Id;
        public Vector2 OutlineSize;
        public Vector2 CanvasSize;
        public Vector2 Pivot;
        public Vector2[] PolygonTopLeft;
        public Sprite Available;
        public Sprite Selected;

        public Vector2[] GetLocalPolygon(Vector2 mapSize, Vector2 sourceSize)
        {
            var points = new Vector2[PolygonTopLeft.Length];
            for (var i = 0; i < points.Length; i++)
                points[i] = new Vector2(
                    (PolygonTopLeft[i].x - OutlineSize.x / 2f) * mapSize.x / sourceSize.x,
                    (OutlineSize.y / 2f - PolygonTopLeft[i].y) * mapSize.y / sourceSize.y);
            return points;
        }

        public Vector2 GetLocalCanvasSize(Vector2 mapSize, Vector2 sourceSize) =>
            new Vector2(CanvasSize.x * mapSize.x / sourceSize.x,
                CanvasSize.y * mapSize.y / sourceSize.y);
    }
}
