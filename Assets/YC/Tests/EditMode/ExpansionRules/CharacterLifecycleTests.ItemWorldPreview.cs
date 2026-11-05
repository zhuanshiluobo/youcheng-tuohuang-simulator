using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 隔离事务和回调边界，不计正常菜单页面验收。
    public sealed partial class CharacterLifecycleTests
    {
        [TestCase(false,false)][TestCase(false,true)][TestCase(true,false)][TestCase(true,true)]
        public void ItemWorldPreviewMoveAndRecycleKeepEveryFieldAndStableTargets(bool recycle,bool alias)
        {
            var state=State();var registry=Registry();string card=Ids(state,"hand",1)[0];
            Cover(state,registry,card);Use(state,registry,card);
            registry.Register("test.item_world_transaction",context=>
            {
                if(alias)context.State.EffectRuntime.EffectNodes[0].ShuffleInputItemIds=context.State.EffectRuntime.EffectNodes[0].BlockerIds;
                if(recycle)return CharacterLifecycleService.Recycle(context,1,false,new List<string>{card});
                string reason=CharacterLifecycleService.MoveCard(context,card,FindZone(context.State,"hand",1).ZoneId);
                return reason.Length==0?EffectStepResult.Completed():EffectStepResult.Failed(reason);
            });
            var reference=GameStateCloneService.DeepClone(state);
            using(CharacterLifecycleService.UseFullCharacterItemWorldPreviewsForDiagnostics())
                Run(reference,registry,EffectSpec.Create("test.item_world_transaction",playerId:1));
            var player=state.FindPlayer(1);var map=state.Map;int publications=0;
            using(GameStateCloneService.VerifyItemWorldNodePreviewsForDiagnostics())
            using(GameStateCloneService.VerifySchedulingPublicationCopies())
            using(GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="character.item_world.publish")publications++;}))
                Run(state,registry,EffectSpec.Create("test.item_world_transaction",playerId:1));
            Assert.That(GameStateCloneService.AreEquivalent(state,reference),Is.True);
            Assert.That(state.FindPlayer(1),Is.SameAs(player));Assert.That(state.Map,Is.SameAs(map));
            if(alias)Assert.That(publications,Is.Zero);else Assert.That(publications,Is.GreaterThan(0));
        }
        [TestCase(false)][TestCase(true)]
        public void ItemWorldPreviewFailureDiscardsEarlierMovesAndCurrentNodeWrites(bool recycle)
        {
            var state=State();var registry=Registry();string card=Ids(state,"hand",1)[0],opponent=Ids(state,"hand",2)[0];
            Cover(state,registry,card);Use(state,registry,card);
            registry.Register("test.item_world_failure",context=>
            {
                if(recycle)return CharacterLifecycleService.Recycle(context,1,false,new[]{card,opponent});
                string reason=CharacterLifecycleService.MoveCard(context,card,"missing-destination");
                return reason.Length==0?EffectStepResult.Completed():EffectStepResult.Failed(reason);
            });
            var reference=GameStateCloneService.DeepClone(state);
            using(CharacterLifecycleService.UseFullCharacterItemWorldPreviewsForDiagnostics())
                Assert.That(Run(reference,registry,EffectSpec.Create("test.item_world_failure",playerId:1)).Status,Is.EqualTo(EffectNodeStatus.Failed));
            using(GameStateCloneService.VerifyItemWorldNodePreviewsForDiagnostics())
            using(GameStateCloneService.VerifySchedulingPublicationCopies())
                Assert.That(Run(state,registry,EffectSpec.Create("test.item_world_failure",playerId:1)).Status,Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(GameStateCloneService.AreEquivalent(state,reference),Is.True);
            Assert.That(Ids(state,"discard",1),Does.Contain(card));
        }
        private sealed class CallbackCoveredIds:IEnumerable<string>
        {
            internal Action Callback;internal string Card;internal int Calls;
            public IEnumerator<string> GetEnumerator(){Calls++;Callback?.Invoke();yield return Card;}
            IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
        }
        [Test]
        public void ItemWorldPreviewCustomCoveredEnumerationKeepsOriginalTwoCallsAndFullSnapshot()
        {
            var state=State();var registry=Registry();string card=Ids(state,"hand",1)[0];
            Cover(state,registry,card);Use(state,registry,card);
            var observations=new List<string>();int publications=0;
            registry.Register("test.item_world_enumeration",context=>
            {
                var ids=new CallbackCoveredIds{Card=card};
                ids.Callback=()=>context.State.EffectRuntime.LastFaultMessage="enumeration:"+ids.Calls;
                var result=CharacterLifecycleService.Recycle(context,1,false,ids);
                observations.Add(ids.Calls+"/"+context.State.EffectRuntime.LastFaultMessage);
                return result;
            });
            var reference=GameStateCloneService.DeepClone(state);
            using(CharacterLifecycleService.UseFullCharacterItemWorldPreviewsForDiagnostics())
                Run(reference,registry,EffectSpec.Create("test.item_world_enumeration",playerId:1));
            using(GameStateCloneService.VerifyItemWorldNodePreviewsForDiagnostics())
            using(GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="character.item_world.publish")publications++;}))
                Run(state,registry,EffectSpec.Create("test.item_world_enumeration",playerId:1));
            Assert.That(observations,Is.EqualTo(new[]{"2/enumeration:1","2/enumeration:1"}));
            Assert.That(publications,Is.Zero);Assert.That(GameStateCloneService.AreEquivalent(state,reference),Is.True);
        }
        [TestCase(false)][TestCase(true)]
        public void ItemWorldPreviewPublishingDoesNotWriteBorrowedHistoryLists(bool recycle)
        {
            var state=State();var registry=Registry();string card=Ids(state,"hand",1)[0];
            Cover(state,registry,card);Use(state,registry,card);int publications=0;bool advanced=false;
            registry.Register("test.item_world_history",context=>
            {
                Assert.That(context.State.EffectRuntime.RuleEvents.Count,Is.GreaterThan(0));
                var history=context.State.EffectRuntime.RuleEvents.GetEnumerator();history.MoveNext();
                var node=context.Node;var player=context.State.FindPlayer(1);EffectStepResult result;
                if(recycle)result=CharacterLifecycleService.Recycle(context,1,false,new[]{card});
                else
                {
                    string reason=CharacterLifecycleService.MoveCard(context,card,FindZone(context.State,"hand",1).ZoneId);
                    Assert.That(reason,Is.Empty);result=EffectStepResult.Completed();
                }
                Assert.That(context.Node,Is.SameAs(node));Assert.That(context.State.FindPlayer(1),Is.SameAs(player));
                Assert.DoesNotThrow(()=>history.MoveNext());advanced=true;history.Dispose();return result;
            });
            using(GameStateCloneService.VerifySchedulingPublicationCopies())
            using(GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="character.item_world.publish")publications++;}))
                Run(state,registry,EffectSpec.Create("test.item_world_history",playerId:1));
            Assert.That(advanced,Is.True);Assert.That(publications,Is.GreaterThan(0));
        }
    }
}
