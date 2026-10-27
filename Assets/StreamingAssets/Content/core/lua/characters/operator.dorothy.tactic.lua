-- 初始数量由Lua选择；提交后重读真实供应，逐枚交通用token移动校验。
local function initial_marker_inventory(ctx, card)
    local carriers = Global.Content.FindInstances({ id = card })
    if #carriers ~= 1 or carriers[1].controller ~= ctx.playerId then error('initial_marker_carrier_invalid') end
    local supplies = Global.Content.FindZones({ templateId = 'core.zone.player_supply', owner = ctx.playerId })
    local storage = Global.Content.FindZones({ parentId = card, localZoneId = 'markers' })
    if #supplies ~= 1 or #storage ~= 1 then error('initial_marker_zone_invalid') end
    local ids, seen = {}, {}
    for _, item in ipairs(Global.Content.FindInstances({ zoneId = supplies[1].id, typeId = 'core.player_marker' })) do
        if item.ownerSubject == ctx.playerId then
            if type(item.id) ~= 'string' or item.id == '' or seen[item.id] then error('initial_marker_inventory_invalid') end
            seen[item.id] = true
            ids[#ids + 1] = item.id
        end
    end
    table.sort(ids)
    return ids, supplies[1].id, storage[1].id
end

local function initial_marker_choice(ctx, card)
    local stock = initial_marker_inventory(ctx, card)
    local maximum = #stock
    if maximum > 4 then maximum = 4 end
    if maximum == 0 then return {} end
    local ids, options = {}, {}
    for count = 0, maximum do
        local id = tostring(count)
        ids[#ids + 1] = id
        options[#options + 1] = { candidateId = id, label = count == 0
            and Text.Ref('core:interaction.dorothy.initial_markers.none')
            or Text.Ref('core:interaction.dorothy.initial_markers.count', { count = count }) }
    end
    return { Effect.WithContext(Effect.WithPresentation(Effect.Choice({ player = ctx.playerId,
        options = ids, minSelections = 1, maxSelections = 1, interactionType = 'lua.choice',
        promptKey = 'core:interaction.dorothy.initial_markers.prompt' }),
        { interaction = { prompt = Text.Ref('core:interaction.dorothy.initial_markers.prompt', { count = maximum }), options = options } }),
        { protocol = 1, stage = 'initial_markers_selected', source = card, maximum = maximum }) }
end

local function initial_markers_selected(ctx, saved)
    if saved.protocol ~= 1 or type(saved.source) ~= 'string' or saved.source == ''
        or type(saved.maximum) ~= 'number' or saved.maximum % 1 ~= 0 or saved.maximum < 1 or saved.maximum > 4 then
        error('initial_marker_context_invalid')
    end
    local raw = ctx.payload.normalizedResult
    local count = type(raw) == 'string' and tonumber(raw) or nil
    if count == nil or count % 1 ~= 0 or count < 0 or count > saved.maximum or tostring(count) ~= raw then
        error('initial_marker_count_invalid')
    end
    local stock, supply, storage = initial_marker_inventory(ctx, saved.source)
    if count == 0 then return {} end
    local effects = {}
    for i = 1, count do
        local ids = {}
        if stock[i] ~= nil then ids[1] = stock[i] end
        -- 缺少实例时保留空列表，让这一枚移动失败；不虚构ID或撤回其它移动。
        effects[#effects + 1] = Effect.MoveItem({ apiVersion = 3, player = ctx.playerId, itemIds = ids,
            expectedSource = { zoneId = supply }, destination = { zoneId = storage },
            insertion = { kind = 'unordered' }, operationProfileId = 'core.cube_storage', requiredCount = 1 })
    end
    return effects
end

-- 仅本牌获授权库存；分页/来源身份在本次查询中保持一致，提交时仍由通用token事务复核。
local function inventory(ctx, card, expectedScope, expectedHash)
    local zones = Global.Content.FindZones({ parentId = card, localZoneId = 'markers' })
    if #zones ~= 1 then error('dorothy_marker_storage_missing') end
    local zone, tokens, seen, cursor, total = zones[1].id, {}, {}, 0, nil
    local scope, contentHash = expectedScope, expectedHash
    repeat
        local page = SourceData.GetTransferableTokens({ carrierItemId = card, localZoneId = 'markers',
            zoneId = zone, cursor = cursor, expectedStateRevision = ctx.stateRevision,
            expectedContentHash = contentHash })
        if scope ~= nil and page.ruleOwnerSourceKey ~= scope then error('dorothy_source_changed') end
        if total ~= nil and page.totalCount ~= total then error('dorothy_inventory_changed') end
        scope, contentHash, total = page.ruleOwnerSourceKey, page.contentHash, page.totalCount
        for _, token in ipairs(page.items) do
            local permitted = false
            for _, operation in ipairs(token.operations) do
                if operation == 'transfer.original_supply' then permitted = true end
            end
            if not permitted or token.physicalKind ~= 'Cube' or token.zoneId ~= zone
                or token.carrierItemId ~= card or token.localZoneId ~= 'markers'
                or token.originalSupplyZoneId == '' or seen[token.itemId] then error('dorothy_token_inventory_invalid') end
            seen[token.itemId] = true
            tokens[#tokens + 1] = token
        end
        if page.nextCursor ~= -1 and page.nextCursor <= cursor then error('dorothy_inventory_cursor_invalid') end
        cursor = page.nextCursor
    until cursor == -1
    if #tokens ~= total then error('dorothy_inventory_incomplete') end
    table.sort(tokens, function(a, b) return a.itemId < b.itemId end)
    return tokens, zone, scope, contentHash
end

local function equivalence_key(token)
    -- 不比较内部ID；保留类型、真实原主/控制者、原供应和随实例保留的规则状态差异。
    local key = token.hasOpenDeparture and '1' or '0'
    for _, field in ipairs({ 'typeId', 'ownerSubject', 'controller', 'originalSupplyZoneId', 'localStateDigest' }) do
        local value = token[field]
        key = key .. #value .. ':' .. value
    end
    return key
end

local function transfer(ctx, zone, ids)
    return Effect.MoveItem({ apiVersion = 3, player = ctx.playerId, itemIds = ids,
        expectedSource = { zoneId = zone }, destination = { kind = 'original_supply' },
        insertion = { kind = 'unordered' }, operationProfileId = 'core.cube_storage', requiredCount = 2 })
end

local function removal(ctx, card)
    local tokens, zone, scope, contentHash = inventory(ctx, card)
    local groups, ordered = {}, {}
    for _, token in ipairs(tokens) do
        local key = equivalence_key(token)
        if groups[key] == nil then groups[key] = {}; ordered[#ordered + 1] = groups[key] end
        -- 每组只需两个真实代表；等价实例换位不是另一种组合。
        if #groups[key] < 2 then groups[key][#groups[key] + 1] = token end
    end
    if #tokens <= 2 or #ordered <= 1 then
        local ids = {}
        local count = #tokens
        if count > 2 then count = 2 end
        for i = 1, count do ids[#ids + 1] = tokens[i].itemId end
        -- 不足也交足额事务返回失败；不能用空左式让Condition放行右式。
        return transfer(ctx, zone, ids)
    end
    local branches, options, pairIds = {}, {}, {}
    local function add_pair(first, second)
        local id = 'pair.' .. tostring(#branches + 1)
        -- 组合固定真实实例；点选后沿原权威续接提交同一个足额移动，不再逐枚选择。
        pairIds[id] = { first.itemId, second.itemId }
        branches[#branches + 1] = { id = id, effects = {} }
        options[#options + 1] = { candidateId = id, label = Text.Ref('core:interaction.dorothy.removal.pair',
            { first = Text.Item(first.itemId), second = Text.Item(second.itemId) }) }
    end
    for i, group in ipairs(ordered) do
        if #group == 2 then add_pair(group[1], group[2]) end
        for j = i + 1, #ordered do add_pair(group[1], ordered[j][1]) end
    end
    return Effect.WithContext(Effect.WithPresentation(Effect.Choice({ player = ctx.playerId,
        branches = branches, interactionType = 'effect.choice.execute',
        promptKey = 'core:interaction.dorothy.removal.prompt' }),
        { interaction = { prompt = Text.Ref('core:interaction.dorothy.removal.prompt'), options = options } }),
        { protocol = 1, stage = 'tokens_pair_selected', source = card, zone = zone, scope = scope, contentHash = contentHash, pairs = pairIds })
end

return function(ctx)
    if ctx.eventType == 'EffectCompleted' then
        local c = ctx.payload.continuationContext
        if c == nil or ctx.payload.outcome ~= 'completed' then return {} end
        if c.stage == 'initial_markers_selected' then return initial_markers_selected(ctx, c) end
        if c.protocol ~= 1 or c.stage ~= 'tokens_pair_selected' or type(c.pairs) ~= 'table' then error('dorothy_continuation_invalid') end
        local selected = ctx.payload.normalizedResult
        local ids = type(selected) == 'string' and c.pairs[selected] or nil
        if type(ids) ~= 'table' or #ids ~= 2 or type(ids[1]) ~= 'string' or type(ids[2]) ~= 'string'
            or ids[1] == ids[2] then error('dorothy_pair_selection_invalid') end
        local tokens, zone = inventory(ctx, c.source, c.scope, c.contentHash)
        if zone ~= c.zone then error('dorothy_source_changed') end
        local current = {}
        for _, token in ipairs(tokens) do current[token.itemId] = true end
        if not current[ids[1]] or not current[ids[2]] then error('dorothy_token_selection_changed') end
        return { transfer(ctx, zone, { ids[1], ids[2] }) }
    end
    local card = ctx.payload.cardInstanceRef
    if ctx.eventType == 'InfluenceRemoved' or ctx.eventType == 'InfluenceReplaced' then
        return { Effect.WithPresentation(Effect.Choice({ interactionType = 'effect.choice.execute', player = ctx.playerId, promptKey = 'operator.dorothy.store', branches = {
            { id = 'store', effects = { Effect.StoreRemovedInfluence({ apiVersion = 2, player = ctx.playerId, cardInstanceId = card, localZoneId = 'markers', influenceItemId = ctx.payload.influenceId }) } },
            { id = 'decline', effects = {} } } }), { interaction = { prompt = Text.Ref('core:interaction.dorothy.store.prompt'), options = { { candidateId = 'store', label = Text.Ref('core:interaction.dorothy.store') }, { candidateId = 'decline', label = Text.Ref('core:interaction.dorothy.decline') } } } }) }
    end
    if ctx.eventType == 'PlayerCleanupStarted' then
        return { Effect.WithPresentation(Effect.Condition({ leftEffects = { removal(ctx, card) }, rightEffects = { Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'player_supply', ownerSubject = ctx.playerId, causeKind = 'other_intrinsic_flow' }) } }), { interaction = { prompt = Text.Ref('core:condition.operator.dorothy.tactic') } }) }
    end
    return initial_marker_choice(ctx, card)
end
