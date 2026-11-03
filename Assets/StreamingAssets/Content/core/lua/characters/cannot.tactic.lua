return function(ctx)
    if ctx.eventType == 'EffectCompleted' then
        if ctx.payload.outcome ~= 'completed' then return {} end
        -- 仅消费真实分支及全部兑换已完成的结果；不再用选项值构造兑换正文。
        return { Effect.GainScore({ recipient = ctx.playerId, amount = 1, reasonId = 'character.cannot.tactic' }) }
    end
    local ids = { 'originium', 'originium-shard', 'iron' }
    local types = { originium = 'originium', ['originium-shard'] = 'originium_shard', iron = 'iron' }
    local branches = {}
    for _, id in ipairs(ids) do
        local exchanges = {}
        for _, player in ipairs(GameData.GetPlayers()) do
            exchanges[#exchanges + 1] = Flow.ExchangeResource({ apiVersion = 1, player = ctx.playerId,
                targetPlayer = player, resourceType = types[id], receiveResourceType = 'gold_voucher',
                amount = 'all', rate = 2, reasonId = 'character.cannot.tactic' })
        end
        branches[#branches + 1] = { id = id, effects = exchanges }
    end
    return { Effect.WithPresentation(Effect.Choice({ player = ctx.playerId, branches = branches,
        promptKey = 'character.cannot.tactic.choose_resource' }), {
        interaction = { prompt = Text.Ref('core:interaction.character.cannot.tactic.resource.prompt'), options = {
            { candidateId = 'originium', label = Text.Ref('core:interaction.resource.originium') },
            { candidateId = 'originium-shard', label = Text.Ref('core:interaction.resource.originium_shard') },
            { candidateId = 'iron', label = Text.Ref('core:interaction.resource.iron') }
        } }
    }) }
end
