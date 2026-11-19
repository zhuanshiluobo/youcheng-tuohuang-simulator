return function(ctx)
    local player = string.gsub(ctx.playerId, '^p', '')
    local candidates = {}
    for _, display in ipairs(Global.Content.FindZones({ templateId = 'core.zone.enterprise_display' })) do
        for _, enterprise in ipairs(Global.Content.FindInstances({ zoneId = display.id, contentType = 'enterprise' })) do
            candidates[#candidates + 1] = enterprise.id
        end
    end
    local effects = { Effect.UpgradeEnterprise({ apiVersion = 1, player = player, enterpriseItemIds = candidates, levels = 2 }) }
    effects[#effects + 1] = Effect.GainScore({ player = ctx.playerId, amount = 1 })
    return effects
end
