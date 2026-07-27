using System.Collections.Generic;

namespace YC.Presentation.Workflows
{
    public sealed class InteractionPresentation
    {
        private static readonly IReadOnlyList<WorkflowHighlight> NoHighlights =
            new List<WorkflowHighlight>().AsReadOnly();

        private static readonly InteractionPresentation EmptyPresentation =
            new InteractionPresentation(
                NoHighlights,
                string.Empty,
                InteractionMode.Hidden,
                false);

        private static readonly InteractionPresentation BusyPresentation =
            new InteractionPresentation(
                NoHighlights,
                string.Empty,
                InteractionMode.Busy,
                false);

        public InteractionPresentation(
            IReadOnlyList<WorkflowHighlight> highlights,
            string promptText,
            InteractionMode panelMode)
            : this(highlights, promptText, panelMode, true)
        {
        }

        public InteractionPresentation(
            IReadOnlyList<WorkflowHighlight> highlights,
            string promptText,
            InteractionMode panelMode,
            bool replacesHighlights)
        {
            Highlights = highlights == null
                ? NoHighlights
                : new List<WorkflowHighlight>(highlights).AsReadOnly();
            PromptText = promptText ?? string.Empty;
            PanelMode = panelMode;
            ReplacesHighlights = replacesHighlights;
        }

        public static InteractionPresentation Empty
        {
            get { return EmptyPresentation; }
        }

        public static InteractionPresentation Busy
        {
            get { return BusyPresentation; }
        }

        public IReadOnlyList<WorkflowHighlight> Highlights { get; private set; }

        public string PromptText { get; private set; }

        public InteractionMode PanelMode { get; private set; }

        public bool ReplacesHighlights { get; private set; }

        public bool IsEmpty
        {
            get
            {
                return Highlights.Count == 0 &&
                       string.IsNullOrEmpty(PromptText) &&
                       PanelMode == InteractionMode.Hidden;
            }
        }
    }
}
