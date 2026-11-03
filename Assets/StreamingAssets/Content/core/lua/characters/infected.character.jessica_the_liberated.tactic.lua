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
local function current_binding(ctx, source)
    for _, binding in ipairs(PlayerData.Get(ctx.playerId).bindings) do if binding.characterItemId == source then return binding end end
    return nil
end
local function move(ctx, id, destination, reveal)
    local item = Global.Content.GetInstance(id)
    if item == nil then error('character_move_source_missing') end
    return Effect.MoveCharacterCard({ apiVersion = 2, player = ctx.playerId, itemRefs = {{ itemId = id }},
        expectedSource = { zoneId = item.zoneId, slotId = item.slotId }, destination = { zoneId = destination },
        insertion = { kind = 'unordered' }, operationProfileId = 'core.character_card_transfer', requiredCount = 1, reveal = reveal == true })
end
return function(ctx)
    local c = state(ctx); if c == nil then return {} end
    if c.stage == 'start' and ctx.eventType == 'PlayerCleanupStarted' then
        local binding = current_binding(ctx, c.source); if binding == nil then return {} end
        local facility = Global.Content.GetInstance(binding.facilityItemId); if facility == nil then return {} end
        local definition = Global.Content.GetDefinition(facility.definitionId)
        c.binding = binding.bindingId; c.facility = binding.facilityItemId; c.cost = definition.goldVoucherCost + 2
        return { tag(Effect.WithPresentation(Effect.Condition({
            leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'gold_voucher', amount = c.cost }) },
            rightEffects = { Effect.GainScore({ recipient = ctx.playerId, amount = 2 }) }
        }), { interaction = { prompt = Text.Ref('core:condition.character.jessica.binding_settlement', { cost = c.cost }) } }), c, 'settlement_paid') }
    elseif c.stage == 'start' then
        return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.LoseScore({ player = ctx.playerId, amount = 2 }) },
            rightEffects = { tag(Effect.Build({ apiVersion=3, targetMode='select', player = ctx.playerId, sourceKind = 'supply', paymentMode = 'free', includeStoredFacilities = true , consumeMainAction=false , allowDecline=false }), c, 'built') }
        }), { interaction = { prompt = Text.Ref('core:condition.infected.character.jessica_the_liberated.tactic') } }) }
    elseif c.stage == 'built' then
        if ctx.payload.outcome ~= 'completed' or ctx.payload.normalizedResult.outcome ~= 'built' or ctx.payload.normalizedResult.builtItemId == '' then return {} end
        return { tag(Effect.BindCharacterToFacility({ apiVersion = 1, player = ctx.playerId, characterItemId = c.source,
            facilityItemId = ctx.payload.normalizedResult.builtItemId }), c, 'done') }
    elseif c.stage == 'settlement_paid' then
        if ctx.payload.outcome ~= 'completed' or ctx.payload.normalizedResult.outcome ~= 'condition_met' then return {} end
        local binding = current_binding(ctx, c.source)
        if binding == nil or binding.bindingId ~= c.binding or binding.facilityItemId ~= c.facility then error('character_binding_source_changed') end
        return { tag(move(ctx, c.source, zone('discard', ctx.playerId)), c, 'done') }
    end
    return {}
end
