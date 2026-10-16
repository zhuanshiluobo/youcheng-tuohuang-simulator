using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Domain.Interactions;

namespace YC.Presentation
{
    /// <summary>信息页只读绑定；不发命令、不触发行动结束、不修改规则状态。</summary>
    public sealed class GameplayInformationPages : MonoBehaviour
    {
        [SerializeField] private Button regionButton;
        [SerializeField] private Button gameBoxButton;
        [SerializeField] private RegionInformationPageView regionPrefab;
        [SerializeField] private GameBoxPageView gameBoxPrefab;
        private Func<GameState> state;
        private Func<int> viewer;
        private Func<GameStateView> visibleView;
        private IMapQueryService map;
        private GameplayDialogRegistry registry;
        private RegionInformationPageView region;
        private GameBoxPageView box;
        private EffectDialogShell browser;
        private GameBoxEventListView eventBrowser;
        private GameBoxCardKind? inspectedKind;
        private int inspectionVersion;
        private CardInspectionSnapshot[] inspectedCards = Array.Empty<CardInspectionSnapshot>();
        private readonly struct CardInspectionSnapshot
        {
            private readonly string instanceId, cardId, backVisualKey;
            private readonly bool faceUp;
            private readonly int privateForPlayerId;
            private readonly Sprite image;
            private readonly Texture2D back;
            public CardInspectionSnapshot(GameBoxCardState card, Sprite image, Texture2D back)
            {
                instanceId=card.InstanceId;cardId=card.CardId;backVisualKey=card.BackVisualKey;
                faceUp=card.FaceUp;privateForPlayerId=card.PrivateForPlayerId;this.image=image;this.back=back;
            }
            public bool Matches(GameBoxCardState card, Sprite image, Texture2D back) =>
                instanceId==card.InstanceId && cardId==card.CardId && faceUp==card.FaceUp &&
                privateForPlayerId==card.PrivateForPlayerId && backVisualKey==card.BackVisualKey &&
                this.image==image && this.back==back;
        }
        private int openedViewer;
        private string openedGameId, regionGameId;
        private int regionViewer;
        public RegionInformationPageView RegionPage => region;
        public GameBoxPageView GameBoxPage => box;
        public Button RegionButton => regionButton;
        public Button GameBoxButton => gameBoxButton;
        public bool TryValidateConfiguration(out string reason)
        {
            reason = "区控或游戏盒入口引用不完整。";
            return regionButton != null && gameBoxButton != null && regionPrefab != null && gameBoxPrefab != null &&
                regionPrefab.TryValidateConfiguration(out reason) && gameBoxPrefab.TryValidateConfiguration(out reason);
        }
        public void Configure(Func<GameState> current, Func<int> viewerId, IMapQueryService query, GameplayDialogRegistry dialogs,
            Func<GameStateView> visible = null)
        {
            state=current;viewer=viewerId;map=query;registry=dialogs;visibleView=visible;
            regionButton.onClick.AddListener(OpenRegion); gameBoxButton.onClick.AddListener(OpenGameBox);
        }
        public void OpenRegion()
        {
            if(state?.Invoke()==null)return;
            if(region==null){region=Instantiate(regionPrefab, PageHost, false);region.SetClose(CloseRegion);}
            regionGameId=state().GameId;regionViewer=viewer();
            region.Render(state(),map); Present(region.gameObject);
        }
        public void OpenGameBox()
        {
            var visible = VisibleGameBoxView();
            if(visible==null)return;
            if(box==null){box=Instantiate(gameBoxPrefab, PageHost, false);box.Configure(CloseGameBox,InspectPile);}
            browser?.Hide(); eventBrowser?.Hide(); inspectedKind=null;
            openedViewer=viewer();openedGameId=visible.GameId;box.RenderVisible(visible,registry.CardVisualCatalog);Present(box.gameObject);
        }
        public void Refresh()
        {
            if(state?.Invoke()==null){InvalidateRegion();InvalidateGameBox();return;}
            if(region!=null && region.gameObject.activeSelf)
            {
                if(regionGameId!=state().GameId || regionViewer!=viewer())InvalidateRegion();
                else region.Render(state(),map);
            }
            if(box==null)return;
            if(openedViewer!=viewer() || openedGameId!=state().GameId)
            {
                InvalidateGameBox();
                openedViewer=viewer();
            }
            if(box.gameObject.activeSelf || browser?.IsShowing == true || eventBrowser?.IsShowing == true)
            {
                var visible=VisibleGameBoxView();
                if(visible==null){InvalidateGameBox();return;}
                box.RenderVisible(visible,registry.CardVisualCatalog);
                if(inspectedKind.HasValue && (browser?.IsShowing == true || eventBrowser?.IsShowing == true) && !CurrentPileMatches())
                {
                    if(browser?.IsVisible == true || eventBrowser?.IsVisible == true)ShowPile(inspectedKind.Value,true);
                    else InvalidateGameBox();
                }
            }
        }
        public void CloseRegion(){if(region!=null)CloseInformation(region.gameObject);}
        private void InvalidateRegion()
        {
            if(region==null)return;
            GameplayHudFrame.Active?.AbandonInformationPage(region.gameObject);region.gameObject.SetActive(false);
        }
        public void CloseGameBox(){browser?.Hide();eventBrowser?.Hide();inspectedKind=null;if(box!=null)CloseInformation(box.gameObject);}
        private void InvalidateGameBox()
        {
            browser?.Hide(); eventBrowser?.Hide(true); inspectedKind=null; inspectionVersion++;
            if(box!=null){GameplayHudFrame.Active?.AbandonInformationPage(box.gameObject);box.gameObject.SetActive(false);}
        }
        public bool TryHandleEscape()
        {
            if(browser?.IsVisible == true || eventBrowser?.IsVisible == true){ReturnToBox();return true;}
            if(region!=null && region.gameObject.activeInHierarchy){CloseRegion();return true;}
            if(box!=null && box.gameObject.activeInHierarchy){CloseGameBox();return true;}
            return false;
        }
        private void InspectPile(GameBoxCardKind kind)
        {
            if(box==null || !box.gameObject.activeInHierarchy || openedViewer!=viewer())return;
            ShowPile(kind,false);
        }

        private void ShowPile(GameBoxCardKind kind, bool preserveScroll)
        {
            var eventPosition = preserveScroll && eventBrowser != null ? eventBrowser.ScrollPosition : 0f;
            if (kind != GameBoxCardKind.Event) eventBrowser?.Hide();
            if(browser==null)browser=new EffectDialogShell(registry);
            var profile=registry.EffectDialogLayoutProfile;
            var position=preserveScroll?browser.ScrollPosition:1f;
            var cards=box.Cards(kind).ToArray(); var images=box.Images(kind).ToArray();
            var gameBoxFaces=kind==GameBoxCardKind.Character;
            var backs=gameBoxFaces?box.CharacterBackImages.ToArray():new Texture2D[cards.Length];
            var ids=cards.Select(card=>card.InstanceId).ToList();
            var artwork=new Dictionary<string,Sprite>(); var labels=new Dictionary<string,string>();
            var backArtwork=new Dictionary<string,Texture2D>();
            for(var i=0;i<ids.Count;i++)
            {
                if(string.IsNullOrEmpty(ids[i]) || artwork.ContainsKey(ids[i]))return;
                artwork.Add(ids[i],images[i]); labels.Add(ids[i],cards[i].CardId ?? string.Empty);
                backArtwork.Add(ids[i],backs[i]);
            }
            inspectedKind=kind;inspectedCards=cards.Select((card,i)=>new CardInspectionSnapshot(card,images[i],backs[i])).ToArray();
            var owner=openedViewer;
            var version=++inspectionVersion;
            var game=openedGameId;
            bool Current()=>box!=null && game==state?.Invoke()?.GameId && owner==viewer() && version==inspectionVersion && inspectedKind==kind && CurrentPileMatches();
            if(kind==GameBoxCardKind.Event)
            {
                browser.Hide();
                if(eventBrowser==null)eventBrowser=Instantiate(box.EventListPrefab,PageHost,false);
                eventBrowser.Present(box.ListTitle(kind),box.InspectHintFor(kind),ids,images,Current,ReturnToBox,eventPosition);
                return;
            }
            var panel=browser.PreparePage(PageHost,"Game Box Card Picker","Game Box Card List",
                profile.SelectionPanelSize,profile.CharacterPanelPosition,true,false,true,cardPicker:true);
            EffectDialogShell.AddCountedHeading(panel,box.ListTitleFormat(kind),ids.Count,string.Empty);
            browser.AddSelection(panel,new EffectDialogSelectionSpec
            {
                ReadOnly=true,IsEffectPage=false,CloseOnBackgroundClick=true,CardPickerHint=box.InspectHintFor(kind),
                InspectGameBoxCharacter=gameBoxFaces,CharacterBack=id=>backArtwork[id],
                Request=new InteractionRequestProjection { VisibleToViewer=true,CandidateIds=ids,MinSelections=0,MaxSelections=0,
                    AllowDecline=true,InteractionId="game-box-view",Status="open" },
                Label=id=>labels[id],CardSprite=id=>artwork[id],IsCurrent=Current,CanSelect=_=>false,
                OptionNamePrefix="Game Box Card ",Cancel=ReturnToBox,CancelLabel=profile.ReadOnlyCloseLabel
            });
            browser.RestoreScroll(position);
        }

        private bool CurrentPileMatches()
        {
            if(box==null || !inspectedKind.HasValue)return false;
            var kind=inspectedKind.Value;var cards=box.Cards(kind);var images=box.Images(kind);
            if(cards.Count!=inspectedCards.Length || images.Count!=cards.Count)return false;
            if(kind==GameBoxCardKind.Character && box.CharacterBackImages.Count!=cards.Count)return false;
            for(var i=0;i<cards.Count;i++)
                if(!inspectedCards[i].Matches(cards[i],images[i],kind==GameBoxCardKind.Character?box.CharacterBackImages[i]:null))return false;
            return true;
        }

        private GameStateView VisibleGameBoxView()
        {
            var result=visibleView!=null?visibleView():state?.Invoke()==null?null:GameStateViewProjector.ProjectForPlayer(state(),viewer());
            return result!=null && result.ViewerRole==GameStateViewerRole.Player && result.ViewerPlayerId==viewer()?result:null;
        }
        private void ReturnToBox()
        {
            browser?.Hide(); eventBrowser?.Hide(); inspectedKind=null;
            if(box!=null && openedViewer==viewer())
            {box.RenderVisible(VisibleGameBoxView(),registry.CardVisualCatalog);Present(box.gameObject);}
        }
        private RectTransform PageHost => GetComponent<GameplayInteractionHudView>().Frame.ContentRect;
        private static void Present(GameObject page)
        { if(GameplayHudFrame.Active!=null)GameplayHudFrame.Active.ShowInformationPage(page);else page.SetActive(true); }
        private static void CloseInformation(GameObject page)
        { if(GameplayHudFrame.Active!=null)GameplayHudFrame.Active.CloseInformationPage(page);else page.SetActive(false); }
        private void OnDestroy()
        {
            if(regionButton!=null)regionButton.onClick.RemoveListener(OpenRegion);
            if(gameBoxButton!=null)gameBoxButton.onClick.RemoveListener(OpenGameBox);
            if(region!=null)Destroy(region.gameObject);if(box!=null)Destroy(box.gameObject);
            if(eventBrowser!=null)Destroy(eventBrowser.gameObject);
            browser?.Hide();
        }
    }
}
