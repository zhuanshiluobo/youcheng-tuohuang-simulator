using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace YC.Editor
{
    public sealed class EffectDialogLayoutBuildReadinessPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => -760;

        public void OnPreprocessBuild(BuildReport report)
        {
            EffectDialogLayoutBuildReadiness.ValidateReadyForBuild();
        }
    }
}
