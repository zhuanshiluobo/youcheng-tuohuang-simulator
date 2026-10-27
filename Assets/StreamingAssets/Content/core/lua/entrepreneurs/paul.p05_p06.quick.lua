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
    if ctx.eventType == 'EffectCompleted' then
        local saved = ctx.payload.continuationContext
        if saved == nil or saved.stage ~= 'gang_resources' or ctx.payload.effectTypeId ~= 'effect.flow.condition' or ctx.payload.outcome ~= 'completed' or ctx.payload.normalizedResult.conditionMet ~= true then return {} end
        local points = gang_points(ctx.playerId)
        local effects = {}
        -- 按正式地图稳定顺序构造一次完整收益子树；恢复沿既有子树，不重查再发。
        for _, point in ipairs(Global.Map.GetMap().resourcePoints) do
            if points[point.locationId] then table.insert(effects,Effect.GainResource({player=ctx.playerId,resourceType=point.resourceType,amount=point.amount})) end
        end
        return effects
    end
    local city = PlayerData.GetCityLocation(ctx.playerId)
    local targets = {}
    if city ~= nil and city ~= '' and Global.Map.GetResourcePoint(city) ~= nil then
        for _, influence in ipairs(Global.Map.FindInfluences({locationId=city})) do table.insert(targets,influence.influenceId) end
    end
    -- 正式地图资源点两槽；不对未来存在真实目标取舍的DIY地图擅自按ID截取两枚。
    if #targets > 2 then error('paul.strict_city_ambiguous_targets') end
    return {Effect.WithPresentation(Effect.WithContext(Effect.Condition({ruleCondition=true,decisionPlayer=ctx.playerId,
        leftEffects={Effect.RemoveInfluence({apiVersion=2,executingPlayer=ctx.playerId,targets=targets,requiredCount=2,reasonId='entrepreneur.P.city_two'})},
        rightEffects={}}),{stage='gang_resources'}),{interaction={prompt=Text.Ref('core:entrepreneur.P.P05_P06.condition')}})}
end
