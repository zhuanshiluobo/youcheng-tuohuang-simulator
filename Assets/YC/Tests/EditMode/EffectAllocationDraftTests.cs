using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Presentation;
using YC.Domain.State;
using YC.Presentation.Workflows;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode
{
    public sealed class EffectAllocationDraftTests
    {
        internal static InteractionRequest Request(bool end=true,int total=3,int aMax=3,int bMax=3) => new InteractionRequest {
            InteractionId="allocation-ui",RequestId="allocation-ui",SourceNodeId="allocation-owner",OwnerEffectId="allocation-owner",InteractionTypeId=AllocationEffects.InteractionType,AnswerSchema=AllocationEffects.AnswerSchema,
            AnsweringPlayerId=1,Visibility="owner",Status="open",CandidateSetId="allocation-candidates",CandidateSetVersion=1,StateRevision=1,MinSelections=0,MaxSelections=99,CandidateIds=new List<string>{"A","B","C"},
            PromptKey="effect.resource.choose_type", // 旧提示键不得抢走正式类型的路由。
            PromptParameters=Object("totalExecutions",NormalizedValue.CreateInteger(total),"allowEndAction",NormalizedValue.CreateBoolean(end),"rowMaximums",Object("A",NormalizedValue.CreateInteger(aMax),"B",NormalizedValue.CreateInteger(bMax),"C",NormalizedValue.CreateInteger(0))),
            Presentation=new GameplayInteractionPresentation {Prompt=new GameplayTextRef("core:test.allocation.prompt"),QuantityRows=new List<GameplayQuantityRowPresentation>{
                new GameplayQuantityRowPresentation{RowId="A",Label=new GameplayTextRef("core:test.allocation.same_text")},new GameplayQuantityRowPresentation{RowId="B",Label=new GameplayTextRef("core:test.allocation.same_text")},new GameplayQuantityRowPresentation{RowId="C",Label=new GameplayTextRef("core:test.allocation.zero")}}}
        };
        private static EffectAllocationDraft Draft(InteractionRequest request=null)
        {var d=new EffectAllocationDraft();d.Bind("game","hash",1,InteractionRequestProjector.ProjectForPlayer(request??Request(),1));return d;}
        [Test] public void OrderedAppendDecreaseAndDirectInputPreserveOccurrenceIdentities()
        {
            var d=Draft();Assert.That(d.TrySetAmount(0,1),Is.True);Assert.That(d.TrySetAmount(1,1),Is.True);Assert.That(d.TrySetAmount(0,2),Is.True);
            var ids=d.Entries.Select(e=>e.Identity).ToArray();Assert.That(d.Entries.Select(e=>e.OptionId),Is.EqualTo(new[]{"A","B","A"}));Assert.That(ids.Distinct().Count(),Is.EqualTo(3));
            Assert.That(d.TrySetAmount(0,1),Is.True);Assert.That(d.Entries.Select(e=>e.OptionId),Is.EqualTo(new[]{"A","B"}));Assert.That(d.Entries.Select(e=>e.Identity),Is.EqualTo(ids.Take(2)));
            Assert.That(d.TrySetAmount(0,2),Is.True);Assert.That(d.Entries.Last().Identity,Is.GreaterThan(ids[2]));Assert.That(d.TrySetAmount(0,0),Is.True);Assert.That(d.Entries.Single().OptionId,Is.EqualTo("B"));
        }
        [TestCase(-1)][TestCase(4)][TestCase(100)] public void InvalidAmountsNeverClampOrChangeTheOriginalQueue(int amount)
        {
            var d=Draft();d.TrySetAmount(0,1);d.TrySetAmount(1,1);var before=JsonConvert.SerializeObject(new{d.Entries,d.Counts});Assert.That(d.TrySetAmount(0,amount),Is.False);Assert.That(JsonConvert.SerializeObject(new{d.Entries,d.Counts}),Is.EqualTo(before));Assert.That(d.TrySetAmount(2,1),Is.False);
        }
        [Test] public void SameRequestRefreshKeepsFullOrderAndNewCandidateVersionClearsBothCountsAndQueue()
        {
            var q=Request();var d=Draft(q);d.TrySetAmount(0,1);d.TrySetAmount(1,1);d.TrySetAmount(0,2);var before=d.Entries.Select(e=>e.Identity).ToArray();q.StateRevision++;
            Assert.That(d.Bind("game","hash",1,InteractionRequestProjector.ProjectForPlayer(q,1)),Is.False);Assert.That(d.Entries.Select(e=>e.Identity),Is.EqualTo(before));
            q.CandidateSetVersion++;Assert.That(d.Bind("game","hash",1,InteractionRequestProjector.ProjectForPlayer(q,1)),Is.True);Assert.That(d.Entries,Is.Empty);Assert.That(d.Counts,Is.EqualTo(new[]{0,0,0}));Assert.That(d.ResetNotice,Is.True);
        }
        [TestCase(false)][TestCase(true)] public void ExactConfirmAndExplicitEndAreIndependentAndSealOnce(bool end)
        {
            var d=Draft(Request(end));Assert.That(d.TrySeal("confirm",out _),Is.False);Assert.That(d.TrySeal("end_action",out var answer),Is.EqualTo(end));
            if(end){Assert.That(Value(answer,"sequence").Items,Is.Empty);Assert.That(d.TrySeal("end_action",out _),Is.False);Assert.That(d.TrySetAmount(0,1),Is.False);d.Reject();}
            d.TrySetAmount(0,1);d.TrySetAmount(1,1);d.TrySetAmount(0,2);Assert.That(d.TrySeal("confirm",out answer),Is.True);Assert.That(answer.Properties.Select(p=>p.Name),Is.EquivalentTo(new[]{"sequence","submitKind","candidateSetId","candidateSetVersion"}));Assert.That(Value(answer,"sequence").Items.Select(v=>v.StringValue),Is.EqualTo(new[]{"A","B","A"}));
        }
        [Test] public void RealNinetyNineEntriesRoundTripTheBoundedCommandCodec()
        {
            var d=Draft(Request(true,99,99,0));Assert.That(d.TrySetAmount(0,99),Is.True);Assert.That(d.TrySeal("confirm",out var a),Is.True);Assert.That(AllocationSequenceAnswerJson.TryDecode(AllocationSequenceAnswerJson.Encode(a),out var parsed),Is.True);Assert.That(Value(parsed,"sequence").Items.Count,Is.EqualTo(99));Assert.That(parsed.ToDeterministicString(),Is.EqualTo(a.ToDeterministicString()));
        }
        [TestCase("{\"sequence\":[],\"submitKind\":\"end_action\",\"candidateSetId\":\"c\",\"candidateSetVersion\":\"1\"}")]
        [TestCase("{\"sequence\":[],\"submitKind\":\"end_action\",\"candidateSetId\":\"c\",\"candidateSetVersion\":1.0}")]
        [TestCase("{\"sequence\":[],\"submitKind\":\"end_action\",\"candidateSetId\":\"c\",\"candidateSetVersion\":1,\"counts\":{}}")]
        [TestCase("{\"sequence\":[],\"sequence\":[\"A\"],\"submitKind\":\"end_action\",\"candidateSetId\":\"c\",\"candidateSetVersion\":1}")]
        [TestCase("{\"sequence\":[1],\"submitKind\":\"confirm\",\"candidateSetId\":\"c\",\"candidateSetVersion\":1}")]
        public void CommandCodecRejectsCoercionUnknownFieldsAndDuplicateFields(string json)=>Assert.That(AllocationSequenceAnswerJson.TryDecode(json,out _),Is.False);
        [Test] public void TypedAllocationProjectionKeepsItsOwnRowsAndHidesThemFromOtherPlayers()
        {
            var q=Request();Assert.That(InteractionRequestProjector.ProjectForPlayer(q,1).Presentation.QuantityRows.Select(r=>r.RowId),Is.EqualTo(new[]{"A","B","C"}));Assert.That(InteractionRequestProjector.ProjectForPlayer(q,2).Presentation,Is.Null);
        }
    }
}
