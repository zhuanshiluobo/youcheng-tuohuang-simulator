local sequences = {
    { id = 'place_place', operations = { 'place', 'place' } },
    { id = 'place_remove', operations = { 'place', 'remove' } },
    { id = 'remove_place', operations = { 'remove', 'place' } },
    { id = 'remove_remove', operations = { 'remove', 'remove' } },
}

local function operation(ctx, kind)
    if kind == 'place' then
        return Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'player_supply', ownerSubject = ctx.playerId, causeKind = 'other_intrinsic_flow' })
    end
    return Effect.SelectInfluence({ player = ctx.playerId, operation = 'remove', subjectFilter = 'any', count = 1, requiredCount = 0, distinctPolicy = 'instance', promptKey = 'effect.influence.choose' })
end

return function(ctx)
    if ctx.eventType == 'EffectCompleted' then
        local saved = ctx.payload.continuationContext
        if saved == nil or ctx.payload.outcome ~= 'completed' then return {} end
        if saved.protocol ~= 1 or saved.stage ~= 'influence_sequence_selected' then error('influence_sequence_context_invalid') end
        local selected = ctx.payload.normalizedResult
        for _, sequence in ipairs(sequences) do
            if sequence.id == selected then
                local effects = {}
                for _, kind in ipairs(sequence.operations) do
                    effects[#effects + 1] = operation(ctx, kind)
                end
                return effects
            end
        end
        error('influence_sequence_selection_invalid')
    end
    local ids, options = {}, {}
    for _, sequence in ipairs(sequences) do
        ids[#ids + 1] = sequence.id
        options[#options + 1] = { candidateId = sequence.id, label = Text.Ref('core:interaction.influence.sequence.' .. sequence.id, {}) }
    end
    -- 先沿原条件付款；组合答案经已有受控续接保存，不再创建第二次放/移选择。
    local selection = Effect.WithContext(Effect.WithPresentation(Effect.Choice({ interactionType = 'effect.choice.execute', player = ctx.playerId,
        promptKey = 'effect.influence.place_or_remove', options = ids, minSelections = 1, maxSelections = 1 }),
        { interaction = { prompt = Text.Ref('core:interaction.influence.sequence.two.prompt', {}), options = options } }),
        { protocol = 1, stage = 'influence_sequence_selected' })
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'gold_voucher', amount = 12 }) },
        rightEffects = { selection } }), { interaction = { prompt = Text.Ref('core:condition.enterprise.department.defense.level_4.reward') } }) }
end
