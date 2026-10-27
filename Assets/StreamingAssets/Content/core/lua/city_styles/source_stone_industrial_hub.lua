return function(ctx)
    return { Effect.GrantMainActions({ player = ctx.playerId, amount = 2, lockCharacterCard = false }) }
end
