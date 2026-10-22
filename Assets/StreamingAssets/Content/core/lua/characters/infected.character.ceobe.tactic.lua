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
    if c.stage == 'start' then return { tag(Effect.RollDice({ apiVersion = 1, player = ctx.playerId, count = 1, sides = 6 }), c, 'rolled') }
    elseif c.stage == 'rolled' then
        if ctx.payload.outcome ~= 'completed' then return {} end
        c.dice = ctx.payload.normalizedResult.diceResult
        if c.dice <= 2 then return { Effect.SelectInfluence({ player = ctx.playerId, operation = 'remove', subjectFilter = 'any', count = 1 }),
            Effect.Repeat({ apiVersion = 1, count = 2, allowEarlyStop = false, effects = { Effect.PlaceInfluence({ executingPlayer = ctx.playerId, ownerSubject = ctx.playerId, influenceSource = 'player_supply' }) } }) } end
        if c.dice <= 4 then return { Effect.GainResource({ player = ctx.playerId, resourceType = 'pure_originium', amount = 2 }) } end
        return { tag(Effect.ExecuteMainAction({ player = ctx.playerId, executionMode = 'immediate', allowedActionTypes = { 'deploy', 'dispatch', 'explore', 'move_city', 'build', 'special' } }), c, 'action_completed') }
    elseif c.stage == 'action_completed' then
        if ctx.payload.outcome ~= 'completed' then return {} end
        local ids, labels = {}, {}
        for _, player in ipairs(GameData.GetPlayers()) do if player.playerId ~= ctx.playerId then
            local id = string.sub(player.playerId, 2); ids[#ids + 1] = id
            labels[id] = Text.Ref('core:interaction.player.name', { player = Text.Player(player.playerId) })
        end end
        if #ids == 0 then return {} end
        return { choose(ctx, c, 'recipient_selected', ids, labels, 'core:interaction.ceobe.transfer.choose', 1, 'lua.choice') }
    elseif c.stage == 'recipient_selected' then
        return { tag(Effect.TransferCharacterCard({ apiVersion = 1, player = ctx.playerId, cardInstanceId = c.source,
            recipientPlayer = ctx.payload.normalizedResult }), c, 'done') }
    end
    return {}
end
