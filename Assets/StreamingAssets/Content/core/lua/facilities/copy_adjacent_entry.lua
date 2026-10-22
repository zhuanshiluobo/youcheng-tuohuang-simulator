return function(ctx)
    local source = ctx.payload.sourceSlotIndex
    local branches = {}
    local options = {}
    for _, facility in ipairs(PlayerData.GetBuiltFacilities(ctx.playerId)) do
        if facility.owner == ctx.playerId and facility.hasEntryEffect and facility.color ~= 'rainbow' then
            local target = facility.slotIndex
            local sameRow = source - source % 3 == target - target % 3
            local adjacent = target == source - 3 or target == source + 3 or
                (sameRow and (target == source - 1 or target == source + 1))
            if adjacent then
                branches[#branches + 1] = { id = facility.id, effects = {
                    Effect.ActivateFacilityEntry({ player = ctx.playerId, instanceId = facility.id })
                } }
                options[#options + 1] = {
                    candidateId = facility.id,
                    label = Text.Ref('core:interaction.entity.item', { entity = Text.Item(facility.id) }),
                    description = Text.Ref('core:interaction.facility.slot', { slot = target + 1 })
                }
            end
        end
    end
    if #branches == 0 then return {} end
    return { Effect.WithPresentation(Effect.Choice({ player = ctx.playerId, branches = branches,
        interactionType = 'facility.entry.choice', promptKey = 'facility.entry.choose_adjacent' }), {
            interaction = { prompt = Text.Ref('core:interaction.facility.adjacent.prompt'), options = options }
        }) }
end
