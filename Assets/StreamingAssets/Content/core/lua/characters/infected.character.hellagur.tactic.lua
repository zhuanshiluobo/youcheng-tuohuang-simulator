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

local function cover_choice(ctx, c)
    local ids, labels = {}, {}
    for _, id in ipairs(PlayerData.GetHand(ctx.playerId)) do ids[#ids + 1] = id end
    for _, id in ipairs(PlayerData.GetDiscard(ctx.playerId)) do if not has(ids, id) then ids[#ids + 1] = id end end
    if #ids == 0 then return {} end
    for _, id in ipairs(ids) do labels[id] = Text.Ref('core:interaction.entity.item', { entity = Text.Item(id) }) end
    return { choose(ctx, c, 'cover_selected', ids, labels, 'core:interaction.hellagur.cover', 1, 'lua.choice') }
end
return function(ctx)
    local c = state(ctx); if c == nil then return {} end
    if c.stage == 'start' then
        local ids, labels = {}, {}
        for _, id in ipairs(PlayerData.GetHand(ctx.playerId)) do ids[#ids + 1] = id; labels[id] = Text.Ref('core:interaction.entity.item', { entity = Text.Item(id) }) end
        return { choose(ctx, c, 'discard_selected', ids, labels, 'core:interaction.hellagur.discard_optional', 0, 'character.ability.discard') }
    elseif c.stage == 'discard_selected' then
        local id = ctx.payload.normalizedResult
        if id == nil then return cover_choice(ctx, c) end
        return { tag(Effect.MoveCharacterCardToZone({ apiVersion = 1, player = ctx.playerId, cardItemId = id,
            destinationZoneId = zone('discard', ctx.playerId) }), c, 'discarded') }
    elseif c.stage == 'discarded' then
        if ctx.payload.outcome ~= 'completed' then return {} end
        return cover_choice(ctx, c)
    elseif c.stage == 'cover_selected' then
        return { tag(Effect.CoverCharacterCard({ apiVersion = 3, player = ctx.playerId, cardInstanceId = ctx.payload.normalizedResult,
            coverMode = 'additional', acceptedTypeId = 'core.character_card', reusable = false,
            blockedActionRoundSequence = PlayerData.Get(ctx.playerId).actionRoundSequence }), c, 'done') }
    end
    return {}
end
