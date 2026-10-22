return function(ctx)
    local candidates = {}
    for _, enterprise in ipairs(Global.Content.FindInstances()) do
        if enterprise.contentType == 'enterprise' then
            candidates[#candidates + 1] = enterprise.id
        end
    end
    local effects = { Flow.UpgradeEnterpriseForAllPlayers({ apiVersion = 1, player = ctx.playerId,
        enterpriseItemIds = candidates, levels = 1 }) }
    effects[#effects + 1] = Effect.ActivateEnterpriseSpecial({ apiVersion = 1,
        player = string.gsub(ctx.playerId, '^p', ''), unlockedByPlayer = string.gsub(ctx.playerId, '^p', '') })
    return effects
end
