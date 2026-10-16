using System.Linq;
using NUnit.Framework;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class InformationPageDataTests
    {
        [TestCase(false,8)] [TestCase(true,7)]
        public void RegionRows_UseMapAndCountOccupiedSlotsIndependentlyOfCityInfluence(bool three,int count)
        {
            var map = StaticMapDefinitions.Resolve(three?StaticMapDefinitions.ThreePlayerMapId:StaticMapDefinitions.FourPlayerMapId);
            var query=new MapQueryService(map);var region=map.Regions.First();
            var location=map.Locations.First(l=>region.LocationIds.Contains(l.LocationId) && l.InfluenceSlotCount>0);
            var state=new GameState();state.Players.Add(new PlayerState{PlayerId=7,Color=PlayerColor.Blue,CityLocationId=location.LocationId});
            var empty=RegionInformationQuery.Build(state,query);
            Assert.That(empty.Count,Is.EqualTo(count));
            if(three)Assert.That(empty.Any(r=>r.RegionId.ToUpperInvariant().EndsWith("G")),Is.False);
            Assert.That(empty[0].Influence[7],Is.EqualTo(2));
            Assert.That(empty[0].EmptySlots,Is.EqualTo(empty[0].TotalSlots),"城市不是影响力槽位中的方块。");
            state.Map.Influences.Add(new InfluencePlacement{PlayerId=7,SlotId=InfluenceSlotReference.ForLocation(location.LocationId,0).SlotId,LocationId=location.LocationId});
            var occupied=RegionInformationQuery.Build(state,query);
            Assert.That(occupied[0].Influence[7],Is.EqualTo(3));
            Assert.That(occupied[0].EmptySlots,Is.EqualTo(empty[0].EmptySlots-1));
            state.Map.Influences[0].SlotId="unknown-slot";
            Assert.That(RegionInformationQuery.Build(state,query)[0].EmptySlots,Is.Null,"未知槽位不能被当成空位。");
        }
        [Test]
        public void GameBox_ClonesAndShowsOnlyAuthorizedIdentitiesRegardlessOfFaceOrientation()
        {
            // 独立验证展示模型的克隆与授权；实体投影另由 GameBoxStateProjectionTests 覆盖。
            var box=new GameBoxState();
            box.Cards.Add(new GameBoxCardState{InstanceId="own-private",CardId="secret",PrivateForPlayerId=3,FaceUp=false,BackColor=PlayerColor.Red});
            box.Cards.Add(new GameBoxCardState{InstanceId="peer-private",CardId="peer-secret",PrivateForPlayerId=4,FaceUp=true});
            box.Cards.Add(new GameBoxCardState{InstanceId="public",CardId="visible",PrivateForPlayerId=-1,FaceUp=false});
            box.Enterprises.Add(new GameBoxEnterpriseState{OwnerPlayerId=3,VisualKey="enterprise-0"});
            box.Enterprises.Add(new GameBoxEnterpriseState{OwnerPlayerId=4,VisualKey="enterprise-1"});
            box.Tokens.Add(new GameBoxTokenState{InstanceId="a",VisualKey="same",State="used",Count=2});
            box.Tokens.Add(new GameBoxTokenState{InstanceId="b",VisualKey="same",State="unused",Count=1});
            var clone=GameStateCloneService.DeepClone(box);clone.Cards[0].CardId="changed";
            Assert.That(box.Cards[0].CardId,Is.EqualTo("secret"));
            Assert.That(clone.Cards[0].PrivateForPlayerId,Is.EqualTo(3));
            var view=GameBoxState.ForViewer(box,GameStateViewer.Player(3));
            Assert.That(view.Cards.Select(card=>card.InstanceId),Is.EqualTo(new[]{"own-private","public"}));
            Assert.That(view.Cards.Select(card=>card.CardId),Is.EqualTo(new[]{"secret","visible"}),"本人私密牌与公开背面牌都保留完整身份。");
            Assert.That(view.Cards.All(card=>!card.FaceUp),Is.True);
            Assert.That(view.Enterprises.Select(board=>board.OwnerPlayerId),Is.EqualTo(new[]{3}));
            Assert.That(view.Tokens.Count,Is.EqualTo(2),"同图不同状态不得自动合并。");
            Assert.That(view.Tokens.Select(token=>token.State),Is.EqualTo(new[]{"used","unused"}));
            Assert.That(view.Tokens.Select(token=>token.Count),Is.EqualTo(new[]{2,1}));
            var peer=GameBoxState.ForViewer(box,GameStateViewer.Player(4));
            Assert.That(peer.Cards.Select(card=>card.InstanceId),Is.EqualTo(new[]{"peer-private","public"}));
            Assert.That(peer.Cards[0].CardId,Is.EqualTo("peer-secret"));
            Assert.That(peer.Enterprises.Select(board=>board.OwnerPlayerId),Is.EqualTo(new[]{4}));
            var spectator=GameBoxState.ForViewer(box,GameStateViewer.Spectator);
            Assert.That(spectator.Cards.Single().CardId,Is.EqualTo("visible"));
            Assert.That(spectator.Enterprises,Is.Empty);
            Assert.That(GameBoxState.ForViewer(box,GameStateViewer.Host).Cards.Count,Is.EqualTo(3));
            view.Cards[0].CardId="view-change";view.Tokens[0].Count=99;
            Assert.That(box.Cards[0].CardId,Is.EqualTo("secret"));
            Assert.That(box.Tokens[0].Count,Is.EqualTo(2));
        }
    }
}
