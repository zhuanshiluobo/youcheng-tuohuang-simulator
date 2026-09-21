return function(ctx)
    return {
        Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 12 }),
        Effect.MoveFacilityCard({ player = ctx.playerId, sourceZone = 'supply', destinationZone = 'deck_bottom',
            interactionType = 'character.ability.target', promptKey = 'character.texas.strategy.choose_facility' })
    }
end
