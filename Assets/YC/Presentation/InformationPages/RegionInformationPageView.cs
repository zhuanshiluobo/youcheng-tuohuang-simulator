using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Maps;
using YC.Domain.Scoring;
using YC.Domain.State;

namespace YC.Presentation
{
    public sealed class RegionInformationPageView : MonoBehaviour
    {
        [SerializeField] private InformationPageDismiss dismiss;
        [SerializeField] private RectTransform window;
        public RectTransform Window => window;
        [SerializeField] private Text mapSummary;
        [SerializeField] private Text emptySummary;
        [SerializeField] private InformationValueCell participantHeaderTemplate;
        [SerializeField] private RectTransform emptyHeader;
        [SerializeField] private RegionInformationRowView[] rows;
        [SerializeField] private string mapSummaryFormat = "{0} · {1} 个区域";
        [SerializeField] private string threePlayerMap = "三人地图";
        [SerializeField] private string fourPlayerMap = "四人地图";
        [SerializeField] private string emptySummaryFormat = "当前空格合计 {0}";
        [SerializeField] private string unnamedPlayerFormat = "玩家 {0}";
        [SerializeField] private string unknown = "—";
        private readonly List<InformationValueCell> headers = new List<InformationValueCell>();
        public IReadOnlyList<RegionInformationRow> Projection { get; private set; }
        public void SetClose(Action close) => dismiss.Dismiss = close;
        public bool TryValidateConfiguration(out string reason)
        {
            var valid = dismiss != null && window != null && mapSummary != null && emptySummary != null &&
                participantHeaderTemplate != null && emptyHeader != null && rows != null && rows.Length >= 8;
            if (valid) foreach (var row in rows) valid &= row != null && row.IsConfigured;
            reason = valid ? string.Empty : "区控页面引用不完整。"; return valid;
        }
        public void Render(GameState state, IMapQueryService map)
        {
            if (state == null || map == null) return;
            var players = new List<PlayerState>(state.Players);
            players.Sort((a,b) => ((int)PlayerColor(a.Color)).CompareTo((int)PlayerColor(b.Color)));
            Projection = RegionInformationQuery.Build(state, map);
            mapSummary.text = string.Format(mapSummaryFormat,
                map.Map.MapId == StaticMapDefinitions.ThreePlayerMapId ? threePlayerMap : fourPlayerMap, Projection.Count);
            while (headers.Count < players.Count)
            {
                var cell = Instantiate(participantHeaderTemplate, participantHeaderTemplate.transform.parent, false);
                cell.transform.SetSiblingIndex(emptyHeader.GetSiblingIndex()); headers.Add(cell);
            }
            participantHeaderTemplate.gameObject.SetActive(false);
            for (var i = 0; i < headers.Count; i++)
            {
                headers[i].gameObject.SetActive(i < players.Count);
                if (i < players.Count) headers[i].Render(PlayerColor(players[i].Color),
                    string.IsNullOrEmpty(players[i].Name) ? string.Format(unnamedPlayerFormat, players[i].PlayerId) : players[i].Name);
            }
            var total = 0; var complete = true;
            for (var i = 0; i < rows.Length; i++)
            {
                rows[i].gameObject.SetActive(i < Projection.Count);
                if (i >= Projection.Count) continue;
                rows[i].Render(Projection[i], players);
                if (Projection[i].EmptySlots.HasValue) total += Projection[i].EmptySlots.Value; else complete = false;
            }
            emptySummary.text = string.Format(emptySummaryFormat, complete ? total.ToString() : unknown);
        }
        public static InfluenceMarker2DColor PlayerColor(YC.Domain.Rules.PlayerColor color) =>
            color == YC.Domain.Rules.PlayerColor.Red ? InfluenceMarker2DColor.Red :
            color == YC.Domain.Rules.PlayerColor.Blue ? InfluenceMarker2DColor.Blue :
            color == YC.Domain.Rules.PlayerColor.Green ? InfluenceMarker2DColor.Green : InfluenceMarker2DColor.Yellow;
        public static InfluenceMarker2DColor RegionColor(string id)
        {
            var key = (id ?? string.Empty).Replace("region-", string.Empty).ToUpperInvariant();
            return Enum.TryParse("Region" + key, out InfluenceMarker2DColor color) ? color : InfluenceMarker2DColor.Empty;
        }
    }
}
