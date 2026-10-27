-- scoringModifiers.v1，处理器1.1.0：一个受控区域、一个真实持有来源。
return function(ctx)
    local facts = ctx.payload.facts
    local targetRegion = facts.regionId
    local counts = {}
    local regions = Global.Map.GetMap().regions
    for _, region in ipairs(regions) do counts[region.regionId] = 0 end
    for _, influence in ipairs(Global.Map.FindInfluences()) do
        if influence.owner == 'company:stonehoove_security' then
            local regionId
            if influence.locationId ~= '' then
                regionId = Global.Map.GetLocation(influence.locationId).regionId
            elseif influence.routeId ~= '' then
                regionId = Global.Map.GetRoute(influence.routeId).regionId
            end
            if regionId ~= nil and counts[regionId] ~= nil then counts[regionId] = counts[regionId] + 1 end
        end
    end
    local items = {}
    for _, region in ipairs(regions) do
        -- 旧只读调用没有区域索引时仍返回全部区域；正式新消费方始终绑定一个合法区域。
        if targetRegion == nil or targetRegion == '' or region.regionId == targetRegion then
            local count = counts[region.regionId]
            local contribution = 0
            while count >= 2 do count = count - 2; contribution = contribution + 1 end
            table.insert(items, {kind='influence_delta',regionId=region.regionId,
                playerId=ctx.payload.authorizedPlayerId,delta=contribution})
        end
    end
    return {
        queryId = ctx.payload.queryId,
        basisDigest = ctx.payload.basisDigest,
        items = items
    }
end
