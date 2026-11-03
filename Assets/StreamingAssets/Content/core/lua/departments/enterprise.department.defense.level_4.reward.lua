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
    return Effect.RemoveInfluence({ apiVersion=3, targetMode='select', executingPlayer= ctx.playerId, subjectFilter = 'any', count = 1, requiredCount = 0, distinctPolicy = 'instance', promptKey = 'effect.influence.choose' })
end

return function(ctx)
    local branches, options = {}, {}
    for _, sequence in ipairs(sequences) do
        local effects = {}
        for _, kind in ipairs(sequence.operations) do effects[#effects + 1] = operation(ctx, kind) end
        branches[#branches + 1] = { id = sequence.id, effects = effects }
        options[#options + 1] = { candidateId = sequence.id, label = Text.Ref('core:interaction.influence.sequence.' .. sequence.id, {}) }
    end
    -- 原付款条件先完整完成，选中后执行该有序组合的两个真实操作。
    local selection = Effect.WithPresentation(Effect.Choice({ interactionType = 'effect.choice.execute', player = ctx.playerId,
        promptKey = 'effect.influence.place_or_remove', branches = branches }),
        { interaction = { prompt = Text.Ref('core:interaction.influence.sequence.two.prompt', {}), options = options } })
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'gold_voucher', amount = 12 }) },
        rightEffects = { selection } }), { interaction = { prompt = Text.Ref('core:condition.enterprise.department.defense.level_4.reward') } }) }
end
