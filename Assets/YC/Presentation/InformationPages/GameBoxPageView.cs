using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.State;
using YC.Domain.Rules;

namespace YC.Presentation
{
    public sealed class GameBoxPageView : MonoBehaviour
    {
        [SerializeField] private InformationPageDismiss dismiss;
        [SerializeField] private RectTransform window;
        public RectTransform Window => window;
        [SerializeField] private InformationPileView characterPile;
        [SerializeField] private InformationPileView eventPile;
        [SerializeField] private InfluenceMarker2DView[] playerMarkers;
        [SerializeField] private RawImage enterpriseImage;
        [SerializeField] private AspectRatioFitter enterpriseAspect;
        [SerializeField] private Text enterpriseCount;
        [SerializeField] private GameObject enterpriseEmpty;
        [SerializeField] private GameObject enterpriseReturned;
        [SerializeField] private EnterpriseBoardCatalog enterprises;
        [SerializeField] private GameBoxArtworkCatalog artwork;
        [SerializeField] private ScrollRect tokenScroll;
        [SerializeField] private InformationTokenRows tokenRows;
        [SerializeField] private GameBoxTokenItemView tokenTemplate;
        [SerializeField] private GameObject tokensEmpty;
        [SerializeField] private float minimumThumbHeight = 36;
        [SerializeField] private string characterListTitle = "游戏盒 · 角色牌 {0}";
        [SerializeField] private string eventListTitle = "游戏盒 · 事件牌 {0}";
        [SerializeField] private string inspectHint = "双击或右键查看卡牌，点击空白返回游戏盒";
        private readonly List<GameBoxTokenItemView> tokens = new List<GameBoxTokenItemView>();
        private readonly Dictionary<Texture2D,Sprite> backs = new Dictionary<Texture2D,Sprite>();
        private readonly List<GameBoxCardState> characterCards = new List<GameBoxCardState>(), eventCards = new List<GameBoxCardState>();
        private readonly List<Sprite> characterImages = new List<Sprite>(), eventImages = new List<Sprite>();
        public InformationPileView CharacterPile => characterPile;
        public InformationPileView EventPile => eventPile;
        public ScrollRect TokenScroll => tokenScroll;
        public IReadOnlyList<GameBoxCardState> Cards(GameBoxCardKind kind) => kind == GameBoxCardKind.Character ? characterCards : eventCards;
        public IReadOnlyList<Sprite> Images(GameBoxCardKind kind) => kind == GameBoxCardKind.Character ? characterImages : eventImages;
        public string ListTitle(GameBoxCardKind kind) => string.Format(kind == GameBoxCardKind.Character ? characterListTitle : eventListTitle, Cards(kind).Count);
        public string InspectHint => inspectHint;
        public void Configure(Action close, Action<GameBoxCardKind> inspect)
        {
            dismiss.Dismiss = close;
            characterPile.InspectButton.onClick.AddListener(() => inspect(GameBoxCardKind.Character));
            eventPile.InspectButton.onClick.AddListener(() => inspect(GameBoxCardKind.Event));
        }
        public bool TryValidateConfiguration(out string reason)
        {
            var references = new UnityEngine.Object[] { dismiss, window, characterPile, eventPile, enterpriseImage,
                enterpriseAspect, enterpriseCount, enterpriseEmpty, enterpriseReturned, enterprises, artwork,
                tokenScroll, tokenRows, tokenTemplate, tokensEmpty };
            var names = new[] { "关闭层", "窗口布局", "角色牌堆", "事件牌堆", "企业板图像", "企业板比例", "企业板数量",
                "企业板空状态", "企业板返回状态", "企业板图集", "组件图集", "Token 滚动", "Token 行布局", "Token 模板", "Token 空状态" };
            for (var i = 0; i < references.Length; i++)
                if (references[i] == null) { reason = "游戏盒页面缺少引用：" + names[i]; return false; }
            if (!characterPile.IsConfigured || !eventPile.IsConfigured)
            { reason = "游戏盒牌堆引用不完整。角色：" + characterPile.ConfigurationReason + "；事件：" + eventPile.ConfigurationReason; return false; }
            if (playerMarkers?.Length != 4)
            { reason = "游戏盒四种玩家标记引用不完整。"; return false; }
            if (tokenScroll.viewport == null || tokenScroll.content == null || tokenScroll.verticalScrollbar == null)
            { reason = "游戏盒 Token 视口、内容或滚动条引用不完整。"; return false; }
            if (!tokenRows.IsConfigured)
            { reason = "游戏盒 Token 原生行列布局引用不完整。"; return false; }
            reason = string.Empty; return true;
        }
        public void Render(GameState state, int viewerId, CardVisualCatalog catalog)
        {
            var box = state?.GameBox ?? new GameBoxState();
            characterCards.Clear(); eventCards.Clear(); characterImages.Clear(); eventImages.Clear();
            foreach (var card in box.Cards)
            {
                if (card == null) continue;
                var image = ResolveCard(card,catalog);
                if (card.Kind == GameBoxCardKind.Character) { characterCards.Add(card); characterImages.Add(image); }
                else { eventCards.Add(card); eventImages.Add(image); }
            }
            characterPile.Render(characterImages); eventPile.Render(eventImages);
            var colors = new[] { PlayerColor.Green,PlayerColor.Yellow,PlayerColor.Blue,PlayerColor.Red };
            for (var i=0;i<4;i++)
            {
                var count=0;foreach(var marker in box.Markers) if(marker!=null && marker.Color==colors[i]) count+=Mathf.Max(0,marker.Count);
                playerMarkers[i].RenderPlayer(colors[i],count);
            }
            var own = box.Enterprises.FindLast(b => b != null && b.OwnerPlayerId == viewerId);
            var board=own==null?null:enterprises.Find(own.VisualKey);
            EnterpriseBoardCatalog.Bind(enterpriseImage,enterpriseAspect,board);
            enterpriseCount.text = own==null ? "0" : "1";
            enterpriseEmpty.SetActive(own==null); enterpriseReturned.SetActive(own!=null);
            var offset=tokenScroll.verticalNormalizedPosition;
            var data=box.Tokens.FindAll(t=>t!=null && t.Count>0);
            while(tokens.Count<data.Count) tokens.Add(Instantiate(tokenTemplate,tokenScroll.content,false));
            tokenTemplate.gameObject.SetActive(false);
            for(var i=0;i<tokens.Count;i++)
            {
                tokens[i].gameObject.SetActive(i<data.Count);if(i>=data.Count)continue;
                var sprite=artwork.Find(data[i].VisualKey);
                tokens[i].Render(sprite,data[i].Count);
            }
            tokenRows.SetItems(tokens);
            tokensEmpty.SetActive(data.Count==0);
            Canvas.ForceUpdateCanvases(); tokenScroll.verticalNormalizedPosition=offset;
        }
        private Sprite ResolveCard(GameBoxCardState card, CardVisualCatalog catalog)
        {
            if(card.FaceUp) return card.Kind==GameBoxCardKind.Character?catalog.GetCharacterFront(card.CardId):catalog.GetEvent(card.CardId);
            if(card.Kind==GameBoxCardKind.Event)return artwork.Find(card.BackVisualKey);
            var texture=catalog.GetCharacterBack(card.BackColor);if(texture==null)return null;
            if(!backs.TryGetValue(texture,out var sprite))
            {sprite=Sprite.Create(texture,new Rect(0,0,texture.width,texture.height),new Vector2(.5f,.5f));backs[texture]=sprite;}
            return sprite;
        }
        private void LateUpdate()
        {
            var bar=tokenScroll.verticalScrollbar;
            if(bar!=null && bar.gameObject.activeInHierarchy && ((RectTransform)bar.transform).rect.height>0)
                bar.size=Mathf.Max(bar.size,minimumThumbHeight/((RectTransform)bar.transform).rect.height);
        }
        private void OnDestroy(){foreach(var sprite in backs.Values)if(sprite!=null)Destroy(sprite);}
    }
}
