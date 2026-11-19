return function(ctx)
    local player = string.gsub(ctx.playerId, '^p', '')
    local candidates = {}
    for _, display in ipairs(Global.Content.FindZones({ templateId = 'core.zone.enterprise_display' })) do
        for _, enterprise in ipairs(Global.Content.FindInstances({ zoneId = display.id, contentType = 'enterprise' })) do
            if enterprise.definitionId ~= 'enterprise.rhodes_island' then
                candidates[#candidates + 1] = enterprise.id
            end
        end
    end
    return { Effect.UpgradeEnterprise({ apiVersion = 1, player = player, enterpriseItemIds = candidates, levels = 1 }) }
end
