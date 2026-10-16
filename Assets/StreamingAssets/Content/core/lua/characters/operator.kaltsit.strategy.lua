return function(ctx)
    local player = string.gsub(ctx.playerId, '^p', '')
    local candidates = {}
    for _, enterprise in ipairs(Global.Content.FindInstances()) do
        if enterprise.contentType == 'enterprise' then
            candidates[#candidates + 1] = enterprise.id
        end
    end
    local effects = { Effect.UpgradeEnterprise({ apiVersion = 1, player = player, enterpriseItemIds = candidates, levels = 2 }) }
    effects[#effects + 1] = Effect.GainScore({ player = ctx.playerId, amount = 1 })
    return effects
end
