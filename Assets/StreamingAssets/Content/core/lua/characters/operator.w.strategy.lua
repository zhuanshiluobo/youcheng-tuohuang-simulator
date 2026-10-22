return function(ctx)
    if ctx.eventType == 'PlayerCleanupStarted' then
        -- 独立收尾义务绑定源Effect；本牌之后回盒也继续结算。
        return { Effect.SelectInfluence({ player = ctx.playerId, operation = 'remove', subjectFilter = 'self', count = 2, requiredCount = 0, distinctPolicy = 'instance', promptKey = 'effect.influence.choose' }) }
    end
    return { Effect.Repeat({ apiVersion = 1, count = 3, effects = { Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'player_supply', ownerSubject = ctx.playerId, causeKind = 'other_intrinsic_flow' }) } }) }
end
