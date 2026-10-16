return function(ctx)
    local function choose()
        return Effect.WithPresentation(Effect.Choice({ interactionType = 'effect.choice.execute', player = ctx.playerId, promptKey = 'effect.influence.place_or_remove', branches = {
            { id = 'place', effects = { Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'player_supply', ownerSubject = ctx.playerId, causeKind = 'other_intrinsic_flow' }) } },
            { id = 'remove', effects = { Effect.SelectInfluence({ player = ctx.playerId, operation = 'remove', subjectFilter = 'any', count = 1, requiredCount = 0, distinctPolicy = 'instance', promptKey = 'effect.influence.choose' }) } }
        } }), { interaction = { prompt = Text.Ref('core:enterprise.blacksteel.level_1.choice.prompt'), options = { { candidateId = 'place', label = Text.Ref('core:enterprise.blacksteel.level_1.choice.place') }, { candidateId = 'remove', label = Text.Ref('core:enterprise.blacksteel.level_1.choice.remove') } } } })
    end
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'gold_voucher', amount = 12 }) }, rightEffects = { choose(), choose() } }), { interaction = { prompt = Text.Ref('core:condition.enterprise.department.defense.level_4.reward') } }) }
end
