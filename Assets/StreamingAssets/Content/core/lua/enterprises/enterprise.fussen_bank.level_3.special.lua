return function(ctx)
    local effects = { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 20 }) }
    for _, opponent in ipairs(GameData.GetTurnOrder()) do
        if opponent.playerId ~= ctx.playerId then
            effects[#effects + 1] = Effect.GainResource({ recipient = opponent, resourceType = 'gold_voucher', amount = 4 })
        end
    end
    return effects
end
