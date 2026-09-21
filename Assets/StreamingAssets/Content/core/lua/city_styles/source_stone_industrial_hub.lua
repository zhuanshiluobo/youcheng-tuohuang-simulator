return function(ctx)
    return { Effect.GrantMainActions({ player = ctx.playerId, amount = ctx.payload.extraMainActionCount, lockCharacterCard = false }) }
end
