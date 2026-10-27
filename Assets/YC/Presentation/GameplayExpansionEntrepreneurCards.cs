using System;
using System.Linq;
using UnityEngine;
using YC.Domain.State;

namespace YC.Presentation
{
    public sealed partial class GameplayExpansionUi
    {
        [SerializeField] private RectTransform entrepreneurRegion;
        [SerializeField] private RectTransform[] entrepreneurSlotRoots;
        [SerializeField] private EntrepreneurUiCopy entrepreneurUiCopy;
        private CardCollectionBrowser entrepreneurBrowser;
        private CardViewer entrepreneurViewer;
        private int entrepreneurViewedPlayerId = -1;
        private object entrepreneurBinding;
        private string entrepreneurSnapshotKey;
        private Func<bool> entrepreneurCurrent;
        private string previewDiagnosticScope;
        public Action<string> ReportEntrepreneurPreviewDiagnostic { private get; set; }
        public EntrepreneurUiCopy EntrepreneurCopy => entrepreneurUiCopy;
        public string[] EntrepreneurPreviewDiagnostics { get; private set; } = Array.Empty<string>();

        // 只改变显示目标，观察者与命令权限仍由原正式投影决定。
        public void RefreshViewedEntrepreneurCards(GameStateView view, int playerId)
        {
            if (view == null || view.ViewerRole != GameStateViewerRole.Player || view.ViewerPlayerId != getPlayer?.Invoke())
            { ClearEntrepreneurPresentation(); if (entrepreneurRegion != null) entrepreneurRegion.gameObject.SetActive(false); return; }
            var target = view.Players.Any(p => p.PlayerId == playerId) ? playerId : view.ViewerPlayerId;
            if (entrepreneurViewedPlayerId != target) ClearEntrepreneurPresentation();
            entrepreneurViewedPlayerId = target;
            RefreshEntrepreneurCards(view);
        }

        private void ClearEntrepreneurPresentation()
        {
            entrepreneurBinding = null; entrepreneurCurrent = null; entrepreneurSnapshotKey = null;
            entrepreneurViewedPlayerId = -1;
            entrepreneurBrowser?.Hide();
            if (entrepreneurViewer != null) entrepreneurViewer.Dismiss();
        }

        private void RefreshEntrepreneurCards(GameStateView view)
        {
            if (entrepreneurViewedPlayerId < 0) entrepreneurViewedPlayerId = view.ViewerPlayerId;
            var own = view.Entrepreneurs?.Players.FirstOrDefault(p => p.PlayerId == entrepreneurViewedPlayerId);
            if (entrepreneurRegion == null) return;
            entrepreneurRegion.gameObject.SetActive(own != null);
            if (entrepreneurSlotRoots == null || entrepreneurSlotRoots.Length != 5) return;
            var target = entrepreneurViewedPlayerId;
            var key = view.GameId + ":" + view.ViewerPlayerId + ":" + view.ContentPackHash + ":" + view.StateRevision + ":" + target;
            if (key == entrepreneurSnapshotKey && entrepreneurCurrent?.Invoke() == true) return;
            entrepreneurBrowser?.Hide();
            if (entrepreneurViewer != null) entrepreneurViewer.Dismiss();
            var binding = new object(); entrepreneurBinding = binding; entrepreneurSnapshotKey = key;
            foreach (var root in entrepreneurSlotRoots) root.GetComponent<GameplayCardSlotView>().Bind(null, null);
            if (own == null) { entrepreneurBrowser?.Hide(); EntrepreneurPreviewDiagnostics = Array.Empty<string>(); previewDiagnosticScope = null; return; }
            var snapshotCurrent = GuardSnapshot(view);
            bool Current()
            {
                var valid = ReferenceEquals(binding, entrepreneurBinding) && entrepreneurViewedPlayerId == target && snapshotCurrent();
                if (!valid && Workflows.UiRegressionMetrics.Enabled)
                {
                    var current = VisibleView();
                    Workflows.UiRegressionMetrics.Note("entrepreneur.guard_rejected", "expected=" + key + ";actual=" +
                        current?.GameId + ":" + current?.ViewerPlayerId + ":" + current?.ContentPackHash + ":" + current?.StateRevision +
                        ";display=" + entrepreneurViewedPlayerId + ";binding=" + ReferenceEquals(binding, entrepreneurBinding));
                }
                return valid;
            }
            entrepreneurCurrent = Current;
            void Inspect(Sprite sprite, string source)
            {
                if (!Current() || sprite == null) return;
                entrepreneurBrowser?.Hide();
                if (entrepreneurViewer == null) entrepreneurViewer = hud.DialogRegistry.InstantiateCardViewer();
                entrepreneurViewer.OpenInspect(sprite, null, Current);
                Workflows.UiRegressionMetrics.Note("entrepreneur.viewer_open", key + ";source=" + source + ";art=" + sprite.name);
            }
            void Open(string group)
            {
                if (!Current()) return;
                if (group == null)
                {
                    Inspect(ExternalContentRuntime.GetDeclaredCardArtwork(own.DefinitionId), own.AbilityItemId);
                    return;
                }
                var slot = own.RoundSlots.FirstOrDefault(s => s.GroupId == group);
                if (slot == null) return;
                if (!string.IsNullOrEmpty(slot.SelectedCandidateId))
                {
                    var selected = slot.Candidates.SingleOrDefault(c => c.CandidateId == slot.SelectedCandidateId);
                    // 已选候选与实际面是公开投影的明确关联；不按相同定义猜实体ItemId。
                    if (selected == null || string.IsNullOrEmpty(slot.ActiveFaceId) || selected.FaceId != slot.ActiveFaceId) return;
                    Inspect(ExternalContentRuntime.GetDeclaredCardArtwork(selected.EntityDefinitionId, slot.ActiveFaceId), slot.SelectedCandidateId + ":" + slot.ActiveFaceId);
                    return;
                }
                if (entrepreneurBrowser == null) entrepreneurBrowser = new CardCollectionBrowser(hud.DialogRegistry, hud.Frame.ContentRect);
                var diagnostic = slot?.PreviewDiagnostic;
                var description = string.IsNullOrEmpty(diagnostic) ? entrepreneurUiCopy.BrowseHint :
                    string.Format(entrepreneurUiCopy.MissingPreviewFormat, slot.RoundNumber, diagnostic);
                var cards = slot.Candidates.OrderBy(c => c.DisplayOrder).Select(EntrepreneurCardPresentation.Candidate).ToArray();
                entrepreneurBrowser.Show(entrepreneurUiCopy.DeckTitle, description, cards, Current, null);
                Workflows.UiRegressionMetrics.Note("entrepreneur.browser_open", key + ";group=" + group + ";count=" + cards.Length);
            }
            for (int i = 0; i < entrepreneurSlotRoots.Length; i++)
                YC.PlayerJourney.PlayerAutomationId.Attach(entrepreneurSlotRoots[i].gameObject, "entrepreneur.slot." + i);
            entrepreneurSlotRoots[0].GetComponent<GameplayCardSlotView>().Bind(
                ExternalContentRuntime.GetDeclaredCardArtwork(own.DefinitionId), () => Open(null));
            var slots = own.RoundSlots.OrderBy(s => s.RoundNumber).ToArray();
            for (int i = 0; i < Math.Min(4, slots.Length); i++)
            {
                var slot = slots[i];
                var displayed = slot.Candidates.FirstOrDefault(c => c.CandidateId == slot.DisplayCandidateId);
                var sprite = displayed == null ? null : ExternalContentRuntime.GetDeclaredCardArtwork(displayed.EntityDefinitionId, displayed.FaceId);
                entrepreneurSlotRoots[i + 1].GetComponent<GameplayCardSlotView>().Bind(sprite, () => Open(slot.GroupId));
            }
            EntrepreneurPreviewDiagnostics = slots.Where(s => !string.IsNullOrEmpty(s.PreviewDiagnostic)).Select(s => s.PreviewDiagnostic).ToArray();
            var diagnosticScope = view.GameId + ":" + view.ViewerPlayerId + ":" + target + ":" + string.Join("|", EntrepreneurPreviewDiagnostics);
            if (previewDiagnosticScope != diagnosticScope && EntrepreneurPreviewDiagnostics.Length > 0)
                ReportEntrepreneurPreviewDiagnostic?.Invoke(string.Join("\n", slots.Where(s => !string.IsNullOrEmpty(s.PreviewDiagnostic))
                    .Select(s => string.Format(entrepreneurUiCopy.MissingPreviewFormat, s.RoundNumber))));
            previewDiagnosticScope = diagnosticScope;
        }
    }
}
