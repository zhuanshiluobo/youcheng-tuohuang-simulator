return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({ decisionPlayer = ctx.playerId,
        leftEffects = { Effect.RetireFacility({ apiVersion = 1, player = ctx.playerId, facilityItemId = ctx.payload.sourceItemId }) },
        rightEffects = { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 12 }) }
    }), { interaction = { prompt = Text.Ref('core:condition.infected.facility.temporary_settlement.declared') } }) }
end
