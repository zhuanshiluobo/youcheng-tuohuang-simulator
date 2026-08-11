using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace YC.Editor
{
    public sealed class FacilityCatalogBuildReadinessPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => -250;

        public void OnPreprocessBuild(BuildReport report)
        {
            FacilityCardCatalogEditorAssetBuilder.ValidateReadyForBuild();
        }
    }
}
