using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ItemTransferEffectTests
    {
        private static EffectSpec ExplicitFacility(int player,string item,string source,string destination,int required=1)
        {
            var spec=MoveSpec(player,new[]{item},source,destination,"top",required);spec.EffectTypeId=LuaDomainEffectTypeIds.MoveFacilityCard;Value(spec.NormalizedArguments,"apiVersion").IntegerValue=2;return spec;
        }
        [Test]
        public void FacilityExplicitCommonMatchesDirectActualStateAndProjectsOriginalResult()
        {
            var initial=State();var registry=Registry();string item=Ids(initial,"facility_deck")[0],source=Zone(initial,"facility_deck").ZoneId,target=Zone(initial,"facility_supply_0").ZoneId;
            var state=GameStateCloneService.DeepClone(initial);var node=Run(state,ExplicitFacility(1,item,source,target),registry);var body=SpecializedItemMovement.Body(state,node);
            Assert.That(node.PositionChanges,Is.Empty);Assert.That(body.CommonItemMovementV4.Receipt.Result.MovedItemIds,Is.EqualTo(new[]{item}));
            var direct=GameStateCloneService.DeepClone(initial);Run(direct,EffectSpec.Create(TypeId,body.NormalizedArguments.Clone(),1),registry);
            Assert.That(JsonConvert.SerializeObject(state.ItemZones),Is.EqualTo(JsonConvert.SerializeObject(direct.ItemZones)));Assert.That(Number(node.NormalizedResult,"movedCount"),Is.EqualTo(1));
            var unchanged=Run(state,ExplicitFacility(1,item,target,target,0),registry);Assert.That(Number(unchanged.NormalizedResult,"movedCount"),Is.Zero);Assert.That(Text(unchanged.NormalizedResult,"reasonCode"),Is.EqualTo("no_candidates"));Restore(state,registry);
        }
        [Test]
        public void FacilityExplicitOldExecutionDefinitionIsRejectedWithoutArchiveRewrite()
        {
            var state=State();var registry=Registry();string item=Ids(state,"facility_deck")[0];var node=Run(state,ExplicitFacility(1,item,Zone(state,"facility_deck").ZoneId,Zone(state,"facility_supply_0").ZoneId),registry);
            var raw=JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));raw["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"].Single(n=>(string)n["EffectId"]==node.EffectId)["DefinitionVersion"]="3.0.0";
            string bad=raw.ToString();Assert.That(HostRecoveryService.RecoverJson(bad,registry).Succeeded,Is.False);Assert.That(raw.ToString(),Is.EqualTo(bad));
        }
    }
}
