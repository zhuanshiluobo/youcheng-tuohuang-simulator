-- 蒂亚戈正式内容。数值与规则只在此冻结Lua声明；宿主负责真实付款、随机、权限及恢复。
-- D09收尾、D07/D08调用共享FundInvestment；各来源和完成处理器仍各自登记。
local modifiers = {
    D01 = { definition = 'entrepreneur.round.D.1', gold = -2 },
    D02 = { definition = 'entrepreneur.round.D.1' },
    D03 = { definition = 'entrepreneur.round.D.3', gold = 4, reputation = 2, extra = true },
    D04 = { definition = 'entrepreneur.round.D.3', total = 1, reputation = 1, adjacent = true },
    D05 = { definition = 'entrepreneur.round.D.5', total = 1, exchange = -1 },
    D06 = { definition = 'entrepreneur.round.D.5', gold = 4, reputation = 1, dice = 3 },
    D07 = { definition = 'entrepreneur.round.D.7' },
    D08 = { definition = 'entrepreneur.round.D.7' }
}
local prefix = 'core:entrepreneur.D.'
local function copy(v)
    if type(v) ~= 'table' then return v end
    local r = {}; for k, x in pairs(v) do r[k] = copy(x) end; return r
end
local function tag(effect, c, stage)
    -- WithContext立即冻结封闭DTO；这里只改顶层stage，不必在VM中重复深拷贝只读骰子／参数。
    local saved = {}; for key, value in pairs(c) do saved[key] = value end
    saved.stage = stage
    return Effect.WithContext(effect, saved)
end
local function presented(effect, key, arguments)
    return Effect.WithPresentation(effect, { interaction = { prompt = Text.Ref(prefix .. key, arguments or {}) } })
end
local function numeric_player(ctx)
    local n = string.match(ctx.playerId, '^p(%d+)$')
    if n == nil then error('tiago_player_invalid') end
    return n
end
local function zone_for(carrier, localId)
    local zones = Global.Content.FindZones({ parentId = carrier, localZoneId = localId })
    if #zones ~= 1 then error('tiago_carrier_zone_missing:' .. localId) end
    return zones[1]
end
local function current_sources()
    local cursor, revision, content, ownerKey, all = 0, nil, nil, nil, {}
    repeat
        local page = SourceData.GetCurrentSources({ cursor = cursor,
            expectedStateRevision = revision, expectedContentHash = content })
        revision, content, ownerKey = page.stateRevision, page.contentHash, page.ruleOwnerSourceKey
        for _, source in ipairs(page.items) do all[#all + 1] = source end
        cursor = page.nextCursor
    until cursor == -1
    return all, ownerKey
end
local function selected_source(source, player, definition, face, ability, carrier)
    local receipt = source.selectionReceipt
    return source.authorizedPlayerId == player and source.definitionId == definition
        and source.activeFaceId == face and source.abilityId == ability
        and source.ruleOwnerItemId == source.itemId and source.effectiveZoneId ~= ''
        and source.abilityOwnerDefinitionId == 'entrepreneur.ability.D09'
        and source.abilityOwnerItemId ~= '' and (carrier == nil or source.abilityOwnerItemId == carrier)
        and receipt ~= nil and receipt.protocolVersion == 1 and receipt.faceId == face
        and receipt.requestId ~= '' and receipt.ownerEffectId ~= '' and receipt.candidateId ~= ''
        and source.authorizationRef == 'selection:' .. receipt.ownerEffectId
end
local function selected_rules(ctx, instance)
    local result, faces, applied = {}, {}, {}
    local sources = current_sources()
    for _, source in ipairs(sources) do
        local face = source.activeFaceId
        local rule = modifiers[face]
        -- 当前来源和真实收据是资格证明；roundChoices只是展示/选择历史，不能代替当前有效面。
        if rule ~= nil and selected_source(source, ctx.playerId, rule.definition, face,
            'entrepreneur.face.' .. face, instance.id) then
            local key = source.itemId .. ':' .. face
            if not applied[key] then
                applied[key] = true
                result[#result + 1] = rule; faces[#faces + 1] = face
            end
        end
    end
    return result, faces
end
local function parameters(ctx, instance)
    local rules, faces = selected_rules(ctx, instance)
    local p = { gold = 4, total = 0, dice = 2, reputation = 1, exchange = 3, adjacent = false, extra = false }
    for _, rule in ipairs(rules) do
        p.gold = p.gold + (rule.gold or 0); p.total = p.total + (rule.total or 0)
        p.reputation = p.reputation + (rule.reputation or 0); p.exchange = p.exchange + (rule.exchange or 0)
        if rule.dice ~= nil then p.dice = rule.dice end
        if rule.adjacent then p.adjacent = true end
        if rule.extra then p.extra = true end
    end
    return p, faces
end
local function reputation_tokens(ctx, carrier, operation)
    local cursor, revision, content, tokens = 0, nil, nil, {}
    repeat
        local page = SourceData.GetTransferableTokens({ carrierItemId = carrier,
            localZoneId = 'reputation', typeId = 'core.player_marker', cursor = cursor,
            expectedStateRevision = revision, expectedContentHash = content })
        revision, content = page.stateRevision, page.contentHash
        for _, item in ipairs(page.items) do
            local permitted = false
            for _, allowed in ipairs(item.operations) do
                if allowed == 'transfer.' .. operation then permitted = true end
            end
            -- D声望只取本人的普通玩家标记；具体合法性/保留/轨道由同次授权查询与提交复验。
            if item.carrierItemId == carrier and item.localZoneId == 'reputation'
                and item.typeId == 'core.player_marker' and item.ownerSubject == ctx.playerId and permitted then
                tokens[#tokens + 1] = item
            end
        end
        cursor = page.nextCursor
    until cursor == -1
    table.sort(tokens, function(a, b) return a.itemId < b.itemId end)
    return tokens
end
local function payment(ctx, c, count, destination)
    local operation = destination or 'original_supply'
    local tokens, ids = reputation_tokens(ctx, c.carrier, operation), {}
    -- 声望按卡面仅计本人普通标记数量；ID本身无选择意义，Lua稳定取足额，原供应逐枚保真。
    -- 短缺明确交空列表给通用足额操作失败，不把部分实例先转走。
    if #tokens >= count then for i = 1, count do ids[#ids + 1] = tokens[i].itemId end end
    local target = { kind = 'original_supply' }
    if operation == 'retired' then
        local retired = Global.Content.FindZones({ templateId = 'core.zone.box_retired' })
        if #retired ~= 1 then error('tiago_retired_zone_missing') end
        target = { zoneId = retired[1].id }
    end
    return Effect.MoveItem({ apiVersion = 3, player = ctx.playerId, itemIds = ids,
        expectedSource = { zoneId = zone_for(c.carrier, 'reputation').id }, destination = target,
        insertion = { kind = 'unordered' }, operationProfileId = 'core.player_marker_storage', requiredCount = count })
end
local function enterprises(ctx)
    local ids = {}
    -- 企业展示区属于公共区域；玩家资格由企业升级效果按本人合作标记筛选。
    for _, zone in ipairs(Global.Content.FindZones({ templateId = 'core.zone.enterprise_display' })) do
        for _, item in ipairs(Global.Content.FindInstances({ zoneId = zone.id, contentType = 'enterprise' })) do ids[#ids + 1] = item.id end
    end
    table.sort(ids); return ids
end
local point
local function choose(ctx, c, points, extra)
    c = copy(c); c.offeredPoints = copy(points)
    local branches, labels = {}, {}
    for _, n in ipairs(points) do
        local id = tostring(n)
        -- 普通点数只读c；只有出售分支需要冻结main及累计结果，交其内部复制一次。
        branches[#branches + 1] = { id = id, effects = point(ctx, c, n, extra) }
        labels[#labels + 1] = { candidateId = id, label = Text.Ref(prefix .. 'point.' .. id, { point = n }) }
    end
    if #branches == 0 then error('tiago_point_choice_empty') end
    local choice = Effect.WithPresentation(Effect.Choice({ player = ctx.playerId, branches = branches,
        minSelections = 1, maxSelections = 1, interactionType = 'effect.choice.execute', promptKey = prefix .. 'points.prompt' }),
        { interaction = { prompt = Text.Ref(prefix .. 'points.prompt'), options = labels } })
    -- 主分支结束后只组织原额外条件；点数正文已在真实分支，不再用所选值生成。
    return extra and choice or tag(choice, c, 'main_completed')
end
local function all_points() return { 0, 1, 2, 3, 4, 5, 6, 7 } end
local function normal_points(c, extra)
    local seen, masks, result = {}, {}, {}
    for i, d in ipairs(c.dice) do
        local bit = i == 1 and 1 or i == 2 and 2 or 4
        local values = c.parameters.adjacent and not extra and { d - 1, d, d + 1 } or { d }
        for _, n in ipairs(values) do
            if not extra or n ~= c.main then
                local key = tostring(n)
                if not seen[key] then seen[key] = true; masks[key] = 0; result[#result + 1] = n end
                masks[key] = masks[key] + bit
            end
        end
    end
    table.sort(result); return result, masks
end
local function choose_main(ctx, c)
    local points, masks
    if c.mode == 'direct' then points = all_points(); masks = {} else points, masks = normal_points(c, false) end
    c.origins = masks
    return { choose(ctx, c, points, false) }
end
local function reputation_supply(ctx)
    local zones = Global.Content.FindZones({ templateId = 'core.zone.player_supply', owner = ctx.playerId })
    if #zones ~= 1 then error('tiago_player_supply_missing') end
    local supply, ids = zones[1], {}
    for _, item in ipairs(Global.Content.FindInstances({ zoneId = supply.id, typeId = 'core.player_marker' })) do
        if item.zoneId == supply.id and item.typeId == 'core.player_marker' and item.ownerSubject == ctx.playerId then
            ids[#ids + 1] = item.id
        end
    end
    table.sort(ids)
    return supply, ids
end
local function choose_reputation(ctx, c)
    local supply, ids = reputation_supply(ctx)
    local limit = c.parameters.reputation
    if #ids < limit then limit = #ids end
    if limit == 0 then return choose_main(ctx, c) end
    local branches, labels = {}, {}
    local target = zone_for(c.carrier, 'reputation').id
    for n = 1, limit do
        local effects = {}
        -- 每枚仍独立提交；只把正数选择和已确定的真实移动绑定，不合并成足量批次。
        for i = 1, n do
            effects[#effects + 1] = Effect.MoveItem({ apiVersion = 3, player = ctx.playerId, itemIds = { ids[i] },
                expectedSource = { zoneId = supply.id }, destination = { zoneId = target },
                insertion = { kind = 'unordered' }, operationProfileId = 'core.player_marker_storage', requiredCount = 1 })
        end
        local id = tostring(n)
        branches[#branches + 1] = { id = id, effects = effects }
        labels[#labels + 1] = { candidateId = id, label = Text.Ref(prefix .. 'reputation.amount', { count = n }) }
    end
    local choice = Effect.WithPresentation(Effect.Choice({ player = ctx.playerId, branches = branches,
        minSelections = 0, maxSelections = 1, interactionType = 'effect.choice.execute', promptKey = prefix .. 'reputation.prompt' }),
        { interaction = { prompt = Text.Ref(prefix .. 'reputation.prompt', { maximum = limit }), options = labels } })
    -- 先等真实存放（含其独立失败）或Choice放弃结束，再沿同一普通后继列表进入一次原点数页。
    -- 点数页不挂在每枚移动回调中，也不由取消答案伪造空移动来推进。
    return { choice, choose_main(ctx, c)[1] }
end
local function sale(ctx, c, stage)
    return tag(Effect.WithPresentation(Effect.SellResource({ apiVersion = 2, player = ctx.playerId, saleMode = 'batch',
        pricePolicy = 'core.resource_sale', interactionType = 'lua.choice', promptKey = 'effect.resource.sell' }),
        { interaction = { prompt = Text.Ref('core:interaction.sale.batch',
            { soldCount = c.saleSoldCount, revenue = c.saleRevenue }) } }), c, stage)
end
point = function(ctx, c, n, extra)
    local p = numeric_player(ctx)
    if n == 0 then return { Effect.ActivateEnterpriseSpecial({ apiVersion = 1, player = p, unlockedByPlayer = p }) }
    elseif n == 1 then return { Effect.ReplaceInfluence({ apiVersion=3, targetMode='select', executingPlayer= ctx.playerId, subjectFilter = 'any', count = 1, requiredCount = 0 }) }
    elseif n == 2 then return { Effect.GainResource({ player = ctx.playerId, resourceType = 'gold_voucher', amount = 15 }) }
    elseif n == 3 or n == 7 then
        return { Effect.UpgradeEnterprise({ apiVersion = 1, player = p, enterpriseItemIds = enterprises(ctx), levels = n == 3 and 1 or 2 }) }
    elseif n == 4 then
        c = copy(c); c.saleSoldCount = 0; c.saleRevenue = 0
        if not extra then c.main = 4 end
        return { sale(ctx, c, extra and 'sale_extra' or 'sale_main') }
    elseif n == 5 then return {
        Effect.GainResource({ player = ctx.playerId, resourceType = 'originium', amount = 2 }),
        Effect.GainResource({ player = ctx.playerId, resourceType = 'originium_shard', amount = 2 }),
        Effect.GainResource({ player = ctx.playerId, resourceType = 'iron', amount = 1 }) }
    elseif n == 6 then return {
        Effect.PlaceInfluence({ executingPlayer = ctx.playerId, ownerSubject = ctx.playerId, influenceSource = 'player_supply' }),
        Effect.MoveInfluence({ apiVersion=3, targetMode='select', executingPlayer= ctx.playerId, subjectFilter = 'self', count = 1, requiredCount = 0 }) }
    end
    error('tiago_point_invalid')
end
local function extra_condition(ctx, c)
    if not c.parameters.extra then return {} end
    local points = c.mode == 'direct' and all_points() or normal_points(c, true)
    if #points == 0 then return {} end -- 正常同点无资格，不付费，不弹空选择。
    local right
    if c.mode ~= 'direct' and c.parameters.dice == 2 then right = point(ctx, c, points[1], true)
    else
        -- 先结算真实的两枚费用，再构造原已冻结点数的实际分支；不把完整Choice再嵌进费用表。
        -- Condition本身有真实左式；只有其condition_met回执可继续，不用空Effect占位或放宽深度预算。
        c = copy(c); c.extraPoints = copy(points)
        return { tag(presented(Effect.Condition({ ruleCondition = true, decisionPlayer = ctx.playerId,
            leftEffects = { payment(ctx, c, 2) }, rightEffects = {} }), 'extra.condition'), c, 'extra_paid') }
    end
    return { presented(Effect.Condition({ ruleCondition = true, decisionPlayer = ctx.playerId,
        leftEffects = { payment(ctx, c, 2) }, rightEffects = right }), 'extra.condition') }
end
local function roll(ctx, c)
    return tag(Effect.RollDice({ apiVersion = 1, player = ctx.playerId, count = 1, sides = 6 }), c, 'rolled')
end
local function FundInvestment(ctx, c, mode)
    c = copy(c); c.mode = mode; c.dice = {}; c.dieIds = {}; c.parameters, c.selectedFaces = parameters(ctx, Global.Content.GetInstance(c.carrier))
    for _, die in ipairs(Global.Content.FindInstances({ definitionId = 'entrepreneur.component.die', owner = ctx.playerId })) do
        c.dieIds[#c.dieIds + 1] = die.id
    end
    table.sort(c.dieIds)
    if #c.dieIds ~= 3 then error('tiago_dice_components_invalid') end
    local args = { player = ctx.playerId, cost = { GoldVoucher = c.parameters.gold } }
    if c.parameters.total > 0 then
        args.apiVersion = 2; args.resourceType = 'total'; args.allowedResourceTypes = { 'Originium', 'OriginiumShard', 'Iron' }; args.amount = c.parameters.total
    end
    return presented(Effect.Condition({ ruleCondition = true, decisionPlayer = ctx.playerId,
        leftEffects = { Effect.PayResource(args) }, rightEffects = { roll(ctx, c) } }), 'investment.condition',
        { gold = c.parameters.gold, total = c.parameters.total, dice = c.parameters.dice, reputation = c.parameters.reputation })
end

-- 纯资格与正文同用实际定义/能力实例；完整分页留在VM，不把面名或回合号当作选面证明。
function TiagoExchangeAvailability(ctx)
    local enabled = false
    if ctx.window.kind ~= 'player_cleanup' or not ctx.facts.source.persistentActive then
        return { enabled = false, disabledReasonKey = prefix .. 'exchange.unavailable' }
    end
    local all, ownerKey = current_sources()
    local owner = nil
    for _, source in ipairs(all) do
        if source.sourceKey == ownerKey and source.itemId == ctx.facts.source.itemId
            and source.abilityId == ctx.facts.source.abilityId then owner = source end
    end
    if owner == nil then return { enabled = false, disabledReasonKey = prefix .. 'exchange.unavailable' } end
    local face = owner.abilityId == 'entrepreneur.D01.exchange' and 'D01'
        or owner.abilityId == 'entrepreneur.D02.exchange' and 'D02' or ''
    if not selected_source(owner, ctx.playerId, 'entrepreneur.round.D.1', face, owner.abilityId, nil) then
        return { enabled = false, disabledReasonKey = prefix .. 'exchange.unavailable' }
    end
    local carrierActive = false
    for _, source in ipairs(all) do
        if source.itemId == owner.abilityOwnerItemId and source.definitionId == 'entrepreneur.ability.D09'
            and source.abilityId == 'entrepreneur.D09.investment' and source.authorizedPlayerId == ctx.playerId
            and source.ruleOwnerItemId == source.itemId then carrierActive = true end
    end
    local cost = 3
    for _, source in ipairs(all) do
        if selected_source(source, ctx.playerId, 'entrepreneur.round.D.5', 'D05',
            'entrepreneur.face.D05', owner.abilityOwnerItemId) then
            cost = cost + modifiers.D05.exchange
            break -- 同一选面多个声明只应用一次；不叠加重复来源或立即处理器。
        end
    end
    enabled = carrierActive and #reputation_tokens(ctx, owner.abilityOwnerItemId, 'original_supply') >= cost
    return { enabled = enabled, disabledReasonKey = enabled and '' or prefix .. 'exchange.unavailable' }
end
function TiagoInvestmentAvailability(ctx)
    local enabled = ctx.window.kind == 'player_cleanup'
    return { enabled = enabled, disabledReasonKey = enabled and '' or 'core:presentation.unavailable' }
end

return function(ctx)
    if ctx.eventType == 'EffectCompleted' then
        local saved = ctx.payload.continuationContext
        if saved == nil then return {} end
        if type(saved) ~= 'table' or saved.version ~= 7 then error('tiago_context_version_invalid') end
        if saved.player ~= ctx.playerId then error('tiago_context_player_invalid') end
        if ctx.payload.outcome ~= 'completed' and not (saved.stage == 'main_completed' and ctx.payload.outcome == 'failed') then return {} end
        local c, result = copy(saved), ctx.payload.normalizedResult
        if c.stage == 'rolled' then
            local d = type(result) == 'table' and result.diceResult or nil
            if type(d) ~= 'number' or d % 1 ~= 0 or d < 1 or d > 6 then error('tiago_dice_result_invalid') end
            if #c.dice >= c.parameters.dice then error('tiago_dice_count_invalid') end
            c.dice[#c.dice + 1] = d
            if #c.dice < c.parameters.dice then return { roll(ctx, c) } end
            return choose_reputation(ctx, c)
        elseif c.stage == 'extra_paid' then
            if type(result) ~= 'table' or result.outcome ~= 'condition_met' then return {} end
            if type(c.extraPoints) ~= 'table' or #c.extraPoints == 0 then error('tiago_extra_points_missing') end
            return { choose(ctx, c, c.extraPoints, true) }
        elseif c.stage == 'main_completed' then
            if result == nil and ctx.payload.outcome == 'failed' then return {} end
            local n = tonumber(result)
            if n == nil or n % 1 ~= 0 or n < 0 or n > 7 then error('tiago_point_answer_invalid') end
            local offered = false
            for _, candidate in ipairs(c.offeredPoints or {}) do
                if candidate == n then offered = true end
            end
            if not offered then error('tiago_point_not_offered') end
            c.main = n
            -- 出售由原实际出售回执等完整结束后继续；不能在外层再开一次额外条件。
            if n == 4 then return {} end
            return extra_condition(ctx, c)
        elseif c.stage == 'sale_main' or c.stage == 'sale_extra' then
            if type(result) ~= 'table' or type(result.finished) ~= 'boolean'
                or type(result.soldCount) ~= 'number' or type(result.revenue) ~= 'number'
                or result.soldCount < 0 or result.soldCount % 1 ~= 0
                or result.revenue < 0 or result.revenue % 1 ~= 0 then error('tiago_sale_result_invalid') end
            c.saleSoldCount = c.saleSoldCount + result.soldCount
            c.saleRevenue = c.saleRevenue + result.revenue
            if not result.finished then return { sale(ctx, c, c.stage) } end
            if c.stage == 'sale_main' then return extra_condition(ctx, c) end
            return {}
        end
        error('tiago_stage_invalid')
    end
    local ability, source = ctx.payload.abilityId, ctx.payload.sourceItemId
    if ability == nil or source == nil then error('tiago_source_missing') end
    if string.match(ability, '^entrepreneur%.face%.D0[1-8]$') then return {} end -- 持续规则身份，非主动正文。
    if ability == 'entrepreneur.D01.immediate' then return {
        Effect.GainResource({ player = ctx.playerId, resourceType = 'originium', amount = 1 }),
        Effect.GainResource({ player = ctx.playerId, resourceType = 'originium_shard', amount = 1 }),
        Effect.GainResource({ player = ctx.playerId, resourceType = 'iron', amount = 1 }) }
    elseif ability == 'entrepreneur.D02.immediate' then return {
        Effect.LoseScore({ player = ctx.playerId, amount = 1 }), Effect.GainResource({ player = ctx.playerId, resourceType = 'gold_voucher', amount = 42 }) }
    elseif ability == 'entrepreneur.D05.immediate' or ability == 'entrepreneur.D06.immediate' then
        return { Effect.LoseScore({ player = ctx.playerId, amount = 1 }) }
    end
    local instance = Global.Content.GetInstance(source)
    local carrier = instance.entrepreneurAbilityItemId or source
    if Global.Content.GetInstance(carrier).definitionId ~= 'entrepreneur.ability.D09' then error('tiago_carrier_invalid') end
    zone_for(carrier, 'reputation')
    local c = { version = 7, player = ctx.playerId, source = source, carrier = carrier }
    if ability == 'entrepreneur.D09.investment' then return { FundInvestment(ctx, c, 'normal') }
    elseif ability == 'entrepreneur.D01.exchange' or ability == 'entrepreneur.D02.exchange' then
        local p = parameters(ctx, Global.Content.GetInstance(carrier))
        return { presented(Effect.Condition({ ruleCondition = true, decisionPlayer = ctx.playerId,
            leftEffects = { payment(ctx, c, p.exchange) }, rightEffects = { Effect.GainScore({ player = ctx.playerId, amount = 1 }) } }),
            'exchange.condition', { count = p.exchange }) }
    elseif ability == 'entrepreneur.D07.quick' or ability == 'entrepreneur.D08.quick' then
        local direct = ability == 'entrepreneur.D07.quick'
        return { presented(Effect.Condition({ ruleCondition = true, decisionPlayer = ctx.playerId,
            leftEffects = { payment(ctx, c, direct and 3 or 2, direct and 'retired' or 'original_supply') },
            rightEffects = { FundInvestment(ctx, c, direct and 'direct' or 'normal') } }), direct and 'quick.retired' or 'quick.payment') }
    end
    error('tiago_ability_unregistered:' .. ability)
end
