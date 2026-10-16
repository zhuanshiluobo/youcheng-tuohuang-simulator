return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({
        leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'gold_voucher', amount = 3 }) },
        rightEffects = { Effect.SelectInfluence({ player = ctx.playerId, operation = 'replace', ownerFilter = 'opponent',
            interactionType = 'character.ability.target', promptKey = 'character.liskarm.tactic.choose_target' }) }
    }), { interaction = { prompt = Text.Ref('core:condition.liskarm.tactic') } }) }
end
