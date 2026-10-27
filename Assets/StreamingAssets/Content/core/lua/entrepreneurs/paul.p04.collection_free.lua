-- QY-015 正式内容 1.1.0；保存由Host真实节点/事件/续接收据负责。
local function gang_points(player)
    local byId = {}
    for _, item in ipairs(Global.Content.FindInstances({typeId='entrepreneur.gang_marker'})) do
        if item.ownerSubject == player then byId[item.id] = true end
    end
    local points = {}
    for _, influence in ipairs(Global.Map.FindInfluences()) do
        if byId[influence.influenceId] and influence.locationId ~= '' and Global.Map.GetResourcePoint(influence.locationId) ~= nil then
            points[influence.locationId] = true
        end
    end
    return points
end
return function(ctx)
    local items = {}
    if ctx.payload.purpose == 'normal' or ctx.payload.purpose == 'extra' then
        local points = gang_points(ctx.playerId)
        for _, id in ipairs(ctx.payload.facts.legalLocationIds) do
            if points[id] then table.insert(items,{kind='free_location',locationId=id}) end
        end
    end
    return {queryId=ctx.payload.queryId,basisDigest=ctx.payload.basisDigest,items=items}
end
