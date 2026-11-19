return function(ctx)
    local candidates = {}
    for _, display in ipairs(Global.Content.FindZones({ templateId = 'core.zone.enterprise_display' })) do
        for _, enterprise in ipairs(Global.Content.FindInstances({ zoneId = display.id, contentType = 'enterprise' })) do
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
