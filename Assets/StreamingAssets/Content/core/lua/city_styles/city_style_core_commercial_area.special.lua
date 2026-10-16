return function(ctx)
    return { Effect.WithPresentation(Effect.Choice({ interactionType = 'effect.choice.execute', player = ctx.playerId, promptKey = 'city_style.core_commercial_area.resource', branches = {
        { id = 'GoldVoucher', effects = { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'GoldVoucher', amount = 4 }) } },
        { id = 'Originium', effects = { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Originium', amount = 1 }) } },
        { id = 'Iron', effects = { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Iron', amount = 1 }) } },
        { id = 'OriginiumShard', effects = { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'OriginiumShard', amount = 1 }) } }
    } }), { interaction = { prompt = Text.Ref('core:interaction.reward.choose'), options = { { candidateId = 'GoldVoucher', label = Text.Ref('core:interaction.reward.four_gold') }, { candidateId = 'Originium', label = Text.Ref('core:interaction.reward.one_originium') }, { candidateId = 'Iron', label = Text.Ref('core:interaction.reward.one_iron') }, { candidateId = 'OriginiumShard', label = Text.Ref('core:interaction.reward.one_shard') } } } }), Effect.ExecuteMainAction({ player = ctx.playerId, executionMode = 'immediate',
        allowedActionTypes = { 'build', 'explore', 'move_city' } }) }
end
