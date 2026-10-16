return function(ctx)
    local player = string.gsub(ctx.playerId, '^p', '')
    local candidates = {}
    for _, enterprise in ipairs(Global.Content.FindInstances()) do
        if enterprise.contentType == 'enterprise' and enterprise.definitionId ~= 'infected.enterprise.rhodes_island' then
            candidates[#candidates + 1] = enterprise.id
        end
    end
    return { Effect.UpgradeEnterprise({ apiVersion = 1, player = player, enterpriseItemIds = candidates, levels = 1 }) }
end
