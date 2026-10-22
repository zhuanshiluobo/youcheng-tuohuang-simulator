return function(ctx)
    local player = string.gsub(ctx.playerId, '^p', '')
    return { Effect.ChangeEnterpriseLevel({ apiVersion = 1, player = player, enterpriseItemId = ctx.payload.enterpriseItemId, levelDelta = -1, resolveArrivalReward = true }) }
end
