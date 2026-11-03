-- 本脚本只在正式response.destination来源中使用。Host把同一departure的机会串行挂入，
-- 实际正文只属于当前保管根；Lua核验当前实例，真实Store执行时复验同一离场与来源授权。
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
    if ctx.eventType == 'EffectCompleted' then return {} end

    local event = ctx.payload
    local item = current_item(ctx, event, nil)
    if item == nil then return {} end
    return { Effect.WithPresentation(Effect.Choice({ player = ctx.playerId,
        interactionType = 'effect.choice.execute', minSelections = 0, maxSelections = 1,
        branches = { { id = 'store', effects = { Effect.StoreRemovedInfluence({ apiVersion = 2, player = ctx.playerId,
            cardInstanceId = event.sourceItemId, localZoneId = 'markers', influenceItemId = event.influenceId }) } } }
    }), { interaction = {
        prompt = Text.Ref('core:entrepreneur.T.store.prompt'), options = {
            { candidateId = 'store', label = Text.Ref('core:entrepreneur.T.store.accept') }
        }
    } }) }
end
