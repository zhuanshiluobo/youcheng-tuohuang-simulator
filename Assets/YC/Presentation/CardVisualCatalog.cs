using System;
using UnityEngine;
using YC.Domain.Cards;
using YC.Domain.Rules;

namespace YC.Presentation
{
    /// <summary>保留界面资产的卡图入口；图片定义与缓存统一由外部内容包持有。</summary>
    [CreateAssetMenu(fileName = "CardVisualCatalog", menuName = "YC/Presentation/Card Visual Catalog")]
    public sealed class CardVisualCatalog : ScriptableObject
    {
        public bool TryValidateConfiguration(out string reason)
        {
            try
            {
                // 内容包加载时已核验定义和图片文件；这里不提前解码所有卡图。
                var pack = ExternalContentRuntime.Pack;
                if (pack.FindSharedArtwork("city_board") == null)
                    throw new InvalidOperationException("外部内容包缺少城市板图片。");
                foreach (PlayerColor color in Enum.GetValues(typeof(PlayerColor)))
                    if (pack.FindSharedArtwork("character_back_" + (int)color) == null)
                        throw new InvalidOperationException("外部内容包缺少角色卡背：" + color);
                reason = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                reason = exception.Message;
                return false;
            }
        }

        public Texture2D GetFacility(string facilityId) =>
            ExternalContentRuntime.GetArtwork("facility", facilityId);

        public Texture2D GetCityStyle(string cityStyleId) =>
            ExternalContentRuntime.GetArtwork("city_style", cityStyleId);

        public Texture2D GetCharacterFront(string cardId)
        {
            var texture = ExternalContentRuntime.GetArtwork("character", cardId);
            if (texture != null) return texture;
            var definition = CharacterCardDatabase.Get(cardId);
            return definition == null ? null :
                ExternalContentRuntime.GetArtwork("character", definition.TemplateId);
        }

        public Texture2D GetCharacterBack(PlayerColor color) =>
            ExternalContentRuntime.GetSharedArtwork("character_back_" + (int)color);

        public Texture2D GetEvent(string cardId) =>
            ExternalContentRuntime.GetArtwork("event", cardId);

        public Texture2D GetCityBoard() => ExternalContentRuntime.GetSharedArtwork("city_board");
    }
}
