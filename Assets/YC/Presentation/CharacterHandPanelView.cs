using UnityEngine;

namespace YC.Presentation
{
    public sealed class CharacterHandPanelView : MonoBehaviour
    {
        [SerializeField] private CharacterHandPanel controller;
        [SerializeField] private RectTransform root;
        [SerializeField] private CardPileView handPile;
        public CharacterHandPanel Controller => controller;
        public RectTransform Root => root;
        public CardPileView HandPile => handPile;
        public RectTransform HandCardsRoot => handPile.Content;
        public bool IsBoundTo(CharacterHandPanel candidate) => candidate != null && controller == candidate;
        public bool TryValidateConfiguration(out string reason)
        {
            if (controller == null || root == null || handPile == null || !handPile.IsConfigured)
            {
                reason = "手牌面板或卡牌散摊容器引用不完整。";
                return false;
            }
            reason = string.Empty;
            return true;
        }
    }
}
