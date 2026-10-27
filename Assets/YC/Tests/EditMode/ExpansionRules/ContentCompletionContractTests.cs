using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Application.Sessions;
using YC.Application.Interactions;
using YC.Domain.Commands;
using System.Globalization;
using YC.Domain.Cards;
using YC.Domain.State;
using YC.Domain.Rules;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class ContentCompletionContractTests
    {
        private const string Hold = "qy007.content.required_response";
        [TestCase(PlayerStageSchedulingMode.Sequential,"A09")]
        [TestCase(PlayerStageSchedulingMode.Concurrent,"A08")]
        public void RealWholeCoverWaitsForRequiredResponseAndRestoresExactlyOnce(PlayerStageSchedulingMode scheduling,string face)
        {
            using var f=new PaulContentTestFixture(primaryEntrepreneur:"entrepreneur.ability.A01",schedulingMode:scheduling,setupOnly:true);
            f.Registry.Register(Hold,c=>c.GetLatestInteractionAnswer()!=null ? EffectStepResult.Completed() : EffectStepResult.Continue("waiting").AddInteraction(new EffectInteractionSpec {
                InteractionTypeId=Hold,AnsweringPlayerId=1,Visibility="owner",AnswerSchema="candidate_id",MinSelections=1,MaxSelections=1,CandidateIds={"continue"}}),EffectExecutorKind.IntrinsicFlow);
            f.Registry.RegisterEventHandler(new EffectEventHandlerRegistration("qy007.cover.hold","CharacterCardCovered","*","qy007.cover.hold",c=>
                c.Event.PlayerId==1 ? new List<EffectSpec>{EffectSpec.Create(Hold,playerId:1)} : new List<EffectSpec>()));
            var launch=f.Rounds.CreateRound(f.State,1,1); Assert.That(launch.IsValid,Is.True,launch.Reason);
            for(int guard=0;guard<200 && !f.Open.Any(q=>q.InteractionTypeId==Hold);guard++)
            {
                var q=f.Open.LastOrDefault(); if(q==null){var advance=f.Rounds.Advance(f.State);Assert.That(advance.IsValid,Is.True,advance.Reason);f.Pump();continue;}
                if(q.InteractionTypeId==EntrepreneurFlow.FaceInteraction && q.AnsweringPlayerId==1) f.Answer(q,"entrepreneur.candidate."+face);
                else if(q.AllowDecline && !q.CandidateIds.Contains("decline")) f.Decline(q);
                else if(q.MaxSelections>1) { var session=new GameSession(f.State);session.RegisterHandler(new AnswerInteractionCommandHandler(f.Registry,f.Rounds));var command=new GameCommand{Kind=GameCommandKind.AnswerInteraction,PlayerId=q.AnsweringPlayerId,OptionIds=q.CandidateIds.Take(q.MinSelections).ToList()};command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter]=q.GetStableInteractionId();command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter]=q.StateRevision.ToString(CultureInfo.InvariantCulture);var result=session.Submit(command);Assert.That(result.Succeeded,Is.True,result.Validation?.Reason);f.Pump(); }
                else f.Answer(q,q.CandidateIds.Contains("decline")?"decline":q.CandidateIds.First());
            }
            Assert.That(f.Open.Any(q=>q.InteractionTypeId==Hold),Is.True);
            // 并行盖放在p1必要响应未结清时，仍可合法回答其他玩家的主牌。
            foreach(var q in f.Open.Where(q=>q.InteractionTypeId==CharacterCoverEffectExecutor.InteractionTypeId).ToList()) f.Answer(q,q.CandidateIds.First());
            Assert.That(f.State.EffectRuntime.RuleEvents.Count(e=>e.EventType=="CharacterCoverCompleted"),Is.Zero);
            Assert.That(f.State.EffectRuntime.AbilitySourceUses.Count(r=>r.AbilityId=="entrepreneur.A01.cover"),Is.Zero);
            f.Restore("真实盖放必要响应待答"); f.Answer(f.Open.Single(q=>q.InteractionTypeId==Hold),"continue");
            for(int guard=0;guard<30 && !f.State.EffectRuntime.RuleEvents.Any(e=>e.EventType=="CharacterCoverCompleted");guard++)
            { var q=f.Open.LastOrDefault(); if(q==null){var advance=f.Rounds.Advance(f.State);Assert.That(advance.IsValid,Is.True,advance.Reason);f.Pump();continue;} if(q.AllowDecline && !q.CandidateIds.Contains("decline")) f.Decline(q); else f.Answer(q,q.CandidateIds.First()); }
            var evt=f.State.EffectRuntime.RuleEvents.Single(e=>e.EventType=="CharacterCoverCompleted");
            Assert.That(evt.PlayerId,Is.EqualTo(-1));
            Assert.That(Value(evt.Payload,"coveredPlayerIds").Items.Select(v=>v.ReferenceId),Is.EquivalentTo(new[]{"p1","p2"}));
            Assert.That(f.State.EffectRuntime.AbilitySourceUses.Count(r=>r.AbilityId=="entrepreneur.A01.cover"),Is.EqualTo(1));
            // A01正文实际构造的可放弃取得窗口继续走真实回答；不以C#写收益。
            foreach(var q in f.Open.Where(q=>q.OwnerEffectId!=null && f.State.EffectRuntime.EffectNodes.Any(n=>n.EffectId==q.OwnerEffectId && n.EffectTypeId==LuaDomainEffectTypeIds.Choice)).ToList())
                f.Answer(q,q.CandidateIds.Contains("1")?"1":q.CandidateIds.First());
            f.Restore("真实全阶段完成后"); f.Pump();
            Assert.That(f.State.EffectRuntime.RuleEvents.Count(e=>e.EventType=="CharacterCoverCompleted"),Is.EqualTo(1));
            Assert.That(f.State.EffectRuntime.AbilitySourceUses.Count(r=>r.AbilityId=="entrepreneur.A01.cover"),Is.EqualTo(1));
        }
    }
}
