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

local function cover(ctx, c)
    for _, slot in ipairs(PlayerData.Get(ctx.playerId).coveredCards) do
        if slot.reusable and slot.sourceEffectId == 'character-card:' .. c.source then return {} end
    end
    local ids, labels = {}, {}
    for _, id in ipairs(PlayerData.GetHand(ctx.playerId)) do
        local item = Global.Content.GetInstance(id)
        if item ~= nil and item.typeId == 'core.generated_character_card' then ids[#ids + 1] = id; labels[id] = Text.Ref('core:interaction.entity.item', { entity = Text.Item(id) }) end
    end
    return { choose(ctx, c, 'cover_selected', ids, labels, 'core:interaction.muelsyse.cover', 0, 'lua.choice') }
end
local function acquire(ctx, c)
    if c.index > #c.items then return cover(ctx, c) end
    local ids = { 'hand', 'discard' }; local labels = { hand = Text.Ref('core:interaction.card.destination.hand'), discard = Text.Ref('core:interaction.card.destination.discard') }
    return { choose(ctx, c, 'destination_selected', ids, labels, 'core:interaction.card.destination.choose', 1, 'lua.choice') }
end
return function(ctx)
    local c = state(ctx); if c == nil then return {} end
    if c.stage == 'start' then
        if ctx.eventType == 'CollectionStarted' then
            c.covered = {}
            for _, slot in ipairs(PlayerData.Get(ctx.playerId).coveredCards) do
                if slot.reusable and slot.sourceEffectId == 'character-card:' .. c.source then c.covered[#c.covered + 1] = slot.cardItemId end
            end
            if #c.covered == 0 then return {} end
            return { choose(ctx, c, 'recycle_selected', { 'recycle', 'decline' }, {
                recycle = Text.Ref('core:interaction.muelsyse.recycle'), decline = Text.Ref('core:interaction.muelsyse.keep') }, 'core:interaction.muelsyse.recycle.choose', 0, 'effect.choice.execute') }
        elseif ctx.eventType == 'CharacterCardCovered' or ctx.eventType == 'CharacterUseFinished' then return cover(ctx, c) end
        c.items = {}; c.index = 1
        for _, item in ipairs(Global.Content.FindInstances({ zoneId = zone('flowing_shapes'), typeId = 'core.generated_character_card' })) do c.items[#c.items + 1] = item.id end
        table.sort(c.items)
        return acquire(ctx, c)
    elseif c.stage == 'destination_selected' then
        local destination = ctx.payload.normalizedResult
        if destination ~= 'hand' and destination ~= 'discard' then error('generated_destination_invalid') end
        return { tag(Effect.MoveCharacterCardToZone({ apiVersion = 1, player = ctx.playerId, cardItemId = c.items[c.index],
            destinationZoneId = zone(destination, ctx.playerId) }), c, 'acquired') }
    elseif c.stage == 'acquired' then
        if ctx.payload.outcome ~= 'completed' then return {} end
        c.index = c.index + 1; return acquire(ctx, c)
    elseif c.stage == 'cover_selected' then
        local id = ctx.payload.normalizedResult; if id == nil then return {} end
        return { tag(Effect.CoverCharacterCard({ apiVersion = 3, player = ctx.playerId, cardInstanceId = id, coverMode = 'additional',
            acceptedTypeId = 'core.generated_character_card', reusable = true, blockedActionRoundSequence = 0 }), c, 'done') }
    elseif c.stage == 'recycle_selected' then
        if ctx.payload.normalizedResult ~= 'recycle' then return {} end
        return { tag(Effect.RecycleCharacterCover({ apiVersion = 1, player = ctx.playerId, coveredItemIds = c.covered, sourceItemId = c.source }), c, 'done') }
    end
    return {}
end
