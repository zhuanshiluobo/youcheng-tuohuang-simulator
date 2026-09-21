return function(ctx)
    return { Effect.SellResource({ player = ctx.playerId,
        interactionType = 'character.ability.choice.sale', promptKey = 'character.cannot.strategy.choose_sale' }) }
end
