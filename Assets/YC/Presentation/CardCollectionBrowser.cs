using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YC.Domain.Interactions;

namespace YC.Presentation
{
    /// <summary>复用原滚动卡牌页的只读列表；没有Host提交入口。</summary>
    internal sealed class CardCollectionBrowser
    {
        private readonly GameplayDialogRegistry registry;
        private readonly RectTransform parent;
        private readonly EffectDialogShell shell;
        private object binding;
        internal bool IsShowing => shell.IsShowing;
        internal CardCollectionBrowser(GameplayDialogRegistry registry, RectTransform parent)
        { this.registry = registry; this.parent = parent; shell = new EffectDialogShell(registry); }
        internal void Show(string title, string description, IReadOnlyList<EntrepreneurCardPresentation> cards,
            Func<bool> current, Action close, string closeLabel = null, bool backgroundClose = true, bool allowClose = true)
        {
            var shownBinding = new object(); binding = shownBinding;
            var copy = registry.EffectDialogLayoutProfile;
            var panel = shell.PreparePage(parent, "Read Only Card Collection", "Read Only Card Collection",
                copy.SelectionPanelSize, copy.CharacterPanelPosition, true, false, true, true);
            EffectDialogShell.AddHeading(panel, title, description);
            shell.AddSelection(panel, new EffectDialogSelectionSpec {
                ReadOnly = true, IsEffectPage = false, CloseOnBackgroundClick = backgroundClose,
                Request = new InteractionRequestProjection { InteractionId = "read-only-collection", VisibleToViewer = true,
                    CandidateIds = cards.Select(c => c.Id).ToList(), MinSelections = 0, MaxSelections = 0, AllowDecline = true, Status = "open" },
                Label = id => cards.First(c => c.Id == id).Label, CardSprite = id => cards.First(c => c.Id == id).Artwork,
                IsCurrent = () => ReferenceEquals(binding, shownBinding) && (current == null || current()),
                CanSelect = _ => false, CancelLabel = closeLabel ?? copy.ReadOnlyCloseLabel,
                Cancel = allowClose ? (Action)(() => { if (!ReferenceEquals(binding, shownBinding)) return; Hide(); close?.Invoke(); }) : null, CardPickerHint = description
            });
        }
        internal void Hide() { binding = null; shell.Hide(); }
    }
}
