using System.Linq;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class InfectedCharacterEffectsTests
    {
        [TestCase(0)][TestCase(2)]
        public void TinManTacticV4RecallsActualDiscardBeforeRewards(int count)
        {
            var state=State();string hand=FindZone(state,"hand",1).ZoneId,discard=FindZone(state,"discard",1).ZoneId;
            var source=state.ItemZones.Items.First(i=>i.DefinitionId=="tin-man"&&i.ZoneId==hand);
            var cards=state.ItemZones.Items.Where(i=>i.ZoneId==hand&&i.ItemId!=source.ItemId).Take(count).Select(i=>i.ItemId).ToArray();Assert.That(cards.Length,Is.EqualTo(count));
            if(count>0)Run(state,MoveSpec(1,cards,hand,discard,"unordered",count,"core.character_card_transfer"));
            int gold=state.FindPlayer(1).Resources.GoldVoucher;
            Run(state,EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId,playerId:1));Answer(state,source.ItemId);Run(state,CharacterLifecycleService.CreateUseSpec(1,source.ItemId,"tactic"));
            var recall=state.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==TypeId&&Number(n.NormalizedArguments,"apiVersion")==4&&Text(n.NormalizedArguments,"operationProfileId")=="core.character_card_transfer");
            Assert.That(recall.Status,Is.EqualTo(EffectNodeStatus.Completed));Assert.That(Number(recall.NormalizedResult,"movedCount"),Is.EqualTo(count));Assert.That(cards.All(id=>state.ItemZones.Items.Single(i=>i.ItemId==id).ZoneId==hand),Is.True);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold));
            state=Restore(state);
            for(int i=0;i<count;i++){Assert.That(Open(state).CandidateIds,Does.Contain("gain-gold"));Answer(state,"gain-gold");}
            Assert.That(Open(state),Is.Null);Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold+5*count));Assert.That(recall.PositionChanges.Count,Is.EqualTo(count));
            state=Restore(state);Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==source.ItemId).ZoneId,Is.EqualTo(discard));
        }
    }
}
