using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace YC.Editor
{
    public sealed class MapFeedbackVisualBuildReadinessPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => -245;

        public void OnPreprocessBuild(BuildReport report)
        {
            MapFeedbackVisualBuildReadiness.ValidateReadyForBuild();
        }
    }
}
