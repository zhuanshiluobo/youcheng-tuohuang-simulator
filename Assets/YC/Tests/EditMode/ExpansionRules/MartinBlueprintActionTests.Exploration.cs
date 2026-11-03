using System.Linq;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Exploration;
using YC.Domain.Facilities;
using YC.Domain.Maps;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class MartinBlueprintActionTests
    {
        [Test] public void M05UnifiedExploreWithoutLegalTargetStillStartsItsOneRealAtomicPair()
        {
            // 当前隔离夹具没有场上城市，明确只覆盖M05无合法探索目标后的真实续接。
            var state = State("entrepreneur.round.M.1", true); ActivateFace(state, "entrepreneur.round.M.1", "M05");
            var blueprint = Blueprint(state, "entrepreneur.facility.M03"); var supply = Supply(state, "entrepreneur.facility.M03", 0);
            Action(state, "M05"); Answer(state, NormalizedValue.CreateString("explore"));
            UntilPrompt(state, "core:entrepreneur.M.blueprint.choose_supply");
            var explore = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == ExplorationEffectTypeIds.Explore && ExplorationContract.IsV3(n.NormalizedArguments));
            Assert.That(explore.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(Text(explore.NormalizedResult, "outcome"), Is.EqualTo("no_legal_target"));
            Assert.That(Flag(explore.NormalizedArguments, "consumeMainAction"), Is.False);
            state = Recover(state, "M05探索无目标后原交换待答"); Answer(state, NormalizedValue.CreateString(supply.ItemId)); Answer(state, NormalizedValue.CreateString(blueprint.ItemId));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == supply.ItemId).ZoneId, Is.EqualTo(BlueprintZone(state)));
            Assert.That(FacilityLayerQuery.Supply(state).Single(s => s.SlotIndex == 0).TopItemId, Is.EqualTo(blueprint.ItemId));
            Assert.That(state.EffectRuntime.EffectNodes.Count(IsActualMartinBatch), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.SourceEffectId == explore.EffectId && e.EventType == "EffectCompleted"), Is.EqualTo(1));
            Recover(state, "M05探索分支只交换一次完成");
        }
    }
}
