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
        [SerializeField] private GameBoxEventListView eventListPrefab;
        public GameBoxEventListView EventListPrefab => eventListPrefab;
        [SerializeField] private InformationPileView enterprisePile;
        [SerializeField] private InfluenceMarker2DView[] playerMarkers;
        [SerializeField] private EnterpriseBoardCatalog enterprises;
        [SerializeField] private GameBoxArtworkCatalog artwork;
        [SerializeField] private ScrollRect tokenScroll;
        [SerializeField] private InformationTokenRows tokenRows;
        [SerializeField] private GameBoxTokenItemView tokenTemplate;
        [SerializeField] private GameObject tokensEmpty;
        [SerializeField] private float minimumThumbHeight = 36;
        [SerializeField] private string characterListTitle = "游戏盒 · 角色牌 {0}";
        [SerializeField] private string eventListTitle = "游戏盒 · 事件牌 {0}";
        [SerializeField] private string enterpriseListTitle;
        [SerializeField] private string inspectHint = "单击、长按或右键查看卡牌，点击空白返回游戏盒";
        [SerializeField] private string characterInspectHint;
        private readonly List<GameBoxTokenItemView> tokens = new List<GameBoxTokenItemView>();
        private readonly Dictionary<Texture2D,Sprite> backs = new Dictionary<Texture2D,Sprite>();
        private readonly Dictionary<string,Sprite> boardImages = new Dictionary<string,Sprite>();
        private readonly List<GameBoxCardState> characterCards = new List<GameBoxCardState>(), eventCards = new List<GameBoxCardState>();
        private readonly List<GameBoxCardState> enterpriseCards = new List<GameBoxCardState>();
        private readonly List<Sprite> characterImages = new List<Sprite>(), eventImages = new List<Sprite>();
        private readonly List<Sprite> enterpriseImages = new List<Sprite>();
        private readonly List<Texture2D> characterBackImages = new List<Texture2D>();
        private readonly HashSet<string> reportedMissingBacks = new HashSet<string>();
        public InformationPileView CharacterPile => characterPile;
        public InformationPileView EventPile => eventPile;
        public InformationPileView EnterprisePile => enterprisePile;
        public ScrollRect TokenScroll => tokenScroll;
        public IReadOnlyList<GameBoxCardState> Cards(GameBoxCardKind kind) => kind == GameBoxCardKind.Character ? characterCards :
            kind == GameBoxCardKind.Event ? eventCards : enterpriseCards;
        public IReadOnlyList<Sprite> Images(GameBoxCardKind kind) => kind == GameBoxCardKind.Character ? characterImages :
            kind == GameBoxCardKind.Event ? eventImages : enterpriseImages;
        public string ListTitleFormat(GameBoxCardKind kind) => kind == GameBoxCardKind.Character ? characterListTitle :
            kind == GameBoxCardKind.Event ? eventListTitle : enterpriseListTitle;
        public string ListTitle(GameBoxCardKind kind) => string.Format(ListTitleFormat(kind), Cards(kind).Count);
        public string InspectHint => inspectHint;
        public string InspectHintFor(GameBoxCardKind kind) => kind == GameBoxCardKind.Character ? characterInspectHint : inspectHint;
        public IReadOnlyList<Texture2D> CharacterBackImages => characterBackImages;
        public void Configure(Action close, Action<GameBoxCardKind> inspect)
        {
            dismiss.Dismiss = close;
            characterPile.InspectButton.onClick.AddListener(() => inspect(GameBoxCardKind.Character));
            eventPile.InspectButton.onClick.AddListener(() => inspect(GameBoxCardKind.Event));
            enterprisePile.InspectButton.onClick.AddListener(() => inspect(GameBoxCardKind.EnterpriseCooperation));
        }
        public bool TryValidateConfiguration(out string reason)
        {
            if (eventListPrefab == null || !eventListPrefab.IsConfigured)
            { reason = "游戏盒事件专用列表引用不完整。"; return false; }
            var references = new UnityEngine.Object[] { dismiss, window, characterPile, eventPile, enterprisePile, enterprises, artwork,
                tokenScroll, tokenRows, tokenTemplate, tokensEmpty };
            var names = new[] { "关闭层", "窗口布局", "角色牌堆", "事件牌堆", "企业合作板堆", "企业板图集", "组件图集",
                "Token 滚动", "Token 行布局", "Token 模板", "Token 模板空状态" };
            for (var i = 0; i < references.Length; i++)
                if (references[i] == null) { reason = "游戏盒页面缺少引用：" + names[i]; return false; }
            if (!characterPile.IsConfigured || !eventPile.IsConfigured || !enterprisePile.IsConfigured)
            { reason = "游戏盒牌堆引用不完整。角色：" + characterPile.ConfigurationReason + "；事件：" + eventPile.ConfigurationReason +
                "；企业：" + enterprisePile.ConfigurationReason; return false; }
            if (playerMarkers?.Length != 4)
            { reason = "游戏盒四种玩家标记引用不完整。"; return false; }
            if (tokenScroll.viewport == null || tokenScroll.content == null || tokenScroll.verticalScrollbar == null)
            { reason = "游戏盒 Token 视口、内容或滚动条引用不完整。"; return false; }
            if (!tokenRows.IsConfigured)
            { reason = "游戏盒 Token 原生行列布局引用不完整。"; return false; }
            reason = string.Empty; return true;
        }
        public void Render(GameState state, int viewerId, CardVisualCatalog catalog)
            => RenderVisible(state == null ? null : GameStateViewProjector.ProjectForPlayer(state, viewerId), catalog);

        public void RenderVisible(GameStateView view, CardVisualCatalog catalog)
        {
            var box = view?.GameBox ?? new GameBoxState();
            characterCards.Clear(); eventCards.Clear(); characterImages.Clear(); eventImages.Clear();
            characterBackImages.Clear();
            enterpriseCards.Clear(); enterpriseImages.Clear();
            foreach (var card in box.Cards)
            {
                if (card == null) continue;
                var image = ResolveCard(card,catalog);
                if (card.Kind == GameBoxCardKind.Character)
                {
                    characterCards.Add(card); characterImages.Add(image);
                    characterBackImages.Add(ResolveCharacterBack(card));
                }
                else if (card.Kind == GameBoxCardKind.Event) { eventCards.Add(card); eventImages.Add(image); }
                else if (card.Kind == GameBoxCardKind.EnterpriseCooperation) { enterpriseCards.Add(card); enterpriseImages.Add(image); }
            }
            // 旧非实体存档只保留原个人企业展示，实体模式完全使用公开 Cards。
            if (view != null && view.ItemZones.Zones.Count == 0)
            {
                var own = box.Enterprises.FindLast(board => board != null && board.OwnerPlayerId == view.ViewerPlayerId);
                if (own != null)
                {
                    var legacy = new GameBoxCardState { InstanceId = "legacy-enterprise-" + own.OwnerPlayerId,
                        CardId = own.VisualKey, Kind = GameBoxCardKind.EnterpriseCooperation, FaceUp = true };
                    enterpriseCards.Add(legacy); enterpriseImages.Add(ResolveCard(legacy, catalog));
                }
            }
            characterPile.RenderCards(characterCards, characterImages); eventPile.RenderCards(eventCards, eventImages);
            enterprisePile.RenderCards(enterpriseCards, enterpriseImages);
            var colors = new[] { PlayerColor.Green,PlayerColor.Yellow,PlayerColor.Blue,PlayerColor.Red };
            for (var i=0;i<4;i++)
            {
                var count=0;foreach(var marker in box.Markers) if(marker!=null && marker.Color==colors[i]) count+=Mathf.Max(0,marker.Count);
                playerMarkers[i].RenderPlayer(colors[i],count);
            }
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
            // 游戏盒集合已按观察者授权；物理朝向不影响已知身份的正面查看。
            var knownFace = !string.IsNullOrEmpty(card.CardId);
            if (card.Kind == GameBoxCardKind.EnterpriseCooperation)
            {
                if (!knownFace) return artwork.Find(card.BackVisualKey);
                var official = ExternalContentRuntime.GetArtworkSprite("enterprise", card.CardId);
                if (official != null) return official;
                if (boardImages.TryGetValue(card.CardId, out var cached)) return cached;
                var board = enterprises.Find(card.CardId);
                if (board?.texture == null) return null;
                var boardTexture = board.texture; var uv = board.uv;
                var boardSprite = Sprite.Create(boardTexture, new Rect(uv.x * boardTexture.width, uv.y * boardTexture.height,
                    uv.width * boardTexture.width, uv.height * boardTexture.height), new Vector2(.5f, .5f));
                boardImages.Add(card.CardId, boardSprite); return boardSprite;
            }
            if(knownFace) return card.Kind==GameBoxCardKind.Character?catalog.GetCharacterFront(card.CardId):catalog.GetEvent(card.CardId);
            if(card.Kind==GameBoxCardKind.Event)return artwork.Find(card.BackVisualKey);
            var texture=ResolveCharacterBack(card);if(texture==null)return null;
            if(!backs.TryGetValue(texture,out var sprite))
            {sprite=Sprite.Create(texture,new Rect(0,0,texture.width,texture.height),new Vector2(.5f,.5f));backs[texture]=sprite;}
            return sprite;
        }
        private Texture2D ResolveCharacterBack(GameBoxCardState card)
        {
            var texture = string.IsNullOrEmpty(card.BackVisualKey) ? null :
                ExternalContentRuntime.GetSharedArtwork(card.BackVisualKey);
            if (texture == null && reportedMissingBacks.Add(card.InstanceId + ":" + card.CardId + ":" + card.BackVisualKey))
                Debug.LogWarning("[GameBoxPageView] 游戏盒角色缺少正式背面：InstanceId=" + card.InstanceId +
                    "，CardId=" + card.CardId + "，BackVisualKey=" + card.BackVisualKey, this);
            return texture;
        }
        private void LateUpdate()
        {
            var bar=tokenScroll.verticalScrollbar;
            if(bar!=null && bar.gameObject.activeInHierarchy && ((RectTransform)bar.transform).rect.height>0)
                bar.size=Mathf.Max(bar.size,minimumThumbHeight/((RectTransform)bar.transform).rect.height);
        }
        private void OnDestroy()
        {
            foreach(var sprite in backs.Values)if(sprite!=null)Destroy(sprite);
            foreach(var sprite in boardImages.Values)if(sprite!=null)Destroy(sprite);
        }
    }
}
