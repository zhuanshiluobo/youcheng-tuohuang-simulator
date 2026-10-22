return function(ctx)
    return {
        Effect.Repeat({ apiVersion = 1, count = 2, effects = { Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'company_supply', ownerSubject = { kind = 'company', companyId = 'stonehoove_security' }, causeKind = 'other_intrinsic_flow' }) } }),
        Effect.WithPresentation(Effect.Condition({
            leftEffects = { Effect.WithPresentation(Effect.SelectInfluence({ player = ctx.playerId, operation = 'remove', subjectFilter = 'self', count = 1, requiredCount = 1, requireCompleteSelection = true, promptKey = 'effect.influence.remove.choose_target' }), {
                interaction = { prompt = Text.Ref('core:interaction.map.influence.remove') }
            }) },
            rightEffects = { Effect.UpgradeEnterprise({ apiVersion = 1, player = ctx.playerId, enterpriseItemId = ctx.payload.enterpriseItemId, levels = 1 }) }
        }), { interaction = { prompt = Text.Ref('core:condition.infected.enterprise.stonehoove_security.level_2.special') } })
    }
end
