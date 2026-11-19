return function(ctx)
    local count = 0
    for _, display in ipairs(Global.Content.FindZones({ templateId = 'core.zone.enterprise_display' })) do
        for _, item in ipairs(Global.Content.FindInstances({ zoneId = display.id, contentType = 'enterprise' })) do
            count = count + 1
        end
    end
    local branches = { { id = 'dispatch', effects = { Flow.DispatchOperator({ apiVersion = 2, player = ctx.playerId, deckZoneId = Global.Content.FindZones({localZoneId='operator_deck'})[1].id }) } } }
    local options = { { candidateId = 'dispatch', label = Text.Ref('core:interaction.facility.rhodes_office.entry.dispatch') } }
    if count >= 2 then table.insert(branches, { id = 'upgrade_two', effects = {
        Flow.UpgradeDistinctEnterprises({ apiVersion = 1, player = ctx.playerId, count = 2, levels = 1 })
    } })
        table.insert(options, { candidateId = 'upgrade_two', label = Text.Ref('core:interaction.facility.rhodes_office.entry.upgrade_two') })
    end
    return { Effect.WithPresentation(Effect.Choice({ player = ctx.playerId,
        interactionType = 'effect.choice.execute', promptKey = 'facility.rhodes_office.entry', branches = branches }), {
        interaction = { prompt = Text.Ref('core:interaction.facility.rhodes_office.entry.prompt'), options = options }
    }) }
end
