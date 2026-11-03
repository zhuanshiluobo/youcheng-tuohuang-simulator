local function choice(ctx)
    return Effect.WithPresentation(Effect.Choice({ interactionType = 'effect.choice.execute', player = ctx.playerId,
        promptKey = 'city_style.security_district.option', branches = {
        { id = 'replace', effects = { Effect.WithPresentation(Effect.Condition({
                leftEffects = { Effect.PayResource({ player = ctx.playerId, cost = { Originium = 2, Iron = 1 } }) },
                rightEffects = { Effect.ReplaceInfluence({ apiVersion=3, targetMode='select', executingPlayer= ctx.playerId, subjectFilter = 'any', count = 1 }) }
            }), { interaction = { prompt = Text.Ref('core:condition.city_style_security_district.special') } }) } },
        { id = 'place', effects = { Effect.PlaceInfluence({ executingPlayer = ctx.playerId, ownerSubject = ctx.playerId,
                influenceSource = 'city_style.security_district' }),
                Effect.GainResource({ recipient = ctx.playerId, resourceType = 'OriginiumShard', amount = 1 }) } },
        { id = 'remove', effects = { Effect.RemoveInfluence({ apiVersion=3, targetMode='select', executingPlayer= ctx.playerId, subjectFilter = 'any', count = 1 }),
                Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Iron', amount = 2 }) } }
    } }), { interaction = { prompt = Text.Ref('core:interaction.security_district.choose'), options = {
        { candidateId = 'replace', label = Text.Ref('core:condition.city_style_security_district.special') },
        { candidateId = 'place', label = Text.Ref('core:interaction.security_district.place') },
        { candidateId = 'remove', label = Text.Ref('core:interaction.security_district.remove') }
    } } })
end

return function(ctx)
    return { choice(ctx), choice(ctx) }
end
