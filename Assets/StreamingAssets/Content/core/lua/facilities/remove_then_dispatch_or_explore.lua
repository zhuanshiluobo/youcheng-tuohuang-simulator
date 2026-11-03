return function(ctx)
    return { Effect.WithPresentation(Effect.Choice({ player = ctx.playerId, interactionType = 'effect.choice.execute',
        promptKey = 'facility.entry.choose_vehicle_action', branches = {
            { id = 'vehicle.remove_move', effects = {
                Effect.RemoveInfluence({ apiVersion=3, targetMode='select', executingPlayer= ctx.playerId, subjectFilter= 'any',
                    interactionType = 'character.ability.target', promptKey = 'effect.influence.remove.choose_target' }),
                Effect.MoveInfluence({ apiVersion=3, targetMode='select', executingPlayer= ctx.playerId, subjectFilter= 'self',
                    interactionType = 'character.ability.choice.move', promptKey = 'effect.influence.move.choose_target' })
            } },
            { id = 'vehicle.explore', effects = { Effect.Explore({apiVersion=3,targetMode='select',player=ctx.playerId,consumeMainAction=false}) } }
        }
    }), { interaction = {
        prompt = Text.Ref('core:interaction.facility.vehicle.prompt'),
        options = {
            { candidateId = 'vehicle.remove_move', label = Text.Ref('core:interaction.facility.vehicle.remove_move') },
            { candidateId = 'vehicle.explore', label = Text.Ref('core:interaction.facility.vehicle.explore') }
        }
    } }) }
end
