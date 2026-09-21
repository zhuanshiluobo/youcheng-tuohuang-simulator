return function(ctx)
    return { Effect.ChooseBuild({ player = ctx.playerId, sourceZone = 'reserve', effectId = 'reserve_extension_hub' }) }
end
