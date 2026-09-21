return function(ctx)
    return { Effect.ChooseBuild({ player = ctx.playerId, sourceZone = 'supply' }) }
end
