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
        public void GameBox_ClonesAndProjectsFaceDownCardsWithoutIdentityAndOnlyOwnEnterprise()
        {
            var state=new GameState();
            state.GameBox.Cards.Add(new GameBoxCardState{InstanceId="hidden",CardId="secret",FaceUp=false,BackColor=PlayerColor.Red});
            state.GameBox.Cards.Add(new GameBoxCardState{InstanceId="public",CardId="visible",FaceUp=true});
            state.GameBox.Enterprises.Add(new GameBoxEnterpriseState{OwnerPlayerId=3,VisualKey="enterprise-0"});
            state.GameBox.Enterprises.Add(new GameBoxEnterpriseState{OwnerPlayerId=4,VisualKey="enterprise-1"});
            state.GameBox.Tokens.Add(new GameBoxTokenState{InstanceId="a",VisualKey="same",State="used",Count=2});
            state.GameBox.Tokens.Add(new GameBoxTokenState{InstanceId="b",VisualKey="same",State="unused",Count=1});
            var clone=GameStateCloneService.DeepClone(state);clone.GameBox.Cards[0].CardId="changed";
            Assert.That(state.GameBox.Cards[0].CardId,Is.EqualTo("secret"));
            var view=GameStateViewProjector.Project(state,GameStateViewer.Player(3));
            Assert.That(view.GameBox.Cards[0].CardId,Is.Empty);
            Assert.That(view.GameBox.Cards[0].FaceUp,Is.False);
            Assert.That(view.GameBox.Cards[1].CardId,Is.EqualTo("visible"));
            Assert.That(view.GameBox.Enterprises.Select(b=>b.OwnerPlayerId),Is.EqualTo(new[]{3}));
            var client=GameStateViewProjector.ToClientState(view);
            Assert.That(client.GameBox.Tokens.Count,Is.EqualTo(2),"同图不同状态不得自动合并。");
            Assert.That(client.GameBox.Cards[0].CardId,Is.Empty);
            Assert.That(GameStateViewProjector.Project(state,GameStateViewer.Spectator).GameBox.Enterprises,Is.Empty);
        }
    }
}
