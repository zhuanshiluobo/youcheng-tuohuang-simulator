return function(ctx)
    return { Effect.Condition({
        leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'gold_voucher', amount = 3 }) },
        rightEffects = {
            Effect.SelectInfluence({ player = ctx.playerId, operation = 'remove', ownerFilter = 'any',
                interactionType = 'character.ability.target', promptKey = 'character.texas.tactic.choose_removal' }),
            Effect.SelectInfluence({ player = ctx.playerId, operation = 'move', ownerFilter = 'self', count = 2, distinctPolicy = 'instance',
                interactionType = 'character.ability.choice.move', promptKey = 'character.texas.tactic.choose_first_move', nextPromptKey = 'character.texas.tactic.choose_second_move' })
        }
    }) }
end
