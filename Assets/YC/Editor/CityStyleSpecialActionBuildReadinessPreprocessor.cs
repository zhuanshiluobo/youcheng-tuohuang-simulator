using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace YC.Editor
{
    public sealed class CityStyleSpecialActionBuildReadinessPreprocessor :
        IPreprocessBuildWithReport
    {
        public int callbackOrder => -260;

        public void OnPreprocessBuild(BuildReport report)
        {
            CityStyleSpecialActionBuildReadiness.ValidateReadyForBuild();
        }
    }
}
