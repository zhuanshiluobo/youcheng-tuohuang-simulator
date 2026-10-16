return function(ctx)
    local candidates = {}
    for _, item in ipairs(Global.Content.FindInstances()) do
        if item.contentType == 'enterprise' then
            table.insert(candidates, item.id)
        end
    end
    return { Effect.UpgradeEnterprise({ apiVersion = 1, player = ctx.playerId,
        enterpriseItemIds = candidates, levels = 2 }) }
end
