using System.Collections.Generic;
using YC.Domain.Rules;

namespace YC.Presentation.Maps
{
    public sealed class ResourceTokenIconDefinition
    {
        public ResourceTokenIconDefinition(ResourceType resourceType, string fileName)
            : this(resourceType, 0, fileName)
        {
        }

        public ResourceTokenIconDefinition(ResourceType resourceType, int amount, string fileName)
        {
            ResourceType = resourceType;
            Amount = amount;
            FileName = fileName;
        }

        public ResourceType ResourceType { get; }
        public int Amount { get; }
        public string FileName { get; }
    }

    public static class ResourceTokenIconDefinitions
    {
        public const string DirectoryName = "ResourceIcons";

        private static readonly ResourceTokenIconDefinition[] RequiredIcons =
        {
            new ResourceTokenIconDefinition(ResourceType.Originium, "源岩.png"),
            new ResourceTokenIconDefinition(ResourceType.OriginiumShard, "源石碎片.png"),
            new ResourceTokenIconDefinition(ResourceType.Iron, "异铁.png"),
            new ResourceTokenIconDefinition(ResourceType.PureOriginium, "至纯源石.png")
        };

        private static readonly ResourceTokenIconDefinition[] AmountSpecificIcons =
        {
            new ResourceTokenIconDefinition(ResourceType.Originium, 2, "源岩x2.png")
        };

        public static IReadOnlyList<ResourceTokenIconDefinition> GetRequiredIcons()
        {
            return RequiredIcons;
        }

        public static IReadOnlyList<ResourceTokenIconDefinition> GetAmountSpecificIcons()
        {
            return AmountSpecificIcons;
        }

        public static string GetFileName(ResourceType resourceType)
        {
            for (var i = 0; i < RequiredIcons.Length; i++)
            {
                var icon = RequiredIcons[i];
                if (icon.ResourceType == resourceType)
                {
                    return icon.FileName;
                }
            }

            return string.Empty;
        }

        public static string GetFileName(ResourceType resourceType, int amount)
        {
            for (var i = 0; i < AmountSpecificIcons.Length; i++)
            {
                var icon = AmountSpecificIcons[i];
                if (icon.ResourceType == resourceType && icon.Amount == amount)
                {
                    return icon.FileName;
                }
            }

            return GetFileName(resourceType);
        }

        public static bool Supports(ResourceType resourceType)
        {
            return !string.IsNullOrEmpty(GetFileName(resourceType));
        }
    }
}
