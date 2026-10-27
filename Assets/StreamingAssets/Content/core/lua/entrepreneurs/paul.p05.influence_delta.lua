-- QY-015 正式内容 1.1.0；保存由Host真实节点/事件/续接收据负责。
local function has_gang_in_region(player, regionId)
    local byId = {}
    for _, item in ipairs(Global.Content.FindInstances({typeId='entrepreneur.gang_marker'})) do
        if item.ownerSubject == player then byId[item.id]=true end
    end
    for _, influence in ipairs(Global.Map.FindInfluences()) do
        if byId[influence.influenceId] then
            local region
            if influence.locationId ~= '' then region=Global.Map.GetLocation(influence.locationId).regionId
            elseif influence.routeId ~= '' then region=Global.Map.GetRoute(influence.routeId).regionId end
            if region == regionId then return true end
        end
    end
    return false
end
return function(ctx)
    local items = {}
    if ctx.payload.purpose == 'influence_delta' and has_gang_in_region(ctx.playerId,ctx.payload.facts.regionId) then
        items[1]={kind='influence_delta',regionId=ctx.payload.facts.regionId,playerId=ctx.payload.authorizedPlayerId,delta=1}
    end
    return {queryId=ctx.payload.queryId,basisDigest=ctx.payload.basisDigest,items=items}
end
