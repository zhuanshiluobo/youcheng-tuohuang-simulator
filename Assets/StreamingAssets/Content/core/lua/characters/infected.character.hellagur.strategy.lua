return function(ctx)
    if ctx.eventType == 'PlayerCleanupStarted' then
        return {Effect.SelectInfluence({player=ctx.playerId,operation='remove',subjectFilter='self',count=2})}
    end
    local function place()
        return Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'player_supply', ownerSubject = ctx.playerId })
    end
    local function remove()
        return Effect.SelectInfluence({ player = ctx.playerId, operation = 'remove', subjectFilter = 'any', count = 1 })
    end
    local branches = {
        { id = 'place_place_place', effects = { place(), place(), place() } },
        { id = 'place_place_remove', effects = { place(), place(), remove() } },
        { id = 'place_remove_place', effects = { place(), remove(), place() } },
        { id = 'place_remove_remove', effects = { place(), remove(), remove() } },
        { id = 'remove_place_place', effects = { remove(), place(), place() } },
        { id = 'remove_place_remove', effects = { remove(), place(), remove() } },
        { id = 'remove_remove_place', effects = { remove(), remove(), place() } },
        { id = 'remove_remove_remove', effects = { remove(), remove(), remove() } },
    }
    local options = {}
    for _, branch in ipairs(branches) do
        options[#options + 1] = { candidateId = branch.id, label = Text.Ref('core:interaction.influence.sequence.' .. branch.id, {}) }
    end
    local selection = Effect.WithPresentation(Effect.Choice({ interactionType = 'effect.choice.execute', player = ctx.playerId,
        promptKey = 'effect.influence.place_or_remove', branches = branches }),
        { interaction = { prompt = Text.Ref('core:interaction.influence.sequence.three.prompt', {}), options = options } })
    return { selection }
end
