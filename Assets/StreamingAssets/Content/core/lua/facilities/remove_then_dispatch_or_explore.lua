return function(ctx)
    return { Effect.WithPresentation(Effect.Choice({ player = ctx.playerId, interactionType = 'effect.choice.execute',
        promptKey = 'facility.entry.choose_vehicle_action', branches = {
            { id = 'vehicle.remove_move', effects = {
                Effect.SelectInfluence({ player = ctx.playerId, operation = 'remove', ownerFilter = 'any',
                    interactionType = 'character.ability.target', promptKey = 'effect.influence.remove.choose_target' }),
                Effect.SelectInfluence({ player = ctx.playerId, operation = 'move', ownerFilter = 'self',
                    interactionType = 'character.ability.choice.move', promptKey = 'effect.influence.move.choose_target' })
            } },
            { id = 'vehicle.explore', effects = { Effect.ChooseExplore({ player = ctx.playerId }) } }
        }
    }), { interaction = {
        prompt = Text.Ref('core:interaction.facility.vehicle.prompt'),
        options = {
            { candidateId = 'vehicle.remove_move', label = Text.Ref('core:interaction.facility.vehicle.remove_move') },
            { candidateId = 'vehicle.explore', label = Text.Ref('core:interaction.facility.vehicle.explore') }
        }
    } }) }
end
