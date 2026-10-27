return function(ctx)
    local function place()
        return Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'player_supply', ownerSubject = ctx.playerId, causeKind = 'other_intrinsic_flow' })
    end
    local function remove()
        return Effect.SelectInfluence({ player = ctx.playerId, operation = 'remove', subjectFilter = 'any', count = 1, requiredCount = 0, distinctPolicy = 'instance', promptKey = 'effect.influence.choose' })
    end
    local branches = {
        { id = 'place_place', effects = { place(), place() } },
        { id = 'place_remove', effects = { place(), remove() } },
        { id = 'remove_place', effects = { remove(), place() } },
        { id = 'remove_remove', effects = { remove(), remove() } },
    }
    local options = {}
    for _, branch in ipairs(branches) do
        options[#options + 1] = { candidateId = branch.id, label = Text.Ref('core:interaction.influence.sequence.' .. branch.id, {}) }
    end
    local selection = Effect.WithPresentation(Effect.Choice({ interactionType = 'effect.choice.execute', player = ctx.playerId,
        promptKey = 'effect.influence.place_or_remove', branches = branches }),
        { interaction = { prompt = Text.Ref('core:interaction.influence.sequence.two.prompt', {}), options = options } })
    return { selection }
end
