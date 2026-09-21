return function(ctx)
    return { Effect.Choice({ player = ctx.playerId, promptKey = 'facility.entry.choose_influence_branch', branches = {
        { id = 'replace-influence', effects = { Effect.SelectInfluence({ player = ctx.playerId, operation = 'replace', ownerFilter = 'opponent', promptKey = 'effect.influence.replace.choose_target' }) } },
        { id = 'place-influence', effects = { Effect.PlaceInfluence({ executingPlayer = ctx.playerId, ownerSubject = ctx.playerId, influenceSource = 'facility.entry.deploy' }) } }
    } }) }
end
