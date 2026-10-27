-- 只读当前真实来源所授权的本牌markers区；不使用跨卡总数或兼容markerCount。
-- SourceData按稳定实例顺序分页。官方T09只消费本人普通标记，等价ID不另开选物页。
local function token_inventory(ctx, card)
    if type(card) ~= 'string' or card == '' then error('entrepreneur.T.cleanup.source_missing') end
    local cursor, count, seen = 0, 0, 0
    local ids, header, source_zone = {}, nil, nil
    repeat
        local page = SourceData.GetTransferableTokens({ carrierItemId = card,
            localZoneId = 'markers', typeId = 'core.player_marker', cursor = cursor,
            expectedStateRevision = ctx.stateRevision,
            expectedContentHash = header and header.contentHash or nil })
        if page.protocolVersion ~= 1 or page.stateRevision ~= ctx.stateRevision
            or page.authorizedPlayerId ~= ctx.playerId or page.ruleOwnerItemId ~= card
            or type(page.ruleOwnerSourceKey) ~= 'string' or page.ruleOwnerSourceKey == '' then
            error('entrepreneur.T.cleanup.token_scope_invalid')
        end
        if header == nil then header = page
        elseif page.ruleOwnerSourceKey ~= header.ruleOwnerSourceKey
            or page.contentHash ~= header.contentHash or page.totalCount ~= header.totalCount then
            error('entrepreneur.T.cleanup.token_scope_changed')
        end
        for _, token in ipairs(page.items) do
            seen = seen + 1
            local permitted = false
            for _, operation in ipairs(token.operations) do
                if operation == 'transfer.original_supply' then permitted = true end
            end
            if token.carrierItemId == card and token.localZoneId == 'markers'
                and token.typeId == 'core.player_marker' and token.ownerSubject == ctx.playerId
                and token.hasOpenDeparture == false and permitted
                and type(token.originalSupplyZoneId) == 'string' and token.originalSupplyZoneId ~= '' then
                if source_zone ~= nil and source_zone ~= token.zoneId then
                    error('entrepreneur.T.cleanup.token_zone_changed')
                end
                source_zone = token.zoneId
                count = count + 1
                if #ids < 3 then ids[#ids + 1] = token.itemId end
            end
        end
        cursor = page.nextCursor
    until cursor == -1
    if seen ~= header.totalCount then error('entrepreneur.T.cleanup.token_page_incomplete') end
    return ids, count, source_zone
end

-- 由正式settlementHandlers登记的纯资格入口，不创建Effect、不清收据。
function availability(ctx)
    local source = ctx.facts.source
    local enabled = false
    if ctx.window.kind == 'player_cleanup' and source ~= nil and source.persistentActive == true then
        local _, count = token_inventory(ctx, source.itemId)
        enabled = count >= 3
    end
    return { enabled = enabled,
        disabledReasonKey = enabled and '' or 'core:entrepreneur.T.cleanup.unavailable' }
end

return function(ctx)
    local card = ctx.payload.sourceItemId
    if type(card) ~= 'string' or card == '' then error('entrepreneur.T.cleanup.source_missing') end
    local ids, _, source_zone = token_inventory(ctx, card)
    -- 空库存也返回合法来源区和空ID，交通用事务按requiredCount=3足额失败，不能空完成左式。
    if source_zone == nil then
        for _, zone in ipairs(Global.Content.FindZones()) do
            if zone.parentKind == 'Item' and zone.parentId == card and zone.localZoneId == 'markers' then
                if source_zone ~= nil then error('entrepreneur.T.cleanup.storage_ambiguous') end
                source_zone = zone.id
            end
        end
    end
    if source_zone == nil then error('entrepreneur.T.cleanup.storage_missing') end
    local choice = Effect.WithPresentation(Effect.Choice({ player = ctx.playerId,
        interactionType = 'effect.choice.execute', branches = {
            { id = 'place', effects = { Effect.PlaceInfluence({ executingPlayer = ctx.playerId,
                ownerSubject = ctx.playerId, influenceSource = 'player_supply',
                causeKind = 'other_intrinsic_flow' }) } },
            { id = 'resource', effects = { Effect.GainResource({ recipient = ctx.playerId,
                resourceType = 'PureOriginium', amount = 1 }) } }
        } }), { interaction = {
            prompt = Text.Ref('core:entrepreneur.T.cleanup.reward.prompt'),
            options = {
                { candidateId = 'place', label = Text.Ref('core:entrepreneur.T.cleanup.reward.place') },
                { candidateId = 'resource', label = Text.Ref('core:entrepreneur.T.cleanup.reward.resource') }
            }
        } })
    -- Lua明确选定3个真实token；api3只作足额原子转移及来源/原供应/恢复校验。
    -- Condition执行/放弃保留，成功后的收益Choice必选，不另造付款选物或固定交付协议。
    return { Effect.WithPresentation(Effect.Condition({ decisionPlayer = ctx.playerId,
        ruleCondition = true,
        leftEffects = { Effect.MoveItem({ apiVersion = 3, player = ctx.playerId,
            itemIds = ids, expectedSource = { zoneId = source_zone },
            destination = { kind = 'original_supply' }, insertion = { kind = 'unordered' },
            operationProfileId = 'core.player_marker_storage', requiredCount = 3 }) },
        rightEffects = { choice }
    }), { interaction = { prompt = Text.Ref('core:entrepreneur.T.cleanup.payment.prompt') } }) }
end
