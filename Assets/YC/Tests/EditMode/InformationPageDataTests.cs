using System;
using System.Collections.Generic;
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
        public void RegionRows_UsePreviewWithoutChangingPhysicalVacanciesOrSource()
        {
            var map = StaticMapDefinitions.Resolve(StaticMapDefinitions.FourPlayerMapId);
            var query = new MapQueryService(map);
            var region = map.Regions.First();
            var location = map.Locations.First(value => region.LocationIds.Contains(value.LocationId) && value.InfluenceSlotCount > 0);
            var state = new GameState { GameId = "预览局", ContentPackHash = "规则版本", MapId = map.MapId };
            state.Players.Add(new PlayerState { PlayerId = 1, CityLocationId = location.LocationId });
            state.Players.Add(new PlayerState { PlayerId = 2 });
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1,
                SlotId = InfluenceSlotReference.ForLocation(location.LocationId, 0).SlotId, LocationId = location.LocationId });
            var before = RegionInformationQuery.Build(state, query).Single(row => row.RegionId == region.RegionId);
            var preview = new RegionControlPreview { GameId = state.GameId, ContentHash = state.ContentPackHash,
                MapId = state.MapId, StateRevision = state.EffectRuntime.StateRevision,
                Regions = map.Regions.Select(value => new RegionControlPreviewRow { RegionId = value.RegionId,
                    ScoreValue = value.ScoreValue, Players = new List<RegionInfluencePreview> {
                        new RegionInfluencePreview { PlayerId = 1 }, new RegionInfluencePreview { PlayerId = 2 } } }).ToList() };
            var scored = preview.Regions.Single(value => value.RegionId == region.RegionId);
            scored.ScoreValue = region.ScoreValue - 2;
            scored.Players[0].MarkerCount = 5;
            scored.Players[1].MarkerCount = 5;

            var row = RegionInformationQuery.Build(state, query, preview).Single(value => value.RegionId == region.RegionId);
            Assert.That(row.Points, Is.EqualTo(region.ScoreValue - 2));
            Assert.That(row.Influence, Is.EquivalentTo(new Dictionary<int, int> { [1] = 5, [2] = 5 }));
            Assert.That(row.EmptySlots, Is.EqualTo(before.EmptySlots), "计分修正不能改变实体空位。");
            row.Influence[1] = 99;
            Assert.That(scored.Players[0].MarkerCount, Is.EqualTo(5), "页面不得修改控制结果。");
            Assert.That(RegionInformationQuery.Build(state, query).Single(value => value.RegionId == region.RegionId).Influence[1], Is.EqualTo(3));

            preview.Regions.RemoveAt(0);
            Assert.Throws<ArgumentException>(() => RegionInformationQuery.Build(state, query, preview));
            preview.Regions.Add(scored);
            preview.Regions[1] = preview.Regions[0];
            Assert.Throws<ArgumentException>(() => RegionInformationQuery.Build(state, query, preview));
        }
        [Test]
        public void RegionRows_SumAdditionalInfluenceAndRejectStalePreview()
        {
            var map = StaticMapDefinitions.Resolve(StaticMapDefinitions.FourPlayerMapId);
            var query = new MapQueryService(map);
            var state = new GameState { GameId = "预览局", ContentPackHash = "规则版本", MapId = map.MapId };
            state.Players.Add(new PlayerState { PlayerId = 1 });
            state.Players.Add(new PlayerState { PlayerId = 2 });
            state.EffectRuntime.StateRevision = 5;
            var preview = new RegionControlPreview { GameId = state.GameId, ContentHash = state.ContentPackHash,
                MapId = state.MapId, StateRevision = 5 };
            foreach (var region in map.Regions)
                preview.Regions.Add(new RegionControlPreviewRow { RegionId = region.RegionId, ScoreValue = region.ScoreValue,
                    Players = new List<RegionInfluencePreview> { new RegionInfluencePreview { PlayerId = 1 }, new RegionInfluencePreview { PlayerId = 2 } } });
            preview.Regions[0].Players[0].MarkerCount = 3;
            preview.Regions[0].Players[0].Contributions.AddRange(new[] { 4, 1, 1 });
            preview.Regions[0].Players[1].MarkerCount = 3;
            preview.Regions[0].Players[1].Contributions.AddRange(new[] { 2, -5 });
            preview.Regions[1].Players[0].MarkerCount = 2;
            preview.Regions[1].Players[0].Contributions.AddRange(new[] { 2, -2 });
            preview.Regions[0].ScoreValue++;
            var row = RegionInformationQuery.Build(state, query, preview).Single(value => value.RegionId == preview.Regions[0].RegionId);
            Assert.That(row.Influence[1], Is.EqualTo(9));
            Assert.That(row.InfluenceDisplay[1], Is.EqualTo("3+6"));
            Assert.That(row.InfluenceDisplay[2], Is.EqualTo("3-3"));
            Assert.That(RegionInformationQuery.Build(state, query, preview)[1].InfluenceDisplay[1], Is.EqualTo("2"));
            Assert.That(row.Points, Is.EqualTo(map.Regions.Single(value => value.RegionId == row.RegionId).ScoreValue + 1));
            state.EffectRuntime.StateRevision++;
            Assert.Throws<ArgumentException>(() => RegionInformationQuery.Build(state, query, preview));
        }

        [Test]
        public void RegionPreviewBindsDynamicValuesOnTheGameplayHudReferencedPrefab()
        {
            var hud = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            Assert.That(hud, Is.Not.Null);
            var pagesType = Type.GetType("YC.Presentation.GameplayInformationPages, Assembly-CSharp", true);
            var pages = hud.GetComponentInChildren(pagesType, true);
            Assert.That(pages, Is.Not.Null);
            var prefab = (UnityEngine.Component)pagesType.GetField("regionPrefab",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(pages);
            Assert.That(prefab, Is.Not.Null);
            var root = new UnityEngine.GameObject("隔离区控页面夹具", typeof(UnityEngine.RectTransform), typeof(UnityEngine.Canvas));
            try
            {
                var page = UnityEngine.Object.Instantiate(prefab, root.transform, false);
                var map = StaticMapDefinitions.Resolve(StaticMapDefinitions.FourPlayerMapId);
                var query = new MapQueryService(map);
                var state = new GameState { GameId = "预览局", ContentPackHash = "规则版本", MapId = map.MapId };
                for (int id = 1; id <= 4; id++) state.Players.Add(new PlayerState { PlayerId = id, Color = (PlayerColor)(id - 1) });
                state.EffectRuntime.StateRevision = 5;
                var preview = new RegionControlPreview { GameId = state.GameId, ContentHash = state.ContentPackHash,
                    MapId = state.MapId, StateRevision = 5 };
                foreach (var region in map.Regions)
                    preview.Regions.Add(new RegionControlPreviewRow { RegionId = region.RegionId, ScoreValue = region.ScoreValue,
                        Players = state.Players.Select(player => new RegionInfluencePreview { PlayerId = player.PlayerId }).ToList() });
                preview.Regions[0].ScoreValue++;
                preview.Regions[0].Players[0].MarkerCount = 3;
                preview.Regions[0].Players[0].Contributions.AddRange(new[] { 4, 1, 1 });
                var before = GameStateCloneService.DeepClone(state);
                var render = page.GetType().GetMethod("Render", new[] { typeof(GameState), typeof(IMapQueryService),
                    typeof(RegionControlPreview), typeof(bool) });
                render.Invoke(page, new object[] { state, query, preview, false });
                var rows = (Array)page.GetType().GetField("rows", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic).GetValue(page);
                var first = rows.GetValue(0);
                var participants = (System.Collections.IList)first.GetType().GetField("participants",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(first);
                var value = (UnityEngine.UI.Text)participants[0].GetType().GetProperty("Label").GetValue(participants[0]);
                var points = (UnityEngine.UI.Text)first.GetType().GetField("points",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(first);
                Assert.That(value.text, Is.EqualTo("3+6"));
                Assert.That(points.text, Is.EqualTo(preview.Regions[0].ScoreValue.ToString()));
                render.Invoke(page, new object[] { state, query, null, true });
                Assert.That(value.text, Is.EqualTo("—"), "预览尚未到达时不展示旧结果。");
                Assert.That(GameStateCloneService.AreEquivalent(before, state), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
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
