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
local function choose(ctx, context, stage, ids, labels, prompt, minimum, kind, promptArguments)
    local options = {}; for _, id in ipairs(ids) do options[#options + 1] = { candidateId = id, label = labels[id] } end
    return tag(Effect.WithPresentation(Effect.Choice({ player = ctx.playerId, options = ids,
        minSelections = minimum or 1, maxSelections = 1, interactionType = kind or 'lua.choice', promptKey = prompt }),
        { interaction = { prompt = Text.Ref(prompt, promptArguments or {}), options = options } }), context, stage)
end

local function locations(ctx, c, values, stage, prompt)
    local ids, labels = {}, {}
    for _, id in ipairs(values) do ids[#ids + 1] = id; labels[id] = Text.Ref('core:interaction.location.name', { location = Text.Location(id) }) end
    if #ids == 0 then return {} end
    return { tag(Effect.WithPresentation(Effect.Choice({ player = ctx.playerId, options = ids, minSelections = 1, maxSelections = 1, interactionType = 'effect.map.location.target', promptKey = prompt }), { interaction = { prompt = Text.Ref(prompt) } }), c, stage) }
end
return function(ctx)
    local c = state(ctx); if c == nil then return {} end
    if c.stage == 'start' then
        for _, item in ipairs(Global.Content.FindInstances({ definitionId = 'infected.component.gavial_token' })) do
            for _, relation in ipairs(item.relations) do
                if relation.relationTypeId == 'core.persistent_token_source' and relation.targetItemId == c.source then c.token = item.id end
            end
        end
        if ctx.eventType == 'PlayerCleanupStarted' then
            if c.token == nil then return {} end
            return locations(ctx, c, Global.Content.GetInstance(c.token).adjacentDestinations, 'adjacent_selected', 'core:interaction.token.adjacent.choose')
        end
        if c.token ~= nil then return {} end
        local ids = {}; for _, point in ipairs(Global.Map.GetMap().resourcePoints) do
            local location = Global.Map.GetLocation(point.locationId)
            if location ~= nil and location.isOpen then ids[#ids + 1] = point.locationId end
        end
        return locations(ctx, c, ids, 'initial_selected', 'core:interaction.token.initial.choose')
    elseif c.stage == 'initial_selected' then
        return { tag(Effect.CreateMapToken({ apiVersion = 1, player = ctx.playerId, definitionId = 'infected.component.gavial_token',
            sourceItemId = c.source, destinationLocationId = ctx.payload.normalizedResult, operationProfileId = 'core.adjacent_location_token_move' }), c, 'done') }
    elseif c.stage == 'adjacent_selected' then
        return { tag(Effect.OperateToken({ apiVersion = 1, player = ctx.playerId, tokenItemId = c.token, operation = 'move',
            destinationLocationId = ctx.payload.normalizedResult, operationProfileId = 'core.adjacent_location_token_move' }), c, 'token_moved') }
    elseif c.stage == 'token_moved' then
        if ctx.payload.outcome ~= 'completed' then return {} end
        local location = Global.Content.GetInstance(c.token).locationId; local ids, labels = {}, {}
        for _, item in ipairs(Global.Map.FindInfluences({ locationId = location })) do
            ids[#ids + 1] = item.influenceId
            labels[item.influenceId] = Text.Ref('core:interaction.token.arrival.influence')
        end
        if #ids == 0 then return {} end
        return { choose(ctx, c, 'influence_selected', ids, labels, 'core:interaction.token.arrival.choose', 1,
            'effect.influence.move.source', { resourcePoint = location }) }
    elseif c.stage == 'influence_selected' then
        return { tag(Effect.MoveInfluence({ executingPlayer = ctx.playerId, targetInfluence = ctx.payload.normalizedResult }), c, 'done') }
    end
    return {}
end
