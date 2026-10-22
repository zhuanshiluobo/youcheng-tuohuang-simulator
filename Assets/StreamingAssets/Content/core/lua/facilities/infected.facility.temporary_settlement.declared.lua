return function(ctx)
    return { Effect.WithPresentation(Effect.Choice({ interactionType = 'effect.choice.execute', player = ctx.playerId, promptKey = 'facility.temporary_settlement.retire', branches = {
        { id = 'retire', effects = { Effect.WithPresentation(Effect.Condition({ leftEffects = {
            Effect.RetireFacility({ apiVersion = 1, player = ctx.playerId, facilityItemId = ctx.payload.sourceItemId })
        }, rightEffects = { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 12 }) } }), { interaction = { prompt = Text.Ref('core:condition.infected.facility.temporary_settlement.declared') } }) } },
        { id = 'keep', effects = {} }
    } }), { interaction = { prompt = Text.Ref('core:interaction.temporary_settlement.choose'), options = { { candidateId = 'retire', label = Text.Ref('core:condition.infected.facility.temporary_settlement.declared') }, { candidateId = 'keep', label = Text.Ref('core:interaction.temporary_settlement.keep') } } } }) }
end
