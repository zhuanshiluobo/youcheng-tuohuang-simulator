return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'GoldVoucher', amount = 18, reasonId = 'event_card.reward:event_red_05' }))
    for _, opponent in ipairs(GameData.GetPlayers()) do
        if opponent.playerId ~= ctx.playerId then table.insert(effects, Effect.GainResource({ recipient = opponent, resourceType = 'GoldVoucher', amount = 3, reasonId = 'event_card.opponent_reward:event_red_05' })) end
    end
    return effects
end
