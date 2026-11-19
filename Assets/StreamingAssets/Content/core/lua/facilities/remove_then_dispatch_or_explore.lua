return function(ctx)
    local branches = {
        { id = 'vehicle.remove_move', effects = {
            Effect.RemoveInfluence({ apiVersion=3, targetMode='select', executingPlayer= ctx.playerId, subjectFilter= 'any',
                interactionType = 'character.ability.target', promptKey = 'effect.influence.remove.choose_target' }),
            Effect.MoveInfluence({ apiVersion=3, targetMode='select', executingPlayer= ctx.playerId, subjectFilter= 'self',
                interactionType = 'character.ability.choice.move', promptKey = 'effect.influence.move.choose_target' })
        } }
    }
    local options = {
        { candidateId = 'vehicle.remove_move', label = Text.Ref('core:interaction.facility.vehicle.remove_move') }
    }
    local has_event = false
    local event_decks = { event_green = true, event_yellow = true, event_red = true }
    for _, deck in ipairs(Global.Content.FindZones({ templateId = 'core.zone.card_deck' })) do
        if event_decks[deck.localZoneId] and deck.itemCount > 0 then has_event = true; break end
    end
    if has_event then
        table.insert(branches, { id = 'vehicle.explore', effects = { Effect.Explore({apiVersion=3,targetMode='select',player=ctx.playerId,consumeMainAction=false}) } })
        table.insert(options, { candidateId = 'vehicle.explore', label = Text.Ref('core:interaction.facility.vehicle.explore') })
    end
    return { Effect.WithPresentation(Effect.Choice({ player = ctx.playerId, interactionType = 'effect.choice.execute',
        promptKey = 'facility.entry.choose_vehicle_action', branches = branches
    }), { interaction = {
        prompt = Text.Ref('core:interaction.facility.vehicle.prompt'),
        options = options
    } }) }
end
