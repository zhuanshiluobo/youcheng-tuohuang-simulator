using System.Globalization;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ItemTransferEffectTests
    {
        [TestCase(false)][TestCase(true)] public void AllocationCommandUsesTheRealHostAnswerProtocolAndDoesNotEndThePlayerTurn(bool end)
        {
            var s=State();var r=AllocationRegistry();var root=Run(s,AllocationSpec(3,end,2),r);var q=Open(s);var command=EffectInteractionCommands.Answer(InteractionRequestProjector.ProjectForPlayer(q,1),1,null);
            command.Parameters[AllocationSequenceAnswerJson.Parameter]=AllocationSequenceAnswerJson.Encode(AllocationAnswer(q,end?"":"A,B,A",end?"end_action":"confirm"));
            var result=new AnswerInteractionCommandHandler(r).Handle(s,command);Assert.That(result.Succeeded,Is.True,result.LogMessage);Assert.That(s.CurrentPlayerId,Is.EqualTo(1));Assert.That(s.FindPlayer(1).Score,Is.EqualTo(end?0:121));
            string before=JsonConvert.SerializeObject(s);Assert.That(new AnswerInteractionCommandHandler(r).Handle(s,command).Succeeded,Is.False);Assert.That(JsonConvert.SerializeObject(s),Is.EqualTo(before));Restore(s,r);
        }
        [TestCase("revision")][TestCase("candidate_version")][TestCase("sequence")][TestCase("player")][TestCase("mixed")][TestCase("legacy_schema")]
        public void AllocationCommandRejectsStaleOrMixedInputWithoutClosingTheRequest(string damage)
        {
            var s=State();var r=AllocationRegistry();Run(s,AllocationSpec(3,true,2),r);var q=Open(s);var answer=AllocationAnswer(q,"A,B,A");var command=EffectInteractionCommands.Answer(InteractionRequestProjector.ProjectForPlayer(q,1),1,null);
            if(damage=="revision")command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter]=(s.EffectRuntime.StateRevision+1).ToString(CultureInfo.InvariantCulture);
            if(damage=="candidate_version")YC.Domain.Effects.ItemTransferEffectExecutor.Value(answer,"candidateSetVersion").IntegerValue++;
            if(damage=="sequence")answer=AllocationAnswer(q,"A,A,A");if(damage=="player")command.PlayerId=2;if(damage=="mixed")command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter]="false";
            if(damage=="legacy_schema")q.AnswerSchema="candidate_id";
            command.Parameters[AllocationSequenceAnswerJson.Parameter]=AllocationSequenceAnswerJson.Encode(answer);string before=JsonConvert.SerializeObject(s);Assert.That(new AnswerInteractionCommandHandler(r).Handle(s,command).Succeeded,Is.False,damage);Assert.That(JsonConvert.SerializeObject(s),Is.EqualTo(before));Assert.That(q.Status,Is.EqualTo("open"));
        }
    }
}
