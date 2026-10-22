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
    local c = state(ctx); if c == nil then return {} end
    if c.stage == 'selected' then
        return { tag(Effect.ActivateCityStyleSpecial({ apiVersion = 1, player = ctx.playerId, specialActionId = ctx.payload.normalizedResult,
            declarationMarkerId = 'public_source', activationSource = 'public_style' }), c, 'done') }
    elseif c.stage ~= 'start' then return {} end
    local ids, labels = {}, {}
    for _, item in ipairs(Global.Content.FindInstances({ contentType = 'city_style' })) do
        local definition = Global.Content.GetDefinition(item.definitionId)
        local z = nil; for _, candidate in ipairs(Global.Content.FindZones({ localZoneId = 'city_styles' })) do if candidate.id == item.zoneId then z = candidate end end
        if z ~= nil and definition.level == 1 and definition.specialActionId ~= nil and definition.specialActionId ~= '' and not has(ids, definition.specialActionId) then
            ids[#ids + 1] = definition.specialActionId
            labels[definition.specialActionId] = Text.Ref('core:interaction.entity.item', { entity = Text.Item(item.id) })
        end
    end
    if #ids == 0 then return {} end
    return { choose(ctx, c, 'selected', ids, labels, 'core:interaction.public_style.choose', 1, 'effect.choice.execute') }
end
