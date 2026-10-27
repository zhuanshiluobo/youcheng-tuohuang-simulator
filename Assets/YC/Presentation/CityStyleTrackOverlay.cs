using System;
using System.Collections.Generic;
using UnityEngine;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>资产保存轨道及四个布局槽；零值和正式数量共用同一玩家槽。</summary>
    public sealed class CityStyleTrackOverlay : MonoBehaviour
    {
        [Serializable] private sealed class Track
        {
            public string layout, area;
            public GameObject root;
            public GameObject[] zeroBlocks;
            public RectTransform[] playerSlots;
        }
        [SerializeField] private Track[] tracks;
        private readonly List<GameObject> optionMarkers = new List<GameObject>();
        private int usedOptionMarkers;

        public Vector2 TrackOffset(string id, string area, CityStyleVisualCatalog catalog)
        {
            var layout = catalog.Find(id)?.trackLayout;
            foreach (var track in tracks)
                if (track.layout == layout && track.area == area)
                    return ((RectTransform)track.root.transform).anchoredPosition;
            return Vector2.zero;
        }

        public RectTransform FindSlot(string id, string area, int color, CityStyleVisualCatalog catalog)
        {
            var layout = catalog.Find(id)?.trackLayout;
            foreach (var track in tracks)
                if ((track.layout == layout || track.layout == "all") && track.area == area &&
                    color >= 0 && color < track.playerSlots.Length)
                    return track.playerSlots[color];
            return null;
        }

        public void RegisterOptionMarker(GameObject marker) => optionMarkers.Add(marker);
        public void BeginOptionMarkers() => usedOptionMarkers = 0;
        public GameObject ReuseOptionMarker(Func<GameObject> create)
        {
            var index = usedOptionMarkers++;
            if (index == optionMarkers.Count) optionMarkers.Add(create());
            else if (optionMarkers[index] == null) optionMarkers[index] = create();
            return optionMarkers[index];
        }
        public void EndOptionMarkers()
        {
            // 仅保留当前实际数量；移除的轨道不留下隐藏旧数量或旧输入对象。
            for (var i = optionMarkers.Count - 1; i >= usedOptionMarkers; i--)
            {
                var marker = optionMarkers[i]; optionMarkers.RemoveAt(i);
                if (marker == null) continue;
                marker.SetActive(false);
                if (UnityEngine.Application.isPlaying) Destroy(marker); else DestroyImmediate(marker);
            }
        }
        public void ClearOptionMarkers()
        {
            BeginOptionMarkers(); EndOptionMarkers();
        }
        public void Render(string id, CityStyleVisualCatalog catalog, IReadOnlyList<CityStyleMarkerViewModel> markers)
        {
            var layout = catalog.Find(id)?.trackLayout;
            foreach (var track in tracks)
            {
                track.root.SetActive(track.layout == layout || track.layout == "all");
                for (var color = 0; color < track.zeroBlocks.Length; color++)
                {
                    var hasMarker = false;
                    if (markers != null)
                        foreach (var marker in markers)
                            if (marker.CityStyleId == id && marker.MarkerArea == track.area && (int)marker.PlayerColor == color)
                            { hasMarker = true; break; }
                    track.zeroBlocks[color].SetActive(!hasMarker);
                }
            }
        }
    }
}
