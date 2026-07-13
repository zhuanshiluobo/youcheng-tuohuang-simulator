using System.Collections.Generic;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    public static class CardImagePathCatalog
    {
        private const string FacilityImageRoot = "Assets/YC/Presentation/Resources/CardImages/Facilities/";
        private const string CityStyleImageRoot = "Assets/YC/Presentation/Resources/CardImages/CityStyles/";

        private static readonly Dictionary<string, string> FacilityImageFileOverrides =
            new Dictionary<string, string>
            {
                { FacilityCardDatabase.EnterpriseOffice, "enterprise_office.jpg" }
            };

        private static readonly Dictionary<string, string> CityStyleImageFiles =
            new Dictionary<string, string>
            {
                { CityStyleDatabase.MilitaryIndustrialArea, "military_industrial_area.jpg" },
                { CityStyleDatabase.MobilizationSupportSystem, "mobilization_support_system.jpg" },
                { CityStyleDatabase.CompositePowerSystem, "composite_power_system.jpg" },
                { CityStyleDatabase.MaterialRelayStation, "material_relay_station.jpg" },
                { CityStyleDatabase.SourceStoneIndustrialHub, "source_stone_industrial_hub.jpg" },
                { CityStyleDatabase.EfficientMobileManagementSystem, "efficient_mobile_management_system.jpg" }
            };

        public static bool TryGetFacilityImageRelativePath(string facilityId, out string relativePath)
        {
            FacilityCardDefinition facility;
            if (string.IsNullOrEmpty(facilityId) ||
                !FacilityCardDatabase.TryGet(facilityId, out facility))
            {
                relativePath = string.Empty;
                return false;
            }

            string imageFileName;
            if (!FacilityImageFileOverrides.TryGetValue(facilityId, out imageFileName))
            {
                imageFileName = facilityId + ".jpg";
            }

            relativePath = FacilityImageRoot + imageFileName;
            return true;
        }

        public static bool TryGetCityStyleImageRelativePath(string cityStyleId, out string relativePath)
        {
            string imageFileName;
            if (string.IsNullOrEmpty(cityStyleId) ||
                !CityStyleImageFiles.TryGetValue(cityStyleId, out imageFileName))
            {
                relativePath = string.Empty;
                return false;
            }

            relativePath = CityStyleImageRoot + imageFileName;
            return true;
        }

        public static bool TryGetCharacterFrontImageRelativePath(string cardId, out string relativePath)
        {
            return CharacterCardImagePathCatalog.TryGetFrontImageRelativePath(cardId, out relativePath);
        }

        public static bool TryGetCharacterBackImageRelativePath(PlayerColor color, out string relativePath)
        {
            return CharacterCardImagePathCatalog.TryGetBackImageRelativePath(color, out relativePath);
        }
    }
}
