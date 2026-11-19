return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'GoldVoucher', amount = 13, reasonId = 'event_card.reward:event_yellow_07' }))
    for _, opponent in ipairs(GameData.GetPlayers()) do
        if opponent.playerId ~= ctx.playerId then table.insert(effects, Effect.GainResource({ recipient = opponent, resourceType = 'Iron', amount = 1, reasonId = 'event_card.opponent_reward:event_yellow_07' })) end
    end
    return effects
end
