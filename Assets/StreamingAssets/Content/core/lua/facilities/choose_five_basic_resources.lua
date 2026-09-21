return function(ctx)
    return { Effect.ChooseResources({ player = ctx.playerId, amount = 5 }) }
end
