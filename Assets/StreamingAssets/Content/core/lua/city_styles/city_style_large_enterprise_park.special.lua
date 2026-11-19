return function(ctx)
    local candidates = {}
    for _, display in ipairs(Global.Content.FindZones({ templateId = 'core.zone.enterprise_display' })) do
        for _, item in ipairs(Global.Content.FindInstances({ zoneId = display.id, contentType = 'enterprise' })) do
            table.insert(candidates, item.id)
        end
    end
    return { Effect.UpgradeEnterprise({ apiVersion = 1, player = ctx.playerId,
        enterpriseItemIds = candidates, levels = 2 }) }
end
