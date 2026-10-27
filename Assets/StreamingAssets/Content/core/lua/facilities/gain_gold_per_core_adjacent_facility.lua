-- facility_layers.v1；规则倍数保留在内容，宿主只提供逐塔邻接事实。
return function(ctx)
    local count = 0
    for _, tower in ipairs(Global.Facilities.GetTowerAdjacency(ctx.playerId)) do
        count = count + #tower.adjacentTopItemIds
    end
    if count == 0 then return {} end
    return { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 4 * count }) }
end
