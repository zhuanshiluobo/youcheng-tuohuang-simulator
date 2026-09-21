return function(ctx)
    return { Effect.OverrideNextStartPlayer({ player = ctx.playerId, targetPlayer = ctx.playerId }) }
end
