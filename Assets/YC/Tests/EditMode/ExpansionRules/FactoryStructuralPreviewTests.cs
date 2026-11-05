using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 结构事务的隔离/回滚验证，不计作正常建局或可见页面验收。
    public sealed class FactoryStructuralPreviewTests
    {
        private static CardZoneDefinition Declaration(bool invalid=false)=>new CardZoneDefinition
        {
            ZoneTemplateId="core.zone.effect_staging",LocalZoneId="isolated_staging",VisibilityPolicyId=ItemZoneVisibility.Owner,
            AcceptedItemKinds=new List<ItemPhysicalKind>{ItemPhysicalKind.Cube},CapacityPolicy=invalid?"isolated.invalid":"unbounded",
            EntryPolicyId="core.explicit_transfer",ExitPolicyId="core.explicit_transfer",CarrierExitPolicyId="core.preserve_contents"
        };
        private static GameState State(PaymentTestFixture fixture,bool alias)
        {
            var state=GameStateCloneService.DeepClone(fixture.State);
            var node=new EffectNodeRuntimeState{EffectId="isolated.factory.owner",PlayerId=1,Status=EffectNodeStatus.Running};
            if(alias){var shared=NormalizedValue.CreateObject(new List<NormalizedValueEntry>());node.NormalizedArguments=shared;node.NormalizedResult=shared;}
            state.EffectRuntime.EffectNodes.Add(node);return state;
        }
        private static bool Create(GameState state,bool grant,CardZoneDefinition declaration,out string id,out string reason)
        {
            var factory=new ItemZoneStateFactory();
            if(!grant)return factory.TryCreateEffectStaging(state,"isolated.factory.owner",declaration,out id,out reason);
            var zone=state.ItemZones.Zones.First(z=>z.TemplateId=="core.zone.resource_point_markers");
            return factory.TryCreateGrantedItem(state,"isolated.factory.owner","isolated_grant","core.resource_point","core.resource_point_token",zone.ZoneId,new ItemSubjectRef(),out id,out reason);
        }
        [TestCase(false,false)][TestCase(false,true)][TestCase(true,false)][TestCase(true,true)]
        public void FactoryResultsMatchFullCopiesAndAliasedInputsKeepTheOriginalPath(bool grant,bool alias)
        {
            using var fixture=new PaymentTestFixture();var state=State(fixture,alias);var expected=State(fixture,alias);var declaration=Declaration();
            bool original;string expectedId,expectedReason;
            using(ItemTransferService.UseFullStructuralPreviewsForDiagnostics())original=Create(expected,grant,declaration,out expectedId,out expectedReason);
            int previews=0;
            using var verify=ItemTransferService.VerifyStructuralPreviewsForDiagnostics();
            using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="transfer.structural_preview")previews++;});
            bool actual=Create(state,grant,declaration,out var id,out var reason);
            Assert.That(actual,Is.True,reason);Assert.That(original,Is.True,expectedReason);Assert.That(id,Is.EqualTo(expectedId));Assert.That(reason,Is.EqualTo(expectedReason));
            Assert.That(GameStateCloneService.AreEquivalent(state,expected),Is.True,"发布后的所有持久字段应等于原完整事务");
            Assert.That(previews>0,Is.EqualTo(!alias));
            var owner=state.EffectRuntime.EffectNodes.Single(n=>n.EffectId=="isolated.factory.owner");
            var expectedOwner=expected.EffectRuntime.EffectNodes.Single(n=>n.EffectId=="isolated.factory.owner");
            Assert.That(ReferenceEquals(owner.NormalizedArguments,owner.NormalizedResult),Is.EqualTo(ReferenceEquals(expectedOwner.NormalizedArguments,expectedOwner.NormalizedResult)),"目标已有引用关系必须与原完整事务相同");
            if(!grant)
            {
                var created=state.ItemZones.Zones.Single(z=>z.ZoneId==id);created.Configuration.AcceptedItemKinds.Clear();
                Assert.That(declaration.AcceptedItemKinds.Count,Is.EqualTo(1));
            }
        }
        [TestCase(false)][TestCase(true)]
        public void NarrowFactoryPublicationKeepsBorrowedHistoryListsUnwritten(bool grant)
        {
            using var fixture=new PaymentTestFixture();var state=State(fixture,false);var history=state.EffectRuntime;
            var nodes=history.EffectNodes;var player=state.Players[0];var node=nodes[0];
            var iterator=nodes.GetEnumerator();Assert.That(iterator.MoveNext(),Is.True);
            int publications=0;
            using var verification=GameStateCloneService.VerifySchedulingPublicationCopies();
            using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="handler.copy.item_world")publications++;});
            Assert.That(Create(state,grant,Declaration(),out _,out var reason),Is.True,reason);
            Assert.That(publications,Is.EqualTo(1));Assert.That(state.EffectRuntime,Is.SameAs(history));
            Assert.That(state.EffectRuntime.EffectNodes,Is.SameAs(nodes));Assert.That(nodes[0],Is.SameAs(node));
            Assert.That(state.Players[0],Is.SameAs(player));Assert.DoesNotThrow(()=>iterator.MoveNext());iterator.Dispose();
        }
        [TestCase(false)][TestCase(true)]
        public void RejectedCreationLeavesEverySourceFieldUnchanged(bool alias)
        {
            using var fixture=new PaymentTestFixture();var state=State(fixture,alias);var before=GameStateCloneService.DeepClone(state);
            using var verify=ItemTransferService.VerifyStructuralPreviewsForDiagnostics();
            Assert.That(Create(state,false,Declaration(true),out var id,out var reason),Is.False);
            Assert.That(id,Is.Empty);Assert.That(reason,Is.Not.Empty);Assert.That(GameStateCloneService.AreEquivalent(state,before),Is.True);
        }
    }
}
