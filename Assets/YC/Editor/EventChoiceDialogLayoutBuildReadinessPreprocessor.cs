using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace YC.Editor
{
    public sealed class EventChoiceDialogLayoutBuildReadinessPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1750;

        public void OnPreprocessBuild(BuildReport report)
        {
            EventChoiceDialogLayoutBuildReadiness.ValidateReadyForBuild();
        }
    }
}
