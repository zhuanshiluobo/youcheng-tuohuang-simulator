using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace YC.Editor
{
    public sealed class NetworkRuntimeBuildReadinessPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => -450;

        public void OnPreprocessBuild(BuildReport report)
        {
            NetworkRuntimeBuildReadiness.ValidateReadyForBuild();
        }
    }
}
