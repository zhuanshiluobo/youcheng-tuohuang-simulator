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
        local selected = ctx.payload.normalizedResult; local chosen = nil
        for _, player in ipairs(GameData.GetPlayers()) do
            for _, item in ipairs(PlayerData.Get(player).enterpriseSpecials) do if item.id == selected then chosen = item end end
        end
        if chosen == nil then error('enterprise_special_source_changed') end
        c.selected = c.selected or {}; c.selected[#c.selected + 1] = chosen.specialId
        return { tag(Effect.ActivateEnterpriseSpecial({ apiVersion = 1, player = ctx.playerId,
            unlockedByPlayer = chosen.unlockedByPlayer, enterpriseItemIds = { chosen.enterpriseItemId }, specialId = chosen.specialId }), c, 'activated') }
    elseif c.stage == 'activated' then c.count = (c.count or 0) + 1
    elseif c.stage ~= 'start' then return {} end
    local borrow = false
    if (c.count or 0) >= (borrow and 1 or 2) then return {} end
    local ids, labels = {}, {}
    for _, player in ipairs(GameData.GetPlayers()) do
        if borrow and player.playerId ~= ctx.playerId or not borrow and player.playerId == ctx.playerId then
            for _, item in ipairs(PlayerData.Get(player).enterpriseSpecials) do
                if (borrow or item.level >= 1 and item.level <= 2) and not has(c.selected, item.specialId) then
                    ids[#ids + 1] = item.id
                    labels[item.id] = Text.Ref('core:interaction.enterprise.special', { player = Text.Player(player.playerId),
                        enterprise = Text.Item(item.enterpriseItemId), level = item.level })
                end
            end
        end
    end
    if #ids == 0 then return {} end
    return { choose(ctx, c, 'selected', ids, labels, 'core:interaction.enterprise.special.choose', 1, 'effect.choice.execute') }
end
