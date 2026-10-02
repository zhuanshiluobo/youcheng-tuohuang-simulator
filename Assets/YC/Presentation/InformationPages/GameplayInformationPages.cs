using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Maps;
using YC.Domain.State;

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
        private IMapQueryService map;
        private GameplayDialogRegistry registry;
        private RegionInformationPageView region;
        private GameBoxPageView box;
        private EffectDialogShellView picker;
        private CardViewer cardViewer;
        private int openedViewer;
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
        public void Configure(Func<GameState> current, Func<int> viewerId, IMapQueryService query, GameplayDialogRegistry dialogs)
        {
            state=current;viewer=viewerId;map=query;registry=dialogs;
            regionButton.onClick.AddListener(OpenRegion); gameBoxButton.onClick.AddListener(OpenGameBox);
        }
        public void OpenRegion()
        {
            if(state?.Invoke()==null)return;
            if(region==null){region=Instantiate(regionPrefab, PageHost, false);region.SetClose(CloseRegion);}
            region.Render(state(),map); Present(region.gameObject);
        }
        public void OpenGameBox()
        {
            if(state?.Invoke()==null)return;
            if(box==null){box=Instantiate(gameBoxPrefab, PageHost, false);box.Configure(CloseGameBox,InspectPile);}
            openedViewer=viewer();box.Render(state(),openedViewer,registry.CardVisualCatalog);Present(box.gameObject);
        }
        public void Refresh()
        {
            if(state?.Invoke()==null)return;
            if(region!=null && region.gameObject.activeSelf)region.Render(state(),map);
            if(box==null)return;
            if(openedViewer!=viewer())
            {
                if(cardViewer!=null)cardViewer.Dismiss();
                if(picker!=null)GameplayHudFrame.Active?.HidePage(picker.gameObject);
                openedViewer=viewer();
            }
            if(box.gameObject.activeSelf)box.Render(state(),viewer(),registry.CardVisualCatalog);
        }
        public void CloseRegion(){if(region!=null)Hide(region.gameObject);}
        public void CloseGameBox(){if(box!=null)Hide(box.gameObject);}
        public bool TryHandleEscape()
        {
            if(picker!=null && picker.gameObject.activeInHierarchy){ReturnToBox();return true;}
            if(region!=null && region.gameObject.activeInHierarchy){CloseRegion();return true;}
            if(box!=null && box.gameObject.activeInHierarchy){CloseGameBox();return true;}
            return false;
        }
        private void InspectPile(GameBoxCardKind kind)
        {
            if(box==null || !box.gameObject.activeInHierarchy || openedViewer!=viewer())return;
            if(picker!=null)Destroy(picker.gameObject);
            picker=registry.InstantiateEffectDialogShell(GetComponent<GameplayInteractionHudView>().Frame.ContentRect,false,false,true);
            var profile=picker.LayoutProfile;
            picker.PrepareForUse("Game Box Card Picker","Game Box Card List",profile.SelectionPanelSize,profile.CharacterPanelPosition,true);
            picker.ConfigureSelectionMode(true,box.InspectHint);
            picker.ConfigureHeading(box.ListTitle(kind),string.Empty,0,"Game Box Card Count","Inspect Hint",0);
            picker.ConfigureCardScroll(profile.SelectionCardSize,profile.SelectionMinimumCardWidth);
            picker.ConfigureBackgroundDismiss(ReturnToBox);
            var images=new List<Sprite>(box.Images(kind));
            for(var i=0;i<images.Count;i++)
            {
                var sprite=images[i];var item=picker.CreateFacilityCard();
                item.gameObject.name="Game Box Card "+i;
                CardArtworkView.Set(item.CardImage,sprite);
                item.FallbackLabel.gameObject.SetActive(false);
                item.Button.interactable=false;
                if(item.SelectionImage!=null)item.SelectionImage.enabled=false;
                item.Outline.enabled=false;
                item.ConfigureInspection(()=>InspectCard(sprite),()=>picker!=null && picker.gameObject.activeInHierarchy && openedViewer==viewer());
            }
        }
        private void InspectCard(Sprite sprite)
        {
            if(sprite==null || picker==null || openedViewer!=viewer())return;
            var position=picker.ScrollPosition;
            if(cardViewer==null)cardViewer=registry.InstantiateCardViewer(transform as RectTransform);
            cardViewer.OpenInspect(sprite,()=>
            {
                if(picker==null || openedViewer!=viewer())return;
                Present(picker.gameObject);picker.RestoreScrollPosition(position);
            },()=>picker!=null && openedViewer==viewer());
        }
        private void ReturnToBox()
        {
            if(picker!=null)Hide(picker.gameObject);
            if(box!=null && openedViewer==viewer())
            {box.Render(state(),viewer(),registry.CardVisualCatalog);Present(box.gameObject);}
        }
        private RectTransform PageHost => GetComponent<GameplayInteractionHudView>().Frame.ContentRect;
        private static void Present(GameObject page)
        { if(GameplayHudFrame.Active!=null)GameplayHudFrame.Active.ShowPage(page,false);else page.SetActive(true); }
        private static void Hide(GameObject page)
        { if(GameplayHudFrame.Active!=null)GameplayHudFrame.Active.HidePage(page);else page.SetActive(false); }
        private void OnDestroy()
        {
            if(regionButton!=null)regionButton.onClick.RemoveListener(OpenRegion);
            if(gameBoxButton!=null)gameBoxButton.onClick.RemoveListener(OpenGameBox);
            if(region!=null)Destroy(region.gameObject);if(box!=null)Destroy(box.gameObject);
            if(picker!=null)Destroy(picker.gameObject);if(cardViewer!=null)Destroy(cardViewer.gameObject);
        }
    }
}
