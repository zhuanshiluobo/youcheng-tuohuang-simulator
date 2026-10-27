-- QY-015 正式内容 1.2.0；CONTENT-R3按真实完整grant分页与选面收据授权。
local function official_ability(ctx)
    local ability = Global.Content.GetInstance(ctx.payload.sourceItemId)
    if ability == nil or ability.id ~= ctx.payload.sourceItemId or
       ability.definitionId ~= 'entrepreneur.ability.P09' or ability.typeId ~= 'entrepreneur.ability_card' or
       ability.entrepreneurAbilityItemId ~= ability.id or ability.entrepreneurDefinitionId ~= 'entrepreneur.ability.P09' or
       ability.ownerSubject ~= ctx.playerId or ability.controller ~= ctx.playerId then return nil end
    local zone = Global.Content.FindZones({id=ability.zoneId})[1]
    if zone == nil or zone.templateId ~= 'core.zone.entrepreneur_active' or zone.owner ~= ctx.playerId then return nil end
    return ability
end
local function grant_sources(ctx, ability)
    local keys = {}
    for _, key in ipairs(ctx.payload.grantingSourceRefs or {}) do keys[key] = true end
    local cursor, revision, content, ownerKey, total = 0, nil, nil, nil, nil
    local sources = {}
    repeat
        local page = SourceData.GetGrantSources({cursor=cursor,
            expectedStateRevision=revision,expectedContentHash=content})
        if page.protocolVersion ~= 1 or page.authorizedPlayerId ~= ctx.playerId or page.ruleOwnerItemId ~= ability.id then
            error('paul.grant_scope_mismatch')
        end
        if revision ~= nil and (page.ruleOwnerSourceKey ~= ownerKey or page.totalCount ~= total) then
            error('paul.grant_page_mismatch')
        end
        revision, content, ownerKey, total = page.stateRevision, page.contentHash, page.ruleOwnerSourceKey, page.totalCount
        for _, source in ipairs(page.items) do
            -- key仅作原值等值关联；完整SourceRef由同一次宿主QuerySources提供。
            if not keys[source.sourceKey] then error('paul.grant_key_mismatch') end
            sources[#sources+1] = source
        end
        cursor = page.nextCursor
    until cursor == -1
    if #sources ~= total then error('paul.grant_page_incomplete') end
    return sources
end
local function has_grant(ctx, expected)
    local ability = official_ability(ctx)
    if ability == nil then return false end
    -- 即使目标在第一页也读取完整分页，并固定本次revision/content与rule owner。
    local grants = grant_sources(ctx, ability)
    for _, group in ipairs(ability.roundChoices or {}) do
        if group.selectedCandidateId ~= '' then
            for _, candidate in ipairs(group.candidates) do
                local rule = expected[candidate.faceId]
                if rule ~= nil and group.groupId == rule.groupId and
                   candidate.candidateId == group.selectedCandidateId and candidate.candidateId == rule.candidateId and
                   candidate.faceId == group.activeFaceId and candidate.definitionId == rule.definitionId then
                    local item = Global.Content.GetInstance(candidate.entityItemId)
                    if item ~= nil and item.id == candidate.entityItemId and item.definitionId == candidate.definitionId and
                       item.typeId == 'entrepreneur.round_card' and item.entrepreneurAbilityItemId == ability.id and
                       item.entrepreneurDefinitionId == ability.definitionId and item.ownerSubject == ctx.playerId and item.controller == ctx.playerId then
                        local zone = Global.Content.FindZones({id=item.zoneId})[1]
                        if zone ~= nil and zone.templateId == 'core.zone.entrepreneur_active' and zone.owner == ctx.playerId then
                            for _, source in ipairs(grants) do
                                local receipt = source.selectionReceipt
                                if source.itemId == item.id and source.definitionId == item.definitionId and
                                   source.activeFaceId == candidate.faceId and source.authorizedPlayerId == ctx.playerId and
                                   source.ruleOwnerItemId == item.id and source.effectiveZoneId == item.zoneId and
                                   source.abilityOwnerItemId == ability.id and source.abilityOwnerDefinitionId == ability.definitionId and
                                   source.authorizationRef ~= '' and source.declarationVersion ~= '' and source.handlerHash ~= '' and
                                   receipt ~= nil and receipt.protocolVersion == 1 and receipt.candidateId == group.selectedCandidateId and
                                   receipt.faceId == group.activeFaceId and receipt.requestId ~= '' and receipt.ownerEffectId ~= '' then return true end
                            end
                        end
                    end
                end
            end
        end
    end
    return false
end
return function(ctx)
    if official_ability(ctx) == nil then return {operation='keep'} end
    local a = ctx.payload.facts.arguments
    local player = tonumber(string.match(ctx.playerId, '^p(%d+)$'))
    if a.ownerSubject.subjectType ~= 'player' or a.ownerSubject.playerId ~= player or (a.itemTypeId ~= nil and a.itemTypeId ~= '') then return {operation='keep'} end
    local function place(typeId)
        return Effect.PlaceInfluence({executingPlayer=a.executingPlayer, influenceSource=a.influenceSource,
            ownerSubject=a.ownerSubject, targetSlotId=a.targetSlotId, targetLocationId=a.targetLocationId,
            candidateScope=a.candidateScope, subjectFilter=a.subjectFilter, excludedSubjectKeys=a.excludedSubjectKeys,
            causeKind=a.causeKind, replaceEffectId=a.replaceEffectId, itemTypeId=typeId})
    end
    local root
    if has_grant(ctx, {P03={groupId='entrepreneur.group.P.3',candidateId='entrepreneur.candidate.P03',definitionId='entrepreneur.round.P.3'},P04={groupId='entrepreneur.group.P.3',candidateId='entrepreneur.candidate.P04',definitionId='entrepreneur.round.P.3'}}) then root = place('entrepreneur.gang_marker')
    else
        root = Effect.WithPresentation(Effect.Choice({player=ctx.playerId, interactionType='effect.choice.execute', branches={
            {id='ordinary',effects={place('core.player_marker')}},
            {id='gang',effects={place('entrepreneur.gang_marker')}}}}), {interaction={
            prompt=Text.Ref('core:entrepreneur.P.place.type'),options={
                {candidateId='ordinary',label=Text.Ref('core:entrepreneur.P.place.ordinary')},
                {candidateId='gang',label=Text.Ref('core:entrepreneur.P.place.gang')}}}})
    end
    return {operation='replace',effect=root,resultContract='place_result',permissionContract='inherit_original'}
end
