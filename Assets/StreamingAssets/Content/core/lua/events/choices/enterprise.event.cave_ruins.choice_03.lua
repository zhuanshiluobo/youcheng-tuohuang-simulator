return function(ctx)
    local candidates = {}
    for _, display in ipairs(Global.Content.FindZones({ templateId = 'core.zone.enterprise_display' })) do
        for _, item in ipairs(Global.Content.FindInstances({ zoneId = display.id, contentType = 'enterprise' })) do
            table.insert(candidates, item.id)
        end
    end
    local right = { Effect.UpgradeEnterprise({ apiVersion = 1, player = ctx.playerId, enterpriseItemIds = candidates, levels = 1 }) }
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ recipient = ctx.playerId, resourceType = 'Iron', amount = 1 }) },
        rightEffects = right }), { interaction = { prompt = Text.Ref('core:condition.enterprise.event.cave_ruins.resolve') } }) }
end
