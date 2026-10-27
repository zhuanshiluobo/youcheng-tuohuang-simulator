-- QY-015 正式内容 1.1.3；保存由Host真实节点/事件/续接收据负责。
function availability(ctx)
    local source = ctx.facts.source
    local enabled = ctx.window.kind == 'player_cleanup' and source ~= nil and source.persistentActive == true
    return {enabled=enabled,disabledReasonKey=enabled and '' or 'core:presentation.unavailable'}
end
return function(ctx)
    if ctx.eventType == 'EffectCompleted' then
        local saved = ctx.payload.continuationContext
        if saved == nil or saved.stage ~= 'share' or ctx.payload.effectTypeId ~= 'effect.influence.select_action'
            or ctx.payload.outcome ~= 'completed' then return {} end
        local result = ctx.payload.normalizedResult
        if result.completedCount ~= 1 or result.removedInfluences == nil or #result.removedInfluences ~= 1 then
            error('paul.share.incomplete_removal')
        end
        local origin = result.removedInfluences[1]
        local point = Global.Map.GetResourcePoint(origin.locationId)
        if point == nil then error('paul.share.resource_point_missing') end
        local beneficiaries = {[ctx.playerId]=true}
        for _, influence in ipairs(Global.Map.FindInfluences({locationId=origin.locationId})) do
            if string.match(influence.owner, '^p%d+$') ~= nil then beneficiaries[influence.owner]=true end
        end
        local effects = {}
        for _, player in ipairs(GameData.GetPlayers()) do
            if beneficiaries[player.playerId] then
                table.insert(effects,Effect.GainResource({recipient=player,resourceType=point.resourceType,amount=point.amount}))
            end
        end
        return effects
    end
    local byId = {}
    for _, item in ipairs(Global.Content.FindInstances({typeId='entrepreneur.gang_marker'})) do
        if item.ownerSubject == ctx.playerId then byId[item.id]=true end
    end
    local targets, options = {}, {}
    for _, influence in ipairs(Global.Map.FindInfluences()) do
        if byId[influence.influenceId] and influence.locationId ~= '' and Global.Map.GetResourcePoint(influence.locationId) ~= nil then
            local id = influence.influenceId
            table.insert(targets,id)
            table.insert(options,{candidateId=id,label=Text.Ref('core:entrepreneur.P.share.target',{piece=Text.Item(id),point=Text.Location(influence.locationId)})})
        end
    end
    if #targets == 0 then return {} end
    return {Effect.WithPresentation(Effect.Condition({ruleCondition=true,decisionPlayer=ctx.playerId,
        leftEffects={Effect.WithContext(Effect.WithPresentation(Effect.SelectInfluence({player=ctx.playerId,
            operation='remove',ownerFilter='self',candidateScope='resource_points',candidateIds=targets,
            count=1,requiredCount=1,requireCompleteSelection=true,allowDecline=false}),
            {interaction={prompt=Text.Ref('core:entrepreneur.P.share.choose'),options=options}}),
            {stage='share'})},rightEffects={}}),
        {interaction={prompt=Text.Ref('core:entrepreneur.P.share.condition')}})}
end
