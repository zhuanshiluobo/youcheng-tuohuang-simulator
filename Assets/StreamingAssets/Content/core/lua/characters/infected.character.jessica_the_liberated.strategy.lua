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

local function specials(ctx,c)
    local ids,labels={},{}
    for _,item in ipairs(PlayerData.Get(c.target).enterpriseSpecials) do
        ids[#ids+1]=item.id
        labels[item.id]=Text.Ref('core:interaction.enterprise.special',{player=Text.Player(c.target),enterprise=Text.Item(item.enterpriseItemId),level=item.level})
    end
    if #ids==0 then return {} end
    return {choose(ctx,c,'selected',ids,labels,'core:interaction.enterprise.special.choose',1,'effect.choice.execute')}
end
return function(ctx)
    local c=state(ctx);if c==nil then return {} end
    if c.stage=='start' then
        local ids,labels={},{}
        for _,player in ipairs(GameData.GetPlayers()) do
            if player.playerId~=ctx.playerId and #PlayerData.Get(player).enterpriseSpecials>0 then
                ids[#ids+1]=player.playerId;labels[player.playerId]=Text.Ref('core:interaction.player.name',{player=Text.Player(player.playerId)})
            end
        end
        if #ids==0 then return {} end
        return {choose(ctx,c,'target_selected',ids,labels,'core:interaction.enterprise.owner.choose',1,'lua.choice')}
    elseif c.stage=='target_selected' then c.target=ctx.payload.normalizedResult;return specials(ctx,c)
    elseif c.stage=='selected' then
        for _,item in ipairs(PlayerData.Get(c.target).enterpriseSpecials) do
            if item.id==ctx.payload.normalizedResult then
                return {tag(Effect.ActivateEnterpriseSpecial({apiVersion=1,player=ctx.playerId,unlockedByPlayer=c.target,enterpriseItemIds={item.enterpriseItemId},specialId=item.specialId}),c,'done')}
            end
        end
        error('enterprise_special_source_changed')
    end
    return {}
end
