return function(ctx)
    local player = string.gsub(ctx.playerId, '^p', '')
    return { Flow.ActivateAllEnterpriseSpecials({ apiVersion = 1, player = player, unlockedByPlayer = player }) }
end
