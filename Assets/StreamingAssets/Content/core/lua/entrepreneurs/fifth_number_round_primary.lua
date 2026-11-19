-- 每条独立立即由004的真实选面节点调用；不在这里登记、移动或重置使用轨道。
local function gain(player, resource, amount)
    return Effect.GainResource({ recipient = player, resourceType = resource, amount = amount })
end

local function place(player)
    return Effect.PlaceInfluence({ executingPlayer = player, ownerSubject = player,
        influenceSource = 'player_supply', causeKind = 'other_intrinsic_flow' })
end

local function upgrade_all(player)
    local candidates = {}
    -- 非Choice业务选择同样只交正式在场企业；不把未启用牌塞给原Flow后再放宽其校验。
    for _, zone in ipairs(Global.Content.FindZones({ templateId = 'core.zone.enterprise_display' })) do
        for _, item in ipairs(Global.Content.FindInstances({ zoneId = zone.id, contentType = 'enterprise' })) do
            candidates[#candidates + 1] = item.id
        end
    end
    -- 只选择一家企业。玩家轮序、满级无变化和逐人的到达奖励由原Flow保存并执行。
    return Flow.UpgradeEnterpriseForAllPlayers({ apiVersion = 1, player = player,
        enterpriseItemIds = candidates, levels = 1 })
end

return function(ctx)
    local face = ctx.payload.activeFaceId
    if face == 'T07' or face == 'T01' then
        return { gain(ctx.playerId, 'PureOriginium', 1) }
    end
    if face == 'T08' then
        return { place(ctx.playerId) }
    end
    if face == 'T05' then
        return { gain(ctx.playerId, 'PureOriginium', 1), upgrade_all(ctx.playerId) }
    end
    if face == 'T06' then
        return { gain(ctx.playerId, 'GoldVoucher', 18), upgrade_all(ctx.playerId) }
    end
    if face == 'T03' or face == 'T04' then
        -- 这是独立立即①，特派主动放弃不成为独立立即②的条件。
        return { Effect.LoseScore({ player = ctx.playerId, amount = 2 }),
            Flow.DispatchOperator({ apiVersion = 2, player = ctx.playerId, deckZoneId = Global.Content.FindZones({localZoneId='operator_deck'})[1].id }) }
    end
    if face == 'T02' then
        -- 两个图标是两个兄弟节点，无第一次成功才执行第二次的条件。
        return { place(ctx.playerId), place(ctx.playerId) }
    end
    error('entrepreneur.T.primary.active_face_invalid')
end
