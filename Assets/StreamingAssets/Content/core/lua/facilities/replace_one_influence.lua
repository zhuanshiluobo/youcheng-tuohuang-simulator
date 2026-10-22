return function(ctx)
    return { Effect.WithPresentation(Effect.Choice({ interactionType = 'effect.choice.execute', player = ctx.playerId, promptKey = 'facility.entry.choose_influence_branch', branches = {
        { id = 'replace-influence', effects = { Effect.SelectInfluence({ player = ctx.playerId, operation = 'replace', ownerFilter = 'opponent', promptKey = 'effect.influence.replace.choose_target' }) } },
        { id = 'place-influence', effects = { Effect.PlaceInfluence({ executingPlayer = ctx.playerId, ownerSubject = ctx.playerId, influenceSource = 'facility.entry.deploy' }) } }
    } }), { interaction = { prompt = Text.Ref('core:interaction.facility.influence_branch.choose'), options = {
        { candidateId = 'replace-influence', label = Text.Ref('core:interaction.facility.influence_branch.replace') },
        { candidateId = 'place-influence', label = Text.Ref('core:interaction.influence.place') }
    } } }) }
end
