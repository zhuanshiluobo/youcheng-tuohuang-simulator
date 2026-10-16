return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({
        leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'gold_voucher', amount = 3 }) },
        rightEffects = { Effect.WithPresentation(Effect.Choice({ interactionType = 'effect.choice.execute', player = ctx.playerId, branches = {
            { id = 'place', effects = { Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'player_supply', ownerSubject = ctx.playerId, causeKind = 'other_intrinsic_flow' }) } },
            { id = 'move', effects = { Effect.SelectInfluence({ player = ctx.playerId, operation = 'move', subjectFilter = 'self', count = 2, requiredCount = 0, distinctPolicy = 'instance', promptKey = 'effect.influence.choose' }) } }
        } }), { interaction = { prompt = Text.Ref('core:enterprise.department.defense.level_3.choice.prompt'), options = {
            { candidateId = 'place', label = Text.Ref('core:enterprise.department.defense.level_3.choice.place') },
            { candidateId = 'move', label = Text.Ref('core:enterprise.department.defense.level_3.choice.move') }
        } } }) }
    }), { interaction = { prompt = Text.Ref('core:condition.enterprise.department.defense.level_3.reward') } }) }
end
