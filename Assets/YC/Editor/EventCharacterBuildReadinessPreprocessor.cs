using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace YC.Editor
{
    public sealed class EventCharacterBuildReadinessPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => -265;

        public void OnPreprocessBuild(BuildReport report)
        {
            EventCharacterBuildReadiness.ValidateReadyForBuild();
        }
    }
}
