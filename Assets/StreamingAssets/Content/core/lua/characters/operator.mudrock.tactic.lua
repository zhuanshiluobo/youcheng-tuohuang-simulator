return function(ctx)
    -- 生命周期把持续模式保留在场；只有本人收尾才提供可选支付。
    if ctx.eventType ~= 'PlayerCleanupStarted' then return {} end
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'originium', amount = 2 }) }, rightEffects = { Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'player_supply', ownerSubject = ctx.playerId, causeKind = 'other_intrinsic_flow' }) } }), { interaction = { prompt = Text.Ref('core:condition.operator.mudrock.tactic') } }) }
end
