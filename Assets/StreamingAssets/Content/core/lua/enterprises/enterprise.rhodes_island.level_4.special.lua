-- 规则阶段只存在权威Effect续接元数据中；不保留Lua VM或UI草稿。
local function copy(value)
    if type(value) ~= 'table' then return value end
    local result = {}; for key, item in pairs(value) do result[key] = copy(item) end; return result
end
local function state(ctx)
    if ctx.eventType == 'EffectCompleted' then
        if ctx.payload.continuationContext == nil or ctx.payload.outcome ~= 'completed' then return nil end
        return copy(ctx.payload.continuationContext)
    end
    local source = ctx.payload.cardInstanceRef or ctx.payload.sourceItemId or ctx.payload.enterpriseItemId
    if source == nil then error('content_source_missing') end
    return { source = source, stage = 'start' }
end
local function tag(effect, context, stage)
    local saved = copy(context); saved.stage = stage; return Effect.WithContext(effect, saved)
end
local function zone(localId, player)
    local filter = { localZoneId = localId }; if player ~= nil then filter.owner = player end
    local values = Global.Content.FindZones(filter)
    if #values ~= 1 then error('content_zone_missing:' .. localId) end
    return values[1].id
end
local function has(values, id)
    for _, value in ipairs(values or {}) do if value == id then return true end end; return false
end
local function choose(ctx, context, stage, ids, labels, prompt, minimum, kind)
    local options = {}; for _, id in ipairs(ids) do options[#options + 1] = { candidateId = id, label = labels[id] } end
    return tag(Effect.WithPresentation(Effect.Choice({ player = ctx.playerId, options = ids,
        minSelections = minimum or 1, maxSelections = 1, interactionType = kind or 'lua.choice', promptKey = prompt }),
        { interaction = { prompt = Text.Ref(prompt), options = options } }), context, stage)
end

return function(ctx)
    local c=state(ctx);if c==nil then return {} end
    if c.stage=='start' then
        local ids,labels={},{}
        for _,item in ipairs(Global.Content.FindInstances({zoneId=zone('discard',ctx.playerId)})) do
            ids[#ids+1]=item.id;labels[item.id]=Text.Ref('core:interaction.entity.item',{entity=Text.Item(item.id)})
        end
        if #ids==0 then return {} end
        return {Effect.WithPresentation(Effect.Condition({leftEffects={choose(ctx,c,'selected',ids,labels,'core:interaction.character.recover.choose',1,'lua.choice')},
            rightEffects={Effect.GainResource({recipient=ctx.playerId,resourceType='gold_voucher',amount=10})}}),
            {interaction={prompt=Text.Ref('core:condition.enterprise.rhodes_island.level_4.special')}})}
    elseif c.stage=='selected' then
        c.recovered=ctx.payload.normalizedResult;c.others={};c.index=1
        return {tag(Effect.MoveCharacterCardToZone({apiVersion=1,player=ctx.playerId,cardItemId=c.recovered,reveal=true,destinationZoneId=zone('hand',ctx.playerId)}),c,'recovered')}
    elseif c.stage=='recovered' or c.stage=='discarded' then
        if ctx.payload.outcome~='completed' then return {} end
        if c.stage=='recovered' then
            for _,id in ipairs(PlayerData.GetHand(ctx.playerId)) do if id~=c.recovered then c.others[#c.others+1]=id end end
        end
        if c.index>#c.others then return {} end
        local id=c.others[c.index];c.index=c.index+1
        return {tag(Effect.MoveCharacterCardToZone({apiVersion=1,player=ctx.playerId,cardItemId=id,reveal=true,destinationZoneId=zone('discard',ctx.playerId)}),c,'discarded')}
    end
    return {}
end
