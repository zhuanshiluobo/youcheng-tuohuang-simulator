return function(ctx)
    local effects = {}
    table.insert(effects, Effect.WithPresentation(Effect.Condition({
        leftEffects = { Effect.PayResource({ player = ctx.playerId, cost = { Originium = 0, OriginiumShard = 0, Iron = 0, PureOriginium = 0, GoldVoucher = 4 } }) },
        rightEffects = { Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'event_card_pending:event_yellow_04', ownerSubject = ctx.playerId, causeKind = 'other_intrinsic_flow' }) }
    }), { interaction = { prompt = Text.Ref('core:condition.event.event_yellow_04.payment') } }))
    return effects
end
