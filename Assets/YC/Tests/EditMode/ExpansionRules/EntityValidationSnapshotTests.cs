using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Cards;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 这里只构造隔离校验输入，不计作合法行动或正常界面验收。
    public sealed class EntityValidationSnapshotTests
    {
        private static readonly Type SnapshotType=typeof(ItemZoneQuery).Assembly.GetType("YC.Domain.Items.EntityValidationSnapshot",true);
        private static IDisposable Begin() => (IDisposable)SnapshotType.GetMethod("Begin",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
        private static bool Original(GameState state,out string reason)
        {using var full=ItemZoneQuery.UseFullValidationSnapshotsForDiagnostics();return new ItemZoneQuery().TryValidate(state,out reason);}
        private static void SameAsOriginal(ItemZoneQuery query,GameState state)
        {bool expected=Original(state,out var expectedReason);bool actual=query.TryValidate(state,out var actualReason);Assert.That(actual,Is.EqualTo(expected));Assert.That(actualReason,Is.EqualTo(expectedReason));}

        [TestCase("zone")][TestCase("schema")][TestCase("legacy")]
        public void SameRevisionMutationsAreComparedAgainstTheIndependentValidInput(string mutation)
        {
            using var fixture=new PaymentTestFixture();var state=fixture.State;var query=new ItemZoneQuery();
            using var scope=Begin();using var verify=ItemZoneQuery.VerifyValidationSnapshotsForDiagnostics();
            int reused=0;
            using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="entity.validation.reused")reused++;});
            Assert.That(query.TryValidate(state,out var reason),Is.True,reason);
            state.EffectRuntime.StateRevision++;
            Assert.That(query.TryValidate(state,out reason),Is.True,reason);
            Assert.That(reused,Is.GreaterThan(0),"只改实体校验不读取的revision仍可复用");
            int before=reused,revision=state.EffectRuntime.StateRevision;
            if(mutation=="zone")state.ItemZones.Items[0].ZoneId="isolated.unknown.zone";
            else if(mutation=="schema")state.EntitySchemaVersion=0;
            else state.Players[0].HandCardIds.Values.Add("isolated.raw.write");
            SameAsOriginal(query,state);
            Assert.That(query.TryValidate(state,out reason),Is.False,reason);
            Assert.That(state.EffectRuntime.StateRevision,Is.EqualTo(revision));
            Assert.That(reused,Is.EqualTo(before),"同revision的坏输入不能取得旧校验结果");
        }

        [Test]
        public void UnrelatedOpenRequestRevisionChangesDoNotInvalidateTheEntityInputs()
        {
            using var fixture=new PaymentTestFixture();var query=new ItemZoneQuery();int reused=0;
            var request=new InteractionRequest{InteractionId="isolated.unrelated",RequestId="isolated.unrelated",Status="open"};
            fixture.State.EffectRuntime.InteractionRequests.Add(request);
            using var scope=Begin();using var verification=ItemZoneQuery.VerifyValidationSnapshotsForDiagnostics();
            using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="entity.validation.reused")reused++;});
            Assert.That(query.TryValidate(fixture.State,out var reason),Is.True,reason);
            request.StateRevision++;request.AnsweredAtRevision++;request.Presentation=new YC.Domain.Presentation.GameplayInteractionPresentation();
            Assert.That(query.TryValidate(fixture.State,out reason),Is.True,reason);Assert.That(reused,Is.EqualTo(1));
        }

        [TestCase(0)][TestCase(-1)][TestCase(int.MinValue)][TestCase(int.MaxValue)]
        public void DerivedScoreBindingKeepsUnchangedCanonicalSlotsAndInvalidatesChangedScores(int score)
        {
            using var fixture=new PaymentTestFixture();var state=fixture.State;var query=new ItemZoneQuery();int reused=0;
            var zone=state.ItemZones.Zones.Single(z=>z.TemplateId=="core.zone.score_track");
            var item=state.ItemZones.Items.First(i=>i.TypeId=="core.player_marker" && i.OwnerSubject.PlayerId==1);
            foreach(var order in state.ItemZones.Orders)order.ItemIds.Remove(item.ItemId);
            item.ZoneId=zone.ZoneId;state.ItemZones.Orders.Find(o=>o.ZoneId==zone.ZoneId)?.ItemIds.Add(item.ItemId);state.FindPlayer(1).Score=score;
            var bind=typeof(ItemZoneStateFactory).GetMethod("BindDerivedScoreMarkers",BindingFlags.Static|BindingFlags.NonPublic);
            bind.Invoke(null,new object[]{state});
            Assert.That(item.SlotId,Is.EqualTo("score_"+score.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            using var scope=Begin();using var verification=ItemZoneQuery.VerifyValidationSnapshotsForDiagnostics();
            using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="entity.validation.reused")reused++;});
            Assert.That(query.TryValidate(state,out var reason),Is.True,reason);
            bind.Invoke(null,new object[]{state});Assert.That(query.TryValidate(state,out reason),Is.True,reason);Assert.That(reused,Is.EqualTo(1));
            item.SlotId="score_incorrect";bind.Invoke(null,new object[]{state});SameAsOriginal(query,state);
            Assert.That(item.SlotId,Is.EqualTo("score_"+score.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        [Test]
        public void UnreadRuleAndPresentationFieldsCannotForceNewEntitySnapshots()
        {
            using var fixture=new PaymentTestFixture();var state=fixture.State;var query=new ItemZoneQuery();int reused=0;
            using var scope=Begin();using var verification=ItemZoneQuery.VerifyValidationSnapshotsForDiagnostics();
            using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="entity.validation.reused")reused++;});
            Assert.That(query.TryValidate(state,out var reason),Is.True,reason);
            state.Round++;state.ActionRound++;state.CurrentPlayerId=2;
            state.ContentDefinitions.Characters.Clear();state.ContentDefinitions.Facilities.Clear();
            state.CharacterUses.Add(new CharacterUseRuntimeState());
            Assert.That(query.TryValidate(state,out reason),Is.True,reason);Assert.That(reused,Is.EqualTo(1));
            state.ItemZones.Items[0].ZoneId="isolated.bad_input";SameAsOriginal(query,state);
            Assert.That(query.TryValidate(state,out reason),Is.False,reason);Assert.That(reused,Is.EqualTo(1));
        }

        [Test]
        public void SnapshotIsIndependentFromTheSourceAndIsReleasedWhenTheScopeEnds()
        {
            using var fixture=new PaymentTestFixture();var query=new ItemZoneQuery();var scope=Begin();int reused=0;
            using(var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="entity.validation.reused")reused++;}))
            using(scope)
            {
                Assert.That(query.TryValidate(fixture.State,out var reason),Is.True,reason);
                var other=GameStateCloneService.DeepClone(fixture.State);
                fixture.State.ItemZones.Items[0].ZoneId="isolated.old.source.changed";
                Assert.That(query.TryValidate(other,out reason),Is.True,reason);
                Assert.That(reused,Is.EqualTo(1),"基线不能借用被外部修改的原始世界对象");
            }
            Assert.That(SnapshotType.GetField("world",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(scope),Is.Null);
            Assert.That(SnapshotType.GetField("requests",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(scope),Is.Null);
            Assert.That(SnapshotType.GetField("current",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null),Is.Null);
        }

        [Test]
        public void StagingOwnerExistenceAndTerminalStatusAreAlwaysRechecked()
        {
            using var fixture=new PaymentTestFixture();var state=fixture.State;var query=new ItemZoneQuery();
            var owner=new EffectNodeRuntimeState{EffectId="isolated.staging.owner",PlayerId=1,Status=EffectNodeStatus.Running};state.EffectRuntime.EffectNodes.Add(owner);
            var declaration=new CardZoneDefinition{ZoneTemplateId="core.zone.effect_staging",LocalZoneId="isolated_staging",VisibilityPolicyId=ItemZoneVisibility.Owner,
                AcceptedItemKinds=new List<ItemPhysicalKind>{ItemPhysicalKind.Cube},CapacityPolicy="unbounded",EntryPolicyId="core.explicit_transfer",ExitPolicyId="core.explicit_transfer",CarrierExitPolicyId="core.preserve_contents"};
            Assert.That(new ItemZoneStateFactory().TryCreateEffectStaging(state,owner.EffectId,declaration,out var zone,out var reason),Is.True,reason);
            owner=state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==owner.EffectId);
            var item=state.ItemZones.Items.First(i=>i.TypeId=="core.player_marker" && i.OwnerSubject.PlayerId==1);
            foreach(var order in state.ItemZones.Orders)order.ItemIds.Remove(item.ItemId);
            item.ZoneId=zone;item.SlotId="";
            using var scope=Begin();using var verify=ItemZoneQuery.VerifyValidationSnapshotsForDiagnostics();
            Assert.That(query.TryValidate(state,out reason),Is.True,reason);
            Assert.That(query.TryValidate(state,out reason),Is.True,reason);
            owner.Status=EffectNodeStatus.Completed;SameAsOriginal(query,state);
            Assert.That(query.TryValidate(state,out reason),Is.False);Assert.That(reason,Is.EqualTo("staging_not_cleared"));
            owner.Status=EffectNodeStatus.Running;Assert.That(query.TryValidate(state,out reason),Is.True,reason);
            state.EffectRuntime.EffectNodes.Clear();SameAsOriginal(query,state);
            Assert.That(query.TryValidate(state,out reason),Is.False);Assert.That(reason,Is.EqualTo("OwnerEffectId_required"));
        }

        [Test]
        public void SourceSelectionReceiptCannotReuseAnOldAnsweredRequest()
        {
            using var fixture=new PaymentTestFixture();var state=fixture.State;var query=new ItemZoneQuery();
            var item=state.ItemZones.Items.First(i=>i.DefinitionId=="test.payment.source_0" && i.ControllerSubject.PlayerId==1);
            const string requestId="isolated.selection",owner="isolated.owner",face="isolated.face",candidate="isolated.candidate";
            item.LocalState=Object("activeFaceId",NormalizedValue.CreateString(face),"activationAuthorizationRef",NormalizedValue.CreateString(owner),
                "sourceSelectionReceipt",Object("protocolVersion",NormalizedValue.CreateInteger(1),"requestId",NormalizedValue.CreateString(requestId),"ownerEffectId",NormalizedValue.CreateString(owner),"candidateId",NormalizedValue.CreateString(candidate),"faceId",NormalizedValue.CreateString(face)));
            var request=new InteractionRequest{InteractionId=requestId,RequestId=requestId,OwnerEffectId=owner,AnsweringPlayerId=1,Status="answered",NormalizedAnswer=NormalizedValue.CreateString(candidate),CandidateIds=new List<string>{candidate},
                PromptParameters=Object("abilitySourceCandidates",NormalizedValue.CreateArray(new List<NormalizedValue>{Object("candidateId",NormalizedValue.CreateString(candidate),"itemId",NormalizedValue.CreateString(item.ItemId),"definitionId",NormalizedValue.CreateString(item.DefinitionId),"faceId",NormalizedValue.CreateString(face))}))};
            state.EffectRuntime.InteractionRequests.Add(request);
            using var scope=Begin();using var verify=ItemZoneQuery.VerifyValidationSnapshotsForDiagnostics();
            Assert.That(query.TryValidate(state,out var reason),Is.True,reason);Assert.That(query.TryValidate(state,out reason),Is.True,reason);
            request.NormalizedAnswer=NormalizedValue.CreateString("isolated.wrong.answer");SameAsOriginal(query,state);
            Assert.That(query.TryValidate(state,out reason),Is.False);Assert.That(reason,Is.EqualTo("ability_source_local_state_invalid"));
        }

        [Test]
        public void ExplicitFrozenBuiltinSchemasCanReuseButCustomDefinitionsCannot()
        {
            using var fixture=new PaymentTestFixture();int reused=0;
            var types=(ItemTypeRegistry)typeof(ItemZoneQuery).GetProperty("BuiltInTypes",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
            var templates=(ZoneTemplateRegistry)typeof(ItemZoneQuery).GetProperty("BuiltInTemplates",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
            using var scope=Begin();using var verification=ItemZoneQuery.VerifyValidationSnapshotsForDiagnostics();
            using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="entity.validation.reused")reused++;});
            Assert.That(new ItemZoneQuery().TryValidate(fixture.State,out var reason),Is.True,reason);
            Assert.That(new ItemZoneQuery(types,templates).TryValidate(fixture.State,out reason),Is.True,reason);Assert.That(reused,Is.EqualTo(1));
            Assert.That(new ItemZoneQuery(ItemTypeRegistry.CreateBuiltIn()).TryValidate(fixture.State,out reason),Is.True,reason);Assert.That(reused,Is.EqualTo(1));
        }

        [Test]
        public void CustomSchemasAndUnscopedCallsKeepTheOriginalPath()
        {
            using var fixture=new PaymentTestFixture();int reused=0;
            using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="entity.validation.reused")reused++;});
            var builtin=new ItemZoneQuery();Assert.That(builtin.TryValidate(fixture.State,out var reason),Is.True,reason);
            using(var scope=Begin())
            {
                var custom=new ItemZoneQuery(ItemTypeRegistry.CreateBuiltIn());
                Assert.That(custom.TryValidate(fixture.State,out reason),Is.True,reason);Assert.That(custom.TryValidate(fixture.State,out reason),Is.True,reason);
                Assert.That(builtin.TryValidate(fixture.State,out reason),Is.True,reason);
            }
            Assert.That(builtin.TryValidate(fixture.State,out reason),Is.True,reason);
            Assert.That(reused,Is.Zero,"显式注册表和跨scope调用不能复用凭据");
        }
    }
}
