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

local function finish(ctx)
    return {}
end
local function next_card(ctx,c)
    if #c.names >= 2 then return finish(ctx) end
    local ids,labels={},{}
    for _, item in ipairs(Global.Content.FindInstances({typeId='core.facility_card'})) do
        local z=Global.Content.FindZones({id=item.zoneId})[1]
        local definition=Global.Content.GetDefinition(item.definitionId)
        if z ~= nil and z.templateId=='core.zone.facility_supply_slot' and not has(c.names,definition.name) then
            ids[#ids+1]=item.id;labels[item.id]=Text.Ref('core:interaction.entity.item',{entity=Text.Item(item.id)})
        end
    end
    return {choose(ctx,c,'selected',ids,labels,'core:interaction.facility.take.choose',0,'lua.choice')}
end
return function(ctx)
    
    local c=state(ctx);if c==nil then return {} end
    if c.stage=='start' then
        c.names={};c.destination=zone('facility_deck');if c.destination==nil then error('facility_destination_missing') end
        return next_card(ctx,c)
    elseif c.stage=='selected' then
        local id=ctx.payload.normalizedResult;if id==nil then return finish(ctx) end
        local item=Global.Content.GetInstance(id);if item==nil then error('facility_source_changed') end
        c.names[#c.names+1]=Global.Content.GetDefinition(item.definitionId).name;c.supply=item.zoneId
        return {tag(Effect.MoveItem({apiVersion=1,player=ctx.playerId,itemRefs={{itemId=id}},expectedSource={zoneId=item.zoneId},
            destination={zoneId=c.destination},insertion={kind='bottom'},operationProfileId='core.facility_card_transfer',requiredCount=1}),c,'moved')}
    elseif c.stage=='moved' then
        if ctx.payload.outcome~='completed' then return {} end
        return {tag(Effect.RefillFacilitySlot({apiVersion=1,player=ctx.playerId,slotZoneId=c.supply}),c,'refilled')}
    elseif c.stage=='refilled' then
        if ctx.payload.outcome~='completed' then return {} end
        return next_card(ctx,c)
    end
    return {}
end
