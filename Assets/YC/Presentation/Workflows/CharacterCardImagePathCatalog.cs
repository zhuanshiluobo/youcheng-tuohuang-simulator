using System.Collections.Generic;
using YC.Domain.Cards;
using YC.Domain.Rules;

namespace YC.Presentation.Workflows
{
    public static class CharacterCardImagePathCatalog
    {
        private const string CharacterImageRoot = "Assets/YC/Presentation/Resources/CardImages/Characters/";

        private static readonly Dictionary<string, string> CharacterImageFiles =
            new Dictionary<string, string>
            {
                { CharacterCardDatabase.Liskarm, "liskarm.jpg" },
                { CharacterCardDatabase.Texas, "texas.jpg" },
                { CharacterCardDatabase.TinMan, "tin-man.jpg" },
                { CharacterCardDatabase.Cannot, "cannot.jpg" },
                { CharacterCardDatabase.Elysium, "elysium.jpg" }
            };

        public static bool TryGetFrontImageRelativePath(string cardId, out string relativePath)
        {
            var definition = CharacterCardDatabase.Get(cardId);
            string imageFileName;
            if (definition == null ||
                string.IsNullOrEmpty(definition.TemplateId) ||
                !CharacterImageFiles.TryGetValue(definition.TemplateId, out imageFileName))
            {
                relativePath = string.Empty;
                return false;
            }

            relativePath = CharacterImageRoot + imageFileName;
            return true;
        }

        public static bool TryGetBackImageRelativePath(PlayerColor color, out string relativePath)
        {
            string imageFileName;
            switch (color)
            {
                case PlayerColor.Red: imageFileName = "back-red.jpg"; break;
                case PlayerColor.Yellow: imageFileName = "back-yellow.jpg"; break;
                case PlayerColor.Green: imageFileName = "back-green.jpg"; break;
                case PlayerColor.Blue: imageFileName = "back-blue.jpg"; break;
                default:
                    relativePath = string.Empty;
                    return false;
            }

            relativePath = CharacterImageRoot + imageFileName;
            return true;
        }
    }
}
