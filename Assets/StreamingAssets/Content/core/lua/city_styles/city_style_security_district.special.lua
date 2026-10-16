return function(ctx)
    if ctx.eventType == 'EffectCompleted' then
        if ctx.payload.effectTypeId ~= 'effect.flow.choice' or ctx.payload.outcome ~= 'completed' then return {} end
        local selected = ctx.payload.normalizedResult
        if selected == 'replace' then
            return { Effect.WithPresentation(Effect.Condition({
                leftEffects = { Effect.PayResource({ player = ctx.playerId, cost = { Originium = 2, Iron = 1 } }) },
                rightEffects = { Effect.SelectInfluence({ player = ctx.playerId, operation = 'replace', subjectFilter = 'any', count = 1 }) }
            }), { interaction = { prompt = Text.Ref('core:condition.city_style_security_district.special') } }) }
        elseif selected == 'place' then
            return { Effect.PlaceInfluence({ executingPlayer = ctx.playerId, ownerSubject = ctx.playerId,
                influenceSource = 'city_style.security_district' }),
                Effect.GainResource({ recipient = ctx.playerId, resourceType = 'OriginiumShard', amount = 1 }) }
        elseif selected == 'remove' then
            return { Effect.SelectInfluence({ player = ctx.playerId, operation = 'remove', subjectFilter = 'any', count = 1 }),
                Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Iron', amount = 2 }) }
        end
        return {}
    end
    local choice = Effect.WithPresentation(Effect.Choice({ interactionType = 'effect.choice.execute', player = ctx.playerId, promptKey = 'city_style.security_district.option', options = { 'replace', 'place', 'remove' } }), { interaction = { prompt = Text.Ref('core:interaction.security_district.choose'), options = { { candidateId = 'replace', label = Text.Ref('core:condition.city_style_security_district.special') }, { candidateId = 'place', label = Text.Ref('core:interaction.security_district.place') }, { candidateId = 'remove', label = Text.Ref('core:interaction.security_district.remove') } } } })
    return { choice, choice }
end
