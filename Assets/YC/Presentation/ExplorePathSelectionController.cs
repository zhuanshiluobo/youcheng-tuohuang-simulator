using System.Collections.Generic;
using YC.Domain.Maps;

namespace YC.Presentation
{
    public sealed class ExplorePathSelectionController
    {
        private readonly List<ExplorePathChoice> pathChoices = new List<ExplorePathChoice>();

        public string TargetLocationId { get; private set; } = string.Empty;
        public MapPath SelectedPath { get; private set; }

        public IReadOnlyList<ExplorePathChoice> PathChoices
        {
            get { return pathChoices; }
        }

        public bool HasTarget
        {
            get { return !string.IsNullOrEmpty(TargetLocationId); }
        }

        public void BeginTarget(string locationId)
        {
            TargetLocationId = locationId ?? string.Empty;
            SelectedPath = null;
            pathChoices.Clear();
        }

        public void BeginWithPath(string locationId, MapPath path)
        {
            TargetLocationId = locationId ?? string.Empty;
            SelectedPath = path;
            pathChoices.Clear();
        }

        public void SetPathChoices(IReadOnlyList<MapPath> paths, System.Func<MapPath, int, string> buildLabel)
        {
            pathChoices.Clear();
            if (paths == null)
            {
                return;
            }

            for (var i = 0; i < paths.Count; i++)
            {
                var label = buildLabel == null ? string.Empty : buildLabel(paths[i], i);
                pathChoices.Add(new ExplorePathChoice(paths[i], label));
            }
        }

        public bool TrySelectPathChoice(int choiceIndex)
        {
            if (choiceIndex < 0 || choiceIndex >= pathChoices.Count)
            {
                return false;
            }

            SelectedPath = pathChoices[choiceIndex].Path;
            return true;
        }

        public void SelectPath(MapPath path)
        {
            SelectedPath = path;
        }

        public void Clear()
        {
            TargetLocationId = string.Empty;
            SelectedPath = null;
            pathChoices.Clear();
        }
    }
}
