-- 本脚本只在正式response.destination来源中使用。Host把同一departure的机会串行挂入，
-- 实际正文只属于当前保管根；Lua读取当前实例，并在答案续接再次核验保存的精确来源区。
local function current_item(ctx, event, expected_zone)
    local player = tonumber(string.match(ctx.playerId, '^p(%d+)$'))
    if event.eventProtocolVersion ~= 2 or event.ownerPlayerId ~= player
        or (event.causeKind ~= 'remove' and event.causeKind ~= 'replace')
        or type(event.departureId) ~= 'string' or event.departureId == ''
        or type(event.sourceEffectId) ~= 'string' or event.sourceEffectId == '' then
        return nil
    end
    local item = Global.Content.GetInstance(event.influenceId)
    if item == nil or item.ownerSubject ~= ctx.playerId or item.typeId ~= 'core.player_marker'
        or item.originalSupplyZoneId ~= event.originalSupplyZoneId
        or (expected_zone ~= nil and item.zoneId ~= expected_zone) then
        return nil
    end
    for _, zone in ipairs(Global.Content.FindZones()) do
        if zone.id == item.zoneId and zone.templateId == 'core.zone.effect_staging'
            and zone.localZoneId == 'removed_influence' and zone.parentKind == 'Zone' then
            return item
        end
    end
    return nil
end

return function(ctx)
    if ctx.eventType == 'EffectCompleted' then
        if ctx.payload.effectTypeId ~= 'effect.flow.choice' or ctx.payload.outcome ~= 'completed'
            or ctx.payload.normalizedResult ~= 'store' then return {} end
        local saved = ctx.payload.continuationContext
        if saved == nil or saved.kind ~= 'entrepreneur.T09.store' then return {} end
        -- saved来自本来源实际Choice的受控续接，不读取另一牌或供应来补领。
        if current_item(ctx, saved, saved.sourceZoneId) == nil then return {} end
        return { Effect.StoreRemovedInfluence({ apiVersion = 2, player = ctx.playerId,
            cardInstanceId = saved.card, localZoneId = 'markers', influenceItemId = saved.influenceId }) }
    end

    local event = ctx.payload
    local item = current_item(ctx, event, nil)
    if item == nil then return {} end
    local saved = {
        kind = 'entrepreneur.T09.store', card = event.sourceItemId,
        eventProtocolVersion = event.eventProtocolVersion, departureId = event.departureId,
        sourceEffectId = event.sourceEffectId, influenceId = event.influenceId,
        ownerPlayerId = event.ownerPlayerId, causeKind = event.causeKind,
        originalSupplyZoneId = event.originalSupplyZoneId, sourceZoneId = item.zoneId
    }
    return { Effect.WithContext(Effect.WithPresentation(Effect.Choice({ player = ctx.playerId,
        interactionType = 'effect.choice.execute', options = { 'store', 'decline' },
        minSelections = 1, maxSelections = 1
    }), { interaction = {
        prompt = Text.Ref('core:entrepreneur.T.store.prompt'), options = {
            { candidateId = 'store', label = Text.Ref('core:entrepreneur.T.store.accept') },
            { candidateId = 'decline', label = Text.Ref('core:entrepreneur.T.store.decline') }
        }
    } }), saved) }
end
