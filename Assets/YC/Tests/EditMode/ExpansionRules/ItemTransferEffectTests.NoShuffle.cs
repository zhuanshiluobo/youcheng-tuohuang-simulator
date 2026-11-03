using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ItemTransferEffectTests
    {
        [TestCase("operation")][TestCase("seed")][TestCase("permutation")]
        public void CommonV4RejectsShuffleOperationsAndRandomInputsWithoutMutatingDeckOrRandomState(string variant)
        {
            var state=State();string deck=Zone(state,"facility_deck").ZoneId,slot=Zone(state,"facility_supply_0").ZoneId;
            string legal="return function(ctx) return {Effect.MoveItem({apiVersion=4,player=ctx.playerId,operation='transfer',selector={mode='top',zoneId='"+deck+"',count=1,itemTypeId='core.facility_card'},destination={zoneId='"+slot+"'},insertion={kind='top'},operationProfileId='core.facility_card_transfer',requiredCount=0})} end";
            var host=new MoonSharpLuaRuntimeHost();var context=new LuaInvocationContext("api30.no_shuffle","isolated","p1",0,"1.0.0",2,0,0);
            Assert.That(host.Invoke(ShuffleScript(legal),context).IsSuccess,Is.True);
            var args=CommonV4(TopV4(state),slot,"top",0).NormalizedArguments.Clone();string invalid;
            if(variant=="operation") { Value(args,"operation").StringValue="shuffle";invalid=legal.Replace("operation='transfer'","operation='shuffle'"); }
            else
            {
                string field=variant=="seed"?"randomSeed":"permutation";
                args.Properties.Add(new NormalizedValueEntry{Name=field,Value=variant=="seed"?NormalizedValue.CreateInteger(1):Strings(Ids(state,"facility_deck").AsEnumerable().Reverse())});
                args.Properties.Sort((a,b)=>System.StringComparer.Ordinal.Compare(a.Name,b.Name));
                invalid=legal.Replace("requiredCount=0}","requiredCount=0,"+field+(variant=="seed"?"=1}":"={'"+Ids(state,"facility_deck")[0]+"'}}"));
            }
            Assert.That(args.IsValid(),Is.True,"手写反例本身满足NormalizedValue约束，必须到达业务参数拒绝。");
            string zones=JsonConvert.SerializeObject(state.ItemZones),random=JsonConvert.SerializeObject(state.RandomRuntime);
            Assert.That(host.Invoke(ShuffleScript(invalid),context).IsSuccess,Is.False);
            var node=Run(state,EffectSpec.Create(TypeId,args,1));Assert.That(node.Status,Is.EqualTo(EffectNodeStatus.Failed));Assert.That(node.PositionChanges,Is.Empty);
            Assert.That(JsonConvert.SerializeObject(state.ItemZones),Is.EqualTo(zones));Assert.That(JsonConvert.SerializeObject(state.RandomRuntime),Is.EqualTo(random));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==DeckShuffleEffectExecutor.TypeId),Is.False);
        }
    }
}
