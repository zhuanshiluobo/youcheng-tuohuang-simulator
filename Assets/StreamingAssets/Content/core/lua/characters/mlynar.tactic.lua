return function(ctx)
    local candidates = {}
    for _, enterprise in ipairs(Global.Content.FindInstances()) do
        if enterprise.contentType == 'enterprise' then
            candidates[#candidates + 1] = enterprise.id
        end
    end
    local effects = { Effect.UpgradeEnterprise({ apiVersion = 1, player = string.gsub(ctx.playerId, '^p', ''),
        enterpriseItemIds = candidates, levels = 1 }) }
    for _, resource in ipairs({ 'originium', 'originium_shard', 'iron' }) do
        effects[#effects + 1] = Effect.GainResource({ recipient = ctx.playerId, resourceType = resource, amount = 1 })
    end
    return effects
end
