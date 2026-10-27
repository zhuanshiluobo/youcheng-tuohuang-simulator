-- QY-015 正式内容 1.1.0；保存由Host真实节点/事件/续接收据负责。
return function(ctx)
    return {Effect.PlaceInfluence({executingPlayer=ctx.playerId,influenceSource='player_supply',
        ownerSubject=ctx.playerId,itemTypeId='core.player_marker',causeKind='other_intrinsic_flow'})}
end
