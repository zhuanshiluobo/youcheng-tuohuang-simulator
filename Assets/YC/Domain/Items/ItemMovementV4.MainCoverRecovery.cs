using System;
using System.Linq;
using System.Globalization;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Items
{
    public static partial class ItemMovementV4
    {
        private static bool ValidateMainCoverEvent(GameState state,EffectNodeRuntimeState node,CharacterCoverMovementPlanV4 plan)
        {
            string id=StableIdFactory.Create("event",node.EffectId,"CharacterCardCovered",node.PlayerId.ToString(CultureInfo.InvariantCulture));var r=plan.Receipt;
            if(r.EventIds.Count!=1||r.EventIds[0]!=id)return false;var e=state.EffectRuntime.RuleEvents.SingleOrDefault(x=>x.EventId==id);if(e==null)return false;
            // Host单独准备无主链索引，沿既有公开/私有事件的默认0；正常任务的索引由MainCoverAuthority严格验证。
            var task=state.EffectRuntime.EffectNodes.SingleOrDefault(n=>n.EffectId==plan.MainTaskEffectId);if(task?.MainCoverFlowV1==null)return false;int index=Number(task.NormalizedArguments,"coverIndex",0);
            var pub=Value(e.Payload,"cardInstanceRef");var secret=Value(e.HostOnlyPayload,"cardInstanceRef");
            return e.EventType=="CharacterCardCovered"&&e.SourceEffectId==node.EffectId&&e.OwnerNodeId==node.EffectId&&e.PlayerId==node.PlayerId&&e.Visibility==GameStateVisibilityPolicy.Public&&
                e.ResponseKind==RuleEventResponseKind.Effects&&Number(e.Payload,"playerId")==node.PlayerId&&Number(e.Payload,"coverIndex",-1)==index&&Number(e.HostOnlyPayload,"coverIndex",-1)==index&&
                pub?.Kind==NormalizedValueKind.StableReference&&pub.ReferenceType=="character_card"&&pub.ReferenceId=="hidden"&&secret?.Kind==NormalizedValueKind.StableReference&&secret.ReferenceType=="character_card"&&secret.ReferenceId==plan.Item.ItemId&&
                Value(e.Payload,"allPlayersCovered")?.Kind==NormalizedValueKind.Boolean&&Flag(e.Payload,"allPlayersCovered")==task.MainCoverFlowV1.AllPlayersCoveredAtCommit;
        }
    }
}
