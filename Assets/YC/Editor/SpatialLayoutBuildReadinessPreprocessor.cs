using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace YC.Editor
{
    public sealed class SpatialLayoutBuildReadinessPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => -750;

        public void OnPreprocessBuild(BuildReport report)
        {
            try
            {
                SpatialLayoutBuildReadiness.ValidateReadyForBuild();
            }
            catch (System.Exception exception)
            {
                throw new BuildFailedException(
                    "空间布局资产未达到构建条件：" + exception.Message);
            }
        }
    }
}
