using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using YC.Domain.Maps;
using YC.Presentation.Maps;

namespace YC.Editor
{
    public sealed class MapBuildReadinessPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            var errors = Validate();
            if (errors.Count > 0)
            {
                throw new BuildFailedException("Map build readiness check failed:\n" + string.Join("\n", errors));
            }
        }

        public static IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            ValidateResourceIcons(errors);
            ValidateFourPlayerMapDisplayLayout(errors);
            return errors;
        }

        private static void ValidateResourceIcons(List<string> errors)
        {
            var iconDirectory = Path.Combine(UnityEngine.Application.streamingAssetsPath, ResourceTokenIconDefinitions.DirectoryName);
            var requiredIcons = ResourceTokenIconDefinitions.GetRequiredIcons();

            for (var i = 0; i < requiredIcons.Count; i++)
            {
                var icon = requiredIcons[i];
                var mappedFileName = ResourceTokenIconDefinitions.GetFileName(icon.ResourceType);
                if (!string.Equals(mappedFileName, icon.FileName, System.StringComparison.Ordinal))
                {
                    errors.Add(
                        "Resource icon mapping for " + icon.ResourceType +
                        " points to " + mappedFileName +
                        " but expected " + icon.FileName + ".");
                }

                var path = Path.Combine(iconDirectory, icon.FileName);
                if (!File.Exists(path))
                {
                    errors.Add("Missing StreamingAssets resource icon: " + path);
                }
            }

            var amountSpecificIcons = ResourceTokenIconDefinitions.GetAmountSpecificIcons();
            for (var i = 0; i < amountSpecificIcons.Count; i++)
            {
                var icon = amountSpecificIcons[i];
                var mappedFileName = ResourceTokenIconDefinitions.GetFileName(icon.ResourceType, icon.Amount);
                if (!string.Equals(mappedFileName, icon.FileName, System.StringComparison.Ordinal))
                {
                    errors.Add(
                        "Resource icon mapping for " + icon.ResourceType +
                        " amount " + icon.Amount +
                        " points to " + mappedFileName +
                        " but expected " + icon.FileName + ".");
                }

                var path = Path.Combine(iconDirectory, icon.FileName);
                if (!File.Exists(path))
                {
                    errors.Add("Missing StreamingAssets amount-specific resource icon: " + path);
                }
            }
        }

        private static void ValidateFourPlayerMapDisplayLayout(List<string> errors)
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var layout = MapDisplayLayoutCatalog.Load(map.MapId);
            errors.AddRange(MapDisplayLayoutValidator.Validate(map, layout));
            if (layout == null)
            {
                return;
            }

            var locationIds = new HashSet<string>(map.Locations.Select(location => location.LocationId));
            var definitionIds = new HashSet<string>(
                layout.Locations
                    .Where(definition => definition != null)
                    .Select(definition => definition.LocationId));
            if (!locationIds.SetEquals(definitionIds))
            {
                errors.Add("Four-player map display layout does not exactly match map locations.");
            }
        }
    }
}
