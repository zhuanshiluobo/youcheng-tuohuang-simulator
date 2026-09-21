return function(ctx)
    return { Effect.Choice({ player = ctx.playerId, interactionType = 'facility.entry.choice',
        promptKey = 'facility.entry.choose_vehicle_action', branches = {
            { id = 'vehicle.remove_move', effects = {
                Effect.SelectInfluence({ player = ctx.playerId, operation = 'remove', ownerFilter = 'any',
                    interactionType = 'character.ability.target', promptKey = 'effect.influence.remove.choose_target' }),
                Effect.SelectInfluence({ player = ctx.playerId, operation = 'move', ownerFilter = 'self',
                    interactionType = 'character.ability.choice.move', promptKey = 'effect.influence.move.choose_target' })
            } },
            { id = 'vehicle.explore', effects = { Effect.ChooseExplore({ player = ctx.playerId }) } }
        }
    }) }
end
