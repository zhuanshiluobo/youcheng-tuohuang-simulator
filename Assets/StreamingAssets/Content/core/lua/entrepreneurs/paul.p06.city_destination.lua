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
    local points = gang_points(ctx.playerId)
    local mixed = {}
    for _, influence in ipairs(Global.Map.FindInfluences()) do
        if points[influence.locationId] then
            local item = Global.Content.GetInstance(influence.influenceId)
            if item ~= nil and item.ownerSubject == ctx.playerId and item.typeId ~= 'entrepreneur.gang_marker' then mixed[influence.locationId]=true end
        end
    end
    local ids = {}
    for _, destination in ipairs(ctx.payload.context.destinations) do
        if mixed[destination.locationId] then table.insert(ids,destination.candidateId) end
    end
    if #ids == 0 then return {} end
    return {Candidate.Remove({ids=ids,reasonCode='entrepreneur.P.mixed_city_destination'})}
end
