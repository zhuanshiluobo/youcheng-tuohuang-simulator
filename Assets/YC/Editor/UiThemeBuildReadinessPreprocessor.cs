using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace YC.Editor
{
    public sealed class UiThemeBuildReadinessPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => -2000;

        public void OnPreprocessBuild(BuildReport report)
        {
            UiThemeBuildReadiness.InitializeRequiredTheme();
            UiThemeBuildReadiness.ValidateReadyForBuild();
        }
    }
}
