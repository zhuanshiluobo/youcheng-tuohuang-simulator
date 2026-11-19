return function(ctx)
    local candidates = {}
    for _, display in ipairs(Global.Content.FindZones({ templateId = 'core.zone.enterprise_display' })) do
        for _, enterprise in ipairs(Global.Content.FindInstances({ zoneId = display.id, contentType = 'enterprise' })) do
            candidates[#candidates + 1] = enterprise.id
        end
    end
    local effects = { Flow.UpgradeEnterpriseForAllPlayers({ apiVersion = 1, player = ctx.playerId,
        enterpriseItemIds = candidates, levels = 1 }) }
    effects[#effects + 1] = Effect.ActivateEnterpriseSpecial({ apiVersion = 1,
        player = string.gsub(ctx.playerId, '^p', ''), unlockedByPlayer = string.gsub(ctx.playerId, '^p', '') })
    return effects
end
