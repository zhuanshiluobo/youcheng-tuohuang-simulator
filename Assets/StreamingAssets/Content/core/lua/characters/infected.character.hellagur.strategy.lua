return function(ctx)
    if ctx.eventType == 'PlayerCleanupStarted' then
        return {Effect.SelectInfluence({player=ctx.playerId,operation='remove',subjectFilter='self',count=2})}
    end
    return {Effect.Repeat({apiVersion=1,count=3,allowEarlyStop=false,effects={Effect.WithPresentation(Effect.Choice({ interactionType = 'effect.choice.execute',player=ctx.playerId,branches={{id='place',effects={Effect.PlaceInfluence({executingPlayer=ctx.playerId, ownerSubject=ctx.playerId, influenceSource='player_supply'})}},{id='remove',effects={Effect.SelectInfluence({player=ctx.playerId, operation='remove', subjectFilter='any', count=1})}}}}), { interaction = { prompt = Text.Ref('core:interaction.influence.place_or_remove'), options = { { candidateId = 'place', label = Text.Ref('core:interaction.influence.place') }, { candidateId = 'remove', label = Text.Ref('core:interaction.influence.remove') } } } })}})}
end
