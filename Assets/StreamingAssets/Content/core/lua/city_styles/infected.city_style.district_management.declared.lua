return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({ decisionPlayer = ctx.playerId,
        leftEffects = { Effect.ReturnCityStyleMarker({ apiVersion = 1, player = ctx.playerId }) },
        rightEffects = { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'pure_originium', amount = 1 }) }
    }), { interaction = { prompt = Text.Ref('core:condition.infected.city_style.district_management.declared') } }) }
end
