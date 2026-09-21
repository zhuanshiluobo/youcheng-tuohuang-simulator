return function(ctx)
    local source = ctx.payload.sourceSlotIndex
    local branches = {}
    for _, facility in ipairs(Global.Content.FindInstances()) do
        local target = facility.slotIndex
        local sameRow = source - source % 3 == target - target % 3
        local adjacent = target == source - 3 or target == source + 3 or
            (sameRow and (target == source - 1 or target == source + 1))
        if facility.owner == ctx.playerId and facility.hasEntryEffect and facility.color ~= 'rainbow' and adjacent then
            branches[#branches + 1] = { id = facility.id, effects = {
                Effect.ActivateFacilityEntry({ player = ctx.playerId, instanceId = facility.id })
            } }
        end
    end
    if #branches == 0 then return {} end
    return { Effect.Choice({ player = ctx.playerId, branches = branches,
        interactionType = 'facility.entry.choice', promptKey = 'facility.entry.choose_adjacent' }) }
end
