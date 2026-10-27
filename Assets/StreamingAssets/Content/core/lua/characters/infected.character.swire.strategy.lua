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
local function has(values, id)
    for _, value in ipairs(values or {}) do if value == id then return true end end; return false
end
return function(ctx)
    local c = state(ctx); if c == nil then return {} end
    c.selected = c.selected or {}
    if c.stage == 'activated' then
        local result = ctx.payload.normalizedResult
        if type(result) ~= 'table' then error('swire_special_result_missing') end
        if result.changed == false and result.reasonCode == 'no_candidates' then return {} end
        local selected = result.selectedSpecialId
        if type(selected) ~= 'string' or selected == '' or has(c.selected, selected) then error('swire_special_selection_invalid') end
        c.selected[#c.selected + 1] = selected
    elseif c.stage ~= 'start' then return {} end
    if #c.selected >= 2 then return {} end
    local candidates = {}
    for _, item in ipairs(PlayerData.Get(ctx.playerId).enterpriseSpecialCandidates) do
        if item.level >= 1 and item.level <= 2 and not has(c.selected, item.specialId) then candidates[#candidates + 1] = item.candidateId end
    end
    if #candidates == 0 then return {} end
    return { tag(Effect.ActivateEnterpriseSpecial({ apiVersion = 1, player = ctx.playerId, unlockedByPlayer = ctx.playerId,
        minLevel = 1, maxLevel = 2, candidateIds = candidates }), c, 'activated') }
end
