using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class AbilitySourceQueryIndexTests
    {
        [Serializable] private sealed class DerivedItems : List<ItemInstanceState> { }
        private static GameState State()
        {
            var state = new GameState(); state.Players.Add(new PlayerState { PlayerId = 2 }); state.Players.Add(new PlayerState { PlayerId = 1 });
            state.ItemZones.Zones.Add(new ZoneInstanceState { ZoneId = "z1", TemplateId = "hand", OwnerPlayerId = 1 });
            state.ItemZones.Zones.Add(new ZoneInstanceState { ZoneId = "z2", TemplateId = "hand", OwnerPlayerId = 2 });
            for (int player = 1; player <= 2; player++) state.ItemZones.Items.Add(new ItemInstanceState {
                ItemId = "item" + player, DefinitionId = "definition", ZoneId = "z" + player,
                OwnerSubject = new ItemSubjectRef { Kind = ItemSubjectKind.Player, PlayerId = player },
                ControllerSubject = new ItemSubjectRef { Kind = ItemSubjectKind.Player, PlayerId = player },
                LocalState = Object("activeFaceId", NormalizedValue.CreateString("front")) });
            string grant = StableIdFactory.Create("ability-source", "item1", "b", "", "v1", "1");
            state.ItemZones.Items[0].LocalState.Properties.Add(new NormalizedValueEntry { Name = "grantingSourceRefs", Value = Strings(new[] { grant, "missing", grant }) });
            ItemZoneLegacyProjection.Bind(state, true);
            return state;
        }
        private static List<AbilitySourceDeclaration> Declarations() => new List<AbilitySourceDeclaration> {
            new AbilitySourceDeclaration { DefinitionId = "definition", AbilityId = "c", Kind = "event", DeclarationVersion = "v1", AuthorizationPolicy = "holder", ActiveFaceId = "front", EffectiveZones = new List<string> { "hand" }, ObservedEvents = new List<string> { "E" } },
            new AbilitySourceDeclaration { DefinitionId = "definition", AbilityId = "b", Kind = "ruleQuery", DeclarationVersion = "v1", AuthorizationPolicy = "owner", EffectiveZones = new List<string> { "hand" }, QueryKinds = new List<string> { "Q" } },
            new AbilitySourceDeclaration { DefinitionId = "definition", AbilityId = "a", Kind = "event", DeclarationVersion = "v1", AuthorizationPolicy = "controller", EffectiveZones = new List<string> { "hand" }, ObservedEvents = new List<string> { "E" } } };
        private static string Outcome(bool reference, GameState state, IEnumerable<AbilitySourceDeclaration> declarations, int player = -1, string kind = "", string trigger = "", string query = "")
        {
            try { return JsonConvert.SerializeObject(reference ? LegacyAbilitySourceQueryReference.QuerySources(state, declarations, player, kind, trigger, query) : AbilitySourceService.QuerySources(state, declarations, player, kind, trigger, query)); }
            catch (Exception error) { return error.GetType().FullName + "|" + error.Message; }
        }
        [Test]
        public void IndexKeepsOrderingAuthorizationAndUnfilteredGrantProofs()
        {
            var state = State(); var declarations = Declarations(); var baseline = GameStateCloneService.DeepClone(state);
            foreach (int player in new[] { -1, 1, 2, 3 })
                foreach (var filter in new[] { ("", "", ""), ("event", "E", ""), ("ruleQuery", "", "Q"), ("", "missing", "") })
                    Assert.That(Outcome(false, state, declarations, player, filter.Item1, filter.Item2, filter.Item3), Is.EqualTo(Outcome(true, state, declarations, player, filter.Item1, filter.Item2, filter.Item3)));
            var source = AbilitySourceService.QuerySources(state, declarations, 1, "event").First(s => s.AbilityId == "a");
            Assert.That(source.GrantingSourceRefs.Count, Is.EqualTo(2), "授予来源须先按全目录核验，再过滤event种类，并保留原重复项");
            Assert.That(GameStateCloneService.AreEquivalent(state, baseline), Is.True);
        }
        [Test]
        public void MalformedDuplicateAndDerivedRootInputsKeepOriginalOutcomes()
        {
            var changes = new Dictionary<string, Action<GameState, List<AbilitySourceDeclaration>>> {
                { "正常", (s,d) => { } },
                { "重复区域先匹配", (s,d) => s.ItemZones.Zones.Add(new ZoneInstanceState { ZoneId = "z1", TemplateId = "wrong" }) },
                { "重复物品保序", (s,d) => s.ItemZones.Items.Add(GameStateCloneService.DeepClone(s.ItemZones.Items[0])) },
                { "空物品末尾", (s,d) => s.ItemZones.Items.Add(null) },
                { "空区域开头", (s,d) => s.ItemZones.Zones.Insert(0,null) },
                { "空玩家", (s,d) => s.Players.Add(null) },
                { "缺物品", (s,d) => s.ItemZones.Items = null },
                { "缺区域", (s,d) => s.ItemZones.Zones = null },
                { "缺玩家", (s,d) => s.Players = null },
                { "缺实体区", (s,d) => s.ItemZones = null },
                { "派生物品表", (s,d) => { var list = new DerivedItems(); list.AddRange(s.ItemZones.Items); s.ItemZones.Items = list; } },
                { "重复能力ID", (s,d) => d.Add(d[0]) },
                { "空声明", (s,d) => d.Add(null) },
                { "空面ID", (s,d) => d[0].ActiveFaceId = null },
                { "无匹配且缺玩家", (s,d) => { s.Players = null; foreach(var declaration in d) declaration.DefinitionId = "missing"; } },
                { "空目录且缺物品", (s,d) => { d.Clear(); s.ItemZones.Items = null; } }
            };
            foreach (var change in changes)
            {
                var state = State(); var declarations = Declarations(); change.Value(state, declarations);
                Assert.That(Outcome(false, state, declarations), Is.EqualTo(Outcome(true, state, declarations)), change.Key);
            }
        }
        [Test]
        public void SameRevisionAndCustomDeclarationEnumerationAreReadAgain()
        {
            var state = State(); var declarations = Declarations();
            var first = AbilitySourceService.QuerySources(state, declarations, 1);
            state.ItemZones.Items[0].ControllerSubject.PlayerId = 2;
            Assert.That(Outcome(false, state, declarations, 1), Is.EqualTo(Outcome(true, state, declarations, 1)));
            Assert.That(first.First(s => s.AbilityId == "a").AuthorizedPlayerId, Is.EqualTo(1));
            IEnumerable<AbilitySourceDeclaration> Enumerate(GameState target)
            {
                target.Players[0].Score++;
                foreach (var declaration in Declarations()) yield return declaration;
            }
            var oldState = State(); var newState = State();
            Assert.That(Outcome(false, newState, Enumerate(newState)), Is.EqualTo(Outcome(true, oldState, Enumerate(oldState))));
            Assert.That(newState.Players[0].Score, Is.EqualTo(2));
            Assert.That(GameStateCloneService.AreEquivalent(oldState,newState), Is.True);
        }
    }
}
