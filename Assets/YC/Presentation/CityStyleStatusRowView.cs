using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>主界面只投影状态数量；行、标签、基础四色标记和布局均来自资产。</summary>
    public sealed class CityStyleStatusRowView : MonoBehaviour
    {
        [Serializable] private sealed class Slot
        {
            public string area;
            public Image[] blocks;
            public Text[] counts;
        }
        [SerializeField] private string trackLayout;
        [SerializeField] private Text nameText;
        [SerializeField] private Slot[] slots;
        public string TrackLayout => trackLayout;
        public string Id { get; private set; }
        public bool HasNameText => nameText != null;
        public void Render(string id, string label, GameState state)
        {
            Id = id;
            nameText.text = label;
            var markers = new List<CityStyleMarkerViewModel>();
            if (state?.Players != null)
                foreach (var player in state.Players)
                    if (player?.DeclaredCityStyles != null)
                        foreach (var declaration in player.DeclaredCityStyles)
                            if (declaration != null && declaration.CityStyleId == id)
                                markers.Add(new CityStyleMarkerViewModel(id, player.PlayerId, player.Color,
                                    CityStyleInteraction.DisplayMarkerArea(player, declaration)));
            RenderMarkers(markers);
        }
        public void RenderMarkers(IReadOnlyList<CityStyleMarkerViewModel> markers)
        {
            foreach (var slot in slots)
            {
                var totals = new int[4];
                foreach (var marker in markers)
                    if (marker.CityStyleId == Id && marker.MarkerArea == slot.area)
                        totals[(int)marker.PlayerColor]++;
                for (var color = 0; color < 4; color++)
                {
                    var square = slot.blocks[color].GetComponent<InfluenceMarker2DView>();
                    if (square != null)
                    {
                        slot.blocks[color].gameObject.SetActive(true);
                        square.SetCount(totals[color], totals[color] > 0 ? 1f : 0f);
                        continue;
                    }
                    slot.counts[color].text = totals[color].ToString();
                    slot.blocks[color].gameObject.SetActive(true);
                    // 零值完全透明，但保留四个布局位置，避免其他玩家随显隐移位。
                    var tint = slot.blocks[color].color;
                    tint.a = totals[color] > 0 ? 1f : 0f;
                    slot.blocks[color].color = tint;
                    var textColor = slot.counts[color].color;
                    textColor.a = tint.a;
                    slot.counts[color].color = textColor;
                }
            }
        }
    }
}
