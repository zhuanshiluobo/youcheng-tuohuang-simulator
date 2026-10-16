return function(ctx)
    return { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'pure_originium', amount = 1 }), Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'company_supply', ownerSubject = { kind = 'company', companyId = 'stonehoove_security' }, causeKind = 'other_intrinsic_flow' }), Flow.AcquireReward({ apiVersion = 1, player = ctx.playerId, poolRole = 'contracts' }) }
end
