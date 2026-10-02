using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.State;
using YC.Domain.Scoring;

namespace YC.Presentation
{
    public sealed class RegionInformationRowView : MonoBehaviour
    {
        [SerializeField] private InformationValueCell region;
        [SerializeField] private Text points;
        [SerializeField] private InformationValueCell participantTemplate;
        [SerializeField] private InformationValueCell empty;
        [SerializeField] private string pointsFormat = "{0}";
        [SerializeField] private string regionLabelFormat = "{0}区";
        [SerializeField] private string unknown = "—";
        private readonly List<InformationValueCell> participants = new List<InformationValueCell>();
        public bool IsConfigured => region != null && region.IsConfigured && points != null &&
            participantTemplate != null && participantTemplate.IsConfigured && empty != null && empty.IsConfigured;
        public void Render(RegionInformationRow row, IReadOnlyList<PlayerState> players)
        {
            region.Render(RegionInformationPageView.RegionColor(row.RegionId),
                string.IsNullOrEmpty(regionLabelFormat) ? row.Label : string.Format(regionLabelFormat, row.RegionId));
            points.text = string.Format(pointsFormat, row.Points);
            while (participants.Count < players.Count)
            {
                var cell = Instantiate(participantTemplate, participantTemplate.transform.parent, false);
                cell.transform.SetSiblingIndex(empty.transform.GetSiblingIndex()); participants.Add(cell);
            }
            participantTemplate.gameObject.SetActive(false);
            for (var i = 0; i < participants.Count; i++)
            {
                participants[i].gameObject.SetActive(i < players.Count);
                if (i >= players.Count) continue;
                row.Influence.TryGetValue(players[i].PlayerId, out var count);
                participants[i].Render(RegionInformationPageView.PlayerColor(players[i].Color), count.ToString());
            }
            empty.Render(InfluenceMarker2DColor.Empty, row.EmptySlots?.ToString() ?? unknown);
        }
    }
}
