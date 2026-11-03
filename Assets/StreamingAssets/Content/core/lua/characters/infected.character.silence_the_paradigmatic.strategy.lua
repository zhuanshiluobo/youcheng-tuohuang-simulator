-- 每个公开一级样式候选直接执行原特殊行动；付款、来源与响应由原激活流程复验。
return function(ctx)
    if ctx.eventType == 'EffectCompleted' then return {} end
    local branches, options, seen = {}, {}, {}
    local public_zones = {}
    for _, zone in ipairs(Global.Content.FindZones({ localZoneId = 'city_styles' })) do public_zones[zone.id] = true end
    for _, item in ipairs(Global.Content.FindInstances({ contentType = 'city_style' })) do
        local definition = Global.Content.GetDefinition(item.definitionId)
        local action = definition.specialActionId
        if public_zones[item.zoneId] and definition.level == 1 and action ~= nil and action ~= '' and not seen[action] then
            seen[action] = true
            branches[#branches + 1] = { id = action, effects = { Effect.ActivateCityStyleSpecial({ apiVersion = 1,
                player = ctx.playerId, specialActionId = action, declarationMarkerId = 'public_source', activationSource = 'public_style' }) } }
            options[#options + 1] = { candidateId = action, label = Text.Ref('core:interaction.entity.item', { entity = Text.Item(item.id) }) }
        end
    end
    if #branches == 0 then return {} end
    return { Effect.WithPresentation(Effect.Choice({ player = ctx.playerId, branches = branches,
        interactionType = 'effect.choice.execute', promptKey = 'core:interaction.public_style.choose' }),
        { interaction = { prompt = Text.Ref('core:interaction.public_style.choose'), options = options } }) }
end
